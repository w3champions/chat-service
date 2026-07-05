using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C4 Task 8 — the END-TO-END MODERATION ACCEPTANCE suite. This drives MULTIPLE real
/// <see cref="ChatHub"/> instances (author / member / moderator / target) plus the two REST surfaces
/// (<see cref="MuteController"/>, <see cref="ModerationHistoryController"/>) through the SHIPPED
/// moderation pipeline (Tasks 1-7) while SHARING one instance of every singleton — the registries,
/// engine, coalescer, accumulator, <see cref="TicketStore"/>, <see cref="SessionRegistry"/>,
/// <see cref="ConnectionMapping"/>, the repos on the shared <see cref="IntegrationTestBase.MongoClient"/>,
/// the <see cref="MuteReconciliationTestHarness"/>, and ONE shared <see cref="HubPushCaptureHarness"/>
/// whose <see cref="HubPushCaptureHarness.HubContext"/> is handed to the
/// <see cref="FanOutEngine"/>/<see cref="ActivityCoalescer"/>/<see cref="ViewersAccumulator"/> so every
/// fan-out push (MessageReceived / MessageDeleted / BulkMessagesDeleted) lands in one capture. This is
/// the <see cref="HubProtocolIntegrationTests"/> multi-instance + shared-singleton idiom (C3 Task 20),
/// extended with the moderation collaborators.
/// <para>
/// These are ACCEPTANCE tests over already-shipped code, so they were GREEN on write. TIME is
/// DETERMINISTIC via a single <see cref="FakeTimeProvider"/>; the one real-clock seam is the one-time
/// ticket (<see cref="ChatHub.OnConnectedAsync"/> consumes it with <c>DateTime.UtcNow</c>, so tickets
/// are minted with <c>DateTime.UtcNow</c> too — exactly as <see cref="HubProtocolIntegrationTests"/>
/// and <see cref="MutePortTests"/> handle it). Two capture surfaces, both keyed by connectionId: the
/// shared <see cref="HubPushCaptureHarness"/> records the <c>IHubContext</c> fan-out pushes; each hub's
/// own capturing <see cref="IHubCallerClients"/> records the connect-path <c>Clients.Caller</c> pushes
/// (<c>SessionState</c>/<c>PlayerBannedFromChat</c>) and any <c>Context.Abort()</c> into the shared
/// <see cref="_hubSends"/>. The endDate-only <c>PlayerBannedFromChat</c> reconciliation push flows
/// through the <see cref="MuteReconciliationTestHarness"/> instead (its own IHubContext).
/// </para>
/// </summary>
public class ModerationIntegrationTests : IntegrationTestBase
{
    private static readonly DateTime T0 = new(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);

    // ---- shared singletons (one instance each, shared across every hub built in a test) ------------
    private FakeTimeProvider _time;
    private HubPushCaptureHarness _harness;

    private TicketStore _ticketStore;
    private SessionRegistry _sessionRegistry;
    private ConnectionMapping _connectionMapping;
    private FocusRegistry _focusRegistry;
    private OnlineMemberRegistry _onlineMemberRegistry;
    private MessageRateLimiter _messageRateLimiter;
    private ChannelCreationRateLimiter _channelCreationRateLimiter;
    private ActivityCoalescer _activityCoalescer;
    private FanOutEngine _fanOutEngine;
    private ViewersAccumulator _viewersAccumulator;
    private UserDirectoryRepository _userDirectory;
    private MuteRepository _muteRepository;
    private MuteReconciliationTestHarness _reconcileHarness;
    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private MessageRepository _messageRepository;
    private MentionInboxRepository _mentionInboxRepository;
    private MentionFanOut _mentionFanOut;
    private CapturingMentionInboxCleaner _mentionCleaner;
    private SessionStateAssembler _assembler;
    private Mock<IChatAuthenticationService> _authService;

    // The two REST surfaces, constructed directly against the shared singletons (mirrors
    // MuteReconciliationTests / ModerationHistoryControllerTests).
    private MuteController _muteController;
    private ModerationHistoryController _moderationController;

    // Every Clients.Caller/Client push + every Context.Abort(), in order, across ALL connections. The
    // fan-out pushes go to _harness instead; the reconciliation PlayerBannedFromChat push goes to the
    // MuteReconciliationTestHarness.
    private readonly List<(string ConnectionId, string Method, object Payload)> _hubSends = new();

    [SetUp]
    public void SetupBeforeEach()
    {
        _hubSends.Clear();
        _time = new FakeTimeProvider(new DateTimeOffset(T0, TimeSpan.Zero));
        _harness = new HubPushCaptureHarness();

        _ticketStore = new TicketStore();
        _sessionRegistry = new SessionRegistry();
        _connectionMapping = new ConnectionMapping();
        _focusRegistry = new FocusRegistry();
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _messageRateLimiter = new MessageRateLimiter();
        _channelCreationRateLimiter = new ChannelCreationRateLimiter();
        _userDirectory = new UserDirectoryRepository(MongoClient);
        _muteRepository = new MuteRepository(MongoClient);
        // Wire ApplyBanAsync to the REAL repo so hub/controller bans persist to (and are removable from)
        // the live DB, and capture the endDate-only PlayerBannedFromChat reconciliation push per-conn.
        _reconcileHarness = new MuteReconciliationTestHarness(_connectionMapping, _muteRepository);
        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _messageRepository = new MessageRepository(MongoClient);
        _mentionInboxRepository = new MentionInboxRepository(MongoClient);
        // The REAL C6 T5 writer (D3/D4), shared with the hubs' own membership/session state, so tests
        // can seed genuine mention-inbox entries the same way the send pipeline would (C6 Task 7).
        _mentionFanOut = new MentionFanOut(_harness.HubContext, _sessionRegistry, _membershipRepository, _mentionInboxRepository);
        // Wraps the REAL C6 Task 7 cleaner (MentionInboxCleaner) so DeleteMessage/PurgeMessagesFromUser
        // physically remove mention-inbox rows in this suite too, while still recording each call's exact
        // id batch for the pre-existing spy assertions below.
        _mentionCleaner = new CapturingMentionInboxCleaner(new MentionInboxCleaner(MongoClient));

        _authService = new Mock<IChatAuthenticationService>();
        _authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync((W3CUserAuthentication id) =>
                new ChatUserResolution(new ChatUser(id.BattleTag, id.IsAdmin, null, new ProfilePicture(), null, null), true));

        _assembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            _messageRepository,
            _muteRepository,
            _onlineMemberRegistry,
            _connectionMapping,
            _mentionInboxRepository);

        // The three fan-out sinks ALL push through the ONE shared harness and read the SHARED registries
        // the hubs mutate, so every push lands in a single ordered capture.
        _activityCoalescer = new ActivityCoalescer(_harness.HubContext, _onlineMemberRegistry);
        _viewersAccumulator = new ViewersAccumulator(_harness.HubContext, _focusRegistry);
        _fanOutEngine = new FanOutEngine(
            _harness.HubContext, _focusRegistry, _onlineMemberRegistry, _activityCoalescer, _sessionRegistry, new PresenceInterestRegistry(), _viewersAccumulator, _time);

        _muteController = new MuteController(_muteRepository, _reconcileHarness.Service);
        _moderationController = new ModerationHistoryController(_channelRepository, _messageRepository);
    }

    // ============================================================================================
    // Fixture plumbing
    // ============================================================================================

    private static W3CUserAuthentication Identity(string battleTag) =>
        new() { BattleTag = battleTag, Name = battleTag.Split('#')[0] };

    // A permissioned moderator identity — HasPermission (IsAdmin ∧ Permissions.Contains) is what the
    // FanOutEngine shadow branch and the GetMessages moderator branch both key on.
    private static W3CUserAuthentication ModeratorIdentity(string battleTag) =>
        new()
        {
            BattleTag = battleTag,
            Name = battleTag.Split('#')[0],
            IsAdmin = true,
            Permissions = new HashSet<EPermission> { EPermission.Moderation },
        };

    private ChatHub BuildHub(string connectionId, string accessToken)
    {
        var hub = new ChatHub(
            _connectionMapping,
            _reconcileHarness.Service,
            _ticketStore,
            _sessionRegistry,
            _userDirectory,
            _assembler,
            _focusRegistry,
            _onlineMemberRegistry,
            _messageRateLimiter,
            _time,
            _channelRepository,
            _membershipRepository,
            _channelCreationRateLimiter,
            _messageRepository,
            _fanOutEngine,
            _viewersAccumulator,
            _mentionCleaner,
            RelationshipProviderTestFactory.CreateIgnored(),
            new UserSettingsRepository(MongoClient),
            new DmInitiationTracker(),
            _authService.Object,
            MentionFanOutTestFactory.CreateIgnored(MongoClient),
            new PresenceInterestRegistry(),
            new MentionInboxRepository(MongoClient));

        var clients = new Mock<IHubCallerClients>();
        clients.Setup(c => c.Caller).Returns(CapturingSingle(connectionId));
        clients.Setup(c => c.Client(It.IsAny<string>())).Returns<string>(CapturingSingle);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(CapturingGroup());
        hub.Clients = clients.Object;

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        context.Setup(c => c.Features).Returns(BuildFeatures(accessToken));
        context.Setup(c => c.Abort()).Callback(() => Record(connectionId, "ABORT", null));
        hub.Context = context.Object;
        hub.Groups = new Mock<IGroupManager>().Object;

        return hub;
    }

    // Mints a fresh one-time ticket (real wall-clock, matching OnConnectedAsync's DateTime.UtcNow
    // consumption — the deliberate seam decoupled from the FakeTimeProvider) and drives the SHIPPED
    // connect path end-to-end for a regular user identity.
    private async Task<ChatHub> Connect(string connectionId, string battleTag) =>
        await ConnectWith(connectionId, Identity(battleTag));

    // Connect path for a permissioned moderator (IsAdmin ∧ Moderation).
    private async Task<ChatHub> ConnectModerator(string connectionId, string battleTag) =>
        await ConnectWith(connectionId, ModeratorIdentity(battleTag));

    private async Task<ChatHub> ConnectWith(string connectionId, W3CUserAuthentication identity)
    {
        var ticket = _ticketStore.Mint(identity, DateTime.UtcNow);
        var hub = BuildHub(connectionId, ticket);
        await hub.OnConnectedAsync();
        return hub;
    }

    private static IFeatureCollection BuildFeatures(string accessToken)
    {
        var features = new FeatureCollection();
        var httpContext = new DefaultHttpContext();
        if (accessToken != null)
        {
            httpContext.Request.QueryString = new QueryString($"?access_token={accessToken}");
        }
        var httpContextFeature = new Mock<IHttpContextFeature>();
        httpContextFeature.Setup(f => f.HttpContext).Returns(httpContext);
        features.Set<IHttpContextFeature>(httpContextFeature.Object);
        return features;
    }

    private ISingleClientProxy CapturingSingle(string target)
    {
        var proxy = new Mock<ISingleClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) => Record(target, method, args.Length > 0 ? args[0] : null))
            .Returns(Task.CompletedTask);
        return proxy.Object;
    }

    private IClientProxy CapturingGroup()
    {
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) => Record("group", method, args.Length > 0 ? args[0] : null))
            .Returns(Task.CompletedTask);
        return proxy.Object;
    }

    private void Record(string target, string method, object payload)
    {
        lock (_hubSends)
        {
            _hubSends.Add((target, method, payload));
        }
    }

    // ---- Mongo seed helpers ------------------------------------------------------------------------

    private async Task<ChatChannel> CreateChannel(string name, ChannelType type = ChannelType.Public, SystemChannelKind? systemKind = null)
    {
        var channel = new ChatChannel
        {
            Type = type,
            Name = name,
            NormalizedName = ChannelNames.Normalize(name),
            SystemKind = systemKind,
            SystemRef = type == ChannelType.System ? "sysref-" + name : null,
        };
        await _channelRepository.Insert(channel);
        return channel;
    }

    private Task SeedMembership(string channelId, string battleTag, NotificationLevel level = NotificationLevel.All, long lastReadSeq = 0) =>
        _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = channelId,
            BattleTag = battleTag,
            NotificationLevel = level,
            LastReadSeq = lastReadSeq,
            JoinedAt = T0,
        });

    // Directory row (mirrors ChatHubSendMessageTests' SeedDirectory) — the send pipeline's step-5.25
    // mention markup gate resolves a target through this collection (resolvability-only).
    private Task SeedDirectory(string battleTag) =>
        _userDirectory.Upsert(new UserDirectoryEntry
        {
            BattleTag = battleTag,
            DisplayBattleTag = battleTag,
            NormalizedName = battleTag.ToLowerInvariant(),
            LastSeenAt = T0,
        });

    // Seeds a durable message via the SAME seq-allocation path the real send pipeline uses, so the
    // channel's LastSeq stays consistent with directly-seeded history.
    private async Task<ChannelMessage> SeedMessage(string channelId, string senderBattleTag, string content, DateTime? expiresAt = null, bool shadow = false)
    {
        var seq = await _channelRepository.AllocateSeq(channelId, T0);
        var message = new ChannelMessage
        {
            ChannelId = channelId,
            Seq = seq,
            Sender = new MessageSender { BattleTag = senderBattleTag, Name = senderBattleTag.Split('#')[0] },
            Content = content,
            SentAt = T0,
            Shadow = shadow,
            ExpiresAt = expiresAt,
        };
        await _messageRepository.Insert(message);
        return message;
    }

    // ---- capture readers ---------------------------------------------------------------------------

    private IReadOnlyList<MessageDto> MessageReceivedFor(string connectionId) =>
        _harness.SignalsFor(connectionId)
            .Where(s => s.Method == ChatEvents.MessageReceived)
            .Select(s => (MessageDto)s.Payload)
            .ToList();

    private IReadOnlyList<MessageDeletedDto> MessageDeletedFor(string connectionId) =>
        _harness.SignalsFor(connectionId)
            .Where(s => s.Method == ChatEvents.MessageDeleted)
            .Select(s => (MessageDeletedDto)s.Payload)
            .ToList();

    private IReadOnlyList<BulkMessagesDeletedDto> BulkMessagesDeletedFor(string connectionId) =>
        _harness.SignalsFor(connectionId)
            .Where(s => s.Method == ChatEvents.BulkMessagesDeleted)
            .Select(s => (BulkMessagesDeletedDto)s.Payload)
            .ToList();

    private SessionStateDto SessionStateFor(string connectionId)
    {
        lock (_hubSends)
        {
            return _hubSends
                .Where(s => s.ConnectionId == connectionId && s.Method == ChatEvents.SessionState)
                .Select(s => (SessionStateDto)s.Payload)
                .LastOrDefault();
        }
    }

    private bool AbortedOn(string connectionId)
    {
        lock (_hubSends)
        {
            return _hubSends.Any(s => s.ConnectionId == connectionId && s.Method == "ABORT");
        }
    }

    private static ChannelDto ChannelDtoFor(SessionStateDto dto, string channelId) =>
        dto.Channels.Single(c => c.Channel.Id == channelId);

    private async Task<ModerationMessagePageDto> RestModerationHistory(string channelId)
    {
        var result = await _moderationController.GetChannelMessages(channelId, beforeSeq: null, limit: 100) as OkObjectResult;
        Assert.IsNotNull(result, "the REST moderation-history endpoint must return 200 OK for a moderatable channel");
        return result.Value as ModerationMessagePageDto;
    }

    private static string EndDate(int daysFromNow) => DateTime.UtcNow.AddDays(daysFromNow).ToString("O");

    // ============================================================================================
    // Scenario 1 — brief acceptance 2 (coordinator check 2): the shadow ILLUSION end-to-end, across a
    // full reconnect and pagination, for author / member / moderator.
    // ============================================================================================

    [Test]
    public async Task ShadowIllusion_SurvivesReconnectAndPagination_EndToEnd()
    {
        // Cast: A is shadow-banned (via the REST MuteController POST path, BEFORE connecting, so the
        // connect ceremony resolves Shadow from the DB and seeds the cache). B is a normal focused
        // member. M is a permissioned, focused MODERATOR. All three are members of the ONE public channel.
        const string ATag = "shadow#1";
        const string BTag = "member#2";
        const string MTag = "moderator#3";

        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        await SeedMembership(channel.Id, ATag, NotificationLevel.All);
        await SeedMembership(channel.Id, BTag, NotificationLevel.All);
        await SeedMembership(channel.Id, MTag, NotificationLevel.All);

        // B and M are directory-resolvable (C6 Task 7 re-assertion): the send pipeline's step-5.25
        // markup gate only accepts a mention whose target resolves via the directory, so A's shadow
        // send below can carry GENUINE mention markup of both — real, eligible targets (durable
        // membership + NotificationLevel.All, seeded above) that WOULD receive an entry for a normal,
        // non-shadow message. That is what makes the "zero entries" assertions below non-vacuous.
        await SeedDirectory(BTag);
        await SeedDirectory(MTag);

        // Shadow-ban A via the REST POST path (ApplyBanAsync persists; no live connection yet to reconcile).
        var banResult = await _muteController.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = ATag,
            endDate = EndDate(1),
            author = MTag,
            reason = "shadow test",
            isShadowBan = true,
        });
        Assert.IsInstanceOf<OkObjectResult>(banResult, "the REST shadow-ban POST must succeed");

        // Connect all three through the shared singletons; A's connect seeds a Shadow mute cache.
        var aHub = await Connect("conn-a1", ATag);
        var bHub = await Connect("conn-b1", BTag);
        var mHub = await ConnectModerator("conn-m1", MTag);

        // All three open the channel in the foreground (focused delivery targets focused connections).
        Assert.AreEqual(ChatResultCode.Ok, (await aHub.FocusChannel(channel.Id)).Code);
        Assert.AreEqual(ChatResultCode.Ok, (await bHub.FocusChannel(channel.Id)).Code);
        Assert.AreEqual(ChatResultCode.Ok, (await mHub.FocusChannel(channel.Id)).Code);

        // A sends to the PUBLIC channel — the shadow mute gate flags it and persists it (returns Ok, the
        // illusion). seq 1. The content carries REAL mention markup of B and M (C6 Task 7 re-assertion) —
        // both resolvable, both durable members with NotificationLevel.All — so the mentions leg below
        // is a genuine end-to-end proof of the shadow guardrail, not a vacuous absence-of-the-feature.
        var send = await aHub.SendMessage(channel.Id, $"am I invisible? <@{BTag}> <@{MTag}>");
        Assert.AreEqual(ChatResultCode.Ok, send.Code, "a shadow send still returns Ok (the illusion)");
        Assert.AreEqual(1L, send.Seq);

        // The persisted row is genuinely flagged shadow (server-side truth).
        var persisted = await _messageRepository.Load(send.MessageId);
        Assert.IsTrue(persisted.Shadow, "the persisted row carries the REAL shadow flag");

        // LIVE: A's own focused connection receives the unflagged illusion echo (Shadow forced false).
        var aLive = MessageReceivedFor("conn-a1");
        Assert.AreEqual(1, aLive.Count, "the shadow author sees their OWN message live (the echo)");
        Assert.AreEqual(send.MessageId, aLive[0].Id);
        Assert.IsFalse(aLive[0].Shadow, "A's own echo is UNFLAGGED (Shadow forced false) — the illusion");

        // LIVE: B, a normal focused member, receives NOTHING — shadow-ban integrity holds for non-mods.
        Assert.AreEqual(0, MessageReceivedFor("conn-b1").Count,
            "a normal focused member receives NOTHING live for a shadow message");

        // LIVE: M, a focused MODERATOR, receives the message FLAGGED (Shadow == true, ForModerator).
        var mLive = MessageReceivedFor("conn-m1");
        Assert.AreEqual(1, mLive.Count, "a focused moderator DOES receive the shadow message live");
        Assert.AreEqual(send.MessageId, mLive[0].Id);
        Assert.IsTrue(mLive[0].Shadow, "the moderator's live copy is REAL-flagged (Shadow == true)");

        // PULL (GetMessages): A sees its OWN row, forced Shadow false (illusion on the read path too).
        var aRead = await aHub.GetMessages(channel.Id, beforeSeq: null, aroundSeq: null, limit: 100);
        Assert.AreEqual(ChatResultCode.Ok, aRead.Code);
        Assert.AreEqual(1, aRead.Messages.Count, "A's own shadow row IS visible in A's own history");
        Assert.IsFalse(aRead.Messages[0].Shadow, "A's own row reads back UNFLAGGED (illusion on the pull path)");

        // PULL: B (non-mod, UserVisible) sees NOTHING — a foreign author's shadow row is excluded.
        var bRead = await bHub.GetMessages(channel.Id, beforeSeq: null, aroundSeq: null, limit: 100);
        Assert.AreEqual(ChatResultCode.Ok, bRead.Code);
        Assert.IsEmpty(bRead.Messages, "a normal member's history excludes a foreign author's shadow row");

        // PULL: M (moderator branch — LoadPage*ForModerator, no UserVisible) sees the row FLAGGED.
        var mRead = await mHub.GetMessages(channel.Id, beforeSeq: null, aroundSeq: null, limit: 100);
        Assert.AreEqual(ChatResultCode.Ok, mRead.Code);
        Assert.AreEqual(1, mRead.Messages.Count, "the moderator's in-channel read includes the shadow row");
        Assert.IsTrue(mRead.Messages[0].Shadow, "the moderator's GetMessages copy is REAL-flagged");

        // REST moderation-history: the shadow row comes back flagged (never filtered like a user read).
        var restPage = await RestModerationHistory(channel.Id);
        Assert.AreEqual(1, restPage.Messages.Count);
        Assert.IsTrue(restPage.Messages[0].Shadow, "the REST moderation-history row carries the real shadow flag");

        // MENTIONS LEG (C6 Task 7 re-assertion — the C4/C6 handoff item): A's message carries REAL
        // mention markup of B and M, both genuinely eligible (durable membership + NotificationLevel.All,
        // resolvable in the directory, neither is the sender) — a normal, non-shadow send with this exact
        // content WOULD create a real inbox entry + MentionNotified for each of them (this is precisely
        // the eligibility the C6 Task 5 fan-out grants). Because A's send is shadow, the pipeline must
        // still produce LITERALLY ZERO entries for anyone: the SendMessage call-site skip
        // (`!isShadow && mentionTags.Count > 0`) never invokes MentionFanOut.NotifyAsync at all, and even
        // if that skip were ever dropped, NotifyAsync's own defense-in-depth `message.Shadow` early-return
        // (C6 Task 5) would still stop it. This is now a genuine, non-vacuous end-to-end proof of the
        // shadow guardrail (C6 Task 5's own unit-level `ShadowSender_MentionsOthers_...` test proves the
        // same rule in isolation; this is the moderation-pipeline, real-write-path proof) — breaking
        // either guard would flip either assertion below from empty to non-empty.
        Assert.IsEmpty(_mentionCleaner.Calls, "a shadow send must never touch the mention-inbox cleaner hook");
        Assert.IsEmpty(await _mentionInboxRepository.LoadForUser(BTag),
            "a shadow send must create NO mention-inbox entry for a genuinely eligible, mentioned member (B)");
        Assert.IsEmpty(await _mentionInboxRepository.LoadForUser(MTag),
            "a shadow send must create NO mention-inbox entry for a genuinely eligible, mentioned moderator (M)");

        // ---- FULL RECONNECT (fresh ticket + new connectionId through the SAME shared singletons) ----

        // A: kill conn-a1, reconnect on conn-a2. The shadow ban is durable, so the reconnect re-seeds a
        // Shadow cache and A still sees its OWN row (forced Shadow false) via GetMessages paging.
        await aHub.OnDisconnectedAsync(null);
        var aHub2 = await Connect("conn-a2", ATag);
        var aReadAfter = await aHub2.GetMessages(channel.Id, beforeSeq: null, aroundSeq: null, limit: 100);
        Assert.AreEqual(ChatResultCode.Ok, aReadAfter.Code);
        Assert.AreEqual(1, aReadAfter.Messages.Count, "after a full reconnect, A's own shadow row is STILL present in A's history");
        Assert.AreEqual(send.MessageId, aReadAfter.Messages[0].Id);
        Assert.IsFalse(aReadAfter.Messages[0].Shadow, "after reconnect A's own row still reads back UNFLAGGED");

        // B: kill conn-b1, reconnect on conn-b2. The D7 count-based unread EXCLUDES the foreign shadow row,
        // so B reconnects with unread 0 / HasUnread false, and its history is still empty.
        await bHub.OnDisconnectedAsync(null);
        var bHub2 = await Connect("conn-b2", BTag);
        var bSession = SessionStateFor("conn-b2");
        Assert.IsNotNull(bSession, "the reconnect pushes a fresh SessionState to B's new connection");
        var bChannel = ChannelDtoFor(bSession, channel.Id);
        Assert.AreEqual(0L, bChannel.UnreadCount, "a foreign author's shadow row generates NO unread for B on reconnect (D7)");
        Assert.IsFalse(bChannel.HasUnread, "B has no unread after reconnect — the shadow row is invisible to it");
        var bReadAfter = await bHub2.GetMessages(channel.Id, beforeSeq: null, aroundSeq: null, limit: 100);
        Assert.IsEmpty(bReadAfter.Messages, "after reconnect B's history still excludes the foreign shadow row");
    }

    // ============================================================================================
    // Scenario 2 — brief acceptance 3 (coordinator check 3): cross-channel PURGE, user vs moderator views.
    // ============================================================================================

    [Test]
    public async Task Purge_EndToEnd_AcrossChannels_UserVsModeratorViews()
    {
        const string TargetTag = "target#123";
        const string ReaderTag = "reader#9";
        const string OtherTag = "other#7";
        const string ModTag = "purgemod#1";

        var pub = await CreateChannel("W3C Lounge", ChannelType.Public);
        var semi = await CreateChannel("clan-haven", ChannelType.SemiPublic);
        var match = await CreateChannel("match-42", ChannelType.System, SystemChannelKind.Match);
        var dm = await CreateChannel("dm-1", ChannelType.Dm);

        // A durable, ms-precise expiry so the "ExpiresAt untouched" assertion compares exactly through Mongo.
        var expiry = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        // The target's messages across all four channel types + an innocent survivor in the public channel.
        var survivorInPub = await SeedMessage(pub.Id, OtherTag, "innocent");
        var tInPub = await SeedMessage(pub.Id, TargetTag, "spam-public", expiresAt: expiry);
        var tInSemi = await SeedMessage(semi.Id, TargetTag, "spam-semi");
        var tInMatch = await SeedMessage(match.Id, TargetTag, "spam-match");
        var tInDm = await SeedMessage(dm.Id, TargetTag, "private-dm");

        // Reader R is a member of the three eligible channels so it can drive the USER read path + receive
        // the focused BulkMessagesDeleted; it is NOT in the DM.
        await SeedMembership(pub.Id, ReaderTag, NotificationLevel.All);
        await SeedMembership(semi.Id, ReaderTag, NotificationLevel.All);
        await SeedMembership(match.Id, ReaderTag, NotificationLevel.All);
        // The target is a member of the public channel so it can focus it (proving the target's own
        // focused connection is EXCLUDED from the BulkMessagesDeleted).
        await SeedMembership(pub.Id, TargetTag, NotificationLevel.All);

        // MENTION SCOPE WALL (C6 Task 7 — acceptance 3 + the cleaner/purge scope-wall parity): seed a
        // REAL mention-inbox entry for each of the target's four messages via the actual T5 writer
        // (MentionFanOut), including the DM one. After the purge, the three eligible-channel entries
        // must be PHYSICALLY REMOVED by the real cleaner, while the DM entry — never in the cleaner's
        // batch, never eligible — must SURVIVE untouched: the same scope wall the message purge itself
        // already honors (Dm/GroupDm/clan/lobby are never purged).
        const string MentionedTag = "mentioned#321";
        await SeedMembership(pub.Id, MentionedTag, NotificationLevel.All);
        await SeedMembership(semi.Id, MentionedTag, NotificationLevel.All);
        await SeedMembership(match.Id, MentionedTag, NotificationLevel.All);
        await SeedMembership(dm.Id, MentionedTag, NotificationLevel.All);
        await _mentionFanOut.NotifyAsync(pub, tInPub, new[] { MentionedTag }, T0);
        await _mentionFanOut.NotifyAsync(semi, tInSemi, new[] { MentionedTag }, T0);
        await _mentionFanOut.NotifyAsync(match, tInMatch, new[] { MentionedTag }, T0);
        await _mentionFanOut.NotifyAsync(dm, tInDm, new[] { MentionedTag }, T0);
        var beforePurgeEntries = await _mentionInboxRepository.LoadForUser(MentionedTag);
        Assert.AreEqual(4, beforePurgeEntries.Count,
            "sanity: the real T5 writer created one genuine entry per message before the purge runs");

        var readerHub = await Connect("conn-reader", ReaderTag);
        var targetHub = await Connect("conn-target", TargetTag);
        var modHub = await ConnectModerator("conn-mod", ModTag);

        // Focus: R on all three eligible channels, the target on the public channel.
        Assert.AreEqual(ChatResultCode.Ok, (await readerHub.FocusChannel(pub.Id)).Code);
        Assert.AreEqual(ChatResultCode.Ok, (await readerHub.FocusChannel(semi.Id)).Code);
        Assert.AreEqual(ChatResultCode.Ok, (await readerHub.FocusChannel(match.Id)).Code);
        Assert.AreEqual(ChatResultCode.Ok, (await targetHub.FocusChannel(pub.Id)).Code);

        // ---- PURGE ----
        var result = await modHub.PurgeMessagesFromUser(TargetTag);
        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.AreEqual(3, result.MessagesDeleted, "exactly the three eligible-channel rows are soft-deleted (DM excluded)");

        // DOC-LEVEL: the three eligible rows carry Deleted{by,at}; the DM row is UNTOUCHED (null).
        Assert.IsNotNull((await _messageRepository.Load(tInPub.Id)).Deleted);
        Assert.IsNotNull((await _messageRepository.Load(tInSemi.Id)).Deleted);
        Assert.IsNotNull((await _messageRepository.Load(tInMatch.Id)).Deleted);
        Assert.AreEqual(ModTag, (await _messageRepository.Load(tInPub.Id)).Deleted.By, "the moderator battleTag is the deletion attribution");
        Assert.IsNull((await _messageRepository.Load(tInDm.Id)).Deleted, "the DM row is NEVER soft-deleted (privacy wall)");

        // DOCS PERSIST (no hard delete) with ExpiresAt/TTL untouched.
        var reloadedPub = await _messageRepository.Load(tInPub.Id);
        Assert.IsNotNull(reloadedPub, "the doc survives — soft-delete only, never a hard delete");
        Assert.AreEqual(expiry, reloadedPub.ExpiresAt, "ExpiresAt/TTL is left untouched by the purge");

        // USER READS (R, non-moderator, UserVisible) EXCLUDE the target's rows across all three eligible
        // channels; the public survivor is still visible.
        var readPub = await readerHub.GetMessages(pub.Id, beforeSeq: null, aroundSeq: null, limit: 100);
        CollectionAssert.AreEqual(new[] { survivorInPub.Id }, readPub.Messages.Select(m => m.Id).ToArray(),
            "the user read excludes the purged public row, keeping the survivor");
        Assert.IsEmpty((await readerHub.GetMessages(semi.Id, beforeSeq: null, aroundSeq: null, limit: 100)).Messages,
            "the user read excludes the purged semiPublic row");
        Assert.IsEmpty((await readerHub.GetMessages(match.Id, beforeSeq: null, aroundSeq: null, limit: 100)).Messages,
            "the user read excludes the purged match row");

        // PER-CHANNEL BulkMessagesDeleted reached the focused reader on each eligible channel...
        var readerBulk = BulkMessagesDeletedFor("conn-reader");
        Assert.AreEqual(3, readerBulk.Count, "the focused reader receives one BulkMessagesDeleted per eligible channel");
        Assert.That(readerBulk.Select(d => d.ChannelId), Is.EquivalentTo(new[] { pub.Id, semi.Id, match.Id }));
        Assert.That(readerBulk.Single(d => d.ChannelId == pub.Id).MessageIds, Is.EquivalentTo(new[] { tInPub.Id }));

        // ...but NOT the target's own focused connection (not tipped off live).
        Assert.AreEqual(0, BulkMessagesDeletedFor("conn-target").Count,
            "the purge target's own focused connection is EXCLUDED from the removal event");

        // The MENTION CLEANER spy received EXACTLY the three deleted (eligible) ids — never the DM id.
        Assert.AreEqual(1, _mentionCleaner.Calls.Count, "the cleaner is invoked exactly once with the whole eligible batch");
        Assert.That(_mentionCleaner.Calls[0], Is.EquivalentTo(new[] { tInPub.Id, tInSemi.Id, tInMatch.Id }),
            "the cleaner receives exactly the soft-deleted eligible ids — never the untouched DM id");

        // The REAL cleaner (C6 Task 7) PHYSICALLY REMOVED the three eligible-channel entries; the DM
        // entry — out of the purge's scope wall — SURVIVES untouched (scope-wall parity between the
        // message purge and the mention-inbox cleanup).
        var afterPurgeEntries = await _mentionInboxRepository.LoadForUser(MentionedTag);
        Assert.AreEqual(1, afterPurgeEntries.Count,
            "only the DM (ineligible-channel) mention-inbox entry survives the purge — the other three are physically gone");
        Assert.AreEqual(tInDm.Id, afterPurgeEntries[0].MessageId,
            "the surviving entry is the DM one — the purge/cleaner scope wall holds");

        // MODERATOR REST history shows the purged rows FLAGGED (deleted + attribution).
        var pubHistory = await RestModerationHistory(pub.Id);
        var flaggedPub = pubHistory.Messages.Single(m => m.Id == tInPub.Id);
        Assert.IsTrue(flaggedPub.Deleted, "the REST moderation-history shows the purged public row flagged deleted");
        Assert.AreEqual(ModTag, flaggedPub.DeletedBy);
        var semiHistory = await RestModerationHistory(semi.Id);
        Assert.IsTrue(semiHistory.Messages.Single(m => m.Id == tInSemi.Id).Deleted,
            "the REST moderation-history shows the purged semiPublic row flagged deleted");
        var matchHistory = await RestModerationHistory(match.Id);
        Assert.IsTrue(matchHistory.Messages.Single(m => m.Id == tInMatch.Id).Deleted,
            "the REST moderation-history shows the purged System+Match row flagged deleted (all three eligible types symmetric)");
    }

    // ============================================================================================
    // Scenario 3 — brief acceptance 1 (integration-grade): both ban WRITE PATHS are equivalent, the
    // reconciliation push is endDate-only, no abort, live send flips to Muted, REST DELETE unbans live.
    // ============================================================================================

    [Test]
    public async Task BanBothWritePaths_NewPipeline_Equivalence_PayloadShape_NoAbort()
    {
        const string VictimTag = "victim#123";
        const string AdminTag = "admin#1";

        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        await SeedMembership(channel.Id, VictimTag, NotificationLevel.All);

        // The victim connects (real path): member of the public channel, registered in ConnectionMapping
        // (so a ban can reconcile it), unbanned cache. The admin connects so hub BanUser's GetUser resolves.
        var victimHub = await Connect("conn-victim", VictimTag);
        var adminHub = await ConnectModerator("conn-admin", AdminTag);

        var endDate = EndDate(1);

        // ---- PATH A: hub BanUser ----
        await adminHub.BanUser(VictimTag, "bad behavior", isShadowBan: false, endDate);

        Assert.IsTrue(_connectionMapping.TryGetMute("conn-victim", out var afterHub));
        Assert.AreEqual(MuteStatus.Full, afterHub.Status, "hub BanUser reconciles the live victim cache to Full");
        var hubStatus = afterHub.Status;

        // The reconciliation push (via MuteReconciliationService's IHubContext) is PlayerBannedFromChat
        // carrying EXACTLY one property, endDate — no reason / isShadowBan / author leak.
        var hubPayload = _reconcileHarness.PayloadFor("conn-victim", ChatEvents.PlayerBannedFromChat);
        AssertPlayerBannedPayloadIsEndDateOnly(hubPayload);

        // NO Context.Abort on either party on the hub ban path (bans never abort).
        Assert.IsFalse(AbortedOn("conn-victim"), "the victim connection is NEVER aborted by a ban (G1)");
        Assert.IsFalse(AbortedOn("conn-admin"), "the admin connection is never aborted");

        // ---- PATH B: REST MuteController POST (reset the cache first, then re-ban via the controller) ----
        _connectionMapping.SetMute("conn-victim", MuteStatus.None, DateTime.MinValue);
        var postResult = await _muteController.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = VictimTag,
            endDate = endDate,
            author = AdminTag,
            reason = "bad behavior",
            isShadowBan = false,
        });
        Assert.IsInstanceOf<OkObjectResult>(postResult, "the REST ban POST must succeed");

        Assert.IsTrue(_connectionMapping.TryGetMute("conn-victim", out var afterController));
        Assert.AreEqual(hubStatus, afterController.Status,
            "hub BanUser and REST AddLoungeMute produce IDENTICAL live-connection cache state (both route through ApplyBanAsync)");
        Assert.AreEqual(MuteStatus.Full, afterController.Status);
        Assert.IsFalse(AbortedOn("conn-victim"), "the REST ban path also never aborts the victim");

        // LIVE SEND flips to Muted WITHOUT a reconnect (write-path reconciliation). The victim's SAME
        // connection is used; enforcement is cache-only, so it holds even after the DB row is wiped.
        await _muteRepository.DeleteLoungeMute(VictimTag);
        var mutedSend = await victimHub.SendMessage(channel.Id, "let me talk");
        Assert.AreEqual(ChatResultCode.Muted, mutedSend.Code,
            "after a ban on either write path, the next public SendMessage on the SAME connection is Muted (no reconnect)");
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.AreEqual(0L, reloaded.LastSeq, "a cache-muted send must not persist");

        // REST DELETE unbans the live connection — the cache clears to None and the user can send again.
        var deleteResult = await _muteController.DeleteLoungeMute(VictimTag);
        // The DB row was wiped above, so DELETE reports 404 but STILL clears the live cache (reconcile-then-respond).
        Assert.IsInstanceOf<NotFoundObjectResult>(deleteResult, "DELETE of an already-gone DB row reports 404 but still frees the live connection");
        Assert.IsTrue(_connectionMapping.TryGetMute("conn-victim", out var afterUnban));
        Assert.AreEqual(MuteStatus.None, afterUnban.Status, "the REST DELETE clears the live victim cache to None");

        var okSend = await victimHub.SendMessage(channel.Id, "I can talk again");
        Assert.AreEqual(ChatResultCode.Ok, okSend.Code, "after the REST unban the victim can send again WITHOUT reconnecting");
    }

    // ============================================================================================
    // Scenario 4 — the single-message DELETE live-flag flow: focused mod flags, focused non-mod removes,
    // author connection silent, reconnect gone-for-users / flagged-for-mod.
    // ============================================================================================

    [Test]
    public async Task Delete_LiveFlagFlow()
    {
        const string AuthorTag = "author#1";
        const string ViewerTag = "viewer#2";
        const string ModTag = "delmod#3";

        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        await SeedMembership(channel.Id, AuthorTag, NotificationLevel.All);
        await SeedMembership(channel.Id, ViewerTag, NotificationLevel.All);
        await SeedMembership(channel.Id, ModTag, NotificationLevel.All);

        var authorHub = await Connect("conn-author", AuthorTag);
        var viewerHub = await Connect("conn-viewer", ViewerTag);
        var modHub = await ConnectModerator("conn-mod", ModTag);

        // All three focus — so the author being SILENT on the delete is a load-bearing exclusion (it WOULD
        // otherwise receive the event as a focused connection).
        Assert.AreEqual(ChatResultCode.Ok, (await authorHub.FocusChannel(channel.Id)).Code);
        Assert.AreEqual(ChatResultCode.Ok, (await viewerHub.FocusChannel(channel.Id)).Code);
        Assert.AreEqual(ChatResultCode.Ok, (await modHub.FocusChannel(channel.Id)).Code);

        var send = await authorHub.SendMessage(channel.Id, "delete me");
        Assert.AreEqual(ChatResultCode.Ok, send.Code);
        var messageId = send.MessageId;

        // ---- MODERATOR DELETE ----
        var del = await modHub.DeleteMessage(messageId);
        Assert.AreEqual(ChatResultCode.Ok, del.Code);

        // Soft-delete committed (doc survives, flagged, attributed).
        var reloaded = await _messageRepository.Load(messageId);
        Assert.IsNotNull(reloaded, "the doc survives — soft-delete only");
        Assert.IsNotNull(reloaded.Deleted);
        Assert.AreEqual(ModTag, reloaded.Deleted.By);

        // LIVE: the focused NON-moderator viewer received the removal event.
        var viewerDeleted = MessageDeletedFor("conn-viewer");
        Assert.AreEqual(1, viewerDeleted.Count, "the focused non-moderator receives the MessageDeleted removal");
        Assert.AreEqual(channel.Id, viewerDeleted[0].ChannelId);
        Assert.AreEqual(messageId, viewerDeleted[0].MessageId);

        // LIVE: the focused MODERATOR receives the SAME event (it branches client-side to flag, not remove).
        var modDeleted = MessageDeletedFor("conn-mod");
        Assert.AreEqual(1, modDeleted.Count, "the focused moderator receives the same MessageDeleted event");
        Assert.AreEqual(messageId, modDeleted[0].MessageId);

        // LIVE: the author's OWN connection got NOTHING (legacy AllExcept(author) — not tipped off live).
        Assert.AreEqual(0, MessageDeletedFor("conn-author").Count,
            "the moderated author's own focused connection is EXCLUDED from the removal event");

        // The moderator can still fetch the FLAGGED row — both in-channel (GetMessages mod branch) and REST.
        var modRead = await modHub.GetMessages(channel.Id, beforeSeq: null, aroundSeq: null, limit: 100);
        Assert.AreEqual(1, modRead.Messages.Count, "the moderator's in-channel read still includes the deleted row");
        Assert.IsTrue(modRead.Messages[0].Deleted, "the moderator's copy is flagged deleted");
        var restPage = await RestModerationHistory(channel.Id);
        var restRow = restPage.Messages.Single(m => m.Id == messageId);
        Assert.IsTrue(restRow.Deleted, "the REST moderation-history shows the deleted row flagged");
        Assert.AreEqual(ModTag, restRow.DeletedBy);

        // ---- RECONNECT: gone for users, flagged for the moderator ----
        await viewerHub.OnDisconnectedAsync(null);
        var viewerHub2 = await Connect("conn-viewer2", ViewerTag);
        var viewerReadAfter = await viewerHub2.GetMessages(channel.Id, beforeSeq: null, aroundSeq: null, limit: 100);
        Assert.IsEmpty(viewerReadAfter.Messages, "after reconnect the deleted row is gone for a user (UserVisible excludes it)");

        await modHub.OnDisconnectedAsync(null);
        var modHub2 = await ConnectModerator("conn-mod2", ModTag);
        var modReadAfter = await modHub2.GetMessages(channel.Id, beforeSeq: null, aroundSeq: null, limit: 100);
        Assert.AreEqual(1, modReadAfter.Messages.Count, "after reconnect the moderator still sees the row");
        Assert.IsTrue(modReadAfter.Messages[0].Deleted, "after reconnect the moderator's copy is still flagged deleted");
    }

    // ============================================================================================
    // Scenario 5 — C6 Task 7 acceptance 3: a real mention-inbox entry created by a genuine send is
    // PHYSICALLY REMOVED once the moderator soft-deletes the mentioning message — the real cleaner,
    // end-to-end, with the audit-before-cleaner ordering left completely untouched.
    // ============================================================================================

    [Test]
    public async Task DeleteMessage_WithMentions_RemovesEntries_EndToEnd()
    {
        const string AuthorTag = "mentauthor#1";
        const string MentionedTag = "mentioned#2";
        const string ModTag = "delmentmod#1";

        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        await SeedMembership(channel.Id, AuthorTag, NotificationLevel.All);
        await SeedMembership(channel.Id, MentionedTag, NotificationLevel.All);
        await SeedDirectory(MentionedTag);

        var authorHub = await Connect("conn-mentauthor", AuthorTag);
        var modHub = await ConnectModerator("conn-delmentmod", ModTag);

        // A real, non-shadow send carrying genuine mention markup — the REAL T5 writer fans it out and
        // creates a real mention-inbox entry for the eligible target.
        var send = await authorHub.SendMessage(channel.Id, $"hey <@{MentionedTag}>");
        Assert.AreEqual(ChatResultCode.Ok, send.Code);

        var beforeDeleteEntries = await _mentionInboxRepository.LoadForUser(MentionedTag);
        Assert.AreEqual(1, beforeDeleteEntries.Count,
            "sanity: sending a real mention creates a real inbox entry (precondition for this test)");
        Assert.AreEqual(send.MessageId, beforeDeleteEntries[0].MessageId);

        // ---- MODERATOR DELETE ----
        var del = await modHub.DeleteMessage(send.MessageId);
        Assert.AreEqual(ChatResultCode.Ok, del.Code);

        // The audit-before-cleaner ordering (C4, untouched by this task) already committed the
        // soft-delete before the cleaner ran — both effects are observable here.
        var reloaded = await _messageRepository.Load(send.MessageId);
        Assert.IsNotNull(reloaded.Deleted, "the message is soft-deleted — the audit-backed write committed");

        // The REAL cleaner (C6 Task 7) physically removed the mention-inbox entry the delete referenced
        // — C4's mentions leg is now load-bearing (acceptance 3).
        Assert.IsEmpty(await _mentionInboxRepository.LoadForUser(MentionedTag),
            "DeleteMessage's real IMentionInboxCleaner must physically remove the referenced mention-inbox entry");

        // The spy still recorded the exact call (the pre-existing capture contract, untouched).
        Assert.AreEqual(1, _mentionCleaner.Calls.Count);
        Assert.That(_mentionCleaner.Calls[0], Is.EquivalentTo(new[] { send.MessageId }));
    }

    // ============================================================================================
    // Helpers
    // ============================================================================================

    /// <summary>
    /// Asserts the slimmed PlayerBannedFromChat payload exposes ONLY an <c>endDate</c> property (a future
    /// DateTime) and leaks neither <c>reason</c> nor <c>isShadowBan</c> nor <c>author</c> to the client.
    /// The payload is an anonymous type, so it is reflected. Mirrors
    /// <see cref="ChatHubBanUserTests"/>' AssertPlayerBannedPayloadIsEndDateOnly.
    /// </summary>
    private static void AssertPlayerBannedPayloadIsEndDateOnly(object payload)
    {
        Assert.IsNotNull(payload, "PlayerBannedFromChat payload must not be null");
        Assert.IsNotInstanceOf<LoungeMute>(payload, "PlayerBannedFromChat must NOT send the full LoungeMute (leaks reason/isShadowBan)");
        var props = payload.GetType().GetProperties();
        Assert.AreEqual(1, props.Length, "the payload must expose EXACTLY one property");
        Assert.AreEqual("endDate", props[0].Name, "the only payload property must be endDate");
        var endDate = (DateTime)props[0].GetValue(payload);
        Assert.Greater(endDate, DateTime.UtcNow, "the endDate in the payload must be the future ban expiry");
    }

    /// <summary>
    /// Capturing <see cref="IMentionInboxCleaner"/> spy — records each message-id batch the hub asks it
    /// to purge (D10), so the purge scenario can assert the cleaner received exactly the eligible ids and
    /// the shadow-send scenario can assert it was never invoked. Mirrors the private spy in
    /// <see cref="ChatHubDeletionTests"/> (which is not accessible across files).
    /// <para>
    /// C6 Task 7: also DELEGATES to a real <paramref name="inner"/> cleaner after recording, so this
    /// suite's DeleteMessage/PurgeMessagesFromUser scenarios can assert PHYSICAL removal from
    /// mention_inbox (acceptance 3), not just that the hook was called with the right ids.
    /// </para>
    /// </summary>
    private sealed class CapturingMentionInboxCleaner(IMentionInboxCleaner inner) : IMentionInboxCleaner
    {
        public List<IReadOnlyCollection<string>> Calls { get; } = new();

        public async Task RemoveForMessages(IReadOnlyCollection<string> messageIds)
        {
            Calls.Add(messageIds);
            await inner.RemoveForMessages(messageIds);
        }
    }
}

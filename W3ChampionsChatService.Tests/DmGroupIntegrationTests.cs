using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Relationships;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C5 Task 10 — the END-TO-END DM/GROUP ACCEPTANCE suite: the acceptance matrix (brief §Acceptance
/// 1-5 + the coordinator state-machine matrix in C5-plan.md §4) rendered as an executable, multi-hub
/// spec. This drives MULTIPLE real <see cref="ChatHub"/> instances (initiator / recipient / moderator /
/// group members) through the SHIPPED C5 pipeline (T1-T9) while SHARING one instance of every singleton
/// — the registries, engine, coalescer, accumulator, repos on the shared
/// <see cref="IntegrationTestBase.MongoClient"/>, the <see cref="DmInitiationTracker"/>, and a single
/// <see cref="RelationshipProvider"/> over a controllable <see cref="FakeRelationshipSource"/> (per-tag
/// friends/blocked, NEVER HTTP). This is the <see cref="ModerationIntegrationTests"/> multi-instance +
/// shared-singleton idiom (C4 Task 8), re-pointed at the DM/group surface, with the C5 relationship
/// collaborators wired in.
/// <para>
/// Two capture surfaces, both keyed by connectionId: the shared <see cref="HubPushCaptureHarness"/>
/// records the <c>IHubContext</c> fan-out pushes (ChannelAdded / MessageReceived / ChannelActivity) the
/// engine/coalescer emit; each hub's own capturing <see cref="IHubCallerClients"/> records the direct
/// <c>Clients.Caller</c>/<c>Clients.Client</c> pushes (SessionState, the targeted RequestReceived) and
/// any <c>Clients.Group</c> broadcast (recorded under the sentinel target "group", so a private-lane
/// event mistakenly broadcast is caught) into the shared <see cref="_hubSends"/>. TIME is deterministic
/// via a single <see cref="FakeTimeProvider"/> SHARED with the relationship provider (so snapshot
/// freshness and the hub clock agree); the one real-clock seam is the one-time ticket
/// (<see cref="ChatHub.OnConnectedAsync"/> consumes it with <c>DateTime.UtcNow</c>, so tickets are minted
/// the same way — exactly as <see cref="ModerationIntegrationTests"/> handles it). NUnit constraint style.
/// </para>
/// <para>
/// The surface pins for the eight new hub methods + RequestReceived already live in
/// <see cref="OldProtocolRemovedTests.HubSurface_ExactlyMatchesPinnedSet"/> and
/// <see cref="ProtocolContractTests.ChatEvents_DefinesPinnedServerEventNames"/> (T3/T6/T7/T8 widened
/// them), so this suite adds NO surface-pin edits — it exercises the shipped surface end-to-end.
/// </para>
/// </summary>
public class DmGroupIntegrationTests : IntegrationTestBase
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
    private UserSettingsRepository _userSettings;
    private DmInitiationTracker _dmInitiationTracker;
    private FakeRelationshipSource _relationshipSource;
    private RelationshipProvider _relationshipProvider;
    private SessionStateAssembler _assembler;
    private Mock<IChatAuthenticationService> _authService;
    private ModerationHistoryController _moderationController;

    // Per-tag friends/blocked, read by the shared fake source's snapshot factory (OrdinalIgnoreCase).
    private readonly Dictionary<string, HashSet<string>> _friends = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _blocked = new(StringComparer.OrdinalIgnoreCase);

    // Every Clients.Caller/Client/Group push, in order, across ALL connections (fan-out pushes go to
    // _harness instead). A Clients.Group broadcast is recorded under the sentinel target "group".
    private readonly List<(string ConnectionId, string Method, object Payload)> _hubSends = new();

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [SetUp]
    public void SetupBeforeEach()
    {
        _friends.Clear();
        _blocked.Clear();
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
        _reconcileHarness = new MuteReconciliationTestHarness(_connectionMapping, _muteRepository);
        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _messageRepository = new MessageRepository(MongoClient);
        _userSettings = new UserSettingsRepository(MongoClient);
        _dmInitiationTracker = new DmInitiationTracker();

        // The three fan-out sinks ALL push through the ONE shared harness and read the SHARED registries
        // the hubs mutate, so every push lands in a single ordered capture.
        _activityCoalescer = new ActivityCoalescer(_harness.HubContext, _onlineMemberRegistry);
        _fanOutEngine = new FanOutEngine(
            _harness.HubContext, _focusRegistry, _onlineMemberRegistry, _activityCoalescer, _sessionRegistry);
        _viewersAccumulator = new ViewersAccumulator(_harness.HubContext, _focusRegistry);

        // One shared provider over a controllable fake source (reads the per-tag dicts, OrdinalIgnoreCase),
        // SHARING the FakeTimeProvider so snapshot freshness and the hub clock agree.
        _relationshipSource = new FakeRelationshipSource((tag, now) => new RelationshipSnapshot(
            tag,
            _friends.TryGetValue(tag, out var f) ? f : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            _blocked.TryGetValue(tag, out var b) ? b : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            now));
        _relationshipProvider = new RelationshipProvider(_relationshipSource, _time);

        _authService = new Mock<IChatAuthenticationService>();
        _authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync((W3CUserAuthentication id) =>
                new ChatUserResolution(new ChatUser(id.BattleTag, id.IsAdmin, id.Name, new ProfilePicture(), null, null), true));

        _assembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            _messageRepository,
            _muteRepository,
            _onlineMemberRegistry,
            _connectionMapping);

        _moderationController = new ModerationHistoryController(_channelRepository, _messageRepository);
    }

    // ============================================================================================
    // Fixture plumbing (mirrors ModerationIntegrationTests, + the C5 relationship deps)
    // ============================================================================================

    private static W3CUserAuthentication Identity(string battleTag) =>
        new() { BattleTag = battleTag, Name = battleTag.Split('#')[0] };

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
            new NoOpMentionInboxCleaner(),
            _relationshipProvider,
            _userSettings,
            _dmInitiationTracker,
            _authService.Object);

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

    // Full connect ceremony (real wall-clock ticket, matching OnConnectedAsync's DateTime.UtcNow) for a
    // regular user — registers the session, upserts the directory row, warms the relationship cache, and
    // seeds this connection's registries from any existing memberships.
    private Task<ChatHub> Connect(string connectionId, string battleTag) =>
        ConnectWith(connectionId, Identity(battleTag));

    private Task<ChatHub> ConnectModerator(string connectionId, string battleTag) =>
        ConnectWith(connectionId, ModeratorIdentity(battleTag));

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

    // A group broadcast is recorded under the sentinel target "group" — so a private-lane event that
    // should be a targeted single-connection push but is mistakenly broadcast is caught (group 9b).
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

    // ---- relationship + Mongo seed helpers ---------------------------------------------------------

    private void SetFriends(string battleTag, params string[] friends) =>
        _friends[battleTag] = new HashSet<string>(friends, StringComparer.OrdinalIgnoreCase);

    private void SetBlocked(string battleTag, params string[] blocked) =>
        _blocked[battleTag] = new HashSet<string>(blocked, StringComparer.OrdinalIgnoreCase);

    private Task SeedDirectory(string battleTag) =>
        _userDirectory.Upsert(new UserDirectoryEntry { BattleTag = battleTag, LastSeenAt = Now });

    private Task SeedPrivacy(string battleTag, DmPrivacy privacy) =>
        _userSettings.Upsert(new UserSettings { BattleTag = battleTag, DmPrivacy = privacy });

    private Task SeedMembership(string channelId, string battleTag, MembershipRole role = MembershipRole.Member) =>
        _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = channelId,
            BattleTag = battleTag,
            Role = role,
            NotificationLevel = NotificationLevel.All,
            JoinedAt = Now,
        });

    // Seeds a durable message via the SAME seq-allocation path the real send pipeline uses.
    private async Task<ChannelMessage> SeedMessage(string channelId, string senderBattleTag, string content)
    {
        var seq = await _channelRepository.AllocateSeq(channelId, Now);
        var message = new ChannelMessage
        {
            ChannelId = channelId,
            Seq = seq,
            Sender = new MessageSender { BattleTag = senderBattleTag, Name = senderBattleTag.Split('#')[0] },
            Content = content,
            SentAt = Now,
        };
        await _messageRepository.Insert(message);
        return message;
    }

    // ---- capture readers ---------------------------------------------------------------------------

    private int HubCount(string connectionId, string method)
    {
        lock (_hubSends)
        {
            return _hubSends.Count(s => s.ConnectionId == connectionId && s.Method == method);
        }
    }

    private object HubPayload(string connectionId, string method)
    {
        lock (_hubSends)
        {
            return _hubSends.Where(s => s.ConnectionId == connectionId && s.Method == method).Select(s => s.Payload).LastOrDefault();
        }
    }

    private List<(string ConnectionId, string Method, object Payload)> HubSendsSnapshot()
    {
        lock (_hubSends)
        {
            return _hubSends.ToList();
        }
    }

    private int FanoutCount(string connectionId, string method) => _harness.SignalCount(connectionId, method);

    private IReadOnlyList<MessageDto> MessageReceivedFor(string connectionId) =>
        _harness.SignalsFor(connectionId)
            .Where(s => s.Method == ChatEvents.MessageReceived)
            .Select(s => (MessageDto)s.Payload)
            .ToList();

    private IReadOnlyList<ChannelAddedDto> AllChannelAddedPushes() =>
        _harness.AllSignals
            .Where(s => s.Method == ChatEvents.ChannelAdded)
            .Select(s => (ChannelAddedDto)s.Payload)
            .ToList();

    private async Task<SessionStateDto> AssembleTray(string battleTag)
    {
        var identity = Identity(battleTag);
        var chatUser = new ChatUser(identity.BattleTag, identity.IsAdmin, identity.Name, new ProfilePicture(), null, null);
        return (await _assembler.AssembleAndSeed(identity, "tray-" + Guid.NewGuid().ToString("N"), Now, chatUser)).Item1;
    }

    // ============================================================================================
    // Group 1 — FULL MATRIX, stranger × Everyone lifecycle (matrix rows: create-pending → tray →
    // accept → activity resumes → reply-accept; +1y shell). Acceptance 1.
    // ============================================================================================

    [Test]
    public async Task FullMatrix_StrangerEveryone_Lifecycle()
    {
        const string initiator = "peter#123";
        const string recipient = "wolf#456";

        // Both parties online. The recipient's connect creates their user_directory row (D14 satisfied) and
        // leaves dmPrivacy at the spec default (Everyone), so the stranger open is admissible.
        await Connect("conn-recip", recipient);
        var initiatorHub = await Connect("conn-init", initiator);

        // --- OpenDm: a stranger-on-Everyone creates a PENDING shell (+30d), initiator recorded ---
        var open = await initiatorHub.OpenDm(recipient);
        Assert.That(open.Code, Is.EqualTo(ChatResultCode.Ok));
        var channelId = open.Channel.Id;
        Assert.That(open.Channel.RequestState, Is.EqualTo(DmRequestState.Pending));
        Assert.That(open.Channel.RequestInitiatedBy, Is.EqualTo(initiator));
        Assert.That((open.Channel.ExpiresAt.Value - Now.AddDays(30)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "a fresh pending shell carries the +30d expiry");
        Assert.That(_dmInitiationTracker.CountActive(initiator, Now), Is.EqualTo(1));

        // --- three pending messages: persist under the cap, materialize + notify ONCE, no activity (D4) ---
        for (var i = 1; i <= 3; i++)
        {
            var send = await initiatorHub.SendMessage(channelId, $"knock {i}");
            Assert.That(send.Code, Is.EqualTo(ChatResultCode.Ok));
            Assert.That(send.Seq, Is.EqualTo((long)i));
        }
        Assert.That((await _channelRepository.Load(channelId)).LastSeq, Is.EqualTo(3L), "all three pending messages persisted");
        Assert.That((await _messageRepository.LoadForModerator(channelId)).Count, Is.EqualTo(3));
        Assert.That(HubCount("conn-recip", ChatEvents.RequestReceived), Is.EqualTo(1), "RequestReceived fires exactly once, not per message");
        Assert.That(_harness.SignalCount("conn-recip", ChatEvents.ChannelAdded), Is.EqualTo(1), "ChannelAdded fires once (first materialization)");
        Assert.That(FanoutCount("conn-recip", ChatEvents.ChannelActivity), Is.EqualTo(0), "a pending request pings NO ChannelActivity to the recipient (D4)");

        // --- the recipient's PENDING TRAY shows the request ---
        var trayBefore = await AssembleTray(recipient);
        Assert.That(trayBefore.PendingDmRequests.Select(r => r.ChannelId), Does.Contain(channelId), "the pending request is in the recipient's tray");
        Assert.That(trayBefore.Channels.Select(c => c.Channel.Id), Does.Contain(channelId), "and the pending DM also appears in Channels (D4 dual-listing)");

        // --- AcceptRequest: permanent flip, tray empties, +1y shell, initiation slot freed ---
        var recipientHub = BuildHub("conn-recip", null);
        var accept = await recipientHub.AcceptRequest(channelId);
        Assert.That(accept.Code, Is.EqualTo(ChatResultCode.Ok));
        var accepted = await _channelRepository.Load(channelId);
        Assert.That(accepted.RequestState, Is.EqualTo(DmRequestState.Accepted), "accept flips the request permanently");
        Assert.That((accepted.ExpiresAt.Value - Now.AddDays(365)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)), "accept re-stamps the +1y shell");
        Assert.That(_dmInitiationTracker.CountActive(initiator, Now), Is.EqualTo(0), "accept frees the initiator's stranger-initiation slot");
        Assert.That((await AssembleTray(recipient)).PendingDmRequests, Is.Empty, "an accepted request leaves the tray");

        // --- activity RESUMES: the initiator's next message pings the (unfocused) recipient once ---
        var afterAccept = await initiatorHub.SendMessage(channelId, "you there?");
        Assert.That(afterAccept.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(FanoutCount("conn-recip", ChatEvents.ChannelActivity), Is.EqualTo(1), "after accept a new message resumes ChannelActivity (D4 suppression lifted)");

        // --- the recipient replies on the now-normal conversation; the shell rides +1y off the send clock ---
        var reply = await recipientHub.SendMessage(channelId, "yep, hi");
        Assert.That(reply.Code, Is.EqualTo(ChatResultCode.Ok));
        var final = await _channelRepository.Load(channelId);
        Assert.That((final.ExpiresAt.Value - (final.LastMessageAt.Value + TimeSpan.FromDays(365))).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "the accepted shell's ExpiresAt is lastMessageAt + 1y");
    }

    // ============================================================================================
    // Group 2 — DECLINE path: decline → open-later reads full history → +24h → new message resurfaces a
    // fresh request → a live block (snapshot mutation + Invalidate) → subsequent 1:1 sends fake-Ok,
    // never delivered/stored. Acceptance 1 + 3.
    // ============================================================================================

    [Test]
    public async Task FullMatrix_DeclinePath()
    {
        const string initiator = "peter#123";
        const string recipient = "wolf#456";

        await Connect("conn-recip", recipient);
        var initiatorHub = await Connect("conn-init", initiator);

        var open = await initiatorHub.OpenDm(recipient);
        var channelId = open.Channel.Id;
        await initiatorHub.SendMessage(channelId, "hi, new here"); // materialize + RequestReceived #1
        Assert.That(HubCount("conn-recip", ChatEvents.RequestReceived), Is.EqualTo(1));

        // --- DECLINE ---
        var recipientHub = BuildHub("conn-recip", null);
        Assert.That((await recipientHub.DeclineRequest(channelId)).Code, Is.EqualTo(ChatResultCode.Ok));

        // Open-later: the recipient reads FULL history and Focus works; Accept/Block are still offered (the
        // recipient can still Accept). The tray is suppressed for 24h, but the DM stays in Channels.
        var history = await recipientHub.GetMessages(channelId, null, null, 50);
        Assert.That(history.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(history.Messages.Count, Is.EqualTo(1), "the pre-decline message is still readable open-later");
        Assert.That((await recipientHub.FocusChannel(channelId)).Code, Is.EqualTo(ChatResultCode.Ok));
        var declinedTray = await AssembleTray(recipient);
        Assert.That(declinedTray.PendingDmRequests, Is.Empty, "a declined request is suppressed from the tray");
        Assert.That(declinedTray.Channels.Select(c => c.Channel.Id), Does.Contain(channelId), "but the declined DM still appears in Channels");

        // --- +24h: the next message resurfaces a FRESH request (DeclinedUntil cleared, re-fires) ---
        _time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        await initiatorHub.SendMessage(channelId, "after 24h");
        Assert.That((await _membershipRepository.Load(channelId, recipient)).DeclinedUntil, Is.Null, "the resurface path clears the decline window");
        Assert.That(HubCount("conn-recip", ChatEvents.RequestReceived), Is.EqualTo(2), "a fresh RequestReceived fires after the window elapses");
        Assert.That((await AssembleTray(recipient)).PendingDmRequests.Select(r => r.ChannelId), Does.Contain(channelId), "the tray re-populates after the window");

        // --- a LIVE block (wb block simulated by snapshot mutation + Invalidate) ---
        var storedBefore = (await _messageRepository.LoadForModerator(channelId)).Count;
        var lastSeqBefore = (await _channelRepository.Load(channelId)).LastSeq;
        SetBlocked(recipient, initiator);
        _relationshipProvider.Invalidate(recipient); // C7 change-ping seam: next read refetches the block

        var dropped = await initiatorHub.SendMessage(channelId, "you around?");
        Assert.That(dropped.Code, Is.EqualTo(ChatResultCode.Ok), "a blocked 1:1 send returns a fabricated Ok (D6) — never leaks the block");
        Assert.That(dropped.MessageId, Is.Not.Null.And.Not.Empty);
        Assert.That(dropped.Seq, Is.Not.Null);
        Assert.That((await _messageRepository.LoadForModerator(channelId)).Count, Is.EqualTo(storedBefore), "the blocked send stores NOTHING");
        Assert.That((await _channelRepository.Load(channelId)).LastSeq, Is.EqualTo(lastSeqBefore), "the blocked send allocates no seq");
    }

    // ============================================================================================
    // Group 3 — friend × {Everyone, Friends, Nobody} ⇒ IDENTICAL born-Accepted behavior (privacy is
    // ignored entirely for friends). Coordinator matrix: friend row. Acceptance 1.
    // ============================================================================================

    [Test]
    public async Task FriendPath_AllPrivacies_BornAccepted()
    {
        var caller = "peter#123";
        RegisterSession("conn-caller", caller);
        var hub = BuildHub("conn-caller", null);

        var cases = new[]
        {
            ("wolf#100", DmPrivacy.Everyone),
            ("fox#200", DmPrivacy.Friends),
            ("bear#300", DmPrivacy.Nobody),
        };

        // ALL three targets are friends of the caller — set up front, because the provider caches the
        // caller's ONE snapshot on the first OpenDm (advancing no clock keeps it fresh), so a per-iteration
        // SetFriends would be masked by the stale cache. The target's dmPrivacy is IRRELEVANT for a friend.
        SetFriends(caller, cases.Select(c => c.Item1).ToArray());

        foreach (var (target, privacy) in cases)
        {
            await SeedPrivacy(target, privacy);
            await SeedDirectory(target);

            var result = await hub.OpenDm(target);

            Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok), $"a friend's DM opens regardless of the target's dmPrivacy ({privacy})");
            Assert.That(result.Channel.Type, Is.EqualTo(ChannelType.Dm));
            Assert.That(result.Channel.RequestState, Is.EqualTo(DmRequestState.Accepted), $"friends' DMs are born Accepted regardless of dmPrivacy ({privacy})");
            Assert.That((result.Channel.ExpiresAt.Value - Now.AddDays(365)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)), "an accepted-at-birth shell gets +1y");
            Assert.That(result.Membership.Role, Is.EqualTo(MembershipRole.Member));
        }

        Assert.That(_dmInitiationTracker.CountActive(caller, Now), Is.EqualTo(0), "the friend path never records a stranger-initiation");
        Assert.That((await _channelRepository.LoadAllOfType(ChannelType.Dm)).Count, Is.EqualTo(3), "three independent accepted DMs, one per target");
    }

    // ============================================================================================
    // Group 4 — stranger × {Friends, Nobody} ⇒ PermissionDenied, NO shell persisted. Coordinator
    // matrix: stranger×{Friends,Nobody} row. Acceptance 1.
    // ============================================================================================

    [Test]
    public async Task StrangerRejections_FriendsOnly_And_Nobody()
    {
        var caller = "peter#123";
        RegisterSession("conn-caller", caller);
        var hub = BuildHub("conn-caller", null);

        foreach (var (target, privacy) in new[] { ("fox#200", DmPrivacy.Friends), ("bear#300", DmPrivacy.Nobody) })
        {
            await SeedDirectory(target);          // present (else the D14 NotFound short-circuits before privacy)
            await SeedPrivacy(target, privacy);

            var result = await hub.OpenDm(target);

            Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied), $"a stranger open against a {privacy} target is PermissionDenied");
            Assert.That(await _channelRepository.LoadByPairKey(caller, target), Is.Null, "no shell is persisted on a privacy reject");
        }

        Assert.That(await _channelRepository.LoadAllOfType(ChannelType.Dm), Is.Empty, "no DM channel doc exists at all");
        Assert.That(await _membershipRepository.LoadForUser(caller), Is.Empty, "no membership persisted either");
        Assert.That(_dmInitiationTracker.CountActive(caller, Now), Is.EqualTo(0), "a privacy reject records no initiation");
    }

    // ============================================================================================
    // Group 5a — pair-key concurrency: two hubs, both sides OpenDm at once ⇒ exactly ONE conversation
    // + BOTH memberships (the unique index + duplicate-key retry). Acceptance 5.
    // ============================================================================================

    [Test]
    public async Task PairKey_ConcurrentOpenDmBothSides_OneConversation()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient); // the pair-key unique index backs the single-doc invariant
        const string a = "alice#1";
        const string b = "bob#2";
        SetFriends(a, b);
        SetFriends(b, a);
        RegisterSession("conn-a", a);
        RegisterSession("conn-b", b);
        var hubA = BuildHub("conn-a", null);
        var hubB = BuildHub("conn-b", null);

        var results = await Task.WhenAll(hubA.OpenDm(b), hubB.OpenDm(a));

        Assert.That(results.Select(r => r.Code), Is.All.EqualTo(ChatResultCode.Ok));
        Assert.That(results.Select(r => r.Channel.Id).Distinct().Count(), Is.EqualTo(1), "both sides resolve to ONE channel id");
        Assert.That((await _channelRepository.LoadAllOfType(ChannelType.Dm)).Count, Is.EqualTo(1), "exactly one Dm channel doc exists");
        var members = await _membershipRepository.LoadForChannel(results[0].Channel.Id);
        Assert.That(members.Select(m => m.BattleTag), Is.EquivalentTo(new[] { a, b }), "each side creates its OWN membership on the one channel");
    }

    // ============================================================================================
    // Group 5b — shell resurrection: delete the shell (simulating the 30d TTL reap), re-open ⇒ a NEW
    // channel id with EMPTY history; the old messages are orphaned-unreachable. Acceptance 5.
    // ============================================================================================

    [Test]
    public async Task ExpiredShell_Resurrects_SameKey_EmptyHistory()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        const string initiator = "peter#123";
        const string recipient = "wolf#456";
        await SeedDirectory(recipient);
        await SeedPrivacy(recipient, DmPrivacy.Everyone);

        // First "session": open a pending shell and store a message into it.
        RegisterSession("conn-init-1", initiator);
        var firstOpen = await BuildHub("conn-init-1", null).OpenDm(recipient);
        var oldChannelId = firstOpen.Channel.Id;
        await SeedMessage(oldChannelId, initiator, "old message");
        Assert.That((await _messageRepository.LoadForModerator(oldChannelId)).Count, Is.EqualTo(1), "the old shell has history");

        // Simulate the TTL reap of the shell doc (the message rows outlive it on their own 90d TTL).
        await _channelRepository.Delete(oldChannelId);
        Assert.That(await _channelRepository.LoadByPairKey(initiator, recipient), Is.Null, "the shell is gone");

        // Re-open on a FRESH connection (the old connection/registry is long gone) — a genuinely NEW shell.
        RegisterSession("conn-init-2", initiator);
        var reopenHub = BuildHub("conn-init-2", null);
        var reopen = await reopenHub.OpenDm(recipient);
        Assert.That(reopen.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(reopen.Channel.Id, Is.Not.EqualTo(oldChannelId), "the resurrected conversation is a NEW channel id");
        Assert.That(reopen.Channel.RequestState, Is.EqualTo(DmRequestState.Pending));

        // The new conversation starts EMPTY; the old messages are orphaned-unreachable (their channel is gone).
        var newHistory = await reopenHub.GetMessages(reopen.Channel.Id, null, null, 50);
        Assert.That(newHistory.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(newHistory.Messages, Is.Empty, "the resurrected conversation has empty history");
        var oldReach = await reopenHub.GetMessages(oldChannelId, null, null, 50);
        Assert.That(oldReach.Code, Is.EqualTo(ChatResultCode.NotFound), "the old messages are orphaned — the deleted channel is unreachable");
        Assert.That((await _messageRepository.LoadForModerator(oldChannelId)).Count, Is.EqualTo(1), "the old rows physically survive (their own TTL) but are unreachable");
    }

    // ============================================================================================
    // Group 6 — initiation cap under concurrency: 15 parallel OpenDm to distinct strangers-on-Everyone ⇒
    // ≤10 shells, the rest Throttled (the tracker's atomic check-and-record). Acceptance 2.
    // ============================================================================================

    [Test]
    public async Task InitiationCap_ConcurrentOpenDms_NeverExceedsTen()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        const string caller = "peter#123";
        const int targetCount = 15;

        var targets = Enumerable.Range(0, targetCount).Select(i => $"stranger{i}#0").ToArray();
        foreach (var target in targets)
        {
            await SeedDirectory(target);
            await SeedPrivacy(target, DmPrivacy.Everyone);
        }
        RegisterSession("conn-caller", caller);
        var hub = BuildHub("conn-caller", null);

        // Fire all 15 concurrently on the ONE caller connection — the FakeRelationshipSource yields, so
        // they genuinely interleave against the shared tracker + repos.
        var results = await Task.WhenAll(targets.Select(t => hub.OpenDm(t)));

        var admitted = results.Count(r => r.Code == ChatResultCode.Ok);
        var throttled = results.Count(r => r.Code == ChatResultCode.Throttled);
        Assert.That(admitted, Is.EqualTo(ChatLimits.StrangerDmInitiationCap), "exactly the cap's worth of initiations are admitted");
        Assert.That(throttled, Is.EqualTo(targetCount - ChatLimits.StrangerDmInitiationCap), "the remainder are throttled");
        Assert.That(results.Where(r => r.Code == ChatResultCode.Throttled), Has.All.Matches<OpenDmResult>(r => r.RetryAfterSeconds > 0),
            "every throttled initiation carries a positive retry-after");
        Assert.That((await _channelRepository.LoadAllOfType(ChannelType.Dm)).Count, Is.EqualTo(ChatLimits.StrangerDmInitiationCap),
            "the tracker's atomicity bounds the shells created to the cap — never more");
        Assert.That(_dmInitiationTracker.CountActive(caller, Now), Is.EqualTo(ChatLimits.StrangerDmInitiationCap));
    }

    // ============================================================================================
    // Group 7 — THE MARQUEE contrast (acceptance 3): the SAME blocker pair — a GROUP message from the
    // blocked sender reaches the blocker in FULL; a 1:1 message from the same sender is silently dropped.
    // ============================================================================================

    [Test]
    public async Task Block_GroupsVsDm_Consistency()
    {
        const string sender = "peter#123";
        const string blocker = "wolf#456";  // blocks the sender
        const string third = "fox#789";     // fills the group's 3-member floor

        // The sender is friends with both group members (needed for CreateGroup + the friend-born DM); the
        // blocker independently has a genuine 1:1 block against the sender. Set BEFORE connect so the
        // snapshots warm correctly.
        SetFriends(sender, blocker, third);
        SetBlocked(blocker, sender);

        var blockerHub = await Connect("conn-blocker", blocker);
        await Connect("conn-third", third);
        var senderHub = await Connect("conn-sender", sender);

        // --- GROUP leg: create a group with the blocker, both focus, the sender posts ---
        var group = await senderHub.CreateGroup("Squad", new[] { blocker, third });
        Assert.That(group.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That((await blockerHub.FocusChannel(group.Channel.Id)).Code, Is.EqualTo(ChatResultCode.Ok));

        var groupSend = await senderHub.SendMessage(group.Channel.Id, "team update");
        Assert.That(groupSend.Code, Is.EqualTo(ChatResultCode.Ok));
        var groupDelivered = MessageReceivedFor("conn-blocker").Where(m => m.ChannelId == group.Channel.Id).ToList();
        Assert.That(groupDelivered, Has.Count.EqualTo(1), "the member who blocked the sender STILL receives the GROUP message in full");
        Assert.That(groupDelivered[0].Content, Is.EqualTo("team update"), "delivered with the full payload — group delivery never consults blocks");

        // --- 1:1 DM leg: the SAME pair. The sender opens (friend ⇒ born accepted) and posts; silently dropped. ---
        var dm = await senderHub.OpenDm(blocker);
        Assert.That(dm.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(dm.Channel.RequestState, Is.EqualTo(DmRequestState.Accepted), "friend-born DM (so the drop is proven at DELIVERY, not creation)");

        var dmSend = await senderHub.SendMessage(dm.Channel.Id, "hey, 1:1");
        Assert.That(dmSend.Code, Is.EqualTo(ChatResultCode.Ok), "a blocked 1:1 send returns a fabricated Ok (D6)");
        Assert.That(dmSend.MessageId, Is.Not.Null.And.Not.Empty);
        Assert.That((await _messageRepository.LoadForModerator(dm.Channel.Id)), Is.Empty, "the blocked 1:1 send stores NOTHING");
        Assert.That((await _channelRepository.Load(dm.Channel.Id)).LastSeq, Is.EqualTo(0L), "no seq allocated");
        Assert.That(MessageReceivedFor("conn-blocker").Where(m => m.ChannelId == dm.Channel.Id), Is.Empty, "the blocker receives NOTHING for the 1:1");
    }

    // ============================================================================================
    // Group 8 — MODERATION INTERPLAY: re-assert the C4 walls from the C5 side, with REAL Dm/GroupDm docs.
    // ============================================================================================

    [Test]
    public async Task ModeratorCannotDeleteDmOrGroupMessage_PermissionDenied()
    {
        var modHub = await ConnectModerator("conn-mod", "mod#1");

        // A real Dm doc + a real GroupDm doc, each with a stored message.
        var dm = await _channelRepository.FindOrCreateDm("alice#1", "bob#2", "alice#1", DmRequestState.Accepted, Now);
        var dmMessage = await SeedMessage(dm.Id, "alice#1", "private dm content");
        var group = new ChatChannel { Type = ChannelType.GroupDm, Name = "squad", LastMessageAt = Now, ExpiresAt = Now.AddDays(365) };
        await _channelRepository.Insert(group);
        var groupMessage = await SeedMessage(group.Id, "alice#1", "group content");

        // The moderation scope wall (ChannelModeration.IsModeratable) rejects BOTH before any delete.
        Assert.That((await modHub.DeleteMessage(dmMessage.Id)).Code, Is.EqualTo(ChatResultCode.PermissionDenied), "a moderator cannot delete a DM message");
        Assert.That((await modHub.DeleteMessage(groupMessage.Id)).Code, Is.EqualTo(ChatResultCode.PermissionDenied), "a moderator cannot delete a group message");

        Assert.That((await _messageRepository.Load(dmMessage.Id)).Deleted, Is.Null, "the DM message is untouched");
        Assert.That((await _messageRepository.Load(groupMessage.Id)).Deleted, Is.Null, "the group message is untouched");

        // The cross-channel purge is walled the SAME way — a purge of alice never touches her DM/group rows.
        var purge = await modHub.PurgeMessagesFromUser("alice#1");
        Assert.That(purge.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(purge.MessagesDeleted, Is.EqualTo(0), "purge finds NO moderatable rows — DM/group are out of scope");
        Assert.That((await _messageRepository.Load(dmMessage.Id)).Deleted, Is.Null, "the DM row survives the purge");
        Assert.That((await _messageRepository.Load(groupMessage.Id)).Deleted, Is.Null, "the group row survives the purge");
    }

    [Test]
    public async Task ModeratorGetMessages_InDmTheyAreMemberOf_IdenticalToUserProjection()
    {
        // A moderator who is a LEGITIMATE member of a DM reads it through GetMessages. Their moderator
        // branch (ForModerator) is byte-identical to the ordinary user projection (ForUserDelivery) for a
        // DM — the emergent-safety pin at ChatHub.Messaging.cs (no shadow/deleted row can exist in a DM, so
        // no private flag can leak through the moderator projection). Acceptance 1 / spec §10 wall.
        const string moderator = "mod#1";
        const string counterpart = "wolf#456";
        SetFriends(moderator, counterpart); // friend ⇒ born-accepted DM, moderator is a member

        var modHub = await ConnectModerator("conn-mod", moderator);
        var dm = await modHub.OpenDm(counterpart);
        Assert.That(dm.Code, Is.EqualTo(ChatResultCode.Ok));

        var send = await modHub.SendMessage(dm.Channel.Id, "a normal dm message");
        Assert.That(send.Code, Is.EqualTo(ChatResultCode.Ok));
        var raw = await _messageRepository.Load(send.MessageId);

        var read = await modHub.GetMessages(dm.Channel.Id, null, null, 50);
        Assert.That(read.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(read.Messages.Count, Is.EqualTo(1));

        // The moderator's projection must equal what a plain user would see — no private flag leaks.
        var moderatorView = JsonSerializer.Serialize(read.Messages[0]);
        var userView = JsonSerializer.Serialize(MessageDto.ForUserDelivery(dm.Channel.Id, raw));
        Assert.That(moderatorView, Is.EqualTo(userView), "a moderator's in-DM read is byte-identical to the user projection");
        Assert.That(read.Messages[0].Deleted, Is.False);
        Assert.That(read.Messages[0].Shadow, Is.False);
    }

    [Test]
    public async Task ModerationRestEndpoints_ExcludeDmAndGroup_403()
    {
        // Real Dm + GroupDm docs. The REST moderation-history surface must never expose them.
        var dm = await _channelRepository.FindOrCreateDm("alice#1", "bob#2", "alice#1", DmRequestState.Accepted, Now);
        var group = new ChatChannel { Type = ChannelType.GroupDm, Name = "squad", LastMessageAt = Now, ExpiresAt = Now.AddDays(365) };
        await _channelRepository.Insert(group);
        // A moderatable control channel so the list assertion below is meaningful.
        var pub = new ChatChannel { Type = ChannelType.Public, Name = "W3C Lounge", NormalizedName = ChannelNames.Normalize("W3C Lounge") };
        await _channelRepository.Insert(pub);

        // The per-channel history read 403s for a resolvable-but-ineligible channel (DM/GroupDm).
        var dmResult = await _moderationController.GetChannelMessages(dm.Id, null, 100);
        var groupResult = await _moderationController.GetChannelMessages(group.Id, null, 100);
        Assert.That(dmResult, Is.InstanceOf<StatusCodeResult>());
        Assert.That(((StatusCodeResult)dmResult).StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden), "the REST moderation-history read 403s for a DM");
        Assert.That(groupResult, Is.InstanceOf<StatusCodeResult>());
        Assert.That(((StatusCodeResult)groupResult).StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden), "the REST moderation-history read 403s for a group");

        // The channel-LIST surface excludes DM/GroupDm entirely (mirrors the scope wall as a Mongo filter).
        var listResult = await _moderationController.GetModeratableChannels(100) as OkObjectResult;
        Assert.That(listResult, Is.Not.Null);
        var listed = ((IEnumerable<ModerationChannelDto>)listResult.Value).Select(c => c.Id).ToList();
        Assert.That(listed, Does.Contain(pub.Id), "the moderatable-channel list includes the public channel");
        Assert.That(listed, Does.Not.Contain(dm.Id), "the moderatable-channel list never surfaces a DM");
        Assert.That(listed, Does.Not.Contain(group.Id), "the moderatable-channel list never surfaces a group");
    }

    [Test]
    public async Task MuteGate_StillPublicOnly_DmAndGroupSendsUnaffectedByFullMute()
    {
        const string user = "peter#123";
        const string counterpart = "wolf#456";
        const string g1 = "fox#789";
        const string g2 = "bear#101";
        SetFriends(user, counterpart, g1, g2); // friend-born DM + friends-only group members

        // A public channel the user is a member of (membership seeded BEFORE connect so the connect
        // ceremony seeds the registry for it).
        var pub = new ChatChannel { Type = ChannelType.Public, Name = "W3C Lounge", NormalizedName = ChannelNames.Normalize("W3C Lounge") };
        await _channelRepository.Insert(pub);
        await SeedMembership(pub.Id, user);

        var userHub = await Connect("conn-user", user);
        var dm = await userHub.OpenDm(counterpart);
        Assert.That(dm.Code, Is.EqualTo(ChatResultCode.Ok));
        var group = await userHub.CreateGroup("Squad", new[] { g1, g2 });
        Assert.That(group.Code, Is.EqualTo(ChatResultCode.Ok));

        // A FULL mute on the user's live connection (cache-only enforcement, the SendMessage mute gate seam).
        _connectionMapping.SetMute("conn-user", MuteStatus.Full, Now.AddDays(1));

        // Public: gated → Muted (the control). DM + group: the mute gate is PUBLIC-ONLY, so both send Ok.
        Assert.That((await userHub.SendMessage(pub.Id, "public")).Code, Is.EqualTo(ChatResultCode.Muted), "a full mute gates a PUBLIC send");
        var dmSend = await userHub.SendMessage(dm.Channel.Id, "dm while muted");
        Assert.That(dmSend.Code, Is.EqualTo(ChatResultCode.Ok), "a full mute does NOT gate a DM send");
        Assert.That(await _messageRepository.Load(dmSend.MessageId), Is.Not.Null, "the DM message really persisted");
        var groupSend = await userHub.SendMessage(group.Channel.Id, "group while muted");
        Assert.That(groupSend.Code, Is.EqualTo(ChatResultCode.Ok), "a full mute does NOT gate a group send");
        Assert.That(await _messageRepository.Load(groupSend.MessageId), Is.Not.Null, "the group message really persisted");
    }

    // ============================================================================================
    // Group 9 — GUARDRAIL greps-as-tests (leak-boundary argument capture).
    // ============================================================================================

    [Test]
    public async Task AllC5ChannelAddedPushes_AreFocusFalse()
    {
        // Drives every C5 path that emits ChannelAdded — OpenDm first-message materialization, CreateGroup,
        // and AddGroupMember — and asserts EVERY push carries Focus == false. A focused auto-open would be a
        // spec violation (the server never auto-opens a DM/group). No-auto-open pinned.
        const string initiator = "peter#123";
        const string recipient = "wolf#456";
        const string groupMate = "fox#789";
        const string groupMate2 = "elk#202";
        const string lateAdd = "bear#101";
        SetFriends(initiator, groupMate, groupMate2, lateAdd);

        await Connect("conn-recip", recipient);
        await Connect("conn-mate", groupMate);
        await Connect("conn-mate2", groupMate2);
        await Connect("conn-late", lateAdd);
        var initiatorHub = await Connect("conn-init", initiator);

        // (a) OpenDm → first pending message materializes the recipient + ChannelAdded.
        var dm = await initiatorHub.OpenDm(recipient);
        await initiatorHub.SendMessage(dm.Channel.Id, "hi there");
        // (b) CreateGroup (creator + 2 members = the 3-member floor) → ChannelAdded to every member + creator.
        var group = await initiatorHub.CreateGroup("Squad", new[] { groupMate, groupMate2 });
        Assert.That(group.Code, Is.EqualTo(ChatResultCode.Ok));
        // (c) AddGroupMember → ChannelAdded to the freshly added member.
        Assert.That((await initiatorHub.AddGroupMember(group.Channel.Id, lateAdd)).Code, Is.EqualTo(ChatResultCode.Ok));

        var pushes = AllChannelAddedPushes();
        Assert.That(pushes, Is.Not.Empty, "the exercised paths DID emit ChannelAdded (the test is not vacuous)");
        Assert.That(pushes, Has.All.Matches<ChannelAddedDto>(p => p.Focus == false),
            "EVERY C5 ChannelAdded push is focus:false — the server never auto-opens a DM/group");
    }

    [Test]
    public async Task RequestReceived_OnlyTargetsRecipientSingleConnection_NeverBroadcast()
    {
        const string initiator = "peter#123";
        const string recipient = "wolf#456";
        await Connect("conn-recip", recipient);
        var initiatorHub = await Connect("conn-init", initiator);

        var dm = await initiatorHub.OpenDm(recipient);
        await initiatorHub.SendMessage(dm.Channel.Id, "first"); // fires exactly one RequestReceived

        var requestReceived = HubSendsSnapshot().Where(s => s.Method == ChatEvents.RequestReceived).ToList();
        Assert.That(requestReceived, Has.Count.EqualTo(1), "a fresh pending request fires RequestReceived exactly once");
        Assert.That(requestReceived[0].ConnectionId, Is.EqualTo("conn-recip"), "RequestReceived is targeted at the recipient's single connection");
        Assert.That(requestReceived.Any(s => s.ConnectionId == "group"), Is.False, "RequestReceived is NEVER a group/channel broadcast");
        Assert.That(HubCount("conn-init", ChatEvents.RequestReceived), Is.EqualTo(0), "the initiator never receives their own RequestReceived");
    }

    // ---- lightweight session registration (for tests that do not need the full connect ceremony) ---

    private void RegisterSession(string connectionId, string battleTag) =>
        _sessionRegistry.Register(
            connectionId,
            new W3CUserAuthentication { BattleTag = battleTag, Name = battleTag.Split('#')[0] },
            null);
}

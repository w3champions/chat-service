using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
/// C3 Task 11: the durable <c>SendMessage(channelId, content)</c> pipeline — validation → limits →
/// mute-gate → persist(seq+expiry) → fan-out hook → typed ack. Direct-hub-instantiation idiom
/// (mirrors <see cref="ChatHubMembershipTests"/>); a <see cref="FakeTimeProvider"/> drives the hub's
/// clock so the rate-limiter, sequence, and expiry assertions are deterministic without real delays.
/// <para>
/// Sender-flair source: the pipeline snapshots the flair-bearing <see cref="ChatUser"/> the connect
/// path cached per connection via <c>ConnectionMapping.RegisterUser</c> (Task 7's
/// <c>SessionStateAssembler.SeedLegacyMuteCache</c>). These tests seed it the SAME way
/// (<see cref="SeedMember"/> calls <c>ConnectionMapping.RegisterUser</c>) and assert the send path
/// NEVER calls <c>IChatAuthenticationService.GetUserFromIdentity</c> (no per-message wb round-trip).
/// </para>
/// </summary>
public class ChatHubSendMessageTests : IntegrationTestBase
{
    private const string BattleTag = "peter#123";

    private static readonly DateTimeOffset FixedNow = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private ConnectionMapping _connectionMapping;
    private UserDirectoryRepository _userDirectory;
    private MuteRepository _muteRepository;
    private MuteReconciliationTestHarness _reconcileHarness;
    private MuteController _muteController;
    private TicketStore _ticketStore;
    private Mock<IChatAuthenticationService> _authService;

    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private MessageRepository _messageRepository;
    private SessionRegistry _sessionRegistry;
    private FocusRegistry _focusRegistry;
    private OnlineMemberRegistry _onlineMemberRegistry;
    private MessageRateLimiter _messageRateLimiter;
    private ReadRateLimiter _readRateLimiter;
    private ChannelCreationRateLimiter _channelCreationRateLimiter;
    private SessionStateAssembler _assembler;
    private FanOutEngine _fanOutEngine;
    // C6 Task 5: a REAL MentionFanOut wired to a capture harness so the end-to-end mention tests below
    // can assert the durable inbox entries (Mongo) AND the targeted MentionNotified pushes (harness).
    private MentionInboxRepository _mentionInboxRepository;
    private MentionFanOut _mentionFanOut;
    private HubPushCaptureHarness _mentionPushHarness;
    private FakeTimeProvider _time;

    // Every (method, payload) the hub pushed to Clients.Caller, in order (for the ThrottleNotice assert).
    private readonly List<(string Method, object Payload)> _callerSends = new();

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [SetUp]
    public void SetupBeforeEach()
    {
        _callerSends.Clear();
        _time = new FakeTimeProvider(FixedNow);

        _connectionMapping = new ConnectionMapping();
        _userDirectory = new UserDirectoryRepository(MongoClient);
        _muteRepository = new MuteRepository(MongoClient);
        _reconcileHarness = new MuteReconciliationTestHarness(_connectionMapping, _muteRepository);
        _muteController = new MuteController(_muteRepository, _reconcileHarness.Service);
        _ticketStore = new TicketStore();

        _authService = new Mock<IChatAuthenticationService>();
        _authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync((W3CUserAuthentication id) =>
                new ChatUserResolution(new ChatUser(id.BattleTag, id.IsAdmin, id.Name, new ProfilePicture(), null, null), true));

        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _messageRepository = new MessageRepository(MongoClient);
        _sessionRegistry = new SessionRegistry();
        _focusRegistry = new FocusRegistry();
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _messageRateLimiter = new MessageRateLimiter();
        _readRateLimiter = new ReadRateLimiter();
        _channelCreationRateLimiter = new ChannelCreationRateLimiter();
        _fanOutEngine = FanOutEngineTestFactory.CreateIgnored();
        _mentionPushHarness = new HubPushCaptureHarness();
        _mentionInboxRepository = new MentionInboxRepository(MongoClient);
        _mentionFanOut = new MentionFanOut(
            _mentionPushHarness.HubContext,
            _sessionRegistry,
            _membershipRepository,
            _mentionInboxRepository,
            _userDirectory, RelationshipProviderTestFactory.CreateIgnored(), new NotificationPreferenceRepository(MongoClient));
        _assembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            _messageRepository,
            _muteRepository,
            _onlineMemberRegistry,
            _connectionMapping,
            _mentionInboxRepository);
    }

    private ChatHub BuildHub(string connectionId)
    {
        var viewerResolver = new ViewerResolver(_sessionRegistry, _connectionMapping);
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
            _readRateLimiter,
            _time,
            _channelRepository,
            _membershipRepository,
            _channelCreationRateLimiter,
            _messageRepository,
            _fanOutEngine,
            ViewersAccumulatorTestFactory.CreateIgnored(),
            new NoOpMentionInboxCleaner(),
            RelationshipProviderTestFactory.CreateIgnored(),
            new UserSettingsRepository(MongoClient),
            new DmInitiationTracker(),
            _authService.Object,
            _mentionFanOut,
            new PresenceInterestRegistry(),
            _mentionInboxRepository,
            new NotificationPreferenceRepository(MongoClient),
            viewerResolver);

        var clients = new Mock<IHubCallerClients>();
        var callerProxy = new Mock<ISingleClientProxy>();
        callerProxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) =>
            {
                lock (_callerSends)
                {
                    _callerSends.Add((method, args.Length > 0 ? args[0] : null));
                }
            })
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Caller).Returns(callerProxy.Object);
        hub.Clients = clients.Object;

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        hub.Context = context.Object;
        hub.Groups = new Mock<IGroupManager>().Object;

        return hub;
    }

    private void RegisterSession(string connectionId, string battleTag) =>
        _sessionRegistry.Register(
            connectionId,
            new W3CUserAuthentication { BattleTag = battleTag, Name = battleTag.Split('#')[0] },
            null);

    private async Task<ChatChannel> CreateChannel(string name, ChannelType type = ChannelType.Public)
    {
        var channel = new ChatChannel { Type = type, Name = name, NormalizedName = ChannelNames.Normalize(name) };
        await _channelRepository.Insert(channel);
        return channel;
    }

    // A System+Match channel — the shape mm's /internal/channels create produces. `ladder` is mm's
    // own declaration that this ref is a LADDER match (as opposed to a custom-game lobby); it is the
    // sole discriminator between the two, since both share SystemKind.Match.
    private async Task<ChatChannel> CreateMatchChannel(string systemRef, bool ladder)
    {
        var channel = new ChatChannel
        {
            Type = ChannelType.System,
            SystemKind = SystemChannelKind.Match,
            SystemRef = systemRef,
            Name = systemRef,
            Ladder = ladder,
        };
        await _channelRepository.Insert(channel);
        return channel;
    }

    private static ChatUser FlairUser(string battleTag) =>
        new(battleTag, false, "W3C", new ProfilePicture { Race = AvatarCategory.NE, PictureId = 42, IsClassic = true }, null, null);

    // Seeds a connection the SAME way the connect path does: a live session, the flair-bearing ChatUser
    // (GetUser snapshot source), the mute cache, and an OnlineMemberRegistry membership for the channel.
    private void SeedMember(
        string connectionId,
        string battleTag,
        string channelId,
        ChatUser chatUser = null,
        MuteStatus mute = MuteStatus.None,
        DateTime? muteEnd = null)
    {
        RegisterSession(connectionId, battleTag);
        _connectionMapping.RegisterUser(connectionId, chatUser ?? FlairUser(battleTag));
        _connectionMapping.SetMute(connectionId, mute, muteEnd ?? DateTime.MinValue);
        _onlineMemberRegistry.Join(channelId, connectionId, new MemberState(battleTag, NotificationLevel.Mentions, 0, ChannelType.Public));
    }

    // ---------------------------------------------------------------------------------------------
    // Happy path + persistence (C1 amendment)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Send_ValidMessage_ReturnsOk_WithStrictlyIncreasingSeq()
    {
        var channel = await CreateChannel("general");
        SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        var first = await hub.SendMessage(channel.Id, "hello");
        var second = await hub.SendMessage(channel.Id, "world");

        Assert.AreEqual(ChatResultCode.Ok, first.Code);
        Assert.AreEqual(ChatResultCode.Ok, second.Code);
        Assert.IsNotNull(first.MessageId);
        Assert.IsNotNull(second.MessageId);
        Assert.AreNotEqual(first.MessageId, second.MessageId, "Each send must persist a distinct message");
        Assert.IsTrue(second.Seq > first.Seq,
            $"Per-channel seq must strictly increase across sends (first={first.Seq}, second={second.Seq})");
    }

    [Test]
    public async Task Send_Persists_ExpiresAt30d_AndChannelLastSeqAndLastMessageAt()
    {
        var channel = await CreateChannel("general");
        SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, "durable");

        Assert.AreEqual(ChatResultCode.Ok, result.Code);

        // The persisted message: 30d channel expiry (NOT the 90d DM window) + the allocated seq.
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.IsNotNull(persisted, "The message must be durably persisted");
        Assert.AreEqual(channel.Id, persisted.ChannelId);
        Assert.AreEqual(result.Seq, persisted.Seq);
        Assert.AreEqual(Now, persisted.SentAt);
        Assert.IsNotNull(persisted.ExpiresAt);
        var expected30d = Now + TimeSpan.FromDays(30);
        Assert.That((persisted.ExpiresAt.Value - expected30d).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "Channel-message expiry must be sentAt + 30d (ExpiryCalculator.ForChannelMessage)");
        Assert.That((persisted.ExpiresAt.Value - (Now + TimeSpan.FromDays(90))).Duration(), Is.GreaterThan(TimeSpan.FromDays(1)),
            "Channel messages must NOT get the 90d DM retention window");

        // C1 amendment: AllocateSeq atomically $inc LastSeq + $set LastMessageAt on the channel doc.
        var reloadedChannel = await _channelRepository.Load(channel.Id);
        Assert.AreEqual(1L, reloadedChannel.LastSeq, "The channel's LastSeq must be incremented on persist");
        Assert.AreEqual(result.Seq, reloadedChannel.LastSeq, "The message's seq must equal the channel's new LastSeq");
        Assert.IsNotNull(reloadedChannel.LastMessageAt, "LastMessageAt must be stamped on persist");
        Assert.That((reloadedChannel.LastMessageAt.Value - Now).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "LastMessageAt must be set to the send-time clock");
    }

    // ---------------------------------------------------------------------------------------------
    // Validation
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Send_513Chars_ReturnsTooLong()
    {
        var channel = await CreateChannel("general");
        SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        var overLength = new string('a', ChatLimits.MaxMessageLength + 1);
        var result = await hub.SendMessage(channel.Id, overLength);

        Assert.AreEqual(ChatResultCode.TooLong, result.Code);
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.AreEqual(0L, reloaded.LastSeq, "An over-length message must not persist / allocate a seq");
    }

    [Test]
    public async Task Send_EmptyAfterTrim_ReturnsTooLong()
    {
        var channel = await CreateChannel("general");
        SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        // Empty-after-trim maps to TooLong by pinned plan decision (the enum has no InvalidContent).
        var result = await hub.SendMessage(channel.Id, "   \t  ");

        Assert.AreEqual(ChatResultCode.TooLong, result.Code);
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.AreEqual(0L, reloaded.LastSeq, "An empty-after-trim message must not persist / allocate a seq");
    }

    // ---------------------------------------------------------------------------------------------
    // Rate limiting
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Send_SixthInBurst_ReturnsThrottled_WithRetryAfter()
    {
        var channel = await CreateChannel("general");
        SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        // Per-channel burst is 5 (ChatLimits.PerChannelBurst); the clock is frozen, so no refill.
        for (var i = 0; i < ChatLimits.PerChannelBurst; i++)
        {
            var ok = await hub.SendMessage(channel.Id, $"msg-{i}");
            Assert.AreEqual(ChatResultCode.Ok, ok.Code, $"Send #{i + 1} is within the burst and must succeed");
        }

        var sixth = await hub.SendMessage(channel.Id, "one too many");

        Assert.AreEqual(ChatResultCode.Throttled, sixth.Code);
        Assert.IsTrue(sixth.RetryAfterSeconds > 0, "A throttled response must carry a positive retry-after");

        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.AreEqual(ChatLimits.PerChannelBurst, (int)reloaded.LastSeq, "Only the 5 allowed sends may allocate a seq");
    }

    [Test]
    public async Task Send_AutoThrottleEscalation_PushesSingleThrottleNotice()
    {
        var channel = await CreateChannel("general");
        SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        // 5 allowed (burst), then throttles. The 5th throttle crosses AutoThrottleViolationThreshold (5)
        // → exactly ONE ThrottleNotice pushed to the caller on that single escalation decision.
        var throttleCount = ChatLimits.AutoThrottleViolationThreshold;
        for (var i = 0; i < ChatLimits.PerChannelBurst + throttleCount; i++)
        {
            await hub.SendMessage(channel.Id, $"spam-{i}");
        }
        // One more send while hard-throttled must NOT push a second notice.
        await hub.SendMessage(channel.Id, "still throttled");

        var notices = _callerSends.Count(s => s.Method == ChatEvents.ThrottleNotice);
        Assert.AreEqual(1, notices, "Exactly one ThrottleNotice must be pushed on the single auto-throttle escalation");
    }

    [Test]
    public async Task Send_AutoThrottle_SurvivesReconnect_SameBattleTag()
    {
        var channel = await CreateChannel("general");
        SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        // Drive conn-1 into hard auto-throttle: burst, then threshold violations (frozen clock — no refill).
        for (var i = 0; i < ChatLimits.PerChannelBurst + ChatLimits.AutoThrottleViolationThreshold; i++)
        {
            await hub.SendMessage(channel.Id, $"spam-{i}");
        }

        // A REAL relaunch: conn-1 actually disconnects — running the hub's full disconnect teardown
        // (FocusRegistry/OnlineMemberRegistry/etc. all torn down) — BEFORE conn-2 is ever seeded. This
        // is what proves MessageRateLimiter state survives actual disconnect (locking in the removed
        // ChatHub.cs RemoveConnection call), not merely that a second connection can coexist.
        await hub.OnDisconnectedAsync(null);

        // "Relaunch": a brand-new connection, SAME battleTag. Pre-§1 this was a clean slate.
        SeedMember("conn-2", BattleTag, channel.Id);
        var reconnected = BuildHub("conn-2");
        var result = await reconnected.SendMessage(channel.Id, "back for more");

        Assert.AreEqual(ChatResultCode.Throttled, result.Code, "the auto-throttle must survive the reconnect");
        Assert.IsTrue(result.RetryAfterSeconds > 0);
    }

    // ---------------------------------------------------------------------------------------------
    // Membership / channel resolution
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Send_NonMember_ReturnsNotMember()
    {
        var channel = await CreateChannel("general");
        RegisterSession("conn-1", BattleTag);
        _connectionMapping.RegisterUser("conn-1", FlairUser(BattleTag));
        // Deliberately NOT a member (no OnlineMemberRegistry seed).
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, "hi");

        Assert.AreEqual(ChatResultCode.NotMember, result.Code);
    }

    [Test]
    public async Task Send_UnknownChannel_ReturnsNotFound()
    {
        // Member-of-a-deleted-channel edge: the registry says member, but no channel doc exists.
        const string ghostChannelId = "ghost-channel-id";
        SeedMember("conn-1", BattleTag, ghostChannelId);
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(ghostChannelId, "anyone there?");

        Assert.AreEqual(ChatResultCode.NotFound, result.Code);
    }

    [Test]
    public async Task Send_UnregisteredConnection_ReturnsPermissionDenied_FailClosed()
    {
        var channel = await CreateChannel("general");
        // No session registered for this connection.
        var hub = BuildHub("conn-ghost");

        var result = await hub.SendMessage(channel.Id, "hi");

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code);
    }

    // ---------------------------------------------------------------------------------------------
    // Mute gate (PUBLIC channels only — guardrail)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Send_FullMuted_PublicChannel_ReturnsMuted()
    {
        var channel = await CreateChannel("W3C Lounge");
        SeedMember("conn-1", BattleTag, channel.Id, mute: MuteStatus.Full, muteEnd: Now.AddDays(1));
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, "let me talk");

        Assert.AreEqual(ChatResultCode.Muted, result.Code);
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.AreEqual(0L, reloaded.LastSeq, "A full-muted send in a public channel must not persist");
    }

    [Test]
    public async Task Send_FullMuted_SemiPublicChannel_Broadcasts()
    {
        // Mute exemption preserved: only Public channels are gated. A full-muted user's send in a
        // semiPublic channel persists and returns Ok.
        var channel = await CreateChannel("semi-room", ChannelType.SemiPublic);
        SeedMember("conn-1", BattleTag, channel.Id, mute: MuteStatus.Full, muteEnd: Now.AddDays(1));
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, "clan chatter");

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.IsNotNull(persisted, "A full-muted send in a semiPublic channel must still persist (mute exemption)");
        Assert.IsFalse(persisted.Shadow, "A full-mute is not a shadow flag");
    }

    [Test]
    public async Task Send_ShadowMuted_PublicChannel_ReturnsOk_PersistsFlagged_NoBroadcast()
    {
        var channel = await CreateChannel("W3C Lounge");
        SeedMember("conn-1", BattleTag, channel.Id, mute: MuteStatus.Shadow, muteEnd: Now.AddDays(1));
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, "shadow whisper");

        // Illusion: Ok to the sender, persisted with Shadow=true. (Fan-out is a stub at this task, so
        // "no broadcast" is trivially satisfied; author-only routing is Task 12's.)
        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.IsNotNull(persisted);
        Assert.IsTrue(persisted.Shadow, "A shadow-muted send must persist with Shadow=true");
    }

    // ---------------------------------------------------------------------------------------------
    // Mute gate — LADDER match channels (System+Match with Ladder=true)
    //
    // A ladder match's post-game room is moderated exactly like a Public room: a full mute rejects the
    // send, a shadow ban persists flagged. A CUSTOM-GAME lobby's room is the same channel shape
    // (System+Match) with Ladder=false and stays exempt — mm's `ladder` flag is the only thing that
    // separates them.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Send_FullMuted_LadderMatchChannel_ReturnsMuted()
    {
        var channel = await CreateMatchChannel("ladder-ref-1", ladder: true);
        SeedMember("conn-1", BattleTag, channel.Id, mute: MuteStatus.Full, muteEnd: Now.AddDays(1));
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, "gg ez");

        Assert.AreEqual(ChatResultCode.Muted, result.Code);
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.AreEqual(0L, reloaded.LastSeq, "A full-muted send in a ladder match channel must not persist");
    }

    [Test]
    public async Task Send_ShadowMuted_LadderMatchChannel_ReturnsOk_PersistsFlagged()
    {
        var channel = await CreateMatchChannel("ladder-ref-2", ladder: true);
        SeedMember("conn-1", BattleTag, channel.Id, mute: MuteStatus.Shadow, muteEnd: Now.AddDays(1));
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, "shadow whisper");

        // Same illusion as a Public room: Ok to the sender, persisted flagged, delivered to nobody else.
        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.IsNotNull(persisted);
        Assert.IsTrue(persisted.Shadow, "A shadow-muted send in a ladder match channel must persist with Shadow=true");
    }

    [Test]
    public async Task Send_Unmuted_LadderMatchChannel_SendsNormally()
    {
        var channel = await CreateMatchChannel("ladder-ref-3", ladder: true);
        SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, "gg wp");

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.IsFalse(persisted.Shadow, "An unmuted ladder send is never flagged");
    }

    [Test]
    public async Task Send_FullMuted_CustomLobbyMatchChannel_StillSends()
    {
        // Custom-game lobby/post-game stays EXEMPT (explicit product decision): same channel shape,
        // Ladder=false.
        var channel = await CreateMatchChannel("custom-ref-1", ladder: false);
        SeedMember("conn-1", BattleTag, channel.Id, mute: MuteStatus.Full, muteEnd: Now.AddDays(1));
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, "lobby chatter");

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.IsNotNull(persisted, "A full-muted send in a custom-lobby match channel must still persist");
        Assert.IsFalse(persisted.Shadow, "A full-mute is not a shadow flag");
    }

    [Test]
    public async Task Send_ShadowMuted_CustomLobbyMatchChannel_PersistsUnflagged()
    {
        var channel = await CreateMatchChannel("custom-ref-2", ladder: false);
        SeedMember("conn-1", BattleTag, channel.Id, mute: MuteStatus.Shadow, muteEnd: Now.AddDays(1));
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, "lobby chatter");

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.IsFalse(persisted.Shadow,
            "A custom-lobby match channel is outside the mute scope — a shadow ban must not flag the message there");
    }

    // ---------------------------------------------------------------------------------------------
    // Sender snapshot (flair at send time, no per-message wb round-trip)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Send_SenderSnapshot_CarriesFlairAtSendTime()
    {
        var channel = await CreateChannel("general");
        var chatUser = FlairUser(BattleTag);
        SeedMember("conn-1", BattleTag, channel.Id, chatUser: chatUser);
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, "with flair");

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.IsNotNull(persisted.Sender, "The message must carry a sender snapshot");
        Assert.AreEqual(chatUser.BattleTag, persisted.Sender.BattleTag);
        Assert.AreEqual(chatUser.Name, persisted.Sender.Name);
        Assert.IsNotNull(persisted.Sender.Flair, "The snapshot must carry the flair resolved at connect");
        Assert.AreEqual(chatUser.ClanTag, persisted.Sender.Flair.ClanId, "Flair.ClanId must come from the cached ChatUser.ClanTag");
        Assert.IsNotNull(persisted.Sender.Flair.ProfilePicture);
        Assert.AreEqual(chatUser.ProfilePicture.PictureId, persisted.Sender.Flair.ProfilePicture.PictureId);
        Assert.AreEqual(chatUser.ProfilePicture.Race, persisted.Sender.Flair.ProfilePicture.Race);

        // The send path must NOT re-resolve the user from wb — the flair comes from the connect-time cache.
        _authService.Verify(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()), Times.Never,
            "The send path must snapshot the cached ChatUser — never call GetUserFromIdentity per message");
    }

    // ---------------------------------------------------------------------------------------------
    // New-pipeline mute-enforcement mirrors of MuteReconciliationTests' hub-driving tests
    // (cache-only enforcement — coverage must never gap while the old tests await Task 19's migration).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task ControllerBan_TakesEffect_NewPipeline_ReturnsMuted_CacheOnly()
    {
        var channel = await CreateChannel("W3C Lounge");
        // A live, unmuted member of a public channel.
        SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        // Moderator issues a FULL ban via the REST controller (persists + reconciles the live cache).
        await _muteController.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = BattleTag,
            endDate = Now.AddDays(1).ToString("O"),
            author = "admin#1",
            reason = "bad behavior",
            isShadowBan = false,
        });
        Assert.IsTrue(_connectionMapping.TryGetMute("conn-1", out var cached));
        Assert.AreEqual(MuteStatus.Full, cached.Status, "Controller full ban must reconcile the live cache to Full");

        // Wipe the DB so a DB read would find NO ban — proving the new pipeline enforces from the cache.
        await _muteRepository.DeleteLoungeMute(BattleTag);

        var result = await hub.SendMessage(channel.Id, "should be rejected");

        Assert.AreEqual(ChatResultCode.Muted, result.Code,
            "After a controller ban, the next SendMessage in a public channel is rejected from the cache (no DB read)");
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.AreEqual(0L, reloaded.LastSeq, "A cache-muted send must not persist");
    }

    [Test]
    public async Task ControllerShadowBan_TakesEffect_NewPipeline_PersistsFlagged_CacheOnly()
    {
        var channel = await CreateChannel("W3C Lounge");
        SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        await _muteController.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = BattleTag,
            endDate = Now.AddDays(1).ToString("O"),
            author = "admin#1",
            reason = "spam",
            isShadowBan = true,
        });
        Assert.IsTrue(_connectionMapping.TryGetMute("conn-1", out var cached));
        Assert.AreEqual(MuteStatus.Shadow, cached.Status, "Controller shadow ban must reconcile the live cache to Shadow");

        await _muteRepository.DeleteLoungeMute(BattleTag);

        var result = await hub.SendMessage(channel.Id, "shadow after controller ban");

        Assert.AreEqual(ChatResultCode.Ok, result.Code, "A shadow-banned user still gets Ok (illusion)");
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.IsTrue(persisted.Shadow, "The message persists flagged Shadow=true from the reconciled cache (no DB read)");
    }

    [Test]
    public async Task ControllerUnban_TakesEffect_NewPipeline_AllowsSend()
    {
        var channel = await CreateChannel("W3C Lounge");
        // A live, fully-banned member.
        SeedMember("conn-1", BattleTag, channel.Id, mute: MuteStatus.Full, muteEnd: Now.AddDays(1));
        await _muteRepository.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = BattleTag,
            endDate = Now.AddDays(1).ToString("O"),
            author = "admin#1",
            reason = "bad behavior",
            isShadowBan = false,
        });
        var hub = BuildHub("conn-1");

        // Moderator lifts the ban via the REST controller → clears the live cache to None.
        await _muteController.DeleteLoungeMute(BattleTag);
        Assert.IsTrue(_connectionMapping.TryGetMute("conn-1", out var cached));
        Assert.AreEqual(MuteStatus.None, cached.Status, "Controller unban must clear the live cache to None");

        var result = await hub.SendMessage(channel.Id, "I can talk again");

        Assert.AreEqual(ChatResultCode.Ok, result.Code,
            "After a controller unban, the user can send again in a public channel without reconnecting");
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.IsNotNull(persisted);
        Assert.IsFalse(persisted.Shadow);
    }

    // ---------------------------------------------------------------------------------------------
    // C6 Task 5 — mention fan-out (D3/D4), end-to-end through the SendMessage pipeline. These exercise
    // the step-8 call site (fix round 1 F2b renumbered it — validated mention list → MentionFanOut →
    // durable entry + targeted event),
    // the shadow call-site skip, focus-irrelevance, and the sender-ack fault isolation. The per-rule
    // eligibility boundary is covered directly in MentionFanOutTests.
    // ---------------------------------------------------------------------------------------------

    private static string Mention(string tag) => $"<@{tag}>";

    // Directory row → the step-5.25 validation gate resolves the mention (resolvability-only).
    private Task SeedDirectory(string battleTag) =>
        _userDirectory.Upsert(new UserDirectoryEntry
        {
            BattleTag = battleTag,
            DisplayBattleTag = battleTag,
            NormalizedName = battleTag.ToLowerInvariant(),
            LastSeenAt = Now,
        });

    // Durable channel_memberships row → the D3c (membership wall) + D3d (level) eligibility source.
    private Task SeedDurableMembership(string channelId, string battleTag, NotificationLevel level) =>
        _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = channelId,
            BattleTag = battleTag,
            NotificationLevel = level,
            JoinedAt = Now,
        });

    // A fully-eligible, ONLINE mention target: a live session (GetByBattleTag → live push), directory
    // resolvability (validation), and a durable membership row (eligibility). NOT focused by default.
    private async Task SeedMentionTarget(string connectionId, string battleTag, string channelId, NotificationLevel level = NotificationLevel.All)
    {
        RegisterSession(connectionId, battleTag);
        await SeedDirectory(battleTag);
        await SeedDurableMembership(channelId, battleTag, level);
    }

    [Test]
    public async Task Mention_UnfocusedMember_InboxEntryCreated_AndTargetedMentionNotified()
    {
        var channel = await CreateChannel("general");
        SeedMember("conn-1", BattleTag, channel.Id);                    // sender peter#123
        await SeedMentionTarget("conn-2", "wolf#456", channel.Id);      // mentioned target — NOT focused
        await SeedMentionTarget("conn-3", "frank#789", channel.Id);     // a THIRD member — focused, NOT mentioned
        _focusRegistry.Focus("conn-3", channel.Id, "frank#789");
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, $"hey {Mention("wolf#456")} you around?");

        Assert.AreEqual(ChatResultCode.Ok, result.Code);

        // Durable entry for the mentioned member (lowercased key), carrying the message ref.
        var wolfInbox = await _mentionInboxRepository.LoadForUser("wolf#456");
        Assert.AreEqual(1, wolfInbox.Count, "the mentioned member gets exactly one inbox entry");
        var entry = wolfInbox[0];
        Assert.AreEqual(channel.Id, entry.ChannelId);
        Assert.AreEqual(result.MessageId, entry.MessageId);
        Assert.AreEqual(result.Seq, entry.Seq);
        Assert.IsNotNull(entry.ExpiresAt);
        Assert.That((entry.ExpiresAt.Value - (Now + TimeSpan.FromDays(30))).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "mention entry expiry is CreatedAt + 30d");

        // Targeted event, ONLY to the mentioned member's connection, carrying the entry id.
        Assert.AreEqual(1, _mentionPushHarness.SignalCount("conn-2", ChatEvents.MentionNotified));
        var dto = (MentionNotifiedDto)_mentionPushHarness.PayloadFor("conn-2", ChatEvents.MentionNotified);
        Assert.AreEqual(entry.Id, dto.EntryId, "the event carries the just-inserted entry id (insert-before-push)");
        Assert.AreEqual(result.MessageId, dto.MessageId);
        Assert.AreEqual(result.Seq, dto.Seq);

        // The third (focused, un-mentioned) member captures ZERO MentionNotified and has no entry.
        Assert.AreEqual(0, _mentionPushHarness.SignalCount("conn-3", ChatEvents.MentionNotified),
            "a third focused member who was NOT mentioned must capture zero MentionNotified — targeting is exact, never a broadcast");
        Assert.IsEmpty(await _mentionInboxRepository.LoadForUser("frank#789"));
    }

    [Test]
    public async Task Mention_FocusedMember_EntryAndEventStillCreated()
    {
        var channel = await CreateChannel("general");
        SeedMember("conn-1", BattleTag, channel.Id);
        await SeedMentionTarget("conn-2", "wolf#456", channel.Id);
        _focusRegistry.Focus("conn-2", channel.Id, "wolf#456");         // the mentioned member IS focused
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, $"look here {Mention("wolf#456")}");

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.AreEqual(1, (await _mentionInboxRepository.LoadForUser("wolf#456")).Count,
            "a focused member STILL gets an inbox entry — the server never guesses 'seen' (create-then-client-ack)");
        Assert.AreEqual(1, _mentionPushHarness.SignalCount("conn-2", ChatEvents.MentionNotified),
            "a focused member STILL gets the MentionNotified event — focus does not suppress mentions (unlike C3 activity)");
    }

    [Test]
    public async Task ShadowSender_MentionsOthers_NoEntriesNoEvents_MessagePersistedFlagged()
    {
        var channel = await CreateChannel("W3C Lounge");
        SeedMember("conn-1", BattleTag, channel.Id, mute: MuteStatus.Shadow, muteEnd: Now.AddDays(1));
        // wolf WOULD be eligible (durable member, online, resolvable) if not for the shadow skip.
        await SeedMentionTarget("conn-2", "wolf#456", channel.Id);
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, $"hey {Mention("wolf#456")}");

        Assert.AreEqual(ChatResultCode.Ok, result.Code, "a shadow sender still gets the Ok illusion");
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.IsTrue(persisted.Shadow, "the message persists flagged Shadow=true");

        // The shadow guardrail (T7 re-asserts this inside C4's suite): literally zero entries + zero
        // events for ANYONE — a shadow sender's mentions must break neither the shadow illusion.
        Assert.IsEmpty(await _mentionInboxRepository.LoadForUser("wolf#456"), "a shadow sender's mention creates NO inbox entry");
        Assert.AreEqual(0, _mentionPushHarness.SignalCount("conn-2", ChatEvents.MentionNotified), "and NO MentionNotified");
        Assert.IsEmpty(_mentionPushHarness.AllSignals, "a shadow message must notify literally nobody");
    }

    [Test]
    public async Task Mention_DeadTargetSocket_SenderAckStillOk_OtherTargetsDelivered()
    {
        var channel = await CreateChannel("general");
        SeedMember("conn-1", BattleTag, channel.Id);
        await SeedMentionTarget("conn-2", "wolf#456", channel.Id);      // this socket throws on push
        await SeedMentionTarget("conn-3", "frank#789", channel.Id);     // healthy
        _mentionPushHarness.ThrowOnSend("conn-2");
        var hub = BuildHub("conn-1");

        // Mention order is wolf then frank (first-occurrence) — wolf's push throws, frank's must still land.
        var result = await hub.SendMessage(channel.Id, $"{Mention("wolf#456")} {Mention("frank#789")}");

        Assert.AreEqual(ChatResultCode.Ok, result.Code,
            "a dead target socket must NOT turn the sender's already-persisted send into an error");

        // wolf: entry created (insert precedes the throwing push), but no captured event.
        Assert.AreEqual(1, (await _mentionInboxRepository.LoadForUser("wolf#456")).Count,
            "the dead-socket target still gets its durable entry (insert-before-push)");
        Assert.AreEqual(0, _mentionPushHarness.SignalCount("conn-2", ChatEvents.MentionNotified));
        // frank: unaffected — entry + event delivered.
        Assert.AreEqual(1, (await _mentionInboxRepository.LoadForUser("frank#789")).Count,
            "the OTHER target is unaffected by the dead socket — entry delivered");
        Assert.AreEqual(1, _mentionPushHarness.SignalCount("conn-3", ChatEvents.MentionNotified),
            "the OTHER target is unaffected by the dead socket — event delivered");
    }

    // ---------------------------------------------------------------------------------------------
    // C6 "strip & deliver as plain" (Marco decision 3), amended by D2 (2026-08-05, server-canonical
    // rendering): a message is NEVER rejected because of its mentions' access/resolvability. But since D2,
    // an unresolvable/garbage tag, or a tag naming a NON-legal-render-target, is no longer delivered with
    // its markup intact — step 5.26 rewrites it to plain text (`@tag`) in the PERSISTED content before the
    // fan-out ever runs, so every reader sees identical canonical content. The membership wall in
    // MentionFanOut still independently decides who gets an inbox entry + notification (now a redundant
    // second line of defense behind the same base condition step 5.26 already evaluated). Since follow-up
    // spec §4, that wall is widened for PUBLIC channels: a directory-RESOLVABLE non-member of a Public
    // channel keeps its markup AND gets an entry + notification (only an unresolvable tag still gets
    // nothing there, and is stripped); Dm/GroupDm/SemiPublic/System keep the unconditional membership wall
    // for BOTH render and notify. These exercise the full SendMessage pipeline end-to-end (real
    // MentionFanOut + inbox repo + push harness).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Mention_GarbageTag_Ok_StrippedToPlainText_NoInboxEntry()
    {
        var channel = await CreateChannel("general");
        SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        // "ghost#999" resolves to nobody (no directory row, no session, no membership) — a garbage mention,
        // and NOT a legal render target (no membership row; Public but not directory-resolvable).
        var content = $"hey {Mention("ghost#999")} are you real?";
        var result = await hub.SendMessage(channel.Id, content);

        Assert.AreEqual(ChatResultCode.Ok, result.Code, "an unresolvable/garbage <@tag> must NEVER reject the send");
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.IsNotNull(persisted, "the message is durably persisted");
        Assert.AreEqual("hey @ghost#999 are you real?", persisted.Content,
            "D2: a non-renderable mention token is stripped to its plain-text form in the persisted content");
        Assert.IsEmpty(await _mentionInboxRepository.LoadForUser("ghost#999"), "a garbage mention writes NO inbox entry");
        Assert.IsEmpty(_mentionPushHarness.AllSignals, "and pushes nothing");
    }

    [Test]
    public async Task Mention_ResolvableNonMemberOfPublicChannel_Ok_GetsInboxEntryAndPush()
    {
        var channel = await CreateChannel("general");
        SeedMember("conn-1", BattleTag, channel.Id);
        // stranger is online AND directory-resolvable but is NOT a member of this public channel.
        RegisterSession("conn-stranger", "stranger#1");
        await SeedDirectory("stranger#1");
        var hub = BuildHub("conn-1");

        var content = $"hey {Mention("stranger#1")}";
        var result = await hub.SendMessage(channel.Id, content);

        Assert.AreEqual(ChatResultCode.Ok, result.Code, "mentioning a resolvable non-member of a public channel is legal — no reject");
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.AreEqual(content, persisted.Content,
            "D2: a directory-resolvable target in a Public channel IS a legal render target — markup stays intact, never stripped");
        Assert.AreEqual(1, (await _mentionInboxRepository.LoadForUser("stranger#1")).Count,
            "follow-up spec §4: a directory-resolvable non-member of a PUBLIC channel now gets an inbox entry — the membership wall is widened away for Public rooms only");
        Assert.AreEqual(1, _mentionPushHarness.SignalCount("conn-stranger", ChatEvents.MentionNotified),
            "and the targeted MentionNotified push");
        Assert.AreEqual(1, _mentionPushHarness.AllSignals.Count, "exactly one push — targeted at the stranger, nobody else");
    }

    [Test]
    public async Task Mention_NonMemberInGroupDm_Ok_OutsiderStripped_MemberKeepsMarkup_MemberControlNotified()
    {
        // Marco's headline case: a GroupDm (a PRIVATE conversation). The sender mentions BOTH an outsider
        // who is NOT a member of the group AND a real member (wolf). The send is NOT rejected, but D2
        // (2026-08-05) strips the outsider's token to plain text — a GroupDm is never Public, so a
        // non-member is never a legal render target there, only a real membership row is — while wolf's
        // markup stays intact. The excerpt PRIVACY WALL still means the non-member outsider gets NO inbox
        // entry and NO MentionNotified (a private conversation's ~120-char excerpt must never reach a
        // non-participant), while the member control still gets both (so this fails against a do-nothing
        // stub too).
        var group = new ChatChannel { Type = ChannelType.GroupDm, Name = "squad", LastSeq = 0, LastMessageAt = Now, ExpiresAt = Now.AddDays(365) };
        await _channelRepository.Insert(group);
        SeedMember("conn-1", BattleTag, group.Id);                     // sender (a member)
        await SeedMentionTarget("conn-wolf", "wolf#456", group.Id);    // member control: session + directory + durable membership

        // The outsider is ONLINE (a leak would be observable) but is NOT a member of the group.
        RegisterSession("conn-outsider", "outsider#1");
        var hub = BuildHub("conn-1");

        var content = $"secret plans {Mention("outsider#1")} and {Mention("wolf#456")}";
        var result = await hub.SendMessage(group.Id, content);

        Assert.AreEqual(ChatResultCode.Ok, result.Code, "a mention of a non-member must NEVER reject the send (strip & deliver as plain)");

        // D2: the outsider's token is stripped to plain text; wolf's (an actual member) stays markup.
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.IsNotNull(persisted, "the message is durably persisted");
        Assert.AreEqual($"secret plans @outsider#1 and {Mention("wolf#456")}", persisted.Content,
            "D2: a GroupDm non-member is never a legal render target — only the outsider's token is stripped, the member's markup is untouched");

        // The excerpt PRIVACY WALL: the non-member outsider gets NO inbox entry and NO MentionNotified —
        // a private (GroupDm) conversation's excerpt NEVER reaches a non-participant.
        Assert.IsEmpty(await _mentionInboxRepository.LoadForUser("outsider#1"),
            "a non-member of a private GroupDm gets NO mention-inbox entry — the excerpt never reaches a non-participant");
        Assert.AreEqual(0, _mentionPushHarness.SignalCount("conn-outsider", ChatEvents.MentionNotified),
            "and NO MentionNotified push to the non-member");

        // The member control proves the send DID fan out to the eligible participant.
        Assert.AreEqual(1, (await _mentionInboxRepository.LoadForUser("wolf#456")).Count, "the member control still gets an entry");
        Assert.AreEqual(1, _mentionPushHarness.SignalCount("conn-wolf", ChatEvents.MentionNotified), "the member control still gets the event");
    }
}

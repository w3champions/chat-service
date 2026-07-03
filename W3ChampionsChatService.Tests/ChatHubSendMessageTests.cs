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
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Settings;
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
    private ChatHistory _chatHistory;
    private UserDirectoryRepository _userDirectory;
    private SettingsRepository _settingsRepository;
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
    private ChannelCreationRateLimiter _channelCreationRateLimiter;
    private SessionStateAssembler _assembler;
    private FanOutEngine _fanOutEngine;
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
        _chatHistory = new ChatHistory();
        _userDirectory = new UserDirectoryRepository(MongoClient);
        _settingsRepository = new SettingsRepository(MongoClient);
        _muteRepository = new MuteRepository(MongoClient);
        _reconcileHarness = new MuteReconciliationTestHarness(_connectionMapping, _muteRepository);
        _muteController = new MuteController(_muteRepository, _reconcileHarness.Service);
        _ticketStore = new TicketStore();

        _authService = new Mock<IChatAuthenticationService>();
        _authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync((W3CUserAuthentication id) =>
                new ChatUser(id.BattleTag, id.IsAdmin, id.Name, new ProfilePicture(), null, null));

        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _messageRepository = new MessageRepository(MongoClient);
        _sessionRegistry = new SessionRegistry();
        _focusRegistry = new FocusRegistry();
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _messageRateLimiter = new MessageRateLimiter();
        _channelCreationRateLimiter = new ChannelCreationRateLimiter();
        _fanOutEngine = new FanOutEngine(new HubPushCaptureHarness().HubContext, new FocusRegistry());
        _assembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            _muteRepository,
            _authService.Object,
            _onlineMemberRegistry,
            _connectionMapping);
    }

    private ChatHub BuildHub(string connectionId)
    {
        var hub = new ChatHub(
            _authService.Object,
            _muteRepository,
            _settingsRepository,
            _connectionMapping,
            _chatHistory,
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
            _fanOutEngine);

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
        _onlineMemberRegistry.Join(channelId, connectionId, new MemberState(battleTag, NotificationLevel.Mentions, 0));
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
}

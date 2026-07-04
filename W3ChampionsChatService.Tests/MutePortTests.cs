using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
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
/// C4 Task 2 — the byte-for-byte mute/ban SEMANTICS PORT of the pre-cutover
/// <c>ChatBanRoomScopeTests</c> (@778aec9) onto the new C3 channel pipeline. Each test is headed by a
/// <c>// LEGACY:</c> lineage comment naming its source method. These CHARACTERIZE already-shipped C3
/// behaviour, so they were green on write; the legacy room-scope model is re-expressed against the
/// type-based enforcement of the new pipeline:
/// <list type="bullet">
/// <item>the old "banned room" (public room name) → <see cref="ChannelType.Public"/>; the old exempt
/// clan/lobby room → the sole name-joinable exempt type <see cref="ChannelType.SemiPublic"/>;</item>
/// <item>the old <c>SwitchRoom</c> gate → <see cref="ChatHub.JoinChannel"/>; the old single-arg
/// <c>SendMessage</c> gate → <see cref="ChatHub.SendMessage(string,string)"/>;</item>
/// <item>the old <c>UserEntered</c>/<c>UserLeft</c> presence broadcasts → the batched
/// <c>ViewersChanged</c> roster deltas (<see cref="ViewersAccumulator"/>);</item>
/// <item>the old <c>GetUsersOfRoom</c> visibility → the <see cref="ChatHub.FocusChannel"/> viewer roster.</item>
/// </list>
/// Guardrails pinned here: mutes gate <see cref="ChannelType.Public"/> ONLY, a full-ban connect NEVER
/// aborts, <c>PlayerBannedFromChat</c> carries <c>endDate</c> only, and a shadow-banned user is a fully
/// VISIBLE member (only their public messages drop).
/// </summary>
public class MutePortTests : IntegrationTestBase
{
    private const string BattleTag = "peter#123";
    private const string OtherTag = "alice#456";

    private static readonly DateTimeOffset FixedNow = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Flush = ChatLimits.ViewersChangedFlush; // 5s

    private FakeTimeProvider _time;
    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private ConnectionMapping _connectionMapping;
    private UserDirectoryRepository _userDirectory;
    private MuteRepository _muteRepository;
    private MuteReconciliationTestHarness _reconcileHarness;
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

    // ViewersChanged emission channel for the shadow-visibility tests. The real accumulator shares the
    // SAME FocusRegistry the hubs mutate, and pushes through the capture harness (NOT the hub's Clients).
    private HubPushCaptureHarness _pushHarness;
    private ViewersAccumulator _accumulator;

    // Connections that had Context.Abort() invoked (join/send no-abort pins). Connect-path sends/aborts
    // are captured separately in _connectSends (target, method | "ABORT").
    private readonly List<string> _aborts = new();
    private readonly List<(string Target, string Method)> _connectSends = new();

    [SetUp]
    public void SetupBeforeEach()
    {
        _aborts.Clear();
        _connectSends.Clear();
        _time = new FakeTimeProvider(FixedNow);

        _connectionMapping = new ConnectionMapping();
        _userDirectory = new UserDirectoryRepository(MongoClient);
        _muteRepository = new MuteRepository(MongoClient);
        _reconcileHarness = new MuteReconciliationTestHarness(_connectionMapping, _muteRepository);
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
        _assembler = NewAssembler(_muteRepository);

        _pushHarness = new HubPushCaptureHarness();
        _accumulator = new ViewersAccumulator(_pushHarness.HubContext, _focusRegistry);
    }

    // ── Hub construction ─────────────────────────────────────────────────────────

    private SessionStateAssembler NewAssembler(IMuteRepository muteRepository) =>
        new(_membershipRepository, _channelRepository, _messageRepository, muteRepository, _authService.Object, _onlineMemberRegistry, _connectionMapping);

    private ChatHub NewHub(SessionStateAssembler assembler, ViewersAccumulator viewers, IHubCallerClients clients, HubCallerContext context)
    {
        var hub = new ChatHub(
            _connectionMapping,
            _reconcileHarness.Service,
            _ticketStore,
            _sessionRegistry,
            _userDirectory,
            assembler,
            _focusRegistry,
            _onlineMemberRegistry,
            _messageRateLimiter,
            _time,
            _channelRepository,
            _membershipRepository,
            _channelCreationRateLimiter,
            _messageRepository,
            FanOutEngineTestFactory.CreateIgnored(),
            viewers,
            new NoOpMentionInboxCleaner())
        {
            Clients = clients,
            Context = context,
            Groups = new Mock<IGroupManager>().Object,
        };
        return hub;
    }

    // A no-op mock clients (Caller/Group/Client all discard sends) — used by result-code tests that
    // never inspect pushes.
    private static IHubCallerClients NoopClients()
    {
        var clients = new Mock<IHubCallerClients>();
        var caller = new Mock<ISingleClientProxy>();
        caller.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var group = new Mock<IClientProxy>();
        group.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Caller).Returns(caller.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(group.Object);
        clients.Setup(c => c.Client(It.IsAny<string>())).Returns(caller.Object);
        return clients.Object;
    }

    private HubCallerContext MockContext(string connectionId)
    {
        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        context.Setup(c => c.Abort()).Callback(() => _aborts.Add(connectionId));
        return context.Object;
    }

    // Result-code hub: no-op pushes, ignored viewers accumulator.
    private ChatHub BuildHub(string connectionId) =>
        NewHub(_assembler, ViewersAccumulatorTestFactory.CreateIgnored(), NoopClients(), MockContext(connectionId));

    // Viewers hub: shares the REAL _accumulator + _focusRegistry so ViewersChanged emissions are captured.
    private ChatHub BuildViewersHub(string connectionId) =>
        NewHub(_assembler, _accumulator, NoopClients(), MockContext(connectionId));

    // Zero-DB hub: its assembler is backed by the counting spy so a test can assert the hot path performs
    // zero mute-repository reads.
    private ChatHub BuildCountingHub(string connectionId, CountingMuteRepository countingRepo) =>
        NewHub(NewAssembler(countingRepo), ViewersAccumulatorTestFactory.CreateIgnored(), NoopClients(), MockContext(connectionId));

    // Connect hub: capturing clients + the real Context.GetHttpContext() ticket path + abort capture, so
    // the connect no-abort pin can assert on the ordered (target, method) send/abort stream.
    private ChatHub BuildConnectHub(string connectionId, string accessToken) =>
        BuildConnectHub(connectionId, accessToken, _assembler);

    // Connect hub variant whose assembler is backed by a caller-supplied instance — used by the zero-DB
    // proofs to connect through the REAL ceremony against a CountingMuteRepository-backed assembler, so
    // the sole live mute-repository read (the connect-time resolve) is proven wired before the hot path
    // is exercised.
    private ChatHub BuildConnectHub(string connectionId, string accessToken, SessionStateAssembler assembler)
    {
        var clients = new Mock<IHubCallerClients>();
        clients.Setup(c => c.Caller).Returns(CapturingSingle(connectionId));
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(CapturingGroup());
        clients.Setup(c => c.Client(It.IsAny<string>())).Returns<string>(CapturingSingle);

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        context.Setup(c => c.Features).Returns(BuildFeatures(accessToken));
        context.Setup(c => c.Abort()).Callback(() => _connectSends.Add((connectionId, "ABORT")));

        return NewHub(assembler, ViewersAccumulatorTestFactory.CreateIgnored(), clients.Object, context.Object);
    }

    private ISingleClientProxy CapturingSingle(string target)
    {
        var proxy = new Mock<ISingleClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, _, _) => _connectSends.Add((target, method)))
            .Returns(Task.CompletedTask);
        return proxy.Object;
    }

    private IClientProxy CapturingGroup()
    {
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, _, _) => _connectSends.Add(("group", method)))
            .Returns(Task.CompletedTask);
        return proxy.Object;
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

    // ── Seeding helpers ──────────────────────────────────────────────────────────

    private static W3CUserAuthentication Identity(string battleTag) =>
        new() { BattleTag = battleTag, Name = battleTag.Split('#')[0] };

    private void RegisterSession(string connectionId, string battleTag) =>
        _sessionRegistry.Register(connectionId, Identity(battleTag), null);

    // Seeds a connection the way the connect path does: a live session, the connection→user mapping, the
    // mute cache, and an OnlineMemberRegistry membership for the channel (the zero-DB "IS a member" signal).
    private void SeedMember(string connectionId, string battleTag, string channelId, MuteStatus mute = MuteStatus.None, DateTime? muteEnd = null)
    {
        RegisterSession(connectionId, battleTag);
        _connectionMapping.RegisterUser(connectionId, new ChatUser(battleTag, false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute(connectionId, mute, muteEnd ?? DateTime.MinValue);
        _onlineMemberRegistry.Join(channelId, connectionId, new MemberState(battleTag, NotificationLevel.Mentions, 0));
    }

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

    private Task AddFullBanToDb(string battleTag) =>
        _muteRepository.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = battleTag,
            endDate = Now.AddDays(1).ToString("O"),
            author = "admin#1",
            reason = "test ban",
            isShadowBan = false,
        });

    private Task AddExpiredBanToDb(string battleTag) =>
        _muteRepository.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = battleTag,
            endDate = Now.AddDays(-1).ToString("O"),
            author = "admin#1",
            reason = "old ban",
            isShadowBan = false,
        });

    private IReadOnlyList<ViewersChangedDto> ViewersChangedFor(string connectionId) =>
        _pushHarness.SignalsFor(connectionId)
            .Where(s => s.Method == ChatEvents.ViewersChanged)
            .Select(s => (ViewersChangedDto)s.Payload)
            .ToList();

    private static bool ContainsTag(IEnumerable<string> tags, string battleTag) =>
        tags.Any(t => string.Equals(t, battleTag, StringComparison.OrdinalIgnoreCase));

    // ── Connect: full ban never aborts (G1) ──────────────────────────────────────

    [Test]
    // LEGACY: ChatBanRoomScopeTests.Login_FullBan_DoesNotAbortConnection @778aec9
    public async Task Connect_FullBan_NeverAborts()
    {
        await AddFullBanToDb(BattleTag);
        // TICKET CLOCK — deliberately the REAL wall clock, NOT the FakeTimeProvider: OnConnectedAsync
        // consumes the ticket against DateTime.UtcNow (ChatHub.cs:89 — a documented seam decoupled from
        // the injected TimeProvider, since the REST mint side has no access to this hub's clock), and
        // TicketStore only expires on `consume_now > mint_now + TicketTtl` with NO lower bound. Minting
        // with a FIXED fake time (e.g. FixedNow) would therefore make this pass only while real UTC stays
        // below that fixed instant + 60s and turn into a wall-clock time-bomb; matching mint to the same
        // real clock the consume uses is what keeps it deterministic. (Mute EXPIRY below rides the fake
        // clock — those are two different clocks, by design.)
        var ticket = _ticketStore.Mint(Identity(BattleTag), DateTime.UtcNow);
        var hub = BuildConnectHub("conn-ban", ticket);

        await hub.OnConnectedAsync();

        Assert.IsFalse(_connectSends.Contains(("conn-ban", "ABORT")),
            "A full-ban connect must NEVER call Context.Abort() — bans never abort (only failed ticket auth / displacement do)");
        Assert.IsTrue(_connectSends.Contains(("conn-ban", ChatEvents.SessionState)),
            "A full-banned user still receives its SessionState snapshot on connect");
        Assert.IsTrue(_connectSends.Contains(("conn-ban", ChatEvents.PlayerBannedFromChat)),
            "A full ban still pushes the legacy PlayerBannedFromChat notice (endDate only) to the caller");
    }

    // ── Join gating (the old SwitchRoom full-ban room-scope rule) ─────────────────

    [Test]
    // LEGACY: ChatBanRoomScopeTests.SwitchRoom_FullBan_IntoClanRoom_IsAllowed @778aec9
    public async Task JoinChannel_FullBanned_SemiPublic_IsAllowed()
    {
        // The old exempt clan/lobby room maps to the sole name-joinable exempt type: SemiPublic.
        var channel = await CreateChannel("clan-haven", ChannelType.SemiPublic);
        RegisterSession("conn-1", BattleTag);
        await AddFullBanToDb(BattleTag);
        _connectionMapping.SetMute("conn-1", MuteStatus.Full, Now.AddDays(1));
        var hub = BuildHub("conn-1");

        var result = await hub.JoinChannel("clan-haven");

        Assert.AreEqual(ChatResultCode.Ok, result.Code,
            "A full-banned user must be allowed into an exempt (SemiPublic) channel — only Public is gated");
        Assert.AreEqual(channel.Id, result.Channel.Id);
        Assert.IsNotNull(await _membershipRepository.Load(channel.Id, BattleTag),
            "The exempt join must persist a normal membership even for a full-banned user");
    }

    [Test]
    // LEGACY: ChatBanRoomScopeTests.SwitchRoom_FullBan_ExemptThenPublic_StillRejected @778aec9
    public async Task JoinChannel_FullBanned_AfterExemptJoin_PublicStillRejected()
    {
        var semi = await CreateChannel("clan-haven", ChannelType.SemiPublic);
        var pub = await CreateChannel("W3C Lounge", ChannelType.Public);
        RegisterSession("conn-1", BattleTag);
        // Cache-only enforcement: the ban lives ONLY in the cache (the DB has no ban), so if either hop
        // consulted the DB it would allow the public join — the surviving cached Full must reject it.
        _connectionMapping.SetMute("conn-1", MuteStatus.Full, Now.AddDays(1));
        var hub = BuildHub("conn-1");

        var exemptJoin = await hub.JoinChannel("clan-haven");
        Assert.AreEqual(ChatResultCode.Ok, exemptJoin.Code, "The first (exempt SemiPublic) hop is allowed");

        var publicJoin = await hub.JoinChannel("W3C Lounge");

        Assert.AreEqual(ChatResultCode.PermissionDenied, publicJoin.Code,
            "A public join after a prior exempt hop must STILL reject — an exempt hop must not downgrade the cached ban");
        Assert.IsNull(await _membershipRepository.Load(pub.Id, BattleTag), "No membership created in the rejected public channel");
        Assert.IsFalse(_aborts.Contains("conn-1"), "A rejected join must never call Context.Abort() (G1)");
    }

    [Test]
    // LEGACY: ChatBanRoomScopeTests.ExpiredFullBan_SwitchRoomToBannedRoom_IsAllowed @778aec9
    public async Task JoinChannel_ExpiredPersistedBan_PublicAllowed()
    {
        var pub = await CreateChannel("W3C Lounge", ChannelType.Public);
        RegisterSession("conn-1", BattleTag);
        await AddExpiredBanToDb(BattleTag);
        // Cache carries a Full status whose endDate is already in the past — CachedMute.EffectiveStatus
        // resolves it to None, so the public join is allowed (expiry honoured from the cache alone).
        _connectionMapping.SetMute("conn-1", MuteStatus.Full, Now.AddDays(-1));
        var hub = BuildHub("conn-1");

        var result = await hub.JoinChannel("W3C Lounge");

        Assert.AreEqual(ChatResultCode.Ok, result.Code, "An expired ban must not block joining a Public channel");
        Assert.IsNotNull(await _membershipRepository.Load(pub.Id, BattleTag), "The expired-ban user's public membership must persist");
    }

    [Test]
    // LEGACY: ChatBanRoomScopeTests.Compat_SwitchRoom_FullBanRejected_DoesNotAbort_KeepsCurrentRoom @778aec9
    public async Task JoinChannel_FullBanReject_NoAbort_KeepsExistingMemberships()
    {
        var existing = await CreateChannel("W3C Lounge", ChannelType.Public);
        var target = await CreateChannel("1 vs 1", ChannelType.Public);
        RegisterSession("conn-1", BattleTag);
        // The user is already a member of an existing public channel (e.g. joined before the ban landed).
        await _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = existing.Id,
            BattleTag = BattleTag,
            NotificationLevel = NotificationLevel.Mentions,
            JoinedAt = Now,
        });
        _connectionMapping.SetMute("conn-1", MuteStatus.Full, Now.AddDays(1));
        var hub = BuildHub("conn-1");

        var result = await hub.JoinChannel("1 vs 1");

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code, "A full-banned user is denied joining a NEW public channel");
        Assert.IsFalse(_aborts.Contains("conn-1"), "G1: a rejected join must NEVER call Context.Abort()");
        Assert.IsNull(await _membershipRepository.Load(target.Id, BattleTag), "No membership created in the rejected target");
        Assert.IsNotNull(await _membershipRepository.Load(existing.Id, BattleTag),
            "G2: the rejected join must leave the caller's EXISTING memberships intact (no corruption)");
    }

    [Test]
    // LEGACY: ChatBanRoomScopeTests.UnbannedUser_SwitchRoom_ToAllRoomTypes_Allowed @778aec9
    public async Task JoinChannel_UnbannedUser_AllNameJoinableTypes_Allowed()
    {
        await CreateChannel("W3C Lounge", ChannelType.Public);
        await CreateChannel("clan-haven", ChannelType.SemiPublic);
        RegisterSession("conn-1", BattleTag);
        _connectionMapping.SetMute("conn-1", MuteStatus.None, DateTime.MinValue);
        var hub = BuildHub("conn-1");

        var pub = await hub.JoinChannel("W3C Lounge");
        Assert.AreEqual(ChatResultCode.Ok, pub.Code, "Unbanned user must be allowed into a Public channel");

        var semi = await hub.JoinChannel("clan-haven");
        Assert.AreEqual(ChatResultCode.Ok, semi.Code, "Unbanned user must be allowed into a SemiPublic channel");

        var created = await hub.JoinChannel("fresh-lobby");
        Assert.AreEqual(ChatResultCode.Ok, created.Code, "Unbanned user may implicitly create + join a brand-new SemiPublic channel");
        Assert.AreEqual(ChannelType.SemiPublic, created.Channel.Type);
    }

    // ── Send mute-gate matrix (gate keys on ChannelType.Public ONLY) ──────────────

    [Test]
    // LEGACY: ChatBanRoomScopeTests.SendMessage_CachedBan_Expired_InBannedRoom_Broadcasts @778aec9
    public async Task Send_ExpiredCachedBan_PublicChannel_Broadcasts()
    {
        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        // Cache a full ban whose endDate is already in the past — resolves to None, so the send is accepted.
        SeedMember("conn-1", BattleTag, channel.Id, mute: MuteStatus.Full, muteEnd: Now.AddDays(-1));
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, "should broadcast after expiry");

        Assert.AreEqual(ChatResultCode.Ok, result.Code, "A cached ban with an expired endDate must be treated as no ban");
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.IsNotNull(persisted, "The message from an expired-ban user must persist");
        Assert.IsFalse(persisted.Shadow, "An expired full ban is not a shadow flag");
    }

    [Test]
    // LEGACY: ChatBanRoomScopeTests.ExpiredShadowBan_SendMessage_CachedExpiredEndDate_Broadcasts @778aec9
    public async Task Send_ExpiredShadowBan_PersistsUnflagged()
    {
        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        SeedMember("conn-1", BattleTag, channel.Id, mute: MuteStatus.Shadow, muteEnd: Now.AddDays(-1));
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, "should broadcast now");

        Assert.AreEqual(ChatResultCode.Ok, result.Code, "An expired shadow ban must not gate the send");
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.IsNotNull(persisted);
        Assert.IsFalse(persisted.Shadow, "An expired shadow ban must persist UNFLAGGED (resolved to None), not shadow-flagged");
    }

    [TestCase(ChannelType.Public)]
    [TestCase(ChannelType.SemiPublic)]
    [TestCase(ChannelType.System)]
    [TestCase(ChannelType.Dm)]
    [TestCase(ChannelType.GroupDm)]
    // LEGACY: ChatBanRoomScopeTests.SendMessage_UnbannedUser_InAllRoomTypes_Broadcasts @778aec9
    public async Task Send_UnbannedUser_AllChannelTypes_Ok(ChannelType type)
    {
        var channel = await CreateChannel($"chan-{type}", type, type == ChannelType.System ? SystemChannelKind.Match : null);
        SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, "hello");

        Assert.AreEqual(ChatResultCode.Ok, result.Code, $"An unbanned user must be able to send in a {type} channel");
        Assert.IsNotNull(await _messageRepository.Load(result.MessageId), $"The {type} message must persist");
    }

    [TestCase(ChannelType.Public, ChatResultCode.Muted)]
    [TestCase(ChannelType.SemiPublic, ChatResultCode.Ok)]
    [TestCase(ChannelType.System, ChatResultCode.Ok)]
    [TestCase(ChannelType.Dm, ChatResultCode.Ok)]
    [TestCase(ChannelType.GroupDm, ChatResultCode.Ok)]
    // LEGACY: ChatBanRoomScopeTests.IsPublicRoom_ClassifiesCorrectly (§16 string matrix → channel-TYPE gate matrix) @778aec9
    public async Task Send_FullMuted_TypeMatrix_OnlyPublicGated(ChannelType type, ChatResultCode expected)
    {
        var channel = await CreateChannel($"chan-{type}", type, type == ChannelType.System ? SystemChannelKind.Match : null);
        SeedMember("conn-1", BattleTag, channel.Id, mute: MuteStatus.Full, muteEnd: Now.AddDays(1));
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, "let me talk");

        Assert.AreEqual(expected, result.Code,
            $"The mute gate must key on ChannelType.Public ONLY — a full mute in a {type} channel must be {expected}");
        var reloaded = await _channelRepository.Load(channel.Id);
        if (expected == ChatResultCode.Muted)
        {
            Assert.AreEqual(0L, reloaded.LastSeq, "A muted send in a Public channel must not persist");
        }
        else
        {
            Assert.AreEqual(1L, reloaded.LastSeq, $"A full-muted send in an exempt {type} channel must still persist (mute exemption)");
        }
    }

    // ── Zero-DB proofs (CountingMuteRepository) ───────────────────────────────────

    // FALSIFIABILITY (legacy `count==1 after login, still 1 after send/join` shape): each zero-DB proof
    // CONNECTS through the real OnConnectedAsync ceremony against a CountingMuteRepository-backed
    // assembler — the sole live mute-repository read — asserts the spy fired exactly once (so it is
    // proven wired), THEN exercises the send/join hot path and asserts the count is UNCHANGED. If the hot
    // path ever started reading the repo the count would climb to 2 and the assertion would FAIL — which
    // is exactly the guarantee a direct cache-seed + `count==0` proof cannot make (its spy never fires).

    [Test]
    // LEGACY: ChatBanRoomScopeTests.SendMessage_UnmutedUser_InPublicRoom_MakesZeroMuteRepositoryCalls @778aec9
    public async Task Send_UnmutedUser_PublicChannel_ZeroMuteRepositoryCalls()
    {
        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        // Persist a membership so the connect ceremony seeds this user as an online member of the channel
        // (the send's zero-DB "IS a member" signal), reached from the same LoadForUser the assembler runs.
        await _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = channel.Id,
            BattleTag = BattleTag,
            NotificationLevel = NotificationLevel.Mentions,
            JoinedAt = Now,
        });
        var countingRepo = new CountingMuteRepository(MongoClient);
        // Real wall clock for the mint — see Connect_FullBan_NeverAborts: the ticket is consumed against
        // DateTime.UtcNow (ChatHub's deliberate seam), so mint MUST use the same real clock, not FixedNow.
        var ticket = _ticketStore.Mint(Identity(BattleTag), DateTime.UtcNow);
        var hub = BuildConnectHub("conn-1", ticket, NewAssembler(countingRepo));

        await hub.OnConnectedAsync();
        Assert.AreEqual(1, countingRepo.GetMutedPlayerCallCount,
            "The connect ceremony performs exactly ONE mute-repository read — the spy is live and wired through the assembler");

        var result = await hub.SendMessage(channel.Id, "hello world");

        Assert.AreEqual(ChatResultCode.Ok, result.Code, "An unmuted member's public send is accepted");
        Assert.AreEqual(1, countingRepo.GetMutedPlayerCallCount,
            "SendMessage must make ZERO FURTHER mute-repository reads on the hot path (cache-only enforcement)");
    }

    [Test]
    // LEGACY: ChatBanRoomScopeTests.SendMessage_FullBan_NoClanLogin_EnforcesWithZeroMuteRepositoryCalls @778aec9
    public async Task Send_FullMuted_EnforcedWithZeroMuteRepositoryCalls()
    {
        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        // Member of the public channel from before the ban landed (kept on a full-ban connect), so the
        // send reaches the mute gate. The DB full ban makes the connect resolve Full and seed the cache.
        await _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = channel.Id,
            BattleTag = BattleTag,
            NotificationLevel = NotificationLevel.Mentions,
            JoinedAt = Now,
        });
        await AddFullBanToDb(BattleTag);
        var countingRepo = new CountingMuteRepository(MongoClient);
        var ticket = _ticketStore.Mint(Identity(BattleTag), DateTime.UtcNow);
        var hub = BuildConnectHub("conn-1", ticket, NewAssembler(countingRepo));

        await hub.OnConnectedAsync();
        Assert.AreEqual(1, countingRepo.GetMutedPlayerCallCount,
            "The connect ceremony resolves the ban with exactly ONE mute-repository read — the spy is live and wired");

        var result = await hub.SendMessage(channel.Id, "should be rejected");

        Assert.AreEqual(ChatResultCode.Muted, result.Code, "A cached full ban rejects the public send");
        Assert.AreEqual(1, countingRepo.GetMutedPlayerCallCount,
            "The full-mute enforcement must read the per-connection cache ONLY — zero FURTHER mute-repository reads");
    }

    [Test]
    // LEGACY: ChatBanRoomScopeTests.SwitchRoom_UnmutedUser_MakesZeroMuteRepositoryCalls @778aec9
    public async Task JoinChannel_MuteGate_ZeroMuteRepositoryCalls()
    {
        await CreateChannel("W3C Lounge", ChannelType.Public);
        var countingRepo = new CountingMuteRepository(MongoClient);
        var ticket = _ticketStore.Mint(Identity(BattleTag), DateTime.UtcNow);
        var hub = BuildConnectHub("conn-1", ticket, NewAssembler(countingRepo));

        await hub.OnConnectedAsync();
        Assert.AreEqual(1, countingRepo.GetMutedPlayerCallCount,
            "The connect ceremony performs exactly ONE mute-repository read — the spy is live and wired through the assembler");

        var result = await hub.JoinChannel("W3C Lounge");

        Assert.AreEqual(ChatResultCode.Ok, result.Code, "An unmuted user joins the public channel");
        Assert.AreEqual(1, countingRepo.GetMutedPlayerCallCount,
            "The JoinChannel full-ban gate must read the per-connection cache ONLY — zero FURTHER mute-repository reads");
    }

    [Test]
    // LEGACY: ChatBanRoomScopeTests.SendMessage_CachedNonNone_ActiveBan_InBannedRoom_NoRepositoryCall @778aec9
    public async Task Send_ActiveCachedBan_RejectedWithEmptyMuteCollection()
    {
        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        // Active full ban in the cache, but the DB mute collection is EMPTY (IntegrationTestBase drops it).
        SeedMember("conn-1", BattleTag, channel.Id, mute: MuteStatus.Full, muteEnd: Now.AddDays(1));
        var countingRepo = new CountingMuteRepository(MongoClient);
        var hub = BuildCountingHub("conn-1", countingRepo);

        var result = await hub.SendMessage(channel.Id, "hello lounge");

        Assert.AreEqual(ChatResultCode.Muted, result.Code,
            "An active cached full ban must reject the send — a DB read would have found nothing and allowed it");
        Assert.AreEqual(0, countingRepo.GetMutedPlayerCallCount, "The rejection came from the cache alone — zero mute-repository reads");
        Assert.IsEmpty(await _muteRepository.GetLoungeMutes(),
            "Proof: the DB mute collection is EMPTY, so the ban decision could ONLY have come from the cache");
    }

    // ── Shadow visibility (no presence-hiding; only public messages drop) ─────────

    [Test]
    // LEGACY: ChatBanRoomScopeTests.SwitchRoom_ShadowBan_CallerReceivesStartChat_SeesAllMembers @778aec9
    public async Task FocusChannel_ShadowBannedViewer_AppearsInRosterForAll()
    {
        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        SeedMember("conn-shadow", BattleTag, channel.Id, mute: MuteStatus.Shadow, muteEnd: Now.AddDays(1));
        SeedMember("conn-normal", OtherTag, channel.Id);

        var shadowHub = BuildHub("conn-shadow");
        await shadowHub.FocusChannel(channel.Id);

        var normalHub = BuildHub("conn-normal");
        var result = await normalHub.FocusChannel(channel.Id);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.That(result.Viewers.Select(v => v.BattleTag), Is.EquivalentTo(new[] { BattleTag, OtherTag }),
            "A shadow-banned viewer must be fully visible in every other viewer's roster (no presence-hiding)");
    }

    [Test]
    // LEGACY: ChatBanRoomScopeTests.SwitchRoom_ShadowBan_IntoPublicRoom_BroadcastsUserEntered / _BroadcastsUserLeftOnOldRoom @778aec9
    public async Task ShadowBanned_FocusUnfocus_EmitsNormalViewersChanged()
    {
        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        SeedMember("conn-obs", OtherTag, channel.Id);
        SeedMember("conn-shadow", BattleTag, channel.Id, mute: MuteStatus.Shadow, muteEnd: Now.AddDays(1));

        var obsHub = BuildViewersHub("conn-obs");   // a stable observer, so any delta has a recipient
        var shadowHub = BuildViewersHub("conn-shadow");

        await obsHub.FocusChannel(channel.Id);
        await shadowHub.FocusChannel(channel.Id);
        await _accumulator.FlushDue(Now + Flush);

        Assert.IsTrue(ViewersChangedFor("conn-obs").Any(b => ContainsTag(b.Joined, BattleTag)),
            "A shadow-banned viewer's focus MUST emit a normal ViewersChanged join (no presence-hiding)");

        _time.SetUtcNow(FixedNow.AddSeconds(6));
        await shadowHub.UnfocusChannel(channel.Id);
        await _accumulator.FlushDue(Now + Flush);

        Assert.IsTrue(ContainsTag(ViewersChangedFor("conn-obs").Last().Left, BattleTag),
            "A shadow-banned viewer's unfocus MUST emit a normal ViewersChanged left (no presence-hiding)");
    }

    [Test]
    // LEGACY: ChatBanRoomScopeTests.OnDisconnectedAsync_ShadowBan_InPublicRoom_BroadcastsUserLeft @778aec9
    public async Task ShadowBanned_Disconnect_EmitsNormalViewersChangedLeft()
    {
        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        SeedMember("conn-obs", OtherTag, channel.Id);
        SeedMember("conn-shadow", BattleTag, channel.Id, mute: MuteStatus.Shadow, muteEnd: Now.AddDays(1));

        var obsHub = BuildViewersHub("conn-obs");
        var shadowHub = BuildViewersHub("conn-shadow");
        await obsHub.FocusChannel(channel.Id);
        await shadowHub.FocusChannel(channel.Id);
        await _accumulator.FlushDue(Now + Flush);

        _time.SetUtcNow(FixedNow.AddSeconds(6));
        await shadowHub.OnDisconnectedAsync(null);
        await _accumulator.FlushDue(Now + Flush);

        Assert.IsTrue(ContainsTag(ViewersChangedFor("conn-obs").Last().Left, BattleTag),
            "A shadow-banned focused viewer's disconnect MUST emit a normal ViewersChanged left (no presence-hiding)");
    }

    [Test]
    // LEGACY: ChatBanRoomScopeTests.SwitchRoom_ShadowBan_IntoPublicRoom_UserIsMovedIntoGroup / _CallerReceivesStartChat_FullMemberList @778aec9
    public async Task ShadowBanned_JoinChannel_CreatesNormalMembership_VisibleInOthersSessionState()
    {
        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        // Shadow is NOT gated on join — only Full is. The shadow user joins the public channel normally.
        RegisterSession("conn-shadow", BattleTag);
        _connectionMapping.RegisterUser("conn-shadow", new ChatUser(BattleTag, false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("conn-shadow", MuteStatus.Shadow, Now.AddDays(1));
        var shadowHub = BuildHub("conn-shadow");

        var join = await shadowHub.JoinChannel("W3C Lounge");

        Assert.AreEqual(ChatResultCode.Ok, join.Code, "A shadow-banned user joins a Public channel normally — only Full is gated");
        var membership = await _membershipRepository.Load(channel.Id, BattleTag);
        Assert.IsNotNull(membership, "The shadow user's membership must persist like any normal member's (no membership-level hiding)");
        Assert.AreEqual(NotificationLevel.Mentions, membership.NotificationLevel);
        Assert.IsTrue(_onlineMemberRegistry.IsMember("conn-shadow", channel.Id), "The shadow user is a first-class online member");

        // Visible to others: once both focus, another member sees the shadow user in the shared roster.
        SeedMember("conn-other", OtherTag, channel.Id);
        await shadowHub.FocusChannel(channel.Id);
        var otherFocus = await BuildHub("conn-other").FocusChannel(channel.Id);
        Assert.That(otherFocus.Viewers.Select(v => v.BattleTag), Is.EquivalentTo(new[] { BattleTag, OtherTag }),
            "Another member sees the shadow user in the shared viewer roster (visible to others, no presence-hiding)");
    }
}

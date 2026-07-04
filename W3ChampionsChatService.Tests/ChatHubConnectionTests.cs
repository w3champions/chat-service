using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Relationships;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C2 connect-path tests: the SignalR hub now authenticates via a one-time TICKET carried in the
/// standard <c>access_token</c> query param (hard cutover — a raw JWT is no longer accepted). Covers
/// valid connect + single-use consumption, all rejection shapes (raw JWT / reused ticket / missing
/// ticket → <c>AuthorizationFailed</c> + <c>Context.Abort()</c>), battleTag displacement
/// (<c>ConnectionDisplaced</c> BEFORE close — acceptance 4), the displaced-old-socket disconnect race,
/// and the directory stub upsert. Drives the REAL <c>Context.GetHttpContext()</c> resolution path via
/// an <see cref="IHttpContextFeature"/> on the connection's feature collection (never
/// <c>IHttpContextAccessor</c>).
/// </summary>
public class ChatHubConnectionTests : IntegrationTestBase
{
    private const string BattleTag = "peter#123";

    private TicketStore _ticketStore;
    private SessionRegistry _sessionRegistry;
    private ConnectionMapping _connectionMapping;
    private UserDirectoryRepository _userDirectory;
    private MuteRepository _muteRepository;
    private MuteReconciliationService _reconcileService;
    private Mock<IChatAuthenticationService> _authService;

    // C3 (Task 8) hub deps: the SessionState assembler + the in-memory fan-out registries the connect
    // path seeds and the disconnect path tears down. The registries are SHARED across every hub built
    // in a test so multi-connection (displacement/reconnect) and disconnect-teardown assertions see
    // the same state. The assembler shares the SAME _onlineMemberRegistry instance it is asked to seed.
    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private FocusRegistry _focusRegistry;
    private OnlineMemberRegistry _onlineMemberRegistry;
    private MessageRateLimiter _messageRateLimiter;
    private ChannelCreationRateLimiter _channelCreationRateLimiter;
    private SessionStateAssembler _assembler;

    // C5 (Task 1): the connect-time relationship prefetch dep. A REAL provider over a fake source so the
    // fire-and-forget prefetch's call (and its non-fatal handling) is observable end-to-end.
    private FakeRelationshipSource _relationshipSource;
    private IRelationshipProvider _relationshipProvider;

    // Shared, ORDERED capture of every per-target signal AND every abort, across all connections, so
    // the event-before-close ordering (acceptance 4) is asserted on ONE deterministic sequence:
    // Clients.Client(id)/Caller sends append (id, method); each Context.Abort() appends (id, "ABORT").
    private readonly List<(string Target, string Method)> _sends = new();

    [SetUp]
    public void SetupBeforeEach()
    {
        _ticketStore = new TicketStore();
        _sessionRegistry = new SessionRegistry();
        _connectionMapping = new ConnectionMapping();
        _userDirectory = new UserDirectoryRepository(MongoClient);
        _muteRepository = new MuteRepository(MongoClient);
        _reconcileService = new MuteReconciliationTestHarness(_connectionMapping, _muteRepository).Service;
        _sends.Clear();

        _authService = new Mock<IChatAuthenticationService>();
        // D9: default happy-path resolution — FreshFromWb: true (a normal successful wb round-trip).
        // Individual tests below override this to simulate a wb outage (FreshFromWb: false).
        _authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync((W3CUserAuthentication id) =>
                new ChatUserResolution(new ChatUser(id.BattleTag, id.IsAdmin, null, new ProfilePicture(), null, null), true));

        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _focusRegistry = new FocusRegistry();
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _messageRateLimiter = new MessageRateLimiter();
        _channelCreationRateLimiter = new ChannelCreationRateLimiter();
        _relationshipSource = new FakeRelationshipSource();
        _relationshipProvider = new RelationshipProvider(_relationshipSource, TimeProvider.System);
        _assembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            new W3ChampionsChatService.Messages.MessageRepository(MongoClient),
            _muteRepository,
            _onlineMemberRegistry,
            _connectionMapping);
    }

    private static W3CUserAuthentication Identity(string battleTag = BattleTag, string name = "peter", bool isAdmin = false) =>
        new() { BattleTag = battleTag, Name = name, IsAdmin = isAdmin };

    private (ChatHub Hub, Mock<HubCallerContext> Context) BuildConnection(string connectionId, string accessToken)
    {
        var hub = new ChatHub(
            _connectionMapping,
            _reconcileService,
            _ticketStore,
            _sessionRegistry,
            _userDirectory,
            _assembler,
            _focusRegistry,
            _onlineMemberRegistry,
            _messageRateLimiter,
            TimeProvider.System,
            _channelRepository,
            _membershipRepository,
            _channelCreationRateLimiter,
            new W3ChampionsChatService.Messages.MessageRepository(MongoClient),
            FanOutEngineTestFactory.CreateIgnored(),
            ViewersAccumulatorTestFactory.CreateIgnored(),
            new NoOpMentionInboxCleaner(),
            _relationshipProvider,
            new UserSettingsRepository(MongoClient),
            new DmInitiationTracker(),
            _authService.Object);

        var clients = new Mock<IHubCallerClients>();
        clients.Setup(c => c.Caller).Returns(CapturingSingle(connectionId));
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(CapturingGroup());
        clients.Setup(c => c.Client(It.IsAny<string>())).Returns<string>(CapturingSingle);
        hub.Clients = clients.Object;

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        context.Setup(c => c.Features).Returns(BuildFeatures(accessToken));
        context.Setup(c => c.Abort()).Callback(() => _sends.Add((connectionId, "ABORT")));
        hub.Context = context.Object;

        hub.Groups = new Mock<IGroupManager>().Object;

        return (hub, context);
    }

    // Real Context.GetHttpContext() path: SignalR reads the connection's IHttpContextFeature, NOT the
    // injected IHttpContextAccessor (null for hub invocations over WebSockets). Mirrors
    // ChatHubPermissionFilterTests.BuildContext.
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
            .Callback<string, object[], CancellationToken>((method, _, _) => _sends.Add((target, method)))
            .Returns(Task.CompletedTask);
        return proxy.Object;
    }

    private IClientProxy CapturingGroup()
    {
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, _, _) => _sends.Add(("group", method)))
            .Returns(Task.CompletedTask);
        return proxy.Object;
    }

    private static (string jwt, string publicKeyPem) CreateSignedJwt(
        string battleTag, bool isAdmin, IEnumerable<string> permissions, DateTime? expires = null)
    {
        using var rsa = RSA.Create(2048);
        var publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();

        var signingCredentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };

        var token = new JwtSecurityToken(
            claims: new[]
            {
                new Claim("battleTag", battleTag),
                new Claim("isAdmin", isAdmin.ToString()),
                new Claim("name", battleTag.Split('#')[0]),
                new Claim("permissions", JsonSerializer.Serialize(permissions.ToList()), JsonClaimValueTypes.JsonArray),
            },
            signingCredentials: signingCredentials,
            expires: expires ?? DateTime.UtcNow.AddDays(7));

        return (new JwtSecurityTokenHandler().WriteToken(token), publicKeyPem);
    }

    [Test]
    public async Task ValidTicket_Connects_RegistersSession_AndConsumesTicketOnce()
    {
        var ticket = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var (hub, _) = BuildConnection("conn-1", ticket);

        await hub.OnConnectedAsync();

        Assert.IsTrue(_sessionRegistry.TryGetByConnectionId("conn-1", out var session),
            "A valid ticket must register the session under its connection id");
        Assert.AreEqual(BattleTag, session.Identity.BattleTag, "The registered identity is the ticket's snapshot");
        Assert.IsNotNull(_connectionMapping.GetUser("conn-1"),
            "The assembler seeds the legacy connection→user mapping (RegisterUser) so MuteReconciliationService can still reach this connection");
        Assert.IsFalse(_ticketStore.TryConsume(ticket, DateTime.UtcNow, out _),
            "The ticket must be single-use — it was already consumed at connect");
    }

    [Test]
    public async Task RawJwt_AsAccessToken_IsRejected()
    {
        // Acceptance 3: a client that mistakenly presents a raw (even cryptographically valid) JWT as
        // access_token is rejected — the hub consumes ONLY one-time tickets after the hard cutover.
        var (jwt, _) = CreateSignedJwt(BattleTag, isAdmin: false, new[] { "Moderation" });
        var (hub, _) = BuildConnection("conn-jwt", jwt);

        await hub.OnConnectedAsync();

        Assert.IsTrue(_sends.Contains(("conn-jwt", "AuthorizationFailed")),
            "A raw JWT is not a valid ticket — the caller must receive AuthorizationFailed");
        Assert.IsTrue(_sends.Contains(("conn-jwt", "ABORT")),
            "Connect-time auth failure is the one rejection-style abort");
        Assert.IsFalse(_sessionRegistry.TryGetByConnectionId("conn-jwt", out _),
            "A rejected connect must register no session");
    }

    [Test]
    public async Task ReusedTicket_SecondConnect_IsRejected()
    {
        // Acceptance 1 at the hub: the ticket is one-time. A second connect presenting the SAME ticket
        // is rejected even though the first succeeded.
        var ticket = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var (first, _) = BuildConnection("conn-a", ticket);
        await first.OnConnectedAsync();

        var (second, _) = BuildConnection("conn-b", ticket);
        await second.OnConnectedAsync();

        Assert.IsTrue(_sends.Contains(("conn-b", "AuthorizationFailed")),
            "A reused ticket must be rejected on the second connect");
        Assert.IsTrue(_sends.Contains(("conn-b", "ABORT")));
        Assert.IsFalse(_sessionRegistry.TryGetByConnectionId("conn-b", out _), "No session for the rejected connect");
        Assert.IsTrue(_sessionRegistry.TryGetByConnectionId("conn-a", out _), "The first connection stays live");
    }

    [Test]
    public async Task MissingTicket_IsRejected()
    {
        var (hub, _) = BuildConnection("conn-none", accessToken: null);

        await hub.OnConnectedAsync();

        Assert.IsTrue(_sends.Contains(("conn-none", "AuthorizationFailed")),
            "A connect with no access_token must be rejected");
        Assert.IsTrue(_sends.Contains(("conn-none", "ABORT")));
        Assert.IsFalse(_sessionRegistry.TryGetByConnectionId("conn-none", out _));
    }

    [Test]
    public async Task SecondConnection_SameBattleTag_DisplacesFirst_EventThenClose()
    {
        // Acceptance 4: a second connection for the same battleTag displaces the first — the OLD
        // connection receives ConnectionDisplaced and is THEN closed (event BEFORE abort); the NEW
        // connection is live. Both hubs share the SAME ticket store / registry / connection mapping.
        var ticketOld = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var (oldHub, _) = BuildConnection("conn-old", ticketOld);
        await oldHub.OnConnectedAsync();

        var ticketNew = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var (newHub, _) = BuildConnection("conn-new", ticketNew);
        await newHub.OnConnectedAsync();

        var displacedIdx = _sends.IndexOf(("conn-old", "ConnectionDisplaced"));
        var abortIdx = _sends.IndexOf(("conn-old", "ABORT"));
        Assert.AreNotEqual(-1, displacedIdx, "The OLD connection must receive ConnectionDisplaced");
        Assert.AreNotEqual(-1, abortIdx, "The OLD connection must then be closed");
        Assert.Less(displacedIdx, abortIdx, "ConnectionDisplaced (event) must be sent BEFORE the abort (close)");

        Assert.AreEqual("conn-new", _sessionRegistry.GetByBattleTag(BattleTag).ConnectionId,
            "The NEW connection is the live one for this battleTag");
        Assert.IsTrue(_sends.Contains(("conn-new", ChatEvents.SessionState)),
            "The NEW connection's caller receives its SessionState snapshot");
    }

    [Test]
    public async Task OldConnectionDisconnect_AfterDisplacement_LeavesNewSessionLive()
    {
        // Hub-level complement to the Task-5 registry race unit test: after displacement, the dying OLD
        // socket's OnDisconnectedAsync must NOT evict the NEW session (identity-checked teardown).
        var ticketOld = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var (oldHub, _) = BuildConnection("conn-old", ticketOld);
        await oldHub.OnConnectedAsync();

        var ticketNew = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var (newHub, _) = BuildConnection("conn-new", ticketNew);
        await newHub.OnConnectedAsync();

        await oldHub.OnDisconnectedAsync(null);

        var live = _sessionRegistry.GetByBattleTag(BattleTag);
        Assert.IsNotNull(live, "The battleTag must still resolve to a live session after the OLD disconnect");
        Assert.AreEqual("conn-new", live.ConnectionId,
            "The dying OLD socket must NOT evict the NEW session");
        Assert.IsNotNull(_connectionMapping.GetUser("conn-new"),
            "The NEW connection's seat must survive the OLD disconnect");
    }

    [Test]
    public async Task Connect_UpsertsDirectory_ProfileAndDisplayTagAndFullTagNormalizedName()
    {
        // D9: the FULL connect-time directory upsert — a fresh wb enrichment writes Profile,
        // DisplayBattleTag (original JWT casing), and NormalizedName (the lowercased FULL battleTag,
        // NOT just the name part — the C3 stub's defect this task fixes).
        const string mixedCaseTag = "Wolf#456";
        var enrichedUser = new ChatUser(mixedCaseTag, false, "clan-1", new ProfilePicture(), new ChatColor("chat_color_blue"), Array.Empty<ChatIcon>())
        {
            RankNumber = 7,
        };
        _authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync(new ChatUserResolution(enrichedUser, true));

        var ticket = _ticketStore.Mint(Identity(battleTag: mixedCaseTag, name: "Wolf"), DateTime.UtcNow);
        var (hub, _) = BuildConnection("conn-dir", ticket);

        await hub.OnConnectedAsync();

        var entry = await _userDirectory.Load(mixedCaseTag);
        Assert.IsNotNull(entry, "The directory entry must exist after connect");
        Assert.AreEqual(mixedCaseTag, entry.DisplayBattleTag, "DisplayBattleTag preserves the caller's original JWT casing");
        Assert.AreEqual("wolf#456", entry.NormalizedName, "NormalizedName must be the lowercased FULL battleTag, not just the name part");
        Assert.Less((DateTime.UtcNow - entry.LastSeenAt).Duration(), TimeSpan.FromSeconds(5),
            "LastSeenAt must be refreshed to now (±5s)");
        Assert.IsNotNull(entry.Profile, "A fresh wb enrichment (FreshFromWb: true) must write the Profile");
        Assert.AreEqual("clan-1", entry.Profile.ClanId);
        Assert.AreEqual(7, entry.Profile.RankNumber);
    }

    [Test]
    public async Task Connect_WbDown_DirectoryUpsertPreservesExistingProfile_UpdatesLastSeenAt()
    {
        // The never-clobber-cached-profile invariant: a wb outage at connect time must NEVER overwrite
        // a good, previously-cached Profile with nulls — LastSeenAt/DisplayBattleTag/NormalizedName
        // still refresh (the user IS connecting, that's still true), but Profile is left untouched.
        var existingProfile = new ChatProfile { ClanId = "clan-1", RankNumber = 7 };
        await _userDirectory.Upsert(new UserDirectoryEntry
        {
            BattleTag = BattleTag,
            DisplayBattleTag = BattleTag,
            NormalizedName = "stale",
            LastSeenAt = DateTime.UtcNow.AddDays(-30),
            Profile = existingProfile,
        });

        // Simulate a wb outage: the plain (non-fresh) fallback resolution.
        _authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync((W3CUserAuthentication id) =>
                new ChatUserResolution(new ChatUser(id.BattleTag, id.IsAdmin, null, new ProfilePicture(), null, null), false));

        var ticket = _ticketStore.Mint(Identity(battleTag: BattleTag, name: "Peter"), DateTime.UtcNow);
        var (hub, _) = BuildConnection("conn-dir-down", ticket);

        await hub.OnConnectedAsync();

        var entry = await _userDirectory.Load(BattleTag);
        Assert.IsNotNull(entry);
        Assert.AreEqual("peter#123", entry.NormalizedName, "NormalizedName still refreshes even on a wb outage — the user IS connecting");
        Assert.Less((DateTime.UtcNow - entry.LastSeenAt).Duration(), TimeSpan.FromSeconds(5),
            "LastSeenAt still refreshes on a wb outage");
        Assert.IsNotNull(entry.Profile, "the PRE-EXISTING cached Profile must survive a wb outage — never clobbered with nulls");
        Assert.AreEqual("clan-1", entry.Profile.ClanId);
        Assert.AreEqual(7, entry.Profile.RankNumber);
    }

    [Test]
    public async Task Disconnect_UpdatesLastSeenAt_PreservesProfile()
    {
        // Acceptance 6's lastSeenAt leg: the disconnect-time write (SetLastSeen, Task 2's partial
        // update) advances LastSeenAt but — by construction of SetLastSeen — never touches Profile.
        var ticket = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var (hub, _) = BuildConnection("conn-disc", ticket);
        await hub.OnConnectedAsync();

        // Simulate whatever Profile the directory cache holds by the time this connection disconnects
        // (e.g. from an earlier successful enrichment) — independent of what THIS connect wrote.
        var cachedProfile = new ChatProfile { ClanId = "clan-1", RankNumber = 7 };
        var seeded = await _userDirectory.Load(BattleTag);
        seeded.Profile = cachedProfile;
        await _userDirectory.Upsert(seeded);

        var beforeDisconnect = DateTime.UtcNow;
        await hub.OnDisconnectedAsync(null);

        var entry = await _userDirectory.Load(BattleTag);
        Assert.IsNotNull(entry);
        Assert.GreaterOrEqual(entry.LastSeenAt, beforeDisconnect.AddSeconds(-1),
            "LastSeenAt must advance to the disconnect time");
        Assert.IsNotNull(entry.Profile, "SetLastSeen (the disconnect-time write) must never clobber the cached Profile");
        Assert.AreEqual("clan-1", entry.Profile.ClanId);
        Assert.AreEqual(7, entry.Profile.RankNumber);
    }

    [Test]
    public async Task Disconnect_DisplacedOldSocket_DoesNotTouchDirectory()
    {
        // A displaced OLD socket's disconnect must NOT rewind LastSeenAt — the user is still online via
        // their NEW connection. TryGetByConnectionId is fail-closed for exactly this race (captured
        // BEFORE Unregister in OnDisconnectedAsync).
        var ticketOld = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var (oldHub, _) = BuildConnection("conn-old-dir", ticketOld);
        await oldHub.OnConnectedAsync();

        var ticketNew = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var (newHub, _) = BuildConnection("conn-new-dir", ticketNew);
        await newHub.OnConnectedAsync();

        // Stamp a known sentinel LastSeenAt (far from "now") so a rewind is unmistakable.
        var sentinelLastSeen = DateTime.UtcNow.AddHours(1);
        var afterSecondConnect = await _userDirectory.Load(BattleTag);
        afterSecondConnect.LastSeenAt = sentinelLastSeen;
        await _userDirectory.Upsert(afterSecondConnect);

        await oldHub.OnDisconnectedAsync(null);

        var entry = await _userDirectory.Load(BattleTag);
        Assert.Less((entry.LastSeenAt - sentinelLastSeen).Duration(), TimeSpan.FromMilliseconds(5),
            "a displaced OLD socket's disconnect must not touch the directory while the user is still online via the NEW connection");
    }

    [Test]
    public async Task SenderSnapshot_And_OwnProfile_UseSameMapper()
    {
        // D9: BuildSenderSnapshot (ChatHub.Messaging.cs, the per-message sender flair) and ToChatProfile
        // (SessionStateAssembler, OwnProfile.Flair) both delegate to the SAME ChatProfileMapper.FromChatUser
        // — this proves the two call sites can never drift on a fully-enriched user.
        var channel = new ChatChannel { Type = ChannelType.Public, Name = "General", NormalizedName = ChannelNames.Normalize("General") };
        await _channelRepository.Insert(channel);
        await _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = channel.Id,
            BattleTag = BattleTag,
            LastReadSeq = 0,
            NotificationLevel = NotificationLevel.All,
            Role = MembershipRole.Member,
            JoinedAt = DateTime.UtcNow,
        });

        var enrichedUser = new ChatUser(
            BattleTag, false, "W3C",
            new ProfilePicture { Race = AvatarCategory.HU, PictureId = 3, IsClassic = true },
            new ChatColor("chat_color_blue"),
            new[] { new ChatIcon("chat_icon_star") })
        {
            LeagueId = 3,
            LeagueName = "Diamond",
            LeagueOrder = 5,
            LeagueDivision = 2,
            RankNumber = 14,
            GameMode = 1,
            GateWay = 20,
            GamesPlayed = 42,
            Season = 22,
        };
        _authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync(new ChatUserResolution(enrichedUser, true));

        var ticket = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var (hub, _) = BuildConnection("conn-mapper", ticket);
        await hub.OnConnectedAsync();

        // Path 1: the per-message sender snapshot.
        var sendResult = await hub.SendMessage(channel.Id, "hello");
        Assert.AreEqual(ChatResultCode.Ok, sendResult.Code);
        var persisted = await new W3ChampionsChatService.Messages.MessageRepository(MongoClient).Load(sendResult.MessageId);

        // Path 2: OwnProfile.Flair, via the assembler directly (already seeded/shared in this fixture).
        var (dto, _) = await _assembler.AssembleAndSeed(Identity(), "conn-mapper-2", DateTime.UtcNow, enrichedUser);

        var expectedFlair = ChatProfileMapper.FromChatUser(enrichedUser);
        foreach (var flair in new[] { persisted.Sender.Flair, dto.OwnProfile.Flair })
        {
            Assert.AreEqual(expectedFlair.ClanId, flair.ClanId);
            Assert.AreEqual(expectedFlair.LeagueId, flair.LeagueId);
            Assert.AreEqual(expectedFlair.LeagueName, flair.LeagueName);
            Assert.AreEqual(expectedFlair.LeagueOrder, flair.LeagueOrder);
            Assert.AreEqual(expectedFlair.LeagueDivision, flair.LeagueDivision);
            Assert.AreEqual(expectedFlair.RankNumber, flair.RankNumber);
            Assert.AreEqual(expectedFlair.GameMode, flair.GameMode);
            Assert.AreEqual(expectedFlair.GateWay, flair.GateWay);
            Assert.AreEqual(expectedFlair.GamesPlayed, flair.GamesPlayed);
            Assert.AreEqual(expectedFlair.Season, flair.Season);
        }
    }

    [Test]
    public async Task ValidTicket_Connect_PushesSessionState_ToCallerOnly()
    {
        // Acceptance 8: on connect the hub pushes the SessionState snapshot to the CALLER only — it is
        // that connection's private state rebuild, never a room/group broadcast. (Replaces the legacy
        // StartChat push.)
        var ticket = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var (hub, _) = BuildConnection("conn-1", ticket);

        await hub.OnConnectedAsync();

        Assert.IsTrue(_sends.Contains(("conn-1", ChatEvents.SessionState)),
            "The caller must receive its SessionState snapshot on connect");
        Assert.IsFalse(_sends.Contains(("group", ChatEvents.SessionState)),
            "SessionState is caller-private — it must NEVER be broadcast to a group");
    }

    [Test]
    public async Task Connect_FullBan_SendsPlayerBannedFromChat_AndSessionState()
    {
        // A full-banned user still connects (bans never abort — C2/G1). The caller receives BOTH its
        // SessionState snapshot AND the legacy PlayerBannedFromChat notice (expiry only). The shadow
        // flag / reason never leave the boundary — the notice carries endDate alone.
        await _muteRepository.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = BattleTag,
            endDate = DateTime.UtcNow.AddDays(1).ToString("O"),
            author = "admin#1",
            reason = "test ban",
            isShadowBan = false,
        });

        var ticket = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var (hub, _) = BuildConnection("conn-ban", ticket);

        await hub.OnConnectedAsync();

        Assert.IsTrue(_sends.Contains(("conn-ban", ChatEvents.SessionState)),
            "A full-banned user still receives its SessionState snapshot");
        Assert.IsTrue(_sends.Contains(("conn-ban", ChatEvents.PlayerBannedFromChat)),
            "A full ban must also push the legacy PlayerBannedFromChat notice to the caller");
    }

    [Test]
    public async Task Reconnect_SecondConnect_GetsFreshSessionState()
    {
        // Acceptance 8 seed: every (re)connect rebuilds state from a FRESH SessionState snapshot. The
        // same battleTag reconnecting (a new connection displacing the old) gets its own snapshot.
        var ticketOld = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var (oldHub, _) = BuildConnection("conn-old", ticketOld);
        await oldHub.OnConnectedAsync();
        Assert.IsTrue(_sends.Contains(("conn-old", ChatEvents.SessionState)),
            "The first connect gets a SessionState snapshot");

        var ticketNew = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var (newHub, _) = BuildConnection("conn-new", ticketNew);
        await newHub.OnConnectedAsync();

        Assert.IsTrue(_sends.Contains(("conn-new", ChatEvents.SessionState)),
            "The reconnect gets its own FRESH SessionState snapshot");
    }

    [Test]
    public async Task Disconnect_RemovesRegistryState()
    {
        // On disconnect the hub tears down every in-memory fan-out registry entry for the connection so
        // nothing leaks past the socket's lifetime. FocusRegistry + MessageRateLimiter are populated by
        // later hub methods (Tasks 9/11) and OnlineMemberRegistry is connect-seeded only when the user
        // has channel-backed memberships (this fresh identity has none) — so seed all three directly to
        // prove OnDisconnectedAsync removes every one.
        var ticket = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var (hub, _) = BuildConnection("conn-1", ticket);
        await hub.OnConnectedAsync();

        _focusRegistry.Focus("conn-1", "chan-1", BattleTag);
        _onlineMemberRegistry.Join("chan-1", "conn-1", new MemberState(BattleTag, NotificationLevel.All, 0, ChannelType.Public));
        _messageRateLimiter.TryAcquire("conn-1", "chan-1", DateTime.UtcNow);

        await hub.OnDisconnectedAsync(null);

        Assert.IsEmpty(_focusRegistry.GetFocusedConnections("chan-1"),
            "FocusRegistry must hold no entry for the connection after disconnect");
        Assert.IsEmpty(_onlineMemberRegistry.GetMembers("chan-1"),
            "OnlineMemberRegistry must hold no entry for the connection after disconnect");
        Assert.AreEqual(0, _messageRateLimiter.TrackedChannelCount("conn-1"),
            "MessageRateLimiter must hold no bucket state for the connection after disconnect");
    }

    [Test]
    public async Task Connect_PrefetchesOwnSnapshot_NonFatalOnFailure()
    {
        // C5 (Task 1, spec §6): connect warms the relationship cache with the CONNECTING user's own
        // snapshot. It is fire-and-forget and NON-FATAL — even when the source throws, the connect still
        // succeeds (session registered + SessionState pushed), and the source is called exactly once with
        // the connecting battleTag.
        _relationshipSource.ShouldThrow = true;

        var ticket = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var (hub, _) = BuildConnection("conn-rel", ticket);

        await hub.OnConnectedAsync();

        Assert.IsTrue(_sessionRegistry.TryGetByConnectionId("conn-rel", out _),
            "a failing relationship prefetch must NOT fail the connect — the session is still registered");
        Assert.IsTrue(_sends.Contains(("conn-rel", ChatEvents.SessionState)),
            "the caller still receives its SessionState snapshot despite the prefetch failure");

        // The prefetch is fire-and-forget; await its completion signal (bounded) rather than racing it.
        var observed = await Task.WhenAny(_relationshipSource.FirstFetch, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.AreSame(_relationshipSource.FirstFetch, observed,
            "the connect-time prefetch must call the relationship source");
        Assert.AreEqual(BattleTag, await _relationshipSource.FirstFetch,
            "the prefetch must fetch the CONNECTING user's own snapshot");
        Assert.AreEqual(1, _relationshipSource.FetchCount, "connect prefetches exactly one snapshot");
    }

    [Test]
    public async Task Connect_RelationshipPrefetch_DoesNotBlockConnect()
    {
        // C5 (Task 1): the prefetch is fire-and-forget — a slow/unreachable wb read must NOT add latency
        // to (or stall) a connect. Hold the fetch open for the whole connect via an unreleased gate; if
        // OnConnectedAsync awaited the prefetch this would deadlock the test. It returns instead, with the
        // session registered and SessionState pushed, while the fetch is still in flight.
        var gate = new TaskCompletionSource();
        _relationshipSource.ReleaseGate = gate.Task;

        var ticket = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var (hub, _) = BuildConnection("conn-rel-nb", ticket);

        await hub.OnConnectedAsync();

        Assert.IsTrue(_sessionRegistry.TryGetByConnectionId("conn-rel-nb", out _),
            "the connect must complete without waiting on the relationship fetch");
        Assert.IsTrue(_sends.Contains(("conn-rel-nb", ChatEvents.SessionState)),
            "the caller receives its SessionState snapshot even while the prefetch is still in flight");
        Assert.IsTrue(_relationshipSource.FirstFetch.IsCompleted,
            "the prefetch was launched (the fetch started) even though the connect did not await it");

        gate.SetResult(); // release the background fetch so it can finish cleanly
    }
}

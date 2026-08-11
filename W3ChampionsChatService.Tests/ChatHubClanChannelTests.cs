using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Relationships;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Clan-channel membership tests (2026-08-09 clan-channel regression). The chat revamp (#33) rebuilt
/// channels as server-persisted membership rows but shipped no clan path at all, so the clan channel —
/// which the PRE-revamp launcher fabricated client-side as an ephemeral <c>clan &lt;tag&gt;</c> SignalR
/// group — silently vanished for every clan member.
/// <para>
/// These tests pin the replacement contract: the connect path reconciles a System+<see
/// cref="SystemChannelKind.Clan"/> channel keyed by the clan id ALREADY resolved from wb's
/// <c>clan-and-picture</c> (<see cref="ChatUser.ClanTag"/>), so the membership is durable and lands in
/// the SAME <c>SessionState</c> the client renders. Removal of a stale clan membership is gated on
/// <see cref="ChatUserResolution.FreshFromWb"/> — the NEVER-CLOBBER invariant already used by the
/// directory upsert: a wb outage resolves ClanTag to null, and that must never rip a user out of their
/// clan channel.
/// </para>
/// </summary>
public class ChatHubClanChannelTests : IntegrationTestBase
{
    private const string BattleTag = "peter#123";
    private const string ClanId = "EwOk";

    private TicketStore _ticketStore;
    private SessionRegistry _sessionRegistry;
    private ConnectionMapping _connectionMapping;
    private UserDirectoryRepository _userDirectory;
    private MuteRepository _muteRepository;
    private MuteReconciliationService _reconcileService;
    private Mock<IChatAuthenticationService> _authService;

    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private FocusRegistry _focusRegistry;
    private OnlineMemberRegistry _onlineMemberRegistry;
    private MessageRateLimiter _messageRateLimiter;
    private ReadRateLimiter _readRateLimiter;
    private ChannelCreationRateLimiter _channelCreationRateLimiter;
    private SessionStateAssembler _assembler;
    private FakeRelationshipSource _relationshipSource;
    private IRelationshipProvider _relationshipProvider;

    // Ordered capture of (target, method, payload) so a test can assert on the ACTUAL SessionState the
    // client would render, not merely that some event fired.
    private readonly List<(string Target, string Method, object[] Args)> _sends = new();

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
        SetupResolution(ClanId, freshFromWb: true);

        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _focusRegistry = new FocusRegistry();
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _messageRateLimiter = new MessageRateLimiter();
        _readRateLimiter = new ReadRateLimiter();
        _channelCreationRateLimiter = new ChannelCreationRateLimiter();
        _relationshipSource = new FakeRelationshipSource();
        _relationshipProvider = new RelationshipProvider(_relationshipSource, TimeProvider.System);
        _assembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            new W3ChampionsChatService.Messages.MessageRepository(MongoClient),
            _muteRepository,
            _onlineMemberRegistry,
            _connectionMapping,
            new MentionInboxRepository(MongoClient));
    }

    /// <summary>Points the flair resolver at a given clan id / freshness tier.</summary>
    private void SetupResolution(string clanId, bool freshFromWb)
    {
        _authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync((W3CUserAuthentication id) =>
                new ChatUserResolution(
                    new ChatUser(id.BattleTag, id.IsAdmin, clanId, new ProfilePicture(), null, null),
                    freshFromWb));
    }

    private static W3CUserAuthentication Identity(string battleTag = BattleTag, string name = "peter") =>
        new() { BattleTag = battleTag, Name = name, IsAdmin = false };

    private ChatHub BuildConnection(string connectionId, string accessToken)
    {
        var viewerResolver = new ViewerResolver(_sessionRegistry, _connectionMapping);
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
            _readRateLimiter,
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
            _authService.Object,
            MentionFanOutTestFactory.CreateIgnored(MongoClient),
            new PresenceInterestRegistry(),
            new MentionInboxRepository(MongoClient),
            new NotificationPreferenceRepository(MongoClient),
            viewerResolver);

        var clients = new Mock<IHubCallerClients>();
        clients.Setup(c => c.Caller).Returns(CapturingSingle(connectionId));
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(CapturingGroup());
        clients.Setup(c => c.Client(It.IsAny<string>())).Returns<string>(CapturingSingle);
        hub.Clients = clients.Object;

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        context.Setup(c => c.Features).Returns(BuildFeatures(accessToken));
        hub.Context = context.Object;
        hub.Groups = new Mock<IGroupManager>().Object;

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
            .Callback<string, object[], CancellationToken>((method, args, _) => _sends.Add((target, method, args)))
            .Returns(Task.CompletedTask);
        return proxy.Object;
    }

    private IClientProxy CapturingGroup()
    {
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) => _sends.Add(("group", method, args)))
            .Returns(Task.CompletedTask);
        return proxy.Object;
    }

    /// <summary>The SessionState pushed to the caller — the exact snapshot the launcher renders from.</summary>
    private SessionStateDto CapturedSessionState() =>
        (SessionStateDto)_sends.Single(s => s.Method == ChatEvents.SessionState).Args[0];

    private async Task<ChatHub> Connect(string connectionId = "conn-1", string battleTag = BattleTag)
    {
        var ticket = _ticketStore.Mint(Identity(battleTag), DateTime.UtcNow);
        var hub = BuildConnection(connectionId, ticket);
        await hub.OnConnectedAsync();
        return hub;
    }

    // ---------------------------------------------------------------------------------------------
    // Auto-join
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Connect_WithClan_CreatesClanChannelAndJoinsIt()
    {
        await Connect();

        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Clan, ClanId);
        Assert.IsNotNull(channel, "A clan member's connect must find-or-create the System+Clan channel shell");
        Assert.AreEqual(ChannelType.System, channel.Type);
        Assert.AreEqual(ClanId, channel.SystemRef, "SystemRef is the clan id straight from wb's clan-and-picture");
        Assert.IsNull(channel.ExpiresAt, "A clan shell is permanent — ExpiresAt must stay ABSENT (ExpiryCalculator)");

        var membership = await _membershipRepository.Load(channel.Id, BattleTag);
        Assert.IsNotNull(membership, "The connecting clan member must be auto-joined to their clan channel");
    }

    [Test]
    public async Task Connect_WithClan_ClanChannelIsInTheSessionStateSnapshot()
    {
        await Connect();

        var state = CapturedSessionState();
        var clanEntry = state.Channels.SingleOrDefault(c =>
            c.Channel.Type == ChannelType.System && c.Channel.SystemKind == SystemChannelKind.Clan);

        Assert.IsNotNull(clanEntry,
            "Reconciliation must run BEFORE AssembleAndSeed — otherwise the membership exists but the "
            + "client never sees it until the NEXT connect, which is the user-visible bug.");
        Assert.AreEqual(ClanId, clanEntry.Channel.SystemRef);
    }

    [Test]
    public async Task Connect_WithoutClan_CreatesNoClanChannel()
    {
        SetupResolution(clanId: null, freshFromWb: true);

        await Connect();

        var state = CapturedSessionState();
        Assert.IsFalse(
            state.Channels.Any(c => c.Channel.SystemKind == SystemChannelKind.Clan),
            "A clanless user must get no clan channel");
    }

    [Test]
    public async Task Connect_Twice_IsIdempotent_OneChannelOneMembership()
    {
        await Connect("conn-1");
        _sends.Clear();
        await Connect("conn-2");

        var channels = await _channelRepository.LoadAllOfType(ChannelType.System);
        Assert.AreEqual(1, channels.Count(c => c.SystemKind == SystemChannelKind.Clan),
            "Reconnecting must not create a second clan channel shell");

        var memberships = await _membershipRepository.LoadForUser(BattleTag);
        Assert.AreEqual(1, memberships.Count, "Reconnecting must not duplicate the clan membership");
    }

    // ---------------------------------------------------------------------------------------------
    // Reconciliation on clan change
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Connect_AfterClanChange_DropsStaleClanMembershipAndJoinsTheNewOne()
    {
        await Connect("conn-1");
        var oldChannel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Clan, ClanId);

        SetupResolution("s4s", freshFromWb: true);
        _sends.Clear();
        await Connect("conn-2");

        Assert.IsNull(await _membershipRepository.Load(oldChannel.Id, BattleTag),
            "Switching clans must drop the membership in the previous clan's channel");

        var newChannel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Clan, "s4s");
        Assert.IsNotNull(await _membershipRepository.Load(newChannel.Id, BattleTag),
            "Switching clans must join the new clan's channel");
    }

    [Test]
    public async Task Connect_AfterLeavingClan_DropsTheClanMembership()
    {
        await Connect("conn-1");
        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Clan, ClanId);

        SetupResolution(clanId: null, freshFromWb: true);
        _sends.Clear();
        await Connect("conn-2");

        Assert.IsNull(await _membershipRepository.Load(channel.Id, BattleTag),
            "A FRESH wb read saying the user has no clan is authoritative — the membership must go");
    }

    [Test]
    public async Task Connect_DuringWbOutage_NeverRemovesAnExistingClanMembership()
    {
        await Connect("conn-1");
        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Clan, ClanId);

        // A total wb + directory-cache miss resolves ClanTag to null with FreshFromWb: false
        // (ChatAuthenticationService tier 3). That null is an ABSENCE OF DATA, not a clan departure.
        SetupResolution(clanId: null, freshFromWb: false);
        _sends.Clear();
        await Connect("conn-2");

        Assert.IsNotNull(await _membershipRepository.Load(channel.Id, BattleTag),
            "NEVER-CLOBBER: a wb outage must not evict the user from their clan channel");

        var state = CapturedSessionState();
        Assert.IsTrue(
            state.Channels.Any(c => c.Channel.SystemKind == SystemChannelKind.Clan),
            "The clan channel must survive an outage connect in the rendered snapshot too");
    }

    [Test]
    public async Task Connect_WhenClanShellCreationFails_StillConnects_WithoutTheClanChannel()
    {
        // PR41 review (P2): creating the channel shell is part of the GRANT, not the revocation, so it
        // belongs on the fail-soft path. Creating a shell grants nobody access, and this call sits on the
        // hot path every clan member takes on every connect — rejecting the connection over a transient
        // channel-upsert fault would be an availability regression, and would contradict the stated
        // policy that only revocation is fatal.
        var throwingChannels = new Mock<ChannelRepository>(MongoClient) { CallBase = true };
        throwingChannels
            .Setup(c => c.FindOrCreateSystem(SystemChannelKind.Clan, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .ThrowsAsync(new TimeoutException("mongo hiccup"));
        _channelRepository = throwingChannels.Object;

        Assert.DoesNotThrowAsync(async () => await Connect(),
            "a failed clan-shell creation must never cost the user their chat session");

        var state = CapturedSessionState();
        Assert.IsFalse(
            state.Channels.Any(c => c.Channel.SystemKind == SystemChannelKind.Clan),
            "the user simply connects without the clan channel and self-heals on the next connect");
    }

    [Test]
    public async Task Connect_AfterLeavingClan_RevokesEvenWhenNoShellIsEverCreated()
    {
        // Guards the PR41 restructure: staleness is now derived from SystemRef, so departure revocation
        // must work without a target shell existing at all (the clanRef == null path creates nothing).
        await Connect("conn-1");
        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Clan, ClanId);

        SetupResolution(clanId: null, freshFromWb: true);
        _sends.Clear();
        await Connect("conn-2");

        Assert.IsNull(await _membershipRepository.Load(channel.Id, BattleTag),
            "a fresh 'no clan' read must still revoke, with no shell resolution involved");
    }

    // ---------------------------------------------------------------------------------------------
    // PR40 review — displaced connects must not write clan state (P2)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Reconcile_FromADisplacedConnection_WritesNothing()
    {
        // Two connects for the same battleTag overlap: registering conn-2 aborts conn-1's context but does
        // NOT cancel its in-flight OnConnectedAsync, which still holds the clan snapshot IT captured. If
        // the loser finished last it would re-add the old membership and delete the new one — durable
        // state decided by scheduling order. Driven directly because the race is mid-connect by nature.
        var ticket = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var staleHub = BuildConnection("conn-1", ticket);
        _sessionRegistry.Register("conn-1", Identity(), staleHub.Context);

        // conn-2 arrives and displaces conn-1.
        _sessionRegistry.Register("conn-2", Identity(), BuildConnection("conn-2", "t2").Context);

        await staleHub.ReconcileClanMembership(
            Identity(),
            new ChatUserResolution(
                new ChatUser(BattleTag, false, ClanId, new ProfilePicture(), null, null), true),
            DateTime.UtcNow);

        Assert.IsNull(await _channelRepository.LoadBySystemRef(SystemChannelKind.Clan, ClanId),
            "a displaced connection must not write clan state — the winner's view is the one that counts");
    }

    // ---------------------------------------------------------------------------------------------
    // Non-leavable
    // ---------------------------------------------------------------------------------------------

    // ---------------------------------------------------------------------------------------------
    // PR40 review — freshness as an access-control gate (P1)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Connect_WithCachedClanFlairDuringOutage_DoesNotGrantClanAccess()
    {
        // The scenario the review identified: directory entries are retained forever, but
        // CleanupJobs.PruneIdleMemberships deletes the membership rows of users idle > 1 year. A user who
        // left their clan while inactive therefore returns with a cached ClanId naming a clan they are no
        // longer in, and no membership row to contradict it. Granting access off that cached value would
        // re-admit them to the clan channel AND its retained history.
        SetupResolution(ClanId, freshFromWb: false);

        await Connect();

        Assert.IsNull(await _channelRepository.LoadBySystemRef(SystemChannelKind.Clan, ClanId),
            "a non-fresh resolution must not even create the clan shell");

        var state = CapturedSessionState();
        Assert.IsFalse(
            state.Channels.Any(c => c.Channel.SystemKind == SystemChannelKind.Clan),
            "clan access must never be granted from unverifiable cached flair");
    }

    [Test]
    public async Task Connect_DuringWbOutage_MakesNoClanWritesAtAll()
    {
        // Regression guard for the tightening: the earlier rule was "additive-only when not fresh", which
        // still granted access. The rule is now "no writes at all" — an existing membership is preserved
        // untouched (asserted below and in the outage test above), nothing is created.
        await Connect("conn-1");
        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Clan, ClanId);
        var before = await _membershipRepository.Load(channel.Id, BattleTag);

        SetupResolution("s4s", freshFromWb: false);
        _sends.Clear();
        await Connect("conn-2");

        Assert.IsNotNull(await _membershipRepository.Load(channel.Id, BattleTag),
            "a non-fresh resolution must preserve the existing membership");
        Assert.IsNull(await _channelRepository.LoadBySystemRef(SystemChannelKind.Clan, "s4s"),
            "a non-fresh resolution must not act on its (unverifiable) new clan id either");
        Assert.AreEqual(before.JoinedAt, (await _membershipRepository.Load(channel.Id, BattleTag)).JoinedAt,
            "the preserved membership must be left byte-identical, not re-written");
    }

    // ---------------------------------------------------------------------------------------------
    // PR40 review — fail-closed revocation (P1)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Connect_WhenStaleClanRemovalFails_FailsTheConnectRatherThanKeepingAccess()
    {
        await Connect("conn-1");
        var oldChannel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Clan, ClanId);

        // A membership repository whose stale-row delete always fails. Swallowing this would leave the
        // user readable/writable in their FORMER clan, which AssembleAndSeed would then seed.
        var throwingMemberships = new Mock<MembershipRepository>(MongoClient, _channelRepository) { CallBase = true };
        throwingMemberships
            .Setup(m => m.Delete(oldChannel.Id, It.IsAny<string>()))
            .ThrowsAsync(new TimeoutException("mongo hiccup"));
        _membershipRepository = throwingMemberships.Object;

        SetupResolution("s4s", freshFromWb: true);
        _sends.Clear();

        var ticket = _ticketStore.Mint(Identity(), DateTime.UtcNow);
        var hub = BuildConnection("conn-2", ticket);

        Assert.ThrowsAsync<TimeoutException>(
            async () => await hub.OnConnectedAsync(),
            "a failed revocation must fail the connect — never silently leave the user in their old clan");
    }

    [Test]
    public async Task LeaveChannel_OnClanChannel_IsRejectedAndMembershipSurvives()
    {
        var hub = await Connect();
        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Clan, ClanId);

        var result = await hub.LeaveChannel(channel.Id);

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code,
            "A clan channel is not user-leavable (product decision 2026-08-09)");
        Assert.IsNotNull(await _membershipRepository.Load(channel.Id, BattleTag),
            "A rejected leave must not delete the membership row");
    }

    [Test]
    public async Task LeaveChannel_OnANonClanChannel_StillWorks()
    {
        var hub = await Connect();
        var join = await hub.JoinChannel("Some Room");

        var result = await hub.LeaveChannel(join.Channel.Id);

        Assert.AreEqual(ChatResultCode.Ok, result.Code,
            "The clan carve-out must not regress the type-agnostic escape hatch for every other kind (H4)");
        Assert.IsNull(await _membershipRepository.Load(join.Channel.Id, BattleTag));
    }
}

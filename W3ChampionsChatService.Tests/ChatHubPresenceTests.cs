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
using MongoDB.Driver;
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
/// C6 Task 9 — the PRESENCE end-to-end acceptance suite (the "who sees whom" privacy boundary). Drives
/// MULTIPLE real <see cref="ChatHub"/> instances through the SHIPPED connect / focus / unfocus / leave /
/// remove / disconnect / displacement behavior while SHARING one instance of every singleton — crucially
/// ONE <see cref="PresenceInterestRegistry"/> and ONE <see cref="FanOutEngine"/> wired to it — plus one
/// shared <see cref="HubPushCaptureHarness"/> whose <see cref="HubPushCaptureHarness.HubContext"/> is the
/// engine's push channel, so every <c>PresenceChanged</c> lands in one per-connection capture.
/// <para>
/// The invariant under test: a connection is told about a user's presence transition IFF it currently has
/// a Dm/GroupDm focused that contains that user — interest is DERIVED, never subscribed. Every revocation
/// leg (unfocus / watcher disconnect / membership change / group deletion) drops it, and a displacement
/// (reconnect of an already-online user) is NOT a transition.
/// </para>
/// TIME is deterministic via a single <see cref="FakeTimeProvider"/>; tickets are minted with real
/// <c>DateTime.UtcNow</c> to match <see cref="ChatHub.OnConnectedAsync"/>'s wall-clock ticket consume
/// (the same seam <see cref="HubProtocolIntegrationTests"/> documents).
/// </summary>
public class ChatHubPresenceTests : IntegrationTestBase
{
    private static readonly DateTime T0 = new(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);

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
    private PresenceInterestRegistry _presenceInterestRegistry; // the shared index under test
    private UserDirectoryRepository _userDirectory;
    private MuteRepository _muteRepository;
    private MuteReconciliationService _reconcileService;
    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private HookableMembershipRepository _hookableMembershipRepository; // same instance as _membershipRepository
    private MessageRepository _messageRepository;
    private SessionStateAssembler _assembler;
    private Mock<IChatAuthenticationService> _authService;

    private readonly List<(string ConnectionId, string Method, object Payload)> _hubSends = new();

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

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
        _presenceInterestRegistry = new PresenceInterestRegistry();
        _userDirectory = new UserDirectoryRepository(MongoClient);
        _muteRepository = new MuteRepository(MongoClient);
        _reconcileService = new MuteReconciliationTestHarness(_connectionMapping, _muteRepository).Service;
        _channelRepository = new ChannelRepository(MongoClient);
        // A hookable MembershipRepository (behaviorally identical to the base when no hook is armed) so ONE
        // test can inject a deterministic concurrent membership mutation inside FocusChannel's read→commit
        // window. Every other test leaves AfterLoadForChannel null → pure pass-through.
        _hookableMembershipRepository = new HookableMembershipRepository(MongoClient, _channelRepository);
        _membershipRepository = _hookableMembershipRepository;
        _messageRepository = new MessageRepository(MongoClient);

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
            new MentionInboxRepository(MongoClient));

        _activityCoalescer = new ActivityCoalescer(_harness.HubContext, _onlineMemberRegistry);
        // The engine shares the SAME presence-interest registry the hubs mutate — so RegisterFocus (hub)
        // and GetInterestedConnections (engine's PushPresenceChanged) see one consistent index.
        _viewersAccumulator = new ViewersAccumulator(_harness.HubContext, _focusRegistry);
        _fanOutEngine = new FanOutEngine(
            _harness.HubContext, _focusRegistry, _onlineMemberRegistry, _activityCoalescer, _sessionRegistry, _presenceInterestRegistry, _viewersAccumulator, _time);
    }

    // ---- fixture plumbing --------------------------------------------------------------------------

    private static W3CUserAuthentication Identity(string battleTag) =>
        new() { BattleTag = battleTag, Name = battleTag.Split('#')[0] };

    private ChatHub BuildHub(string connectionId, string accessToken)
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
            _time,
            _channelRepository,
            _membershipRepository,
            _channelCreationRateLimiter,
            _messageRepository,
            _fanOutEngine,
            _viewersAccumulator,
            new NoOpMentionInboxCleaner(),
            RelationshipProviderTestFactory.CreateIgnored(),
            new UserSettingsRepository(MongoClient),
            new DmInitiationTracker(),
            _authService.Object,
            MentionFanOutTestFactory.CreateIgnored(MongoClient),
            _presenceInterestRegistry, // SHARED — same instance the engine reads
            new MentionInboxRepository(MongoClient));

        var clients = new Mock<IHubCallerClients>();
        clients.Setup(c => c.Caller).Returns(CapturingSingle(connectionId));
        clients.Setup(c => c.Client(It.IsAny<string>())).Returns<string>(CapturingSingle);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
        hub.Clients = clients.Object;

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        context.Setup(c => c.Features).Returns(BuildFeatures(accessToken));
        context.Setup(c => c.Abort()).Callback(() => Record(connectionId, "ABORT", null));
        hub.Context = context.Object;
        hub.Groups = new Mock<IGroupManager>().Object;

        return hub;
    }

    private async Task<ChatHub> Connect(string connectionId, string battleTag)
    {
        var ticket = _ticketStore.Mint(Identity(battleTag), DateTime.UtcNow);
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

    private void Record(string target, string method, object payload)
    {
        lock (_hubSends)
        {
            _hubSends.Add((target, method, payload));
        }
    }

    // ---- Mongo seed helpers ------------------------------------------------------------------------

    private async Task<ChatChannel> CreateChannel(string name, ChannelType type)
    {
        var channel = new ChatChannel
        {
            Type = type,
            Name = name,
            NormalizedName = type is ChannelType.Public or ChannelType.SemiPublic ? ChannelNames.Normalize(name) : null,
            LastSeq = 0,
        };
        if (type == ChannelType.Dm)
        {
            channel.RequestState = DmRequestState.Accepted;
        }
        await _channelRepository.Insert(channel);
        return channel;
    }

    private Task SeedMembership(string channelId, string battleTag, MembershipRole role = MembershipRole.Member) =>
        _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = channelId,
            BattleTag = battleTag,
            NotificationLevel = NotificationLevel.All,
            Role = role,
            JoinedAt = Now,
        });

    // ---- capture readers ---------------------------------------------------------------------------

    private IReadOnlyList<PresenceChangedDto> PresenceFor(string connectionId) =>
        _harness.SignalsFor(connectionId)
            .Where(s => s.Method == ChatEvents.PresenceChanged)
            .Select(s => (PresenceChangedDto)s.Payload)
            .ToList();

    private int PresenceCount(string connectionId, string battleTag) =>
        PresenceFor(connectionId).Count(p => string.Equals(p.BattleTag, battleTag, StringComparison.OrdinalIgnoreCase));

    private int PresenceCount(string connectionId, string battleTag, bool online) =>
        PresenceFor(connectionId).Count(p =>
            string.Equals(p.BattleTag, battleTag, StringComparison.OrdinalIgnoreCase) && p.Online == online);

    // ================================================================================================
    // Acceptance 6 — the positive interest leg.
    // ================================================================================================

    [Test]
    public async Task FocusDm_ThenCounterpartConnects_WatcherGetsPresenceChangedOnline()
    {
        const string AliceTag = "Alice#1";
        const string XavierTag = "Xavier#9";

        var dm = await CreateChannel("dm-ax", ChannelType.Dm);
        await SeedMembership(dm.Id, AliceTag);
        await SeedMembership(dm.Id, XavierTag);

        // Alice connects and focuses the DM while Xavier is OFFLINE — deriving interest in Xavier.
        var alice = await Connect("conn-alice", AliceTag);
        Assert.That((await alice.FocusChannel(dm.Id)).Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(PresenceCount("conn-alice", XavierTag), Is.Zero, "no event yet — Xavier is still offline");

        // Xavier connects → genuine offline→online transition → Alice (the sole interested watcher) is told.
        await Connect("conn-xavier", XavierTag);

        Assert.That(PresenceCount("conn-alice", XavierTag, online: true), Is.EqualTo(1),
            "the watcher focused on the DM containing Xavier receives exactly one PresenceChanged(online)");
        var payload = PresenceFor("conn-alice").Single();
        Assert.That(payload.BattleTag, Is.EqualTo(XavierTag));
        Assert.That(payload.Online, Is.True);
    }

    // ================================================================================================
    // The strict boundary — a genuinely-wired third connection watching ELSEWHERE gets nothing.
    // ================================================================================================

    [Test]
    public async Task NonInterestedConnection_NeverReceivesPresenceChanged()
    {
        const string AliceTag = "Alice#1";
        const string XavierTag = "Xavier#9";
        const string CharlieTag = "Charlie#7";
        const string YoungTag = "Young#5";

        var dmAx = await CreateChannel("dm-ax", ChannelType.Dm);
        await SeedMembership(dmAx.Id, AliceTag);
        await SeedMembership(dmAx.Id, XavierTag);

        // Charlie is GENUINELY wired: online, and focused on an UNRELATED DM (with Young), so Charlie
        // is a live watcher — just not of Xavier.
        var dmCy = await CreateChannel("dm-cy", ChannelType.Dm);
        await SeedMembership(dmCy.Id, CharlieTag);
        await SeedMembership(dmCy.Id, YoungTag);

        var alice = await Connect("conn-alice", AliceTag);
        await alice.FocusChannel(dmAx.Id); // Alice watches Xavier

        var charlie = await Connect("conn-charlie", CharlieTag);
        await charlie.FocusChannel(dmCy.Id); // Charlie watches Young, NOT Xavier

        // Xavier's presence changes (connect, then disconnect).
        var xavier = await Connect("conn-xavier", XavierTag);
        await xavier.OnDisconnectedAsync(null);

        Assert.That(PresenceCount("conn-alice", XavierTag), Is.EqualTo(2),
            "the interested watcher receives BOTH Xavier transitions (online + offline)");
        Assert.That(PresenceFor("conn-charlie"), Is.Empty,
            "a genuinely-wired connection focused elsewhere receives ZERO PresenceChanged about Xavier — the strict boundary");
    }

    // ================================================================================================
    // A user is never told about their OWN presence.
    // ================================================================================================

    [Test]
    public async Task SubjectOwnConnection_NeverSelfNotified()
    {
        const string AliceTag = "Alice#1";
        const string XavierTag = "Xavier#9";

        var group = await CreateChannel("grp-ax", ChannelType.GroupDm);
        await SeedMembership(group.Id, AliceTag, MembershipRole.Owner);
        await SeedMembership(group.Id, XavierTag);

        var alice = await Connect("conn-alice", AliceTag);
        await alice.FocusChannel(group.Id); // Alice watches Xavier

        var xavier = await Connect("conn-xavier", XavierTag);
        await xavier.FocusChannel(group.Id); // Xavier watches Alice — a genuinely-wired watcher

        // Alice transitions offline → Xavier (who watches Alice) is told; this proves Xavier's own
        // connection IS a live recipient in general.
        await alice.OnDisconnectedAsync(null);

        Assert.That(PresenceCount("conn-xavier", AliceTag, online: false), Is.EqualTo(1),
            "Xavier is a genuinely-wired watcher — it DOES receive Alice's transition");
        Assert.That(PresenceCount("conn-xavier", XavierTag), Is.Zero,
            "a user is NEVER notified about its OWN presence transition");
    }

    // ================================================================================================
    // Revocation leg — unfocus.
    // ================================================================================================

    [Test]
    public async Task UnfocusDm_ThenCounterpartConnectsAndDisconnects_NoEvent()
    {
        const string AliceTag = "Alice#1";
        const string XavierTag = "Xavier#9";

        var dm = await CreateChannel("dm-ax", ChannelType.Dm);
        await SeedMembership(dm.Id, AliceTag);
        await SeedMembership(dm.Id, XavierTag);

        var alice = await Connect("conn-alice", AliceTag);
        await alice.FocusChannel(dm.Id);          // Alice watches Xavier
        await alice.UnfocusChannel(dm.Id);        // ...and immediately stops (interest revoked)

        // Xavier now transitions twice; with interest revoked, Alice must hear nothing.
        var xavier = await Connect("conn-xavier", XavierTag);
        await xavier.OnDisconnectedAsync(null);

        Assert.That(PresenceFor("conn-alice"), Is.Empty,
            "unfocus revokes the derived interest — no PresenceChanged reaches the ex-watcher");
    }

    // ================================================================================================
    // Revocation leg — watcher disconnect.
    // ================================================================================================

    [Test]
    public async Task WatcherDisconnect_InterestGone()
    {
        const string AliceTag = "Alice#1";
        const string BobTag = "Bob#2";
        const string XavierTag = "Xavier#9";

        var group = await CreateChannel("grp-abx", ChannelType.GroupDm);
        await SeedMembership(group.Id, AliceTag, MembershipRole.Owner);
        await SeedMembership(group.Id, BobTag);
        await SeedMembership(group.Id, XavierTag);

        var alice = await Connect("conn-alice", AliceTag);
        await alice.FocusChannel(group.Id); // Alice watches Bob + Xavier
        var bob = await Connect("conn-bob", BobTag);
        await bob.FocusChannel(group.Id);   // Bob watches Alice + Xavier

        // Alice disconnects — her interest as a WATCHER must be fully dropped.
        await alice.OnDisconnectedAsync(null);

        // Xavier connects. Only Bob (still watching) should hear it; Alice's dead connection must not be
        // in the interested set at all.
        await Connect("conn-xavier", XavierTag);

        Assert.That(PresenceCount("conn-bob", XavierTag, online: true), Is.EqualTo(1),
            "the surviving watcher still receives Xavier's transition");
        Assert.That(PresenceCount("conn-alice", XavierTag), Is.Zero,
            "a disconnected watcher's interest is gone — Xavier's transition never targets it");
    }

    // ================================================================================================
    // The false-transition guard — displacement fires nothing in either direction.
    // ================================================================================================

    [Test]
    public async Task Displacement_NoOfflineNoOnlineEvent()
    {
        const string AliceTag = "Alice#1";
        const string XavierTag = "Xavier#9";

        var group = await CreateChannel("grp-ax", ChannelType.GroupDm);
        await SeedMembership(group.Id, AliceTag, MembershipRole.Owner);
        await SeedMembership(group.Id, XavierTag);

        // Xavier is already online on an OLD socket; Alice then focuses and watches him.
        var xavierOld = await Connect("conn-xavier-old", XavierTag);
        var alice = await Connect("conn-alice", AliceTag);
        await alice.FocusChannel(group.Id); // Alice watches Xavier (no event — focusing doesn't emit)
        Assert.That(PresenceFor("conn-alice"), Is.Empty, "focusing an already-online user emits nothing");

        // Xavier RECONNECTS on a new socket, displacing the old one — online before AND after.
        await Connect("conn-xavier-new", XavierTag);
        Assert.That(_hubSends.Any(s => s.ConnectionId == "conn-xavier-old" && s.Method == ChatEvents.ConnectionDisplaced),
            Is.True, "sanity: the old socket is displaced");

        // The displaced OLD socket now tears down (its late OnDisconnectedAsync).
        await xavierOld.OnDisconnectedAsync(null);

        Assert.That(PresenceFor("conn-alice"), Is.Empty,
            "a displacement (reconnect of an already-online user) is NOT a transition — neither the new " +
            "socket's connect nor the old socket's teardown fires any PresenceChanged");
    }

    // ================================================================================================
    // Public/SemiPublic/System focus registers NO interest at all.
    // ================================================================================================

    [Test]
    public async Task PublicChannelFocus_RegistersNoInterest()
    {
        const string AliceTag = "Alice#1";
        const string XavierTag = "Xavier#9";

        var pub = await CreateChannel("general", ChannelType.Public);
        await SeedMembership(pub.Id, AliceTag);
        await SeedMembership(pub.Id, XavierTag);

        var alice = await Connect("conn-alice", AliceTag);
        Assert.That((await alice.FocusChannel(pub.Id)).Code, Is.EqualTo(ChatResultCode.Ok));

        // Directly: a public-channel focus derives NO presence interest.
        Assert.That(_presenceInterestRegistry.GetInterestedConnections(XavierTag), Is.Empty,
            "focusing a Public channel registers no presence interest — presence is a private-lane-only concept");

        // End-to-end: Xavier connecting produces no PresenceChanged to the public co-member.
        await Connect("conn-xavier", XavierTag);
        Assert.That(PresenceFor("conn-alice"), Is.Empty,
            "a public co-member is never told about another member's presence");
    }

    // ================================================================================================
    // Membership change — forced removal (owner kicks an OFFLINE member) revokes watchers' interest.
    // ================================================================================================

    [Test]
    public async Task GroupMemberRemoved_WatchersStopReceivingTheirPresence()
    {
        const string OwnerTag = "Owner#1";
        const string XavierTag = "Xavier#9";

        var group = await CreateChannel("grp-ox", ChannelType.GroupDm);
        await SeedMembership(group.Id, OwnerTag, MembershipRole.Owner);
        await SeedMembership(group.Id, XavierTag);

        var owner = await Connect("conn-owner", OwnerTag);
        await owner.FocusChannel(group.Id); // Owner watches Xavier

        // Xavier is OFFLINE when kicked — the membership-change hook must still fire (it runs BEFORE the
        // offline early-return inside PushChannelRemoved).
        Assert.That((await owner.RemoveGroupMember(group.Id, XavierTag)).Code, Is.EqualTo(ChatResultCode.Ok));

        // Xavier now comes online — the owner has been de-interested and must hear nothing.
        await Connect("conn-xavier", XavierTag);

        Assert.That(PresenceFor("conn-owner"), Is.Empty,
            "a forced removal revokes the watchers' interest in the removed member, even while that member is offline");
    }

    // ================================================================================================
    // Membership change — a member ADDED while watchers are focused gains interest, even if offline.
    // ================================================================================================

    [Test]
    public async Task GroupMemberAdded_WhileFocused_WatchersGainInterest_EvenIfNewMemberOffline()
    {
        const string OwnerTag = "Owner#1";
        const string AmyTag = "Amy#3";
        const string YoungTag = "Young#5";

        var group = await CreateChannel("grp-oa", ChannelType.GroupDm);
        await SeedMembership(group.Id, OwnerTag, MembershipRole.Owner);
        await SeedMembership(group.Id, AmyTag);

        var owner = await Connect("conn-owner", OwnerTag);
        await owner.FocusChannel(group.Id); // Owner watches Amy

        // Young is added to the group while OFFLINE. Driven through the real emit helper: OnMemberAdded
        // runs at the TOP of PushChannelAdded, BEFORE the offline early-return (Young has no session).
        var youngMembership = new ChannelMembership
        {
            ChannelId = group.Id,
            BattleTag = YoungTag,
            NotificationLevel = NotificationLevel.All,
            JoinedAt = Now,
        };
        await _fanOutEngine.PushChannelAdded(group, youngMembership, focus: false);

        // Young then comes online — the owner, who was focused when Young was added, must be told.
        await Connect("conn-young", YoungTag);

        Assert.That(PresenceCount("conn-owner", YoungTag, online: true), Is.EqualTo(1),
            "a member added while a watcher is focused gains interest even though it was offline at add-time");
    }

    // ================================================================================================
    // Membership change — voluntary leave drops the leaver for the remaining watchers.
    // ================================================================================================

    [Test]
    public async Task VoluntaryLeave_Group_WatchersDropLeaver()
    {
        const string WatcherTag = "Watcher#1";
        const string LeaverTag = "Leaver#2";

        var group = await CreateChannel("grp-wl", ChannelType.GroupDm);
        await SeedMembership(group.Id, WatcherTag, MembershipRole.Owner);
        await SeedMembership(group.Id, LeaverTag);

        var watcher = await Connect("conn-watcher", WatcherTag);
        await watcher.FocusChannel(group.Id); // Watcher watches Leaver

        // Leaver connects (watcher is told: online), then voluntarily leaves.
        var leaver = await Connect("conn-leaver", LeaverTag);
        Assert.That(PresenceCount("conn-watcher", LeaverTag, online: true), Is.EqualTo(1),
            "sanity: the online transition reached the watcher before the leave");

        Assert.That((await leaver.LeaveChannel(group.Id)).Code, Is.EqualTo(ChatResultCode.Ok));

        // Leaver disconnects — with interest revoked by the leave, no OFFLINE event may reach the watcher.
        await leaver.OnDisconnectedAsync(null);

        Assert.That(PresenceCount("conn-watcher", LeaverTag, online: false), Is.Zero,
            "a voluntary leave drops the leaver from the remaining watchers — no offline event follows");
        Assert.That(PresenceCount("conn-watcher", LeaverTag), Is.EqualTo(1),
            "the watcher saw ONLY the pre-leave online transition, nothing after the leave");
    }

    // ================================================================================================
    // Refcount-by-channel end-to-end — unfocusing ONE of two channels reaching a tag keeps interest.
    // ================================================================================================

    [Test]
    public async Task RefcountByChannel_UnfocusOne_InterestSurvives()
    {
        const string AliceTag = "Alice#1";
        const string XavierTag = "Xavier#9";

        // Two groups, both containing Alice and Xavier — Alice reaches Xavier via BOTH.
        var g1 = await CreateChannel("grp-1", ChannelType.GroupDm);
        await SeedMembership(g1.Id, AliceTag, MembershipRole.Owner);
        await SeedMembership(g1.Id, XavierTag);
        var g2 = await CreateChannel("grp-2", ChannelType.GroupDm);
        await SeedMembership(g2.Id, AliceTag, MembershipRole.Owner);
        await SeedMembership(g2.Id, XavierTag);

        var alice = await Connect("conn-alice", AliceTag);
        await alice.FocusChannel(g1.Id);
        await alice.FocusChannel(g2.Id);

        // Unfocus only ONE — interest in Xavier must survive via the other.
        await alice.UnfocusChannel(g1.Id);

        await Connect("conn-xavier", XavierTag);

        Assert.That(PresenceCount("conn-alice", XavierTag, online: true), Is.EqualTo(1),
            "interest survives unfocusing one of two focused channels that both reach the tag (refcount-by-channel)");
    }

    // ================================================================================================
    // C6 T9 review fix — the FocusChannel/PresenceInterestRegistry read→commit TOCTOU.
    //
    // Reproduces the race by CONTROLLED INTERLEAVING (no timing / no sleep): a hookable MembershipRepository
    // lands a concurrent member removal in the exact window between FocusChannel's roster read and its
    // interest commit. Against the pre-fix code the focuser ends up watching the just-departed member
    // (stale grant, never revoked until re-focus/disconnect); with the version guard it does not. This test
    // FAILS against the pre-fix FocusChannel and PASSES with the fix — verified by reverting only the
    // FocusChannel private-lane branch (see task-9-report.md).
    // ================================================================================================

    [Test]
    public async Task FocusPrivateLane_ConcurrentMemberRemovalDuringRosterRead_DoesNotRegisterStaleInterest()
    {
        const string AliceTag = "Alice#1";
        const string BobTag = "Bob#2";
        const string XavierTag = "Xavier#9";

        var group = await CreateChannel("grp-abx", ChannelType.GroupDm);
        await SeedMembership(group.Id, AliceTag, MembershipRole.Owner);
        await SeedMembership(group.Id, BobTag);
        await SeedMembership(group.Id, XavierTag);

        var alice = await Connect("conn-alice", AliceTag);

        // Arm the interleaving hook AFTER connect (so connect-time reads are untouched): the FIRST
        // LoadForChannel for this group — Alice's FocusChannel roster read — returns the STALE
        // [Alice, Bob, Xavier] snapshot, and immediately AFTER that read (still inside FocusChannel's
        // read→commit window, before Alice is recorded as a watcher) Bob concurrently leaves: his membership
        // row is deleted from Mongo AND OnMemberRemoved fires on the shared registry — exactly what a real
        // concurrent LeaveChannel/RemoveGroupMember does. Because Alice is NOT yet a watcher at that instant,
        // Bob's OnMemberRemoved is a no-op FOR HER (the precise condition that made the pre-fix code register
        // stale interest in Bob). One-shot: the hook clears itself so the version guard's re-read observes the
        // clean post-removal roster [Alice, Xavier] and commits THAT.
        _hookableMembershipRepository.AfterLoadForChannel = async loadedChannelId =>
        {
            if (loadedChannelId != group.Id)
            {
                return;
            }
            _hookableMembershipRepository.AfterLoadForChannel = null; // only the FIRST read races
            await _membershipRepository.Delete(group.Id, BobTag);
            _presenceInterestRegistry.OnMemberRemoved(group.Id, BobTag);
        };

        Assert.That((await alice.FocusChannel(group.Id)).Code, Is.EqualTo(ChatResultCode.Ok));

        // The fix: Alice's committed interest reflects the POST-removal roster — she watches Xavier (still a
        // member) but NOT Bob (who left inside the read→commit window). Against the pre-fix code the stale
        // snapshot wins and Bob IS watched — the assertion below is exactly what fails there.
        Assert.That(_presenceInterestRegistry.GetInterestedConnections(BobTag), Is.Empty,
            "a member who left inside the read→commit window must NOT be watched — the TOCTOU is closed");
        Assert.That(_presenceInterestRegistry.GetInterestedConnections(XavierTag), Is.EquivalentTo(new[] { "conn-alice" }),
            "a member still present at commit time IS watched — the version guard re-reads without over-removing");
    }

    // A MembershipRepository seam (subclass over the real Mongo-backed repo, mirroring
    // CountingUserDirectoryRepository / MentionFanOutTests.ThrowingInsertRepository — there is no interface
    // seam here) that fires an injected async hook AFTER each real LoadForChannel returns. A test sets the
    // hook to land a concurrent membership mutation deterministically inside FocusChannel's read→commit
    // window, so the TOCTOU is reproduced by controlled interleaving rather than timing luck.
    private sealed class HookableMembershipRepository(MongoClient client, ChannelRepository channelRepository)
        : MembershipRepository(client, channelRepository)
    {
        // Fired with the just-read channelId; null (default) means pure pass-through.
        public Func<string, Task> AfterLoadForChannel { get; set; }

        public override async Task<List<ChannelMembership>> LoadForChannel(string channelId)
        {
            var result = await base.LoadForChannel(channelId);
            var hook = AfterLoadForChannel;
            if (hook != null)
            {
                await hook(channelId);
            }
            return result;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
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
/// C6 Task 11 (D13, acceptance 6 — the FRIENDS leg): <c>FriendPresenceChanged</c>, the mechanism a later
/// cross-repo (W3) item retires wb's <c>FriendOnlineStatus</c> broadcast against (mirrors wb's
/// <c>NotifyFriendsWithIsOnline</c>). Complements <see cref="ChatHubPresenceTests"/> (Task 9's
/// DERIVED-focus <c>PresenceChanged</c> stream — a DIFFERENT targeting mechanism entirely) — this suite
/// targets the subject's actual FRIENDS list (C5's <see cref="IRelationshipProvider"/>), not
/// focus/membership-derived interest.
/// <para>
/// Drives MULTIPLE real <see cref="ChatHub"/> instances sharing ONE <see cref="SessionRegistry"/> +
/// <see cref="FanOutEngine"/> + <see cref="HubPushCaptureHarness"/> (mirrors
/// <see cref="ChatHubPresenceTests"/>), plus a real <see cref="RelationshipProvider"/> over a
/// <see cref="FakeRelationshipSource"/> (mirrors <see cref="ChatHubPresenceReadTests"/>) for per-tag
/// friend-list control.
/// </para>
/// <para>
/// DETERMINISM: the disconnect-side friend push rides a FIRE-AND-FORGET background task (never awaited
/// by <c>OnDisconnectedAsync</c>), so most tests set <see cref="FakeRelationshipSource.ReleaseGate"/> to
/// <see cref="Task.CompletedTask"/> (already completed) — this removes the fetch's internal
/// <c>await Task.Yield()</c> suspension entirely, so the WHOLE background chain (fetch → cache publish →
/// friend push) runs SYNCHRONOUSLY to completion as part of the discarded call, observable immediately
/// after <c>Connect</c>/<c>OnDisconnectedAsync</c> returns — no sleeps, no polling. The disconnect-side
/// "rides the existing task, no new await" test deliberately does the OPPOSITE: it holds an UNRELEASED
/// gate to force genuine asynchrony, proving the disconnect path itself completes without waiting on it.
/// Follow-up spec §6 changed the CONNECT side of this story: the connect path now AWAITS one
/// relationship fetch before assembly (the bounded 1:1-DM snapshot needs the block list) and hands that
/// resolved snapshot straight to the fire-and-forget dispatch, which never touches the relationship SOURCE
/// itself anymore — so a gate on the source can no longer distinguish "connect awaits it" from "the push
/// rides behind it". <c>Connect_AwaitsRelationshipFetch_ThenFriendPushRidesTheWarmCache</c> instead gates
/// the push's own SignalR send (<see cref="HubPushCaptureHarness.GateSend"/>) to pin the still-true
/// contract: connect never awaits the friend push.
/// </para>
/// </summary>
public class ChatHubFriendPresenceTests : IntegrationTestBase
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
    private PresenceInterestRegistry _presenceInterestRegistry;
    private UserDirectoryRepository _userDirectory;
    private MuteRepository _muteRepository;
    private MuteReconciliationService _reconcileService;
    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private MessageRepository _messageRepository;
    private SessionStateAssembler _assembler;
    private Mock<IChatAuthenticationService> _authService;

    private FakeRelationshipSource _relationshipSource;
    private RelationshipProvider _relationshipProvider;

    // Per-tag friends, read by the fake source's snapshot factory (mirrors ChatHubPresenceReadTests).
    // Blocked is always empty; friend-presence never consults it.
    private readonly Dictionary<string, HashSet<string>> _friends = new(StringComparer.OrdinalIgnoreCase);

    [SetUp]
    public void SetupBeforeEach()
    {
        _friends.Clear();
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
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _messageRepository = new MessageRepository(MongoClient);

        _relationshipSource = new FakeRelationshipSource((tag, now) => new RelationshipSnapshot(
            tag,
            _friends.TryGetValue(tag, out var f) ? f : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            now));
        // See the class doc comment — this makes every fetch resolve SYNCHRONOUSLY by default (no genuine
        // suspension), so the fire-and-forget friend push is deterministically observable right after
        // Connect/disconnect return. Individual tests override this with a real (unreleased)
        // TaskCompletionSource when they need to prove genuine non-blocking behavior.
        _relationshipSource.ReleaseGate = Task.CompletedTask;
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
            _connectionMapping,
            new MentionInboxRepository(MongoClient));

        _activityCoalescer = new ActivityCoalescer(_harness.HubContext, _onlineMemberRegistry);
        // The engine shares the SAME SessionRegistry every hub registers into — PushFriendPresenceChanged
        // resolves each friend's live connection through it.
        _viewersAccumulator = new ViewersAccumulator(_harness.HubContext, _focusRegistry);
        _fanOutEngine = new FanOutEngine(
            _harness.HubContext, _focusRegistry, _onlineMemberRegistry, _activityCoalescer, _sessionRegistry, _presenceInterestRegistry, _viewersAccumulator, _time);
    }

    // ---- fixture plumbing (mirrors ChatHubPresenceTests / ChatHubPresenceReadTests) -------------------

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
            _relationshipProvider,
            new UserSettingsRepository(MongoClient),
            new DmInitiationTracker(),
            _authService.Object,
            MentionFanOutTestFactory.CreateIgnored(MongoClient),
            _presenceInterestRegistry,
            new MentionInboxRepository(MongoClient));

        var clients = new Mock<IHubCallerClients>();
        clients.Setup(c => c.Caller).Returns(new Mock<ISingleClientProxy>().Object);
        clients.Setup(c => c.Client(It.IsAny<string>())).Returns(new Mock<ISingleClientProxy>().Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
        hub.Clients = clients.Object;

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        context.Setup(c => c.Features).Returns(BuildFeatures(accessToken));
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

    // ---- capture readers ---------------------------------------------------------------------------

    private IReadOnlyList<FriendPresenceChangedDto> FriendPresenceFor(string connectionId) =>
        _harness.SignalsFor(connectionId)
            .Where(s => s.Method == ChatEvents.FriendPresenceChanged)
            .Select(s => (FriendPresenceChangedDto)s.Payload)
            .ToList();

    // ================================================================================================
    // Acceptance 6 — exact targeting: online friend YES, online non-friend NO, offline friend NO/safe.
    // ================================================================================================

    [Test]
    public async Task Connect_PushesFriendPresenceOnline_ToExactlyOnlineFriends()
    {
        const string SubjectTag = "Subject#1";
        const string OnlineFriendTag = "OnlineFriend#2";
        const string OnlineNonFriendTag = "OnlineNonFriend#3";
        const string OfflineFriendTag = "OfflineFriend#4";

        _friends[SubjectTag] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            OnlineFriendTag, OfflineFriendTag,
        };
        // OnlineNonFriendTag is deliberately absent from the subject's friends list.

        await Connect("conn-online-friend", OnlineFriendTag);
        await Connect("conn-online-nonfriend", OnlineNonFriendTag);
        // OfflineFriendTag deliberately never connects.

        ChatHub subjectHub = null;
        Assert.DoesNotThrowAsync(async () => subjectHub = await Connect("conn-subject", SubjectTag),
            "an offline friend in the list must never crash the connect path");
        Assert.That(subjectHub, Is.Not.Null);

        var friendEvents = FriendPresenceFor("conn-online-friend");
        Assert.That(friendEvents, Has.Count.EqualTo(1),
            "the online friend receives exactly one FriendPresenceChanged");
        Assert.That(friendEvents.Single().BattleTag, Is.EqualTo(SubjectTag),
            "the payload battleTag must be the SUBJECT's display casing, not the recipient's");
        Assert.That(friendEvents.Single().Online, Is.True);

        Assert.That(FriendPresenceFor("conn-online-nonfriend"), Is.Empty,
            "an online NON-friend must receive NOTHING — the strict friends-only boundary");
    }

    // ================================================================================================
    // Disconnect leg.
    // ================================================================================================

    [Test]
    public async Task Disconnect_PushesFriendPresenceOffline_ToOnlineFriends()
    {
        const string SubjectTag = "Subject#1";
        const string FriendTag = "Friend#2";
        _friends[SubjectTag] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FriendTag };

        await Connect("conn-friend", FriendTag);
        var subject = await Connect("conn-subject", SubjectTag);

        Assert.That(FriendPresenceFor("conn-friend").Count(p => p.BattleTag == SubjectTag && p.Online), Is.EqualTo(1),
            "sanity: the connect-side online push already landed");

        await subject.OnDisconnectedAsync(null);

        Assert.That(FriendPresenceFor("conn-friend").Count(p => p.BattleTag == SubjectTag && !p.Online), Is.EqualTo(1),
            "the disconnect-side push delivers FriendPresenceChanged(offline) to the online friend");
    }

    // ================================================================================================
    // Displacement must produce silence in BOTH directions.
    // ================================================================================================

    [Test]
    public async Task Displacement_NoFriendPresenceEvents()
    {
        const string AliceTag = "Alice#1";
        const string XavierTag = "Xavier#9";
        _friends[XavierTag] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AliceTag };

        var alice = await Connect("conn-alice", AliceTag);
        _ = alice;
        var xavierOld = await Connect("conn-xavier-old", XavierTag);

        Assert.That(FriendPresenceFor("conn-alice").Count(p => p.BattleTag == XavierTag && p.Online), Is.EqualTo(1),
            "sanity: Xavier's genuine connect DOES push to his friend Alice");
        var baselineCount = FriendPresenceFor("conn-alice").Count;

        // Xavier RECONNECTS on a new socket, displacing the old one — online before AND after.
        await Connect("conn-xavier-new", XavierTag);
        // The displaced OLD socket now tears down (its late OnDisconnectedAsync).
        await xavierOld.OnDisconnectedAsync(null);

        Assert.That(FriendPresenceFor("conn-alice").Count, Is.EqualTo(baselineCount),
            "a displacement (reconnect of an already-online user, and the old socket's stale teardown) " +
            "produces NO additional FriendPresenceChanged in either direction");
    }

    // ================================================================================================
    // Honest degradation — snapshot totally unavailable.
    // ================================================================================================

    [Test]
    public async Task SnapshotUnavailable_NoPush_ConnectStillSucceeds()
    {
        const string SubjectTag = "Subject#1";
        const string FriendTag = "Friend#2";
        _friends[SubjectTag] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FriendTag };
        _relationshipSource.ShouldThrow = true; // nothing has ever been cached for SubjectTag — total miss

        await Connect("conn-friend", FriendTag);

        Assert.DoesNotThrowAsync(async () => await Connect("conn-subject", SubjectTag),
            "an unavailable relationship snapshot must never fail (or throw out of) the connect path");
        Assert.That(_sessionRegistry.TryGetByConnectionId("conn-subject", out _), Is.True,
            "the connect still succeeds despite the snapshot being unavailable");

        Assert.That(FriendPresenceFor("conn-friend"), Is.Empty,
            "no friend push can happen when the snapshot is entirely unavailable — honest degradation, not a bug");
    }

    // ================================================================================================
    // Stale-snapshot tolerance (C5/Task 10 policy) — a stale-but-cached snapshot is still USABLE.
    // ================================================================================================

    [Test]
    public async Task StaleSnapshot_StillUsed()
    {
        const string SubjectTag = "Subject#1";
        const string FriendTag = "Friend#2";
        _friends[SubjectTag] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FriendTag };

        // Seed a FRESH cache entry directly against the provider (bypassing the hub).
        await _relationshipProvider.GetSnapshotAsync(SubjectTag);
        Assert.That(_relationshipSource.FetchCount, Is.EqualTo(1), "sanity: exactly one real fetch seeded the cache");

        // Advance past the TTL so the cached entry is now stale, then take the live source down.
        _time.Advance(ChatLimits.RelationshipCacheTtl + TimeSpan.FromSeconds(1));
        _relationshipSource.ShouldThrow = true;

        await Connect("conn-friend", FriendTag);
        await Connect("conn-subject", SubjectTag);

        Assert.That(FriendPresenceFor("conn-friend").Count(p => p.BattleTag == SubjectTag && p.Online), Is.EqualTo(1),
            "a stale-but-cached snapshot is still USABLE for the friend push when the live source is unreachable — " +
            "no freshness check gates this, mirroring GetPresenceDetails' own stale-usable policy");
    }

    // ================================================================================================
    // Fault isolation — a dead friend socket must not prevent other friends from being notified.
    // ================================================================================================

    [Test]
    public async Task DeadFriendSocket_OtherFriendsStillNotified()
    {
        const string SubjectTag = "Subject#1";
        const string FriendATag = "FriendA#2";
        const string FriendBTag = "FriendB#3";
        _friends[SubjectTag] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FriendATag, FriendBTag };

        await Connect("conn-frienda", FriendATag);
        await Connect("conn-friendb", FriendBTag);
        _harness.ThrowOnSend("conn-frienda");

        Assert.DoesNotThrowAsync(async () => await Connect("conn-subject", SubjectTag),
            "one friend's dead socket must never propagate out of the connect path");

        Assert.That(FriendPresenceFor("conn-friendb").Count(p => p.BattleTag == SubjectTag && p.Online), Is.EqualTo(1),
            "FriendB is still notified even though FriendA's socket throws on send");
    }

    // ================================================================================================
    // Structural: the connect path's own awaited section rides the pre-existing fire-and-forget task —
    // no new await was added.
    // ================================================================================================

    [Test]
    public async Task Connect_AwaitsRelationshipFetch_ThenFriendPushRidesTheWarmCache()
    {
        // Follow-up spec §6: the connect path now AWAITS one relationship fetch BEFORE assembly (the
        // bounded 1:1-DM snapshot needs the block list) and hands that ALREADY-RESOLVED snapshot straight
        // to the fire-and-forget PushFriendPresenceFromSnapshot dispatch, which no longer calls
        // GetSnapshotAsync itself at all (see ChatHub.PushFriendPresenceFromSnapshot — a duplicate wb
        // round-trip there would double load on wb during exactly the outage where it's least welcome).
        // That means the relationship SOURCE can no longer be gated to distinguish "connect awaits it" from
        // "the push rides behind it" — there is nothing left downstream of connect's own await that
        // touches the source. So this test gates the PUSH'S OWN SignalR send instead (HubPushCaptureHarness
        // .GateSend) and proves connect completes and returns while that send is STILL in flight — i.e.
        // genuinely fire-and-forget, not merely fast enough to look that way.
        const string SubjectTag = "Subject#1";
        const string FriendTag = "Friend#2";
        _friends[SubjectTag] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FriendTag };

        await Connect("conn-friend", FriendTag);

        var pushGate = new TaskCompletionSource();
        _harness.GateSend("conn-friend", pushGate.Task); // hold the friend-presence send open indefinitely

        var subjectHub = BuildHub("conn-subject", _ticketStore.Mint(Identity(SubjectTag), DateTime.UtcNow));
        var connectTask = subjectHub.OnConnectedAsync();

        var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.That(completed, Is.SameAs(connectTask),
            "connect must complete WITHOUT waiting on the friend-presence send — it is fire-and-forget");
        Assert.That(_sessionRegistry.TryGetByConnectionId("conn-subject", out _), Is.True);
        Assert.That(FriendPresenceFor("conn-friend"), Is.Empty,
            "the friend-presence send is still gated open at the moment connect returns — proof it was " +
            "never awaited by the connect path, not just delivered quickly");

        pushGate.SetResult(); // release — the in-flight send completes
        await Task.Delay(TimeSpan.FromMilliseconds(50)); // let the fire-and-forget chain finish recording
        Assert.That(FriendPresenceFor("conn-friend").Count(p => p.BattleTag == SubjectTag && p.Online), Is.EqualTo(1),
            "the friend push lands once its gated send is released");
    }

    // ================================================================================================
    // Structural: the disconnect path's own teardown likewise does not await the friend push.
    // ================================================================================================

    [Test]
    public async Task Disconnect_PushRidesFireAndForget_NoAwaitOnDisconnectPath()
    {
        const string SubjectTag = "Subject#1";
        const string FriendTag = "Friend#2";
        _friends[SubjectTag] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FriendTag };

        await Connect("conn-friend", FriendTag);
        var subject = await Connect("conn-subject", SubjectTag);
        var baselineCount = FriendPresenceFor("conn-friend").Count; // the connect-time online push already landed

        // Force the disconnect-time fetch to actually reach the source (else it would hit the still-fresh
        // tier-1 cache from the connect-time prefetch above and never touch the gate at all).
        _relationshipProvider.Invalidate(SubjectTag);
        var gate = new TaskCompletionSource();
        _relationshipSource.ReleaseGate = gate.Task;

        await subject.OnDisconnectedAsync(null); // must return WITHOUT waiting on the held gate

        Assert.That(_sessionRegistry.TryGetByConnectionId("conn-subject", out _), Is.False,
            "disconnect teardown completed even though the friend-presence fetch is still stuck on the gate");
        Assert.That(FriendPresenceFor("conn-friend").Count, Is.EqualTo(baselineCount),
            "the offline push has NOT happened yet — still blocked behind the fetch gate");

        gate.SetResult();

        Assert.That(FriendPresenceFor("conn-friend").Count(p => p.BattleTag == SubjectTag && !p.Online), Is.EqualTo(1),
            "once released, the SAME fire-and-forget task delivers the offline push");
    }
}

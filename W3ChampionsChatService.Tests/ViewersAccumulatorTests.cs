using System;
using System.Collections.Generic;
using System.Linq;
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
/// C3 (Task 14) tests for the <see cref="ViewersAccumulator"/> — the batched, idempotent
/// <c>ViewersChanged</c> roster-delta sink, and the C2 displacement amendment (a reconnect that
/// re-focuses within the flush window must NOT be read as a leave). Two layers:
/// <list type="bullet">
/// <item>PURE accumulator tests: drive <see cref="ViewersAccumulator.RecordChange"/>/
/// <see cref="ViewersAccumulator.FlushDue"/> against a fixed sequence of <c>now</c> values (NO sleeping,
/// NO wall-clock) and a real <see cref="FocusRegistry"/>, asserting the ≤5s flush cadence, the SAME batch
/// object to every focused viewer, and the idempotent current-vs-baseline delta (join+leave / leave+rejoin
/// flaps cancel).</item>
/// <item>HUB-LEVEL tests (direct-hub-instantiation, SHARED singletons — the ChatHubFocusTests /
/// ChatHubConnectionTests idiom): prove the hub's FocusChannel/UnfocusChannel/OnDisconnectedAsync routing
/// feeds the accumulator BEFORE the FocusRegistry mutation, so the pre-window baseline reconciles a
/// displaced socket's leave against a same-window reconnect.</item>
/// </list>
/// The accumulator emits through a <see cref="HubPushCaptureHarness"/>; the pinned pre-window-baseline
/// ordering (RecordChange BEFORE the FocusRegistry mutation) is the crux the displacement tests defend.
/// </summary>
public class ViewersAccumulatorTests : IntegrationTestBase
{
    private const string ChannelId = "channel-x";

    private static readonly DateTime T0 = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Flush = ChatLimits.ViewersChangedFlush; // 5s

    // ---- Pure accumulator fixture ------------------------------------------------------------------

    private static (HubPushCaptureHarness harness, FocusRegistry focus, ViewersAccumulator accumulator)
        NewAccumulator()
    {
        var harness = new HubPushCaptureHarness();
        var focus = new FocusRegistry();
        var accumulator = new ViewersAccumulator(harness.HubContext, focus);
        return (harness, focus, accumulator);
    }

    private static IReadOnlyList<ViewersChangedDto> ViewersChangedFor(HubPushCaptureHarness harness, string connectionId) =>
        harness.SignalsFor(connectionId)
            .Where(s => s.Method == ChatEvents.ViewersChanged)
            .Select(s => (ViewersChangedDto)s.Payload)
            .ToList();

    private static int TotalViewersChanged(HubPushCaptureHarness harness) =>
        harness.AllSignals.Count(s => s.Method == ChatEvents.ViewersChanged);

    private static bool Contains(IEnumerable<string> tags, string battleTag) =>
        tags.Any(t => string.Equals(t, battleTag, StringComparison.OrdinalIgnoreCase));

    // ---- PURE: accumulate / flush ------------------------------------------------------------------

    [Test]
    public async Task FocusChange_AccumulatesUntilFlush_NoImmediateEvent()
    {
        var (harness, focus, accumulator) = NewAccumulator();

        // A viewer joins: RecordChange BEFORE the FocusRegistry mutation (the pinned ordering) captures
        // the pre-window baseline (NOT-viewing) — then the actual focus makes it viewing.
        accumulator.RecordChange(ChannelId, "alice#1", T0);
        focus.Focus("conn-a", ChannelId, "alice#1");

        // RecordChange itself NEVER emits — the change only accumulates.
        Assert.AreEqual(0, TotalViewersChanged(harness), "RecordChange must not emit — changes only accumulate until a due flush");

        // A flush BEFORE the 5s window elapses must not emit either.
        await accumulator.FlushDue(T0.AddSeconds(1));
        Assert.AreEqual(0, TotalViewersChanged(harness), "a flush before the 5s window has elapsed must emit nothing");

        // Once the window is due, the accumulated join flushes as a single ViewersChanged.
        await accumulator.FlushDue(T0 + Flush);
        var batches = ViewersChangedFor(harness, "conn-a");
        Assert.AreEqual(1, batches.Count, "the accumulated change flushes exactly once when the window is due");
        Assert.AreEqual(ChannelId, batches[0].ChannelId);
        Assert.IsTrue(Contains(batches[0].Joined, "alice#1"), "the flushed batch must report alice as joined");
        Assert.IsEmpty(batches[0].Left);
    }

    [Test]
    public async Task FlushDue_EmitsSameBatch_ToAllFocusedViewers()
    {
        var (harness, focus, accumulator) = NewAccumulator();

        // Three viewers all newly join within one window (RecordChange BEFORE each Focus).
        foreach (var (conn, tag) in new[] { ("conn-a", "alice#1"), ("conn-b", "bob#2"), ("conn-d", "dan#4") })
        {
            accumulator.RecordChange(ChannelId, tag, T0);
            focus.Focus(conn, ChannelId, tag);
        }

        await accumulator.FlushDue(T0 + Flush);

        // Decision 5: ONE batch object is delivered to EVERY current focused connection — no per-connection
        // deltas. Assert the payload is the SAME reference across all three recipients.
        var payloadA = harness.PayloadFor("conn-a", ChatEvents.ViewersChanged);
        var payloadB = harness.PayloadFor("conn-b", ChatEvents.ViewersChanged);
        var payloadD = harness.PayloadFor("conn-d", ChatEvents.ViewersChanged);
        Assert.IsNotNull(payloadA);
        Assert.AreSame(payloadA, payloadB, "the SAME batch object must be sent to every focused viewer (no per-connection deltas)");
        Assert.AreSame(payloadA, payloadD, "the SAME batch object must be sent to every focused viewer (no per-connection deltas)");

        var batch = (ViewersChangedDto)payloadA;
        Assert.That(batch.Joined, Is.EquivalentTo(new[] { "alice#1", "bob#2", "dan#4" }), "the shared batch carries every joiner");
        Assert.IsEmpty(batch.Left);
        Assert.AreEqual(1, harness.SignalCount("conn-a", ChatEvents.ViewersChanged), "each focused viewer receives the batch exactly once");
    }

    [Test]
    public async Task JoinThenLeave_WithinWindow_NetsToNoDelta()
    {
        var (harness, focus, accumulator) = NewAccumulator();
        // A stable observer (focused directly, NOT via RecordChange) is a would-be recipient of any batch,
        // so an assertion of "no emit" proves the NET is empty rather than merely "no one was listening".
        focus.Focus("conn-observer", ChannelId, "obs#9");

        // alice joins then leaves, both within the same pre-flush window.
        accumulator.RecordChange(ChannelId, "alice#1", T0);
        focus.Focus("conn-a", ChannelId, "alice#1");
        accumulator.RecordChange(ChannelId, "alice#1", T0.AddSeconds(1));
        focus.Unfocus("conn-a", ChannelId);

        await accumulator.FlushDue(T0 + Flush);

        // Baseline (not-viewing at window start) == current (not-viewing after the leave) → idempotent, no
        // delta. The observer receives nothing.
        Assert.AreEqual(0, TotalViewersChanged(harness), "a join+leave flap within one window must cancel to no delta — no ViewersChanged at all");
    }

    [Test]
    public async Task LeaveThenRejoin_WithinWindow_NetsToNoDelta()
    {
        var (harness, focus, accumulator) = NewAccumulator();
        focus.Focus("conn-observer", ChannelId, "obs#9");

        // Establish alice as an EXISTING viewer, then flush so the window resets with alice = viewing at
        // the new window's baseline (this is what makes the subsequent leave+rejoin a true flap).
        accumulator.RecordChange(ChannelId, "alice#1", T0);
        focus.Focus("conn-a", ChannelId, "alice#1");
        await accumulator.FlushDue(T0 + Flush);
        var afterEstablish = TotalViewersChanged(harness);
        Assert.Greater(afterEstablish, 0, "sanity: the establishing flush emitted alice's join");

        // alice leaves then rejoins within the next window.
        accumulator.RecordChange(ChannelId, "alice#1", T0.AddSeconds(6));
        focus.Unfocus("conn-a", ChannelId);
        accumulator.RecordChange(ChannelId, "alice#1", T0.AddSeconds(7));
        focus.Focus("conn-a", ChannelId, "alice#1");

        await accumulator.FlushDue(T0.AddSeconds(10));

        // Baseline (viewing) == current (viewing) → no delta; the flush emits nothing new.
        Assert.AreEqual(afterEstablish, TotalViewersChanged(harness), "a leave+rejoin flap within one window must cancel to no delta — the second flush emits nothing new");
    }

    [Test]
    public async Task FlushCadence_AtMostEvery5s()
    {
        var (harness, focus, accumulator) = NewAccumulator();

        // Drive a continuous stream: every second a NEW distinct viewer joins AND the 1s-granularity flush
        // service ticks (mirrors Task 15's cadence). Record the `now` of each tick that actually emitted.
        var emitTimes = new List<DateTime>();
        for (var second = 0; second <= 60; second++)
        {
            var now = T0.AddSeconds(second);
            accumulator.RecordChange(ChannelId, $"viewer#{second}", now);
            focus.Focus($"conn-{second}", ChannelId, $"viewer#{second}");

            var before = TotalViewersChanged(harness);
            await accumulator.FlushDue(now);
            if (TotalViewersChanged(harness) > before)
            {
                emitTimes.Add(now);
            }
        }

        Assert.That(emitTimes.Count, Is.GreaterThan(1), "a minute of continuous joins must produce several batched flushes");
        for (var i = 1; i < emitTimes.Count; i++)
        {
            var gapSeconds = (emitTimes[i] - emitTimes[i - 1]).TotalSeconds;
            Assert.That(gapSeconds, Is.GreaterThanOrEqualTo(5), $"consecutive ViewersChanged flushes were {gapSeconds}s apart — below the 5s cadence floor");
        }
    }

    [Test]
    public async Task DrainedWindow_ForDormantChannel_IsEvicted_PreventingUnboundedGrowth()
    {
        var (harness, focus, accumulator) = NewAccumulator();

        // A viewer joins → the channel gets a window.
        accumulator.RecordChange(ChannelId, "alice#1", T0);
        focus.Focus("conn-a", ChannelId, "alice#1");
        Assert.AreEqual(1, accumulator.TrackedChannelCount());

        // Flushing while alice is still focused RETAINS the window — the channel is actively viewed, so its
        // LastFlushedAt is the cadence reference for the next batch.
        await accumulator.FlushDue(T0 + Flush);
        Assert.AreEqual(1, accumulator.TrackedChannelCount(), "an actively-viewed channel keeps its window across a flush");

        // alice leaves (RecordChange BEFORE Unfocus) — the channel goes dormant (no focused connections).
        accumulator.RecordChange(ChannelId, "alice#1", T0.AddSeconds(6));
        focus.Unfocus("conn-a", ChannelId);

        // The next due flush drains the leave AND evicts the now-viewer-less window, so _windows cannot grow
        // unbounded across the process lifetime.
        await accumulator.FlushDue(T0.AddSeconds(10));
        Assert.AreEqual(0, accumulator.TrackedChannelCount(), "a drained window for a channel with no focused connections must be evicted");

        // A later re-focus simply re-creates a fresh window — eviction loses nothing.
        accumulator.RecordChange(ChannelId, "alice#1", T0.AddSeconds(11));
        focus.Focus("conn-a", ChannelId, "alice#1");
        Assert.AreEqual(1, accumulator.TrackedChannelCount(), "a re-focus after eviction re-creates the window");
    }

    // ---- HUB-LEVEL fixture (shared singletons) -----------------------------------------------------

    private const string BattleTagB = "bravo#1";
    private const string BattleTagC = "charlie#2";

    private FakeTimeProvider _time;
    private HubPushCaptureHarness _harness;
    private ViewersAccumulator _accumulator;
    private FocusRegistry _focusRegistry;
    private OnlineMemberRegistry _onlineMemberRegistry;
    private SessionRegistry _sessionRegistry;
    private MessageRateLimiter _messageRateLimiter;
    private ConnectionMapping _connectionMapping;
    private UserDirectoryRepository _userDirectory;
    private MuteRepository _muteRepository;
    private MuteReconciliationService _reconcileService;
    private TicketStore _ticketStore;
    private Mock<IChatAuthenticationService> _authService;
    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private ChannelCreationRateLimiter _channelCreationRateLimiter;
    private SessionStateAssembler _assembler;

    [SetUp]
    public void SetupHubFixture()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(T0, TimeSpan.Zero));
        _harness = new HubPushCaptureHarness();
        _focusRegistry = new FocusRegistry();
        // The accumulator shares the SAME FocusRegistry the hubs mutate — so its baseline capture (in
        // RecordChange) and its current-state read (in FlushDue) see the live roster the hubs produce.
        _accumulator = new ViewersAccumulator(_harness.HubContext, _focusRegistry);

        _onlineMemberRegistry = new OnlineMemberRegistry();
        _sessionRegistry = new SessionRegistry();
        _messageRateLimiter = new MessageRateLimiter();
        _connectionMapping = new ConnectionMapping();
        _userDirectory = new UserDirectoryRepository(MongoClient);
        _muteRepository = new MuteRepository(MongoClient);
        _reconcileService = new MuteReconciliationTestHarness(_connectionMapping, _muteRepository).Service;
        _ticketStore = new TicketStore();

        _authService = new Mock<IChatAuthenticationService>();
        _authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync((W3CUserAuthentication id) =>
                new ChatUserResolution(new ChatUser(id.BattleTag, id.IsAdmin, id.Name, new ProfilePicture(), null, null), true));

        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _channelCreationRateLimiter = new ChannelCreationRateLimiter();
        _assembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            new MessageRepository(MongoClient),
            _muteRepository,
            _onlineMemberRegistry,
            _connectionMapping);
    }

    private ChatHub BuildHub(string connectionId)
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
            new MessageRepository(MongoClient),
            FanOutEngineTestFactory.CreateIgnored(),
            _accumulator,
            new NoOpMentionInboxCleaner(),
            RelationshipProviderTestFactory.CreateIgnored(),
            new UserSettingsRepository(MongoClient),
            new DmInitiationTracker(),
            _authService.Object,
            MentionFanOutTestFactory.CreateIgnored(MongoClient),
            new PresenceInterestRegistry(),
            new MentionInboxRepository(MongoClient));

        hub.Clients = new Mock<IHubCallerClients>().Object;
        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        hub.Context = context.Object;
        hub.Groups = new Mock<IGroupManager>().Object;
        return hub;
    }

    // Registers a live session (SessionRegistry.Register) for battleTag under connectionId — the same
    // in-memory idiom ChatHubFocusTests uses. A second Register for the SAME battleTag DISPLACES the first
    // (MaxConnectionsPerBattleTag == 1), exactly as OnConnectedAsync's connect-time register does.
    private void Register(string connectionId, string battleTag) =>
        _sessionRegistry.Register(
            connectionId,
            new W3CUserAuthentication { BattleTag = battleTag, Name = battleTag.Split('#')[0] },
            null);

    // Seeds the hot-path "IS a member" signal FocusChannel reads (zero DB), the way the assembler would
    // at connect — so FocusChannel takes the member hot path and never touches Mongo.
    private void SeedMembership(string connectionId, string battleTag) =>
        _onlineMemberRegistry.Join(ChannelId, connectionId, new MemberState(battleTag, NotificationLevel.All, 0, ChannelType.Public));

    // ---- HUB-LEVEL: mid-window joiner --------------------------------------------------------------

    [Test]
    public async Task MidWindowJoiner_GetsRosterFromFocusResponse_AndAbsorbsBatchOverlap()
    {
        // Two viewers established, one joins mid-window. The joiner's FocusChannel roster (Task 9) already
        // contains ALL THREE, AND the next shared batch REDUNDANTLY contains the joiner — the client applies
        // ViewersChanged.joined as an idempotent SET union, so the overlap (joiner present in both its
        // initial roster and the batch) is harmless, never a duplicate.
        Register("conn-a", "alpha#1");
        Register("conn-b", BattleTagB);
        SeedMembership("conn-a", "alpha#1");
        SeedMembership("conn-b", BattleTagB);

        var hubA = BuildHub("conn-a");
        var hubB = BuildHub("conn-b");
        Assert.AreEqual(ChatResultCode.Ok, (await hubA.FocusChannel(ChannelId)).Code);
        Assert.AreEqual(ChatResultCode.Ok, (await hubB.FocusChannel(ChannelId)).Code);

        // Flush the establishing window so alpha+bravo are the baseline-viewing set of the next window.
        await _accumulator.FlushDue(T0 + Flush);

        // charlie joins mid next window.
        Register("conn-c", BattleTagC);
        SeedMembership("conn-c", BattleTagC);
        var hubC = BuildHub("conn-c");
        var charlieFocus = await hubC.FocusChannel(ChannelId);

        Assert.AreEqual(ChatResultCode.Ok, charlieFocus.Code);
        Assert.That(charlieFocus.Viewers.Select(v => v.BattleTag),
            Is.EquivalentTo(new[] { "alpha#1", BattleTagB, BattleTagC }),
            "the mid-window joiner's FocusChannel roster (Task 9) already contains ALL active viewers");

        // The next shared batch redundantly contains charlie's join, delivered to every focused viewer
        // (alpha, bravo AND charlie itself).
        await _accumulator.FlushDue(T0.AddSeconds(10));

        var charlieBatches = ViewersChangedFor(_harness, "conn-c");
        Assert.AreEqual(1, charlieBatches.Count, "the joiner receives the shared batch for its own window");
        Assert.IsTrue(Contains(charlieBatches[0].Joined, BattleTagC),
            "the shared batch REDUNDANTLY re-announces the joiner (client absorbs it via idempotent-set application)");
        Assert.IsTrue(Contains(ViewersChangedFor(_harness, "conn-a").Last().Joined, BattleTagC),
            "an already-present viewer receives the SAME batch announcing the new joiner");
    }

    // ---- HUB-LEVEL: the C2 displacement amendment --------------------------------------------------

    [Test]
    public async Task Displacement_ReconnectRefocusWithinWindow_EmitsNoLeave()
    {
        // conn-charlie is a stable observer; conn-old (bravo) is the socket that gets displaced.
        Register("conn-charlie", BattleTagC);
        SeedMembership("conn-charlie", BattleTagC);
        Register("conn-old", BattleTagB);
        SeedMembership("conn-old", BattleTagB);

        var charlieHub = BuildHub("conn-charlie");
        var oldHub = BuildHub("conn-old");
        await charlieHub.FocusChannel(ChannelId);
        await oldHub.FocusChannel(ChannelId);

        // Flush so bravo is an ESTABLISHED viewer at the next window's baseline (a prior batch already told
        // everyone bravo joined).
        await _accumulator.FlushDue(T0 + Flush);

        // A second connect for bravo DISPLACES conn-old (SessionRegistry.Register semantics), then conn-old's
        // OnDisconnectedAsync routes its focus removal through the accumulator (RecordChange BEFORE
        // FocusRegistry.RemoveConnection) — baseline captured = VIEWING, because the removal hasn't happened
        // yet.
        Register("conn-new", BattleTagB);
        SeedMembership("conn-new", BattleTagB);
        await oldHub.OnDisconnectedAsync(null);

        // The NEW connection re-focuses the SAME channel with the SAME battleTag WITHIN the window: another
        // RecordChange (baseline already captured = viewing) then FocusRegistry.Focus restores bravo to the
        // roster.
        var newHub = BuildHub("conn-new");
        await newHub.FocusChannel(ChannelId);

        var beforeFinalFlush = TotalViewersChanged(_harness);
        await _accumulator.FlushDue(T0.AddSeconds(10));

        // C2 amendment: current (viewing, via conn-new) == baseline (viewing) → NO delta. Neither the
        // observer nor anyone else is told bravo left.
        Assert.IsFalse(ViewersChangedFor(_harness, "conn-charlie").Any(v => Contains(v.Left, BattleTagB)),
            "a displaced socket that reconnects and re-focuses within the window must NOT be reported as a leave");
        Assert.AreEqual(beforeFinalFlush, TotalViewersChanged(_harness),
            "the reconnect nets to no delta — the final flush emits nothing new");

        // The roster still contains bravo EXACTLY once (not duplicated by the old+new sockets).
        var roster = _focusRegistry.GetRoster(ChannelId);
        Assert.AreEqual(1, roster.Count(t => string.Equals(t, BattleTagB, StringComparison.OrdinalIgnoreCase)),
            "the roster must contain the reconnected battleTag exactly once");
    }

    [Test]
    public async Task Displacement_NewConnectionNeverRefocuses_EmitsLeaveAtNextFlush()
    {
        // Same setup, but the new connection NEVER re-focuses — a genuine stop-viewing that MUST still be
        // reported to the remaining viewer.
        Register("conn-charlie", BattleTagC);
        SeedMembership("conn-charlie", BattleTagC);
        Register("conn-old", BattleTagB);
        SeedMembership("conn-old", BattleTagB);

        var charlieHub = BuildHub("conn-charlie");
        var oldHub = BuildHub("conn-old");
        await charlieHub.FocusChannel(ChannelId);
        await oldHub.FocusChannel(ChannelId);

        await _accumulator.FlushDue(T0 + Flush);

        Register("conn-new", BattleTagB);
        SeedMembership("conn-new", BattleTagB);
        await oldHub.OnDisconnectedAsync(null);
        // conn-new deliberately does NOT re-focus the channel.

        var before = ViewersChangedFor(_harness, "conn-charlie").Count;
        await _accumulator.FlushDue(T0.AddSeconds(10));

        var charlieBatches = ViewersChangedFor(_harness, "conn-charlie");
        Assert.AreEqual(before + 1, charlieBatches.Count, "the remaining viewer receives one batch for the leave");
        Assert.IsTrue(Contains(charlieBatches.Last().Left, BattleTagB),
            "a genuine stop-viewing (no same-window reconnect-refocus) MUST still be reported as a leave");
        Assert.IsFalse(Contains(charlieBatches.Last().Joined, BattleTagB));
    }

    // ---- HUB-LEVEL: explicit LeaveChannel while staying connected ----------------------------------

    [Test]
    public async Task LeaveChannel_WhileConnected_EmitsLeaveToOtherViewers()
    {
        // conn-charlie is a stable observer; conn-leaver (bravo) is an established, focused viewer who
        // then explicitly LeaveChannels while STAYING connected — a genuine stop-viewing that MUST still
        // be reported to the remaining viewer. Without LeaveChannel routing its roster change through the
        // accumulator (RecordChange BEFORE FocusRegistry.Unfocus), bravo vanishes from the roster with NO
        // `left` delta, leaving charlie's client showing a phantom viewer indefinitely.
        Register("conn-charlie", BattleTagC);
        SeedMembership("conn-charlie", BattleTagC);
        Register("conn-leaver", BattleTagB);
        SeedMembership("conn-leaver", BattleTagB);

        var charlieHub = BuildHub("conn-charlie");
        var leaverHub = BuildHub("conn-leaver");
        await charlieHub.FocusChannel(ChannelId);
        await leaverHub.FocusChannel(ChannelId);

        // Flush so bravo is an ESTABLISHED viewer at the next window's baseline (a prior batch already
        // told everyone bravo joined).
        await _accumulator.FlushDue(T0 + Flush);

        // bravo explicitly leaves the channel while STAYING connected (no displacement, no disconnect).
        await leaverHub.LeaveChannel(ChannelId);

        var before = ViewersChangedFor(_harness, "conn-charlie").Count;
        await _accumulator.FlushDue(T0.AddSeconds(10));

        var charlieBatches = ViewersChangedFor(_harness, "conn-charlie");
        Assert.AreEqual(before + 1, charlieBatches.Count, "the remaining viewer receives one batch for the leave");
        Assert.IsTrue(Contains(charlieBatches.Last().Left, BattleTagB),
            "an explicit LeaveChannel while staying connected MUST be reported as a leave");
        Assert.IsFalse(Contains(charlieBatches.Last().Joined, BattleTagB));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Unit tests for the C3 (Task 13) <see cref="ActivityCoalescer"/> and the
/// <see cref="FanOutEngine.OnMessagePersisted"/> activity-ROUTING extension. Two layers:
/// <list type="bullet">
/// <item>DIRECT coalescer tests drive <see cref="ActivityCoalescer.Offer"/>/<see cref="ActivityCoalescer.FlushDue"/>
/// against a fixed sequence of <c>now</c> values (NO sleeping, NO wall-clock) and a real
/// <see cref="OnlineMemberRegistry"/>, asserting the ≥10s coalescing window, the lossless
/// latest-seq-only payload, and the &gt;100-unread suppression re-checked AT EMIT time.</item>
/// <item>ENGINE-routing tests drive the whole <see cref="FanOutEngine"/> to prove the focused/unfocused
/// and notification-level split (decision 3), and the SHADOW hard constraint that a shadow message
/// produces ZERO <c>ChannelActivity</c> to a non-author.</item>
/// </list>
/// Pure in-memory: a <see cref="HubPushCaptureHarness"/> captures every push; no Mongo, no live hub.
/// </summary>
public class ActivityCoalescerTests
{
    private const string ChannelId = "channel-1";
    private const string MemberConn = "conn-member";
    private const string MemberTag = "Member#1";
    private const string AuthorConn = "conn-author";
    private const string AuthorTag = "Author#7";

    // A fixed base instant. Every decision takes an explicit `now` derived from this — the component is
    // deterministic-time, so the whole "simulated minute" is just arithmetic on T0.
    private static readonly DateTime T0 = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);

    private static ChatChannel Channel() =>
        new ChatChannel { Id = ChannelId, Type = ChannelType.Public };

    private static ChannelMessage Message(long seq = 5, bool shadow = false) =>
        new ChannelMessage
        {
            Id = "message-1",
            ChannelId = ChannelId,
            Seq = seq,
            Sender = new MessageSender { BattleTag = AuthorTag, Name = "Author" },
            Content = "hello world",
            SentAt = T0,
            Shadow = shadow,
        };

    // A coalescer over a registry seeded with ONE level-All member at the given last-read seq.
    private static (HubPushCaptureHarness harness, OnlineMemberRegistry members, ActivityCoalescer coalescer)
        NewCoalescer(long memberLastReadSeq = 0)
    {
        var harness = new HubPushCaptureHarness();
        var members = new OnlineMemberRegistry();
        members.Join(ChannelId, MemberConn, new MemberState(MemberTag, NotificationLevel.All, memberLastReadSeq));
        var coalescer = new ActivityCoalescer(harness.HubContext, members);
        return (harness, members, coalescer);
    }

    // A full engine fixture wired to a real coalescer + registries, for the routing tests.
    private static (HubPushCaptureHarness harness, FocusRegistry focus, OnlineMemberRegistry members, FanOutEngine engine)
        NewEngineFixture()
    {
        var harness = new HubPushCaptureHarness();
        var focus = new FocusRegistry();
        var members = new OnlineMemberRegistry();
        var coalescer = new ActivityCoalescer(harness.HubContext, members);
        var engine = new FanOutEngine(harness.HubContext, focus, members, coalescer);
        return (harness, focus, members, engine);
    }

    private static IReadOnlyList<ChannelActivityDto> ActivityPayloads(HubPushCaptureHarness harness, string connectionId) =>
        harness.SignalsFor(connectionId)
            .Where(s => s.Method == ChatEvents.ChannelActivity)
            .Select(s => (ChannelActivityDto)s.Payload)
            .ToList();

    // ---- Direct coalescer tests --------------------------------------------------------------------

    [Test]
    public async Task FirstActivity_SendsImmediately_WithChannelIdAndLastSeq()
    {
        var (harness, _, coalescer) = NewCoalescer();

        // The FIRST offer for a (connection, channel) has no prior emit, so its window is trivially due
        // (LastSentAt defaults to DateTime.MinValue) — it emits immediately rather than coalescing.
        await coalescer.Offer(MemberConn, ChannelId, lastSeq: 5, T0);

        Assert.AreEqual(1, harness.SignalCount(MemberConn, ChatEvents.ChannelActivity));
        var dto = harness.PayloadFor(MemberConn, ChatEvents.ChannelActivity) as ChannelActivityDto;
        Assert.IsNotNull(dto, "first activity must carry a ChannelActivityDto payload");
        Assert.AreEqual(ChannelId, dto.ChannelId);
        Assert.AreEqual(5, dto.LastSeq);
    }

    [Test]
    public async Task Burst_WithinTenSeconds_CoalescesToOnePendingWithLatestSeq()
    {
        var (harness, _, coalescer) = NewCoalescer();

        await coalescer.Offer(MemberConn, ChannelId, lastSeq: 5, T0);                 // immediate emit — opens the window
        await coalescer.Offer(MemberConn, ChannelId, lastSeq: 6, T0.AddSeconds(1));   // within window — coalesce
        await coalescer.Offer(MemberConn, ChannelId, lastSeq: 7, T0.AddSeconds(2));   // within window — coalesce
        await coalescer.Offer(MemberConn, ChannelId, lastSeq: 8, T0.AddSeconds(3));   // within window — pending is now the LATEST (8)

        // The burst produced NO extra emits — it collapsed into a single pending (not three pushes).
        Assert.AreEqual(1, harness.SignalCount(MemberConn, ChatEvents.ChannelActivity));

        // Once the window elapses, the single pending flushes carrying ONLY the latest seq — coalescing
        // is lossless because the payload is just "the newest seq", so dropping 6 and 7 loses nothing.
        await coalescer.FlushDue(T0.AddSeconds(11));
        var payloads = ActivityPayloads(harness, MemberConn);
        Assert.AreEqual(2, payloads.Count);
        Assert.AreEqual(5, payloads[0].LastSeq);
        Assert.AreEqual(8, payloads[1].LastSeq, "the coalesced flush must carry the LATEST seq of the burst, not an intermediate one");
    }

    [Test]
    public async Task FlushDue_AfterTenSeconds_EmitsPending_ResetsWindow()
    {
        var (harness, _, coalescer) = NewCoalescer();

        await coalescer.Offer(MemberConn, ChannelId, lastSeq: 5, T0);                 // immediate emit — window opens at T0
        await coalescer.Offer(MemberConn, ChannelId, lastSeq: 9, T0.AddSeconds(3));   // coalesce — pending = 9

        // A flush BEFORE the 10s window elapses must not emit the pending.
        await coalescer.FlushDue(T0.AddSeconds(5));
        Assert.AreEqual(1, harness.SignalCount(MemberConn, ChatEvents.ChannelActivity));

        // A flush once the window HAS elapsed emits the pending (latest seq) and resets the window to now.
        await coalescer.FlushDue(T0.AddSeconds(10));
        var afterFlush = ActivityPayloads(harness, MemberConn);
        Assert.AreEqual(2, afterFlush.Count);
        Assert.AreEqual(9, afterFlush[1].LastSeq);

        // Window RESET proof: an offer 1s after the flush coalesces (no immediate emit) instead of firing.
        await coalescer.Offer(MemberConn, ChannelId, lastSeq: 12, T0.AddSeconds(11));
        Assert.AreEqual(2, harness.SignalCount(MemberConn, ChatEvents.ChannelActivity));

        // ...and it flushes only once the NEW window elapses, confirming LastSentAt advanced to T0+10.
        await coalescer.FlushDue(T0.AddSeconds(20));
        Assert.AreEqual(3, harness.SignalCount(MemberConn, ChatEvents.ChannelActivity));
    }

    [Test]
    public async Task Spacing_NeverBelowTenSeconds_UnderContinuousBurst()
    {
        var (harness, _, coalescer) = NewCoalescer();

        // Drive a continuous burst across a simulated minute: every second, offer a new seq AND run the
        // 1s-granularity flush (Task 15's flush service cadence). Record the `now` of each actual emit.
        var emitTimes = new List<DateTime>();
        for (var second = 0; second <= 60; second++)
        {
            var now = T0.AddSeconds(second);
            var before = harness.SignalCount(MemberConn, ChatEvents.ChannelActivity);
            await coalescer.Offer(MemberConn, ChannelId, lastSeq: second + 1, now);
            await coalescer.FlushDue(now);
            var emittedThisTick = harness.SignalCount(MemberConn, ChatEvents.ChannelActivity) - before;
            for (var k = 0; k < emittedThisTick; k++)
            {
                emitTimes.Add(now);
            }
        }

        // Acceptance 1: under a continuous burst the per-(conn,channel) inter-emit spacing is a MINIMUM
        // of 10s — emission only ever happens when the 10s window has elapsed since the last emit.
        Assert.That(emitTimes.Count, Is.GreaterThan(1), "a minute-long continuous burst must produce several coalesced emits");
        for (var i = 1; i < emitTimes.Count; i++)
        {
            var gapSeconds = (emitTimes[i] - emitTimes[i - 1]).TotalSeconds;
            Assert.That(gapSeconds, Is.GreaterThanOrEqualTo(10), $"inter-emit gap {gapSeconds}s dropped below the 10s coalesce floor");
        }
    }

    [Test]
    public async Task Unread_Over100_SuppressesEmission()
    {
        // Member has read nothing (LastReadSeq 0); an offer of seq 150 means unread = 150 > 100.
        var (harness, _, coalescer) = NewCoalescer(memberLastReadSeq: 0);

        await coalescer.Offer(MemberConn, ChannelId, lastSeq: 150, T0);
        // Suppressed at emit time — NO push despite the window being due.
        Assert.AreEqual(0, harness.SignalCount(MemberConn, ChatEvents.ChannelActivity));

        // The window STILL advanced (LastSentAt = T0) even though the emit was suppressed: a second offer
        // within 10s coalesces (no emit) rather than re-firing immediately.
        await coalescer.Offer(MemberConn, ChannelId, lastSeq: 151, T0.AddSeconds(1));
        Assert.AreEqual(0, harness.SignalCount(MemberConn, ChatEvents.ChannelActivity));

        // A due flush is likewise suppressed while unread stays >100.
        await coalescer.FlushDue(T0.AddSeconds(11));
        Assert.AreEqual(0, harness.SignalCount(MemberConn, ChatEvents.ChannelActivity));
    }

    [Test]
    public async Task Unread_DropsBackTo100_ViaMarkRead_ResumesEmission()
    {
        // Start suppressed: unread = 200 - 0 = 200 > 100.
        var (harness, members, coalescer) = NewCoalescer(memberLastReadSeq: 0);
        await coalescer.Offer(MemberConn, ChannelId, lastSeq: 200, T0);
        Assert.AreEqual(0, harness.SignalCount(MemberConn, ChatEvents.ChannelActivity));

        // A MarkRead advances the member's last-read so unread drops to exactly 100 (== threshold, which
        // is NOT >100). Because suppression is re-checked AT EMIT time, the next due offer resumes.
        members.SetLastReadSeq(ChannelId, MemberConn, 100);

        await coalescer.Offer(MemberConn, ChannelId, lastSeq: 200, T0.AddSeconds(11));
        Assert.AreEqual(1, harness.SignalCount(MemberConn, ChatEvents.ChannelActivity));
        var dto = harness.PayloadFor(MemberConn, ChatEvents.ChannelActivity) as ChannelActivityDto;
        Assert.AreEqual(200, dto.LastSeq);
    }

    [Test]
    public async Task PreviewSlot_ExistsInPayload_NullUntilC5()
    {
        var (harness, _, coalescer) = NewCoalescer();

        await coalescer.Offer(MemberConn, ChannelId, lastSeq: 5, T0);

        var dto = harness.PayloadFor(MemberConn, ChatEvents.ChannelActivity) as ChannelActivityDto;
        Assert.IsNotNull(dto);
        // The DM preview slot is part of the C3 wire contract (clients can bind it now) but is ALWAYS
        // null in C3 — only DM channels populate it, and only once C5 lands.
        Assert.IsNull(dto.Preview, "the ChannelActivity preview slot must be null in C3");
    }

    // ---- Engine-routing tests ----------------------------------------------------------------------

    [Test]
    public async Task LevelAll_UnfocusedMember_GetsActivity()
    {
        var (harness, focus, members, engine) = NewEngineFixture();
        // A level-All member who is NOT focused on the channel — the exact recipient of a ChannelActivity.
        members.Join(ChannelId, MemberConn, new MemberState(MemberTag, NotificationLevel.All, LastReadSeq: 0));
        // The author is a separate, focused connection (its own echo is Task 12's concern, not asserted here).
        focus.Focus(AuthorConn, ChannelId, AuthorTag);

        await engine.OnMessagePersisted(Channel(), Message(seq: 7), AuthorConn, isShadow: false, T0);

        // The unfocused level-All member gets a first-activity ChannelActivity immediately (unread 7 ≤ 100)...
        Assert.AreEqual(1, harness.SignalCount(MemberConn, ChatEvents.ChannelActivity));
        var dto = harness.PayloadFor(MemberConn, ChatEvents.ChannelActivity) as ChannelActivityDto;
        Assert.AreEqual(ChannelId, dto.ChannelId);
        Assert.AreEqual(7, dto.LastSeq);
        // ...and NEVER a full MessageReceived (the "no full payloads to unfocused" guardrail).
        Assert.AreEqual(0, harness.SignalCount(MemberConn, ChatEvents.MessageReceived));
    }

    [Test]
    public async Task Sender_UnfocusedLevelAll_GetsNoActivityForOwnMessage()
    {
        var (harness, _, members, engine) = NewEngineFixture();
        // The SENDER is themselves an unfocused level-All member (SendMessage requires membership, NOT
        // focus) — without the sender guard they would self-notify about their own message.
        members.Join(ChannelId, AuthorConn, new MemberState(AuthorTag, NotificationLevel.All, LastReadSeq: 0));

        await engine.OnMessagePersisted(Channel(), Message(seq: 5), senderConnectionId: AuthorConn, isShadow: false, T0);

        Assert.AreEqual(0, harness.SignalCount(AuthorConn, ChatEvents.ChannelActivity), "the sender must never be pinged about their own message");
    }

    [Test]
    public async Task LevelMentions_GetsNothing()
    {
        var (harness, _, members, engine) = NewEngineFixture();
        members.Join(ChannelId, MemberConn, new MemberState(MemberTag, NotificationLevel.Mentions, LastReadSeq: 0));

        await engine.OnMessagePersisted(Channel(), Message(), AuthorConn, isShadow: false, T0);

        // Level Mentions gets no ChannelActivity on the send path (mentions are C6's job, not this one).
        Assert.AreEqual(0, harness.SignalCount(MemberConn, ChatEvents.ChannelActivity));
        Assert.AreEqual(0, harness.SignalCount(MemberConn, ChatEvents.MessageReceived));
    }

    [Test]
    public async Task LevelNone_GetsNothing()
    {
        var (harness, _, members, engine) = NewEngineFixture();
        members.Join(ChannelId, MemberConn, new MemberState(MemberTag, NotificationLevel.None, LastReadSeq: 0));

        await engine.OnMessagePersisted(Channel(), Message(), AuthorConn, isShadow: false, T0);

        Assert.AreEqual(0, harness.SignalCount(MemberConn, ChatEvents.ChannelActivity));
        Assert.AreEqual(0, harness.SignalCount(MemberConn, ChatEvents.MessageReceived));
    }

    [Test]
    public async Task FocusedConnection_GetsNoActivity()
    {
        var (harness, focus, members, engine) = NewEngineFixture();
        // A level-All member who IS focused on the channel: they already receive the full MessageReceived,
        // so they must get NO coalesced activity ping.
        members.Join(ChannelId, MemberConn, new MemberState(MemberTag, NotificationLevel.All, LastReadSeq: 0));
        focus.Focus(MemberConn, ChannelId, MemberTag);

        await engine.OnMessagePersisted(Channel(), Message(), AuthorConn, isShadow: false, T0);

        Assert.AreEqual(0, harness.SignalCount(MemberConn, ChatEvents.ChannelActivity), "a focused member already got the full MessageReceived — no activity ping");
        Assert.AreEqual(1, harness.SignalCount(MemberConn, ChatEvents.MessageReceived));
    }

    [Test]
    public async Task Shadow_UnfocusedLevelAllMember_GetsZeroActivity()
    {
        var (harness, focus, members, engine) = NewEngineFixture();
        // A level-All non-author member, NOT focused — precisely the recipient a shadow post must NOT ping.
        members.Join(ChannelId, MemberConn, new MemberState(MemberTag, NotificationLevel.All, LastReadSeq: 0));
        // The shadow author is a separate focused connection (its own visible echo is delivered — the illusion).
        focus.Focus(AuthorConn, ChannelId, AuthorTag);
        members.Join(ChannelId, AuthorConn, new MemberState(AuthorTag, NotificationLevel.All, LastReadSeq: 0));

        await engine.OnMessagePersisted(Channel(), Message(shadow: true), AuthorConn, isShadow: true, T0);

        // SHADOW hard constraint: the unfocused non-author level-All member gets ZERO ChannelActivity —
        // a shadow message must never surface as an activity/unread ping to a non-author.
        Assert.AreEqual(0, harness.SignalCount(MemberConn, ChatEvents.ChannelActivity));
        Assert.AreEqual(0, harness.SignalCount(MemberConn, ChatEvents.MessageReceived));
        // Sanity: fan-out DID run — the shadow author's own focused connection received its echo (Task 12).
        Assert.AreEqual(1, harness.SignalCount(AuthorConn, ChatEvents.MessageReceived));
    }

    // ---- Lifecycle (disconnect eviction) -----------------------------------------------------------

    [Test]
    public async Task RemoveConnection_DropsState_SoNextOfferIsTreatedAsFirst()
    {
        var (harness, _, coalescer) = NewCoalescer();

        await coalescer.Offer(MemberConn, ChannelId, lastSeq: 5, T0);   // emits, opens window at T0
        Assert.AreEqual(1, coalescer.TrackedChannelCount(MemberConn), "the offer must have created coalescing state");

        coalescer.RemoveConnection(MemberConn);
        Assert.AreEqual(0, coalescer.TrackedChannelCount(MemberConn), "RemoveConnection must drop the connection's coalescing state");

        // With the window evicted, an offer 1s later (well within the OLD 10s window) is treated as a
        // fresh first activity and emits immediately, rather than coalescing against stale state.
        await coalescer.Offer(MemberConn, ChannelId, lastSeq: 6, T0.AddSeconds(1));
        Assert.AreEqual(2, harness.SignalCount(MemberConn, ChatEvents.ChannelActivity));
    }

    [Test]
    public async Task EngineOnConnectionClosed_EvictsCoalescingState()
    {
        var harness = new HubPushCaptureHarness();
        var members = new OnlineMemberRegistry();
        members.Join(ChannelId, MemberConn, new MemberState(MemberTag, NotificationLevel.All, LastReadSeq: 0));
        var coalescer = new ActivityCoalescer(harness.HubContext, members);
        var engine = new FanOutEngine(harness.HubContext, new FocusRegistry(), members, coalescer);

        // A send routes an offer to the unfocused level-All member, creating coalescing state.
        await engine.OnMessagePersisted(Channel(), Message(seq: 5), AuthorConn, isShadow: false, T0);
        Assert.AreEqual(1, coalescer.TrackedChannelCount(MemberConn), "the routed offer must have created coalescing state");

        // The hub's disconnect teardown routes through the engine (which owns the coalescer).
        engine.OnConnectionClosed(MemberConn);
        Assert.AreEqual(0, coalescer.TrackedChannelCount(MemberConn), "OnConnectionClosed must evict the connection's coalescing state");
    }
}

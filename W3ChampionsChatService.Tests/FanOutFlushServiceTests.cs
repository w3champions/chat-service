using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C3 (Task 15) test for the <see cref="FanOutFlushService"/> — the single production
/// <c>BackgroundService</c> whose 1s <see cref="System.Threading.PeriodicTimer"/> drains the Task 13
/// <see cref="ActivityCoalescer"/> and Task 14 <see cref="ViewersAccumulator"/>. The pure aggregator
/// tests (<see cref="ActivityCoalescerTests"/>/<see cref="ViewersAccumulatorTests"/>) already prove the
/// coalescing/batching decisions against explicit <c>now</c> values; THIS test proves the OTHER half of
/// acceptance — that the timer loop actually invokes <c>FlushDue(now)</c> in production, not just in
/// unit tests. It drives the REAL aggregators over the REAL <see cref="PeriodicTimer"/> path using a
/// <see cref="FakeTimeProvider"/>, so there is NO wall-clock sleep: advancing the fake clock past both
/// cadences (10s) must produce BOTH a coalesced <c>ChannelActivity</c> and a batched <c>ViewersChanged</c>.
/// </summary>
public class FanOutFlushServiceTests
{
    private const string ChannelId = "channel-flush";

    // Coalescer arming: an unfocused level-All member whose pending activity flushes after the 10s window.
    private const string MemberConn = "conn-member";
    private const string MemberTag = "Member#1";

    // Accumulator arming: a focused viewer whose baseline-not-viewing → now-viewing flush emits a join.
    private const string ViewerConn = "conn-viewer";
    private const string ViewerTag = "Viewer#2";

    private static readonly DateTime T0 = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<ViewersChangedDto> ViewersChangedFor(HubPushCaptureHarness harness, string connectionId) =>
        harness.SignalsFor(connectionId)
            .Where(s => s.Method == ChatEvents.ViewersChanged)
            .Select(s => (ViewersChangedDto)s.Payload)
            .ToList();

    [Test]
    public async Task FlushService_OnFakeClockAdvance_InvokesFlushDue()
    {
        // One shared harness → one shared mock hub the real aggregators push through, so both flushes are
        // observable through a single capture surface.
        var harness = new HubPushCaptureHarness();

        // --- Arm the coalescer: a level-All unfocused member with a PENDING activity that is only due
        // after the 10s window. The first Offer emits immediately (opening the window at T0); the second
        // coalesces into a pending that a FlushDue at >= T0+10s must drain. Unread (6-0) <= 100 → not
        // suppressed, so the flush is observable.
        var members = new OnlineMemberRegistry();
        members.Join(ChannelId, MemberConn, new MemberState(MemberTag, NotificationLevel.All, LastReadSeq: 0, ChannelType: ChannelType.Public));
        var coalescer = new ActivityCoalescer(harness.HubContext, members);
        await coalescer.Offer(MemberConn, ChannelId, lastSeq: 5, T0); // immediate emit — opens window at T0
        await coalescer.Offer(MemberConn, ChannelId, lastSeq: 6, T0); // within window — coalesce into pending

        // --- Arm the accumulator: RecordChange BEFORE the Focus captures a not-viewing baseline; the Focus
        // makes it viewing, so a due FlushDue (>= T0+5s) emits a `joined` to the focused viewer.
        var focus = new FocusRegistry();
        var accumulator = new ViewersAccumulator(harness.HubContext, focus, ViewersAccumulatorTestFactory.EmptyViewerResolver());
        accumulator.RecordChange(ChannelId, ViewerTag, T0);
        focus.Focus(ViewerConn, ChannelId, ViewerTag);

        // Sanity: nothing beyond the coalescer's single immediate emit has fired yet — the pending activity
        // and the accumulated join are both still waiting on the timer.
        Assert.AreEqual(1, harness.SignalCount(MemberConn, ChatEvents.ChannelActivity),
            "only the coalescer's immediate first-activity emit should have fired before the timer runs");
        Assert.AreEqual(0, ViewersChangedFor(harness, ViewerConn).Count,
            "the accumulated join must not emit until the flush service ticks");

        // --- Arm the flair-refresh coalescer: it has no cadence of its own (every pending tag is due on
        // every flush), so it needs no arming beyond a no-op refresher — this test's acceptance is about
        // the timer loop, not the coalescer's own collapsing behaviour (covered by FlairRefreshCoalescerTests).
        var flairRefreshCoalescer = new FlairRefreshCoalescer(new NoOpFlairRefresher());

        var fakeTime = new FakeTimeProvider(new DateTimeOffset(T0, TimeSpan.Zero));
        var service = new FanOutFlushService(coalescer, accumulator, flairRefreshCoalescer, fakeTime);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // Advance the fake clock one 1s step at a time, yielding after each so the PeriodicTimer loop
            // body gets a turn to process the tick, until the clock is past BOTH cadences (5s and 10s).
            for (var step = 0; step < 12; step++)
            {
                fakeTime.Advance(TimeSpan.FromSeconds(1));
                await Task.Yield();
            }

            // Deterministic, sleep-free wait: spin on Task.Yield (NO wall-clock Task.Delay) giving the
            // thread-pool loop body turns until BOTH flushes have been captured. A correct timer path
            // satisfies this within a few yields; the spin cap only guards against a hang if the loop
            // never fires the FlushDue calls.
            var flushed = await SpinUntil(() =>
                harness.SignalCount(MemberConn, ChatEvents.ChannelActivity) >= 2 &&
                ViewersChangedFor(harness, ViewerConn).Count >= 1);

            Assert.IsTrue(flushed,
                "the 1s PeriodicTimer loop must invoke BOTH FlushDue methods so the pending activity and the accumulated join emit");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // The coalescer's pending (latest seq 6) flushed via the timer path — a SECOND ChannelActivity on
        // top of the immediate one.
        var activities = harness.SignalsFor(MemberConn)
            .Where(s => s.Method == ChatEvents.ChannelActivity)
            .Select(s => (ChannelActivityDto)s.Payload)
            .ToList();
        Assert.AreEqual(2, activities.Count, "the coalesced pending activity must flush exactly once via the timer");
        Assert.AreEqual(6, activities[^1].LastSeq, "the timer-driven flush must carry the latest coalesced seq");

        // The accumulator's join flushed via the same timer path.
        var batches = ViewersChangedFor(harness, ViewerConn);
        Assert.AreEqual(1, batches.Count, "the accumulated join must flush exactly once via the timer");
        Assert.IsTrue(batches[0].Joined.Any(v => string.Equals(v.BattleTag, ViewerTag, StringComparison.OrdinalIgnoreCase)),
            "the timer-driven ViewersChanged batch must report the viewer as joined");
        Assert.IsEmpty(batches[0].Left);
    }

    // Bounded, wall-clock-free spin: yields to the scheduler up to maxSpins times, returning true as soon
    // as the condition holds. Unlike Task.Delay this consumes no real time budget — it only cedes the
    // current turn so the background loop's continuation can run — so it is deterministic rather than a
    // "hope it flushed in N milliseconds" flake.
    private static async Task<bool> SpinUntil(Func<bool> condition, int maxSpins = 100_000)
    {
        for (var i = 0; i < maxSpins; i++)
        {
            if (condition())
            {
                return true;
            }
            await Task.Yield();
        }
        return condition();
    }

    // Arming filler for the third flush participant — this test's acceptance is the timer loop calling
    // all three drains, not the flair coalescer's own behaviour (covered by FlairRefreshCoalescerTests).
    private class NoOpFlairRefresher : IFlairRefresher
    {
        public Task Refresh(string battleTag) => Task.CompletedTask;
    }
}

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
/// <see cref="ActivityCoalescer"/>, Task 14 <see cref="ViewersAccumulator"/>, and the live-flair
/// <see cref="FlairRefreshCoalescer"/>. The pure aggregator tests
/// (<see cref="ActivityCoalescerTests"/>/<see cref="ViewersAccumulatorTests"/>/
/// <see cref="FlairRefreshCoalescerTests"/>) already prove each one's own coalescing/batching decisions;
/// THIS test proves the OTHER half of acceptance — that the timer loop actually invokes all three drains
/// in production, not just in unit tests. It drives the REAL participants over the REAL
/// <see cref="PeriodicTimer"/> path using a <see cref="FakeTimeProvider"/>, so there is NO wall-clock
/// sleep: advancing the fake clock past every cadence (10s) must produce a coalesced
/// <c>ChannelActivity</c>, a batched <c>ViewersChanged</c>, AND an observed flair refresh.
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

    // Flair coalescer arming: a battleTag recorded before the service starts, so the timer loop's third
    // drain must be the thing that gets it refreshed — never invoked directly by the test.
    private const string FlairTag = "Flair#3";

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
        // every flush), so RecordChange before the service even starts is enough to make it due on the
        // very first tick. The refresher RECORDS what it was asked to refresh, so this test can assert
        // the timer loop's third drain actually ran — not just that it was constructed.
        var flairRefresher = new RecordingFlairRefresher();
        var flairRefreshCoalescer = new FlairRefreshCoalescer(flairRefresher);
        flairRefreshCoalescer.RecordChange(FlairTag);

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
                ViewersChangedFor(harness, ViewerConn).Count >= 1 &&
                flairRefresher.Refreshed.Contains(FlairTag));

            Assert.IsTrue(flushed,
                "the 1s PeriodicTimer loop must invoke all three drains so the pending activity, the " +
                "accumulated join, and the pending flair refresh all emit");
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

        // The flair coalescer's pending tag, recorded before the service even started, flushed via the
        // same timer path. This is the crux of this test's fix: deleting the flair drain from
        // FanOutFlushService.ExecuteAsync must make this assertion fail.
        Assert.That(flairRefresher.Refreshed, Is.EqualTo(new[] { FlairTag }),
            "the pending flair refresh must be drained exactly once via the timer");
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

    // Records what it was asked to refresh, so this test can assert the timer loop's third drain
    // genuinely ran — the coalescer's own collapsing/budget behaviour is covered by
    // FlairRefreshCoalescerTests, this class exists only to make the drain OBSERVABLE here.
    private class RecordingFlairRefresher : IFlairRefresher
    {
        public List<string> Refreshed { get; } = new();

        public Task Refresh(string battleTag)
        {
            Refreshed.Add(battleTag);
            return Task.CompletedTask;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using W3ChampionsChatService.FanOut;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Fix round (P1) test for <see cref="FlairRefreshFlushService"/> — the live-flair drain's OWN
/// <c>BackgroundService</c>, split out of <see cref="FanOutFlushService"/> so a website-backend outage
/// during a flair refresh can never stall the unrelated ActivityCoalescer/ViewersAccumulator fan-out on a
/// shared loop (see the service's own doc comment). This test proves the isolated drain still actually
/// runs in production, not just in <see cref="FlairRefreshCoalescerTests"/>'s direct-call unit tests: it
/// drives the REAL <see cref="FlairRefreshCoalescer"/> over the REAL <see cref="PeriodicTimer"/> path
/// using a <see cref="FakeTimeProvider"/>, so there is no wall-clock sleep.
/// <para>
/// This is the coverage that used to live in <c>FanOutFlushServiceTests</c> (moved here verbatim in
/// spirit, not deleted) when the drain lived on the shared service.
/// </para>
/// </summary>
public class FlairRefreshFlushServiceTests
{
    // Recorded before the service even starts, so the timer loop's OWN drain must be the thing that gets
    // it refreshed — never invoked directly by the test.
    private const string FlairTag = "Flair#3";

    private static readonly DateTime T0 = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task FlushService_OnFakeClockAdvance_DrainsTheFlairRefreshCoalescer()
    {
        var flairRefresher = new RecordingFlairRefresher();
        var flairRefreshCoalescer = new FlairRefreshCoalescer(flairRefresher);
        flairRefreshCoalescer.RecordChange(FlairTag);

        var fakeTime = new FakeTimeProvider(new DateTimeOffset(T0, TimeSpan.Zero));
        var service = new FlairRefreshFlushService(flairRefreshCoalescer, fakeTime);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // The coalescer has no cadence of its own — every pending tag is due on the very first tick —
            // so a single 1s advance is enough. Advance a couple of steps for margin, yielding after each
            // so the PeriodicTimer loop body gets a turn to process the tick.
            for (var step = 0; step < 3; step++)
            {
                fakeTime.Advance(TimeSpan.FromSeconds(1));
                await Task.Yield();
            }

            // Deterministic, sleep-free wait: spin on Task.Yield (NO wall-clock Task.Delay). A correct
            // timer path satisfies this within a few yields; the spin cap only guards against a hang if
            // the loop never fires Flush.
            var flushed = await SpinUntil(() => flairRefresher.Refreshed.Contains(FlairTag));

            Assert.IsTrue(flushed,
                "the 1s PeriodicTimer loop must invoke FlairRefreshCoalescer.Flush so the pending refresh emits");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // The crux of this test: deleting the Flush() call from FlairRefreshFlushService.ExecuteAsync
        // must make this assertion fail (mutation-sensitive — verified by hand against that mutation).
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

    // Records what it was asked to refresh, so this test can assert the timer loop's drain genuinely ran
    // — the coalescer's own collapsing/budget behaviour is covered by FlairRefreshCoalescerTests, this
    // class exists only to make the drain OBSERVABLE here.
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

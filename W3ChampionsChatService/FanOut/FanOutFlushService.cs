using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace W3ChampionsChatService.FanOut;

/// <summary>
/// The single production driver that makes the Task 13/14/live-flair fan-out timers actually fire. The
/// <see cref="ActivityCoalescer"/> and <see cref="ViewersAccumulator"/> are PURE, deterministic-time
/// sinks — they emit ONLY when their own cadence (10s coalesce / 5s viewers flush) has elapsed as of the
/// explicit <c>now</c> handed to <c>FlushDue</c>, and NOTHING calls <c>FlushDue</c> outside tests. The
/// <see cref="FlairRefreshCoalescer"/> is the same idea with no cadence of its own — its window IS the
/// flush tick, so every pending battleTag is due on every flush. This hosted service is the caller for
/// all three: a 1s <see cref="PeriodicTimer"/> that, every tick, drains each with the current clock. It
/// is what turns their unit-tested coalescing/batching decisions into live behaviour in production
/// (acceptance: makes tasks 1/2/4 run in production, not just tests).
/// <para>
/// Mirrors <see cref="Domain.WeeklyCleanupService"/>'s do/while +
/// <see cref="PeriodicTimer.WaitForNextTickAsync"/> loop and its catch-log-continue discipline — a single
/// tick's failure is logged and swallowed so the loop never dies. TWO deliberate deviations make the
/// timer deterministically testable: the timer is built with the <see cref="TimeProvider"/> overload and
/// <c>now</c> is read from that same injected clock (never <see cref="DateTime.UtcNow"/>), so a
/// <c>FakeTimeProvider</c> drives the whole path without wall-clock sleeps. Each drain call is isolated
/// in its OWN try/catch so a throw in one can neither crash the loop nor skip the others on the same
/// tick.
/// </para>
/// </summary>
public class FanOutFlushService(
    ActivityCoalescer coalescer,
    ViewersAccumulator accumulator,
    FlairRefreshCoalescer flairRefreshCoalescer,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        do
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;

            // Independent try/catch per aggregator: a failure draining one must not skip the other on this
            // tick, and neither may crash the loop — the next tick retries regardless.
            try
            {
                await coalescer.FlushDue(now);
            }
            catch (Exception e)
            {
                Log.Error(e, "ActivityCoalescer flush failed; will retry next tick");
            }

            try
            {
                await accumulator.FlushDue(now);
            }
            catch (Exception e)
            {
                Log.Error(e, "ViewersAccumulator flush failed; will retry next tick");
            }

            try
            {
                await flairRefreshCoalescer.Flush();
            }
            catch (Exception e)
            {
                Log.Error(e, "FlairRefreshCoalescer flush failed; will retry next tick");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

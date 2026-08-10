using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace W3ChampionsChatService.FanOut;

/// <summary>
/// The single production driver that makes the Task 13/14 fan-out timers actually fire. The
/// <see cref="ActivityCoalescer"/> and <see cref="ViewersAccumulator"/> are PURE, deterministic-time
/// sinks — they emit ONLY when their own cadence (10s coalesce / 5s viewers flush) has elapsed as of the
/// explicit <c>now</c> handed to <c>FlushDue</c>, and NOTHING calls <c>FlushDue</c> outside tests. This
/// hosted service is the caller for both: a 1s <see cref="PeriodicTimer"/> that, every tick, drains each
/// with the current clock. It is what turns their unit-tested coalescing/batching decisions into live
/// behaviour in production (acceptance: makes tasks 1/2 run in production, not just tests).
/// <para>
/// The live-flair <see cref="FlairRefreshCoalescer"/> deliberately does NOT share this loop — see
/// <see cref="FlairRefreshFlushService"/>, its own dedicated driver. A flair refresh is a website-backend
/// HTTP round trip plus Mongo I/O plus per-connection sends, qualitatively heavier than the pure in-memory
/// work here; sharing a loop would let a website-backend outage stall this service's unrelated fan-out
/// for as long as the outage lasts (fix round, P1).
/// </para>
/// <para>
/// Mirrors <see cref="Domain.WeeklyCleanupService"/>'s do/while +
/// <see cref="PeriodicTimer.WaitForNextTickAsync"/> loop and its catch-log-continue discipline — a single
/// tick's failure is logged and swallowed so the loop never dies. TWO deliberate deviations make the
/// timer deterministically testable: the timer is built with the <see cref="TimeProvider"/> overload and
/// <c>now</c> is read from that same injected clock (never <see cref="DateTime.UtcNow"/>), so a
/// <c>FakeTimeProvider</c> drives the whole path without wall-clock sleeps. Each drain call is isolated
/// in its OWN try/catch so a throw in one can neither crash the loop nor skip the other on the same tick.
/// </para>
/// </summary>
public class FanOutFlushService(
    ActivityCoalescer coalescer,
    ViewersAccumulator accumulator,
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
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

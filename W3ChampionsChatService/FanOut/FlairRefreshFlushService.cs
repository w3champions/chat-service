using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace W3ChampionsChatService.FanOut;

/// <summary>
/// The single production driver behind <see cref="FlairRefreshCoalescer"/>. Split out from
/// <see cref="FanOutFlushService"/> (fix round, P1) because a flair refresh is qualitatively different
/// work from that service's other two participants: <see cref="ActivityCoalescer"/> and
/// <see cref="ViewersAccumulator"/> are pure, in-memory, bounded-time sinks, while
/// <see cref="FlairRefreshCoalescer.Flush"/> is a serial loop of website-backend HTTP round trips (each
/// capped at <see cref="Chats.WebsiteBackendRepository"/>'s 2s timeout) plus Mongo I/O plus per-connection
/// SignalR sends. On a shared flush loop, a website-backend outage occupies the loop for up to
/// budget × 2s, stalling the unrelated coalescer/accumulator fan-out globally for as long as the outage
/// lasts — merely shrinking the per-tick budget rescales that stall, it does not remove it. Draining on
/// its OWN <see cref="BackgroundService"/> means a flair-refresh stall can never delay live-chat fan-out,
/// no matter how large or slow the flair burst is.
/// <para>
/// Mirrors <see cref="Domain.WeeklyCleanupService"/>'s do/while + <see cref="PeriodicTimer.WaitForNextTickAsync"/>
/// loop and its catch-log-continue discipline — a single tick's failure is logged and swallowed so the
/// loop never dies. Like <see cref="FanOutFlushService"/>, the timer is built with the
/// <see cref="TimeProvider"/> overload so a <c>FakeTimeProvider</c> drives the whole path deterministically
/// in tests, with no wall-clock sleep.
/// </para>
/// </summary>
public class FlairRefreshFlushService(
    FlairRefreshCoalescer flairRefreshCoalescer,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        do
        {
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

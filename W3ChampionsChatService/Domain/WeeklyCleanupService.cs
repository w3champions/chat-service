using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace W3ChampionsChatService.Domain;

/// <summary>
/// Runs CleanupJobs.RunOnce immediately at startup and then every RetentionPeriods.CleanupInterval
/// (weekly). Failures are logged and retried on the next tick — never crash the service.
/// </summary>
public class WeeklyCleanupService(CleanupJobs cleanupJobs) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RetentionPeriods.CleanupInterval);
        do
        {
            try
            {
                await cleanupJobs.RunOnce(DateTime.UtcNow);
            }
            catch (Exception e)
            {
                Log.Error(e, "Weekly chat cleanup run failed; will retry next tick");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

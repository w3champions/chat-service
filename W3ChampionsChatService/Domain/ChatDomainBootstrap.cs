using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using W3ChampionsChatService.Channels;

namespace W3ChampionsChatService.Domain;

/// <summary>
/// Startup bootstrap: creates all code-managed indexes, then seeds the public catalog.
/// Runs before the app serves traffic (hosted services start ahead of Kestrel).
/// Registered BEFORE WeeklyCleanupService so cleanup always sees indexed collections.
/// </summary>
public class ChatDomainBootstrap(MongoClient mongoClient, PublicChannelSeeder seeder) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ChatDomainIndexes.EnsureAllAsync(mongoClient);
        await seeder.SeedPublicChannels();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

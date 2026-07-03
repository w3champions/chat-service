using System.Threading.Tasks;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Starts ONE ephemeral MongoDB container for the whole test run (NUnit SetUpFixture at the
/// root test namespace). Individual tests get isolation via IntegrationTestBase's per-test
/// DropDatabaseAsync, exactly as before — but against a local throwaway instance instead of
/// the shared remote Mongo. Requires a running Docker daemon (preinstalled on the
/// ubuntu-24.04 CI pool; Docker Desktop/colima locally).
/// </summary>
[SetUpFixture]
public class MongoTestServer
{
    private static MongoDbContainer _container;

    public static MongoClient Client { get; private set; }

    [OneTimeSetUp]
    public async Task StartMongo()
    {
        _container = new MongoDbBuilder("mongo:7.0").Build();
        await _container.StartAsync();
        Client = new MongoClient(_container.GetConnectionString());
    }

    [OneTimeTearDown]
    public async Task StopMongo()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

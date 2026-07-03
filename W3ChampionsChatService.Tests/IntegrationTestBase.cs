using System.Threading.Tasks;
using MongoDB.Driver;
using NUnit.Framework;

namespace W3ChampionsChatService.Tests;

public class IntegrationTestBase
{
    protected MongoClient MongoClient => MongoTestServer.Client;

    [SetUp]
    public async Task Setup()
    {
        await MongoClient.DropDatabaseAsync("W3Champions-Chat-Service");
    }
}

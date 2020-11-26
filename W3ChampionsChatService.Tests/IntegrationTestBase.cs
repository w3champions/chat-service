using System.Threading.Tasks;
using MongoDB.Driver;
using NUnit.Framework;

namespace W3ChampionsChatService.Tests
{
    public class IntegrationTestBase
    {
        //protected readonly MongoClient MongoClient = new MongoClient("mongodb://localhost:27017/");
        protected readonly MongoClient MongoClient = new MongoClient("mongodb://176.28.16.249:3512/");

        [SetUp]
        public async Task Setup()
        {
            await MongoClient.DropDatabaseAsync("W3Champions-Chat-Service");
        }
    }
}
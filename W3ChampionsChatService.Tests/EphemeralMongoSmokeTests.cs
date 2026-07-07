using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;

namespace W3ChampionsChatService.Tests;

public class EphemeralMongoSmokeTests
{
    [Test]
    public async Task EphemeralMongo_RoundTripsADocument()
    {
        var db = MongoTestServer.Client.GetDatabase("W3Champions-Chat-Service");
        var collection = db.GetCollection<BsonDocument>("smoke");

        await collection.InsertOneAsync(new BsonDocument("hello", "world"));
        var found = await collection.Find(new BsonDocument("hello", "world")).FirstOrDefaultAsync();

        Assert.IsNotNull(found);
        Assert.AreEqual("world", found["hello"].AsString);
    }
}

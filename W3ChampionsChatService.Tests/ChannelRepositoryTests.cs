using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Tests;

public class ChannelRepositoryTests : IntegrationTestBase
{
    [Test]
    public async Task Channel_RoundTrips_WithEnumsAsStrings()
    {
        var repo = new ChannelRepository(MongoClient);
        var channel = new ChatChannel
        {
            Type = ChannelType.Dm,
            PairKey = DmPairKey.For("Peter#123", "Wolf#456"),
            RequestState = DmRequestState.Pending,
            LastSeq = 0,
        };

        await repo.Insert(channel);
        var loaded = await repo.Load(channel.Id);

        Assert.AreEqual(ChannelType.Dm, loaded.Type);
        Assert.AreEqual("peter#123|wolf#456", loaded.PairKey);
        Assert.AreEqual(DmRequestState.Pending, loaded.RequestState);
        Assert.IsNull(loaded.ExpiresAt);

        // enum stored as readable string, and stored in the spec-named collection
        var raw = await MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName)
            .GetCollection<BsonDocument>(ChatCollections.Channels)
            .Find(new BsonDocument("_id", channel.Id)).FirstAsync();
        Assert.AreEqual("Dm", raw["Type"].AsString);
        Assert.IsFalse(raw.Contains("ExpiresAt"), "null ExpiresAt must be omitted so TTL never sees it");
    }

    [Test]
    public async Task LoadByNormalizedName_FindsPublicChannel()
    {
        var repo = new ChannelRepository(MongoClient);
        await repo.Insert(new ChatChannel { Type = ChannelType.Public, Name = "W3C Lounge", NormalizedName = "w3c lounge" });

        var loaded = await repo.LoadByNormalizedName(ChannelType.Public, "w3c lounge");

        Assert.IsNotNull(loaded);
        Assert.AreEqual("W3C Lounge", loaded.Name);
    }

    [Test]
    public async Task ChannelIndexes_AreCreated_UniquePairKeyPartial_AndTtl()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        await ChatDomainIndexes.EnsureAllAsync(MongoClient); // idempotent — second run must not throw

        var db = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        var indexes = await (await db.GetCollection<ChatChannel>(ChatCollections.Channels).Indexes.ListAsync()).ToListAsync();

        var pairKey = indexes.Single(i => i["name"] == "ux_pairKey_dm");
        Assert.IsTrue(pairKey["unique"].AsBoolean);
        Assert.AreEqual("Dm", pairKey["partialFilterExpression"]["Type"].AsString);

        var ttl = indexes.Single(i => i["name"] == "ttl_expiresAt");
        Assert.AreEqual(0, ttl["expireAfterSeconds"].ToDouble());
        Assert.AreEqual(1, ttl["key"]["ExpiresAt"].ToInt32());
    }

    [Test]
    public async Task DuplicateDmPairKey_IsRejected_ButNonDmChannelsIgnoreTheIndex()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var repo = new ChannelRepository(MongoClient);
        var pairKey = DmPairKey.For("Peter#123", "Wolf#456");

        await repo.Insert(new ChatChannel { Type = ChannelType.Dm, PairKey = pairKey });
        Assert.ThrowsAsync<MongoWriteException>(
            () => repo.Insert(new ChatChannel { Type = ChannelType.Dm, PairKey = pairKey }));

        // two public channels without PairKey are fine (partial index only covers Type == Dm)
        await repo.Insert(new ChatChannel { Type = ChannelType.Public, Name = "A", NormalizedName = "a" });
        await repo.Insert(new ChatChannel { Type = ChannelType.Public, Name = "B", NormalizedName = "b" });
    }
}

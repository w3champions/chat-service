using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Tests;

public class PublicChannelSeederTests : IntegrationTestBase
{
    [Test]
    public async Task Seeding_CreatesExactlyTheHardcodedCatalog()
    {
        await new PublicChannelSeeder(MongoClient).SeedPublicChannels();

        var channels = await new ChannelRepository(MongoClient).LoadAllOfType(ChannelType.Public);

        Assert.AreEqual(12, channels.Count);
        CollectionAssert.AreEquivalent(DefaultChatRooms.Rooms, channels.Select(c => c.Name).ToList());
        Assert.IsTrue(channels.All(c => c.ExpiresAt == null), "public channels are permanent");
        Assert.IsTrue(channels.All(c => c.NormalizedName == ChannelNames.Normalize(c.Name)));
    }

    [Test]
    public async Task Seeding_Twice_IsIdempotent_AndPreservesLiveCounters()
    {
        var seeder = new PublicChannelSeeder(MongoClient);
        var repo = new ChannelRepository(MongoClient);

        await seeder.SeedPublicChannels();
        var lounge = (await repo.LoadAllOfType(ChannelType.Public)).Single(c => c.Name == "W3C Lounge");
        await repo.AllocateSeq(lounge.Id); // simulate live traffic between restarts

        await seeder.SeedPublicChannels(); // "restart"

        var after = await repo.LoadAllOfType(ChannelType.Public);
        Assert.AreEqual(12, after.Count, "re-seeding must not duplicate rows");
        var loungeAfter = after.Single(c => c.Name == "W3C Lounge");
        Assert.AreEqual(lounge.Id, loungeAfter.Id, "existing row must be kept, not recreated");
        Assert.AreEqual(1L, loungeAfter.LastSeq, "live seq counter must survive re-seeding");
    }
}

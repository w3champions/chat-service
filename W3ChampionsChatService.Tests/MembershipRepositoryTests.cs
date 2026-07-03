using System;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Driver;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Memberships;

namespace W3ChampionsChatService.Tests;

public class MembershipRepositoryTests : IntegrationTestBase
{
    [Test]
    public async Task Membership_RoundTrips_WithDefaults()
    {
        var repo = new MembershipRepository(MongoClient);
        var membership = new ChannelMembership
        {
            ChannelId = "chan1",
            BattleTag = "Peter#123",
            JoinedAt = DateTime.UtcNow,
        };

        await repo.Insert(membership);
        var loaded = await repo.Load("chan1", "Peter#123");

        Assert.AreEqual(MembershipRole.Member, loaded.Role);
        Assert.AreEqual(NotificationLevel.All, loaded.NotificationLevel);
        Assert.AreEqual(0L, loaded.LastReadSeq);
    }

    [Test]
    public async Task LoadForUser_ReturnsAllChannelsOfThatUser_ViaBattleTagIndex()
    {
        var repo = new MembershipRepository(MongoClient);
        await repo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });
        await repo.Insert(new ChannelMembership { ChannelId = "chan2", BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });
        await repo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "Wolf#456", JoinedAt = DateTime.UtcNow });

        var mine = await repo.LoadForUser("Peter#123");

        Assert.AreEqual(2, mine.Count);
        CollectionAssert.AreEquivalent(new[] { "chan1", "chan2" }, mine.Select(m => m.ChannelId).ToList());
    }

    [Test]
    public async Task DuplicateMembership_IsRejectedByUniqueIndex()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var repo = new MembershipRepository(MongoClient);
        await repo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });

        Assert.ThrowsAsync<MongoWriteException>(
            () => repo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow }));
    }

    [Test]
    public async Task MembershipIndexes_AreCreated()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var db = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        var indexes = await (await db.GetCollection<ChannelMembership>(ChatCollections.ChannelMemberships).Indexes.ListAsync()).ToListAsync();

        var unique = indexes.Single(i => i["name"] == "ux_channelId_battleTag");
        Assert.IsTrue(unique["unique"].AsBoolean);
        Assert.AreEqual(1, unique["key"]["ChannelId"].ToInt32());
        Assert.AreEqual(1, unique["key"]["BattleTag"].ToInt32());

        var byUser = indexes.Single(i => i["name"] == "ix_battleTag");
        Assert.AreEqual(1, byUser["key"]["BattleTag"].ToInt32());
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Driver;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Mentions;

namespace W3ChampionsChatService.Tests;

public class MentionInboxRepositoryTests : IntegrationTestBase
{
    [Test]
    public async Task InboxEntry_RoundTrips_AndIsQueriedPerUser()
    {
        var repo = new MentionInboxRepository(MongoClient);
        var entry = new MentionInboxEntry
        {
            BattleTag = "Peter#123",
            ChannelId = "chan1",
            MessageId = "msg1",
            AuthorBattleTag = "Wolf#456",
            AuthorName = "Wolf",
            Excerpt = "hey @Peter check this",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
        };
        await repo.Insert(entry);
        await repo.Insert(new MentionInboxEntry
        {
            BattleTag = "Other#999",
            ChannelId = "chan1",
            MessageId = "msg2",
            AuthorBattleTag = "Wolf#456",
            AuthorName = "Wolf",
            Excerpt = "other",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
        });

        var mine = await repo.LoadForUser("Peter#123");

        Assert.AreEqual(1, mine.Count);
        Assert.AreEqual("msg1", mine[0].MessageId);
        Assert.IsNull(mine[0].ReadAt);
    }

    [Test]
    public async Task MentionInboxIndexes_AreCreated()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var db = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        var indexes = await (await db.GetCollection<MentionInboxEntry>(ChatCollections.MentionInbox).Indexes.ListAsync()).ToListAsync();

        Assert.AreEqual(1, indexes.Single(i => i["name"] == "ix_battleTag")["key"]["BattleTag"].ToInt32());
        Assert.AreEqual(0, indexes.Single(i => i["name"] == "ttl_expiresAt")["expireAfterSeconds"].ToDouble());
    }
}

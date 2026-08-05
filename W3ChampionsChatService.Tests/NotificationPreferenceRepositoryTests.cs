using System;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Driver;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Memberships;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// PR36 follow-up (D2), fix round 1 (F4): direct repository-level coverage for
/// <see cref="NotificationPreferenceRepository"/> — previously exercised only indirectly through the hub
/// and fan-out test suites (which is how the ctor's original <c>_id</c>/ObjectId deserialization bug was
/// actually caught, see task-1-report.md). Mirrors <see cref="MembershipRepositoryTests"/>' index-assertion
/// style: <see cref="ChatDomainIndexes.EnsureAllAsync"/> is never called by <c>IntegrationTestBase</c>
/// itself (it only drops the DB), so every index-dependent test here calls it explicitly first.
/// </summary>
public class NotificationPreferenceRepositoryTests : IntegrationTestBase
{
    [Test]
    public async Task Upsert_Then_Load_RoundTrips()
    {
        var repo = new NotificationPreferenceRepository(MongoClient);
        var now = DateTime.UtcNow;

        await repo.Upsert("Peter#123", "chan1", NotificationLevel.None, now);
        var loaded = await repo.Load("Peter#123", "chan1");

        Assert.IsNotNull(loaded);
        Assert.AreEqual(NotificationLevel.None, loaded.NotificationLevel);
        Assert.That((loaded.UpdatedAt - now).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task Load_NoPreferenceEverSet_ReturnsNull()
    {
        var repo = new NotificationPreferenceRepository(MongoClient);

        Assert.IsNull(await repo.Load("nobody#1", "chan1"),
            "null means 'no opinion' — callers must never treat it as an implicit None");
    }

    [Test]
    public async Task Upsert_RepeatedSets_LastWriteWins_NeverAccumulatesDuplicateRows()
    {
        var repo = new NotificationPreferenceRepository(MongoClient);
        var now = DateTime.UtcNow;

        await repo.Upsert("Peter#123", "chan1", NotificationLevel.None, now);
        await repo.Upsert("Peter#123", "chan1", NotificationLevel.Mentions, now.AddMinutes(1));

        var loaded = await repo.Load("Peter#123", "chan1");
        Assert.AreEqual(NotificationLevel.Mentions, loaded.NotificationLevel, "the LAST set must win");

        var collection = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName)
            .GetCollection<NotificationPreference>(ChatCollections.NotificationPreferences);
        var count = await collection.CountDocumentsAsync(p => p.BattleTag == "peter#123" && p.ChannelId == "chan1");
        Assert.AreEqual(1, count, "a repeated set must upsert the SAME row, never accumulate duplicates");
    }

    // Fix round 1 (F3): a mutation that removed NormalizeTag's ToLowerInvariant() would pass every OTHER
    // test in this suite unnoticed (they always write and read under the SAME casing). Writing under one
    // casing and reading under another asymmetric casing pins the normalization at both the Upsert AND
    // Load call sites.
    [Test]
    public async Task Upsert_MixedCase_IsReadableByDifferentCasing_PinsNormalizeTag()
    {
        var repo = new NotificationPreferenceRepository(MongoClient);
        var now = DateTime.UtcNow;

        await repo.Upsert("Wolf#456", "chan1", NotificationLevel.None, now);

        Assert.IsNotNull(await repo.Load("wolf#456", "chan1"), "a lowercase read must resolve an upper/mixed-case write");
        Assert.IsNotNull(await repo.Load("WOLF#456", "chan1"), "an uppercase read must resolve it too");

        var collection = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName)
            .GetCollection<NotificationPreference>(ChatCollections.NotificationPreferences);
        var stored = await collection.Find(p => p.ChannelId == "chan1").FirstOrDefaultAsync();
        Assert.AreEqual("wolf#456", stored.BattleTag, "the durable row is stored lowercased regardless of the write-time casing");
    }

    [Test]
    public async Task NotificationPreferenceIndexes_AreCreated()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var db = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        var indexes = await (await db.GetCollection<NotificationPreference>(ChatCollections.NotificationPreferences).Indexes.ListAsync()).ToListAsync();

        var unique = indexes.Single(i => i["name"] == "ux_battleTag_channelId");
        Assert.IsTrue(unique["unique"].AsBoolean);
        Assert.AreEqual(1, unique["key"]["BattleTag"].ToInt32());
        Assert.AreEqual(1, unique["key"]["ChannelId"].ToInt32());
    }

    [Test]
    public async Task DuplicateNotificationPreference_RawInsert_IsRejectedByUniqueIndex()
    {
        // A RAW insert (bypassing Upsert's findAndModify) proves the unique index itself is what makes
        // Upsert's last-write-wins semantics well-defined — mirrors MembershipRepositoryTests'
        // DuplicateMembership_IsRejectedByUniqueIndex.
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var collection = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName)
            .GetCollection<NotificationPreference>(ChatCollections.NotificationPreferences);
        await collection.InsertOneAsync(new NotificationPreference
        {
            BattleTag = "peter#123",
            ChannelId = "chan1",
            NotificationLevel = NotificationLevel.None,
            UpdatedAt = DateTime.UtcNow,
        });

        Assert.ThrowsAsync<MongoWriteException>(() => collection.InsertOneAsync(new NotificationPreference
        {
            BattleTag = "peter#123",
            ChannelId = "chan1",
            NotificationLevel = NotificationLevel.Mentions,
            UpdatedAt = DateTime.UtcNow,
        }));
    }
}

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
        // Seeded LOWERCASED — the C6 T5 storage convention (MentionFanOut always lowercases BattleTag
        // before Insert); LoadForUser's own normalization is pinned separately below
        // (LoadForUser_IsCaseInsensitive_MatchesJwtCasedCaller).
        var entry = new MentionInboxEntry
        {
            BattleTag = "peter#123",
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
            BattleTag = "other#999",
            ChannelId = "chan1",
            MessageId = "msg2",
            AuthorBattleTag = "Wolf#456",
            AuthorName = "Wolf",
            Excerpt = "other",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
        });

        var mine = await repo.LoadForUser("peter#123");

        Assert.AreEqual(1, mine.Count);
        Assert.AreEqual("msg1", mine[0].MessageId);
        Assert.IsNull(mine[0].ReadAt);
    }

    // ---------------------------------------------------------------------------------------------
    // C6 T6 (D6 + boyscout fix): every read/update below normalizes its battleTag argument to the
    // lowercased mention-inbox key convention (mirrors MembershipRepository) — a caller may pass the
    // JWT-cased identity battleTag straight through, exactly like ChatHub does.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task LoadForUser_IsCaseInsensitive_MatchesJwtCasedCaller()
    {
        var repo = new MentionInboxRepository(MongoClient);
        await repo.Insert(NewEntry("peter#123", DateTime.UtcNow));

        var mine = await repo.LoadForUser("PETER#123");

        Assert.That(mine, Has.Count.EqualTo(1),
            "LoadForUser must normalize a mixed/upper-cased caller tag to match the lowercased stored key");
    }

    [Test]
    public async Task LoadForUser_NewestFirst()
    {
        var repo = new MentionInboxRepository(MongoClient);
        var now = DateTime.UtcNow;
        var older = await InsertReturning(repo, NewEntry("peter#123", now.AddMinutes(-5)));
        var newer = await InsertReturning(repo, NewEntry("peter#123", now));

        var mine = await repo.LoadForUser("peter#123");

        Assert.That(mine.Select(e => e.Id), Is.EqualTo(new[] { newer.Id, older.Id }));
    }

    [Test]
    public async Task LoadForUser_CapsAtMentionInboxMaxEntries()
    {
        // Bulk-seeded via the RAW collection (not the one-by-one repo.Insert) purely for test speed —
        // mirrors MentionInboxIndexes_AreCreated's direct-collection access below. One entry per minute
        // going backward so CreatedAt ordering is unambiguous; the newest ChatLimits.MentionInboxMaxEntries
        // entries are indices [0, cap).
        var db = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        var collection = db.GetCollection<MentionInboxEntry>(ChatCollections.MentionInbox);
        var now = DateTime.UtcNow;
        const int seedCount = ChatLimits.MentionInboxMaxEntries + 5;
        var entries = Enumerable.Range(0, seedCount)
            .Select(i => NewEntry("peter#123", now.AddMinutes(-i)))
            .ToList();
        await collection.InsertManyAsync(entries);

        var repo = new MentionInboxRepository(MongoClient);
        var mine = await repo.LoadForUser("peter#123");

        Assert.That(mine, Has.Count.EqualTo(ChatLimits.MentionInboxMaxEntries),
            "LoadForUser must cap at ChatLimits.MentionInboxMaxEntries even when more entries exist");
        // The newest MentionInboxMaxEntries entries (i = 0..cap-1) must be the ones returned.
        var expectedIds = entries.Take(ChatLimits.MentionInboxMaxEntries).Select(e => e.Id).ToHashSet();
        Assert.That(mine.Select(e => e.Id), Is.SubsetOf(expectedIds));
    }

    [Test]
    public async Task MarkRead_SetsReadAt_ForOwnerAndUnreadOnly()
    {
        var repo = new MentionInboxRepository(MongoClient);
        var now = DateTime.UtcNow;
        var mine = await InsertReturning(repo, NewEntry("peter#123", now));
        var someoneElses = await InsertReturning(repo, NewEntry("wolf#456", now));

        // Mixed casing on the argument — MarkRead must normalize it (D6/boyscout fix).
        var modified = await repo.MarkRead(new[] { mine.Id, someoneElses.Id }, "PETER#123", now);

        Assert.That(modified, Is.EqualTo(1), "only the caller's OWN entry among the listed ids may match");
        var reloadedMine = (await repo.LoadForUser("peter#123")).Single();
        // Tolerance-based (mirrors MessageRepositoryTests' MarkDeleted assertions): Mongo's BSON
        // DateTime is millisecond-precision, so an exact-tick comparison against DateTime.UtcNow would
        // flake on sub-millisecond truncation during the round-trip.
        Assert.That(reloadedMine.ReadAt.HasValue && (reloadedMine.ReadAt.Value - now).Duration() < TimeSpan.FromSeconds(1), Is.True);
        var reloadedOthers = (await repo.LoadForUser("wolf#456")).Single();
        Assert.That(reloadedOthers.ReadAt, Is.Null, "an id belonging to another user's row must never be acked");
    }

    [Test]
    public async Task MarkRead_AlreadyRead_SecondCallDoesNotOverwriteReadAt()
    {
        var repo = new MentionInboxRepository(MongoClient);
        var firstAck = DateTime.UtcNow;
        var entry = await InsertReturning(repo, NewEntry("peter#123", firstAck));

        await repo.MarkRead(new[] { entry.Id }, "peter#123", firstAck);
        var secondModified = await repo.MarkRead(new[] { entry.Id }, "peter#123", firstAck.AddMinutes(5));

        Assert.That(secondModified, Is.EqualTo(0), "an already-read entry must be excluded from a re-ack's update");
        var reloaded = (await repo.LoadForUser("peter#123")).Single();
        // Tolerance-based (BSON millisecond precision — see the class doc note above): the load-bearing
        // assertion is that ReadAt is still close to the FIRST ack (firstAck), NOT 5 minutes later.
        Assert.That(reloaded.ReadAt.HasValue && (reloaded.ReadAt.Value - firstAck).Duration() < TimeSpan.FromSeconds(1), Is.True,
            "ReadAt must keep its FIRST-seen value, not the second (5-minutes-later) ack's timestamp");
    }

    [Test]
    public async Task MarkAllRead_SetsReadAtForEveryUnreadEntry_ForOwnerOnly()
    {
        var repo = new MentionInboxRepository(MongoClient);
        var now = DateTime.UtcNow;
        await repo.Insert(NewEntry("peter#123", now));
        await repo.Insert(NewEntry("peter#123", now));
        await repo.Insert(NewEntry("wolf#456", now));

        var modified = await repo.MarkAllRead("PETER#123", now);

        Assert.That(modified, Is.EqualTo(2));
        Assert.That(
            (await repo.LoadForUser("peter#123")).All(e => e.ReadAt.HasValue && (e.ReadAt.Value - now).Duration() < TimeSpan.FromSeconds(1)),
            Is.True);
        Assert.That((await repo.LoadForUser("wolf#456")).Single().ReadAt, Is.Null,
            "MarkAllRead must never touch another user's entries");
    }

    [Test]
    public async Task CountUnread_CountsOnlyUnreadForOwner_IsCaseInsensitive()
    {
        var repo = new MentionInboxRepository(MongoClient);
        var now = DateTime.UtcNow;
        await repo.Insert(NewEntry("peter#123", now));
        var readEntry = NewEntry("peter#123", now);
        readEntry.ReadAt = now;
        await repo.Insert(readEntry);
        await repo.Insert(NewEntry("wolf#456", now));

        var count = await repo.CountUnread("PETER#123");

        Assert.That(count, Is.EqualTo(1), "only the caller's OWN unread (ReadAt == null) entries count");
    }

    private static MentionInboxEntry NewEntry(string battleTag, DateTime createdAt) => new()
    {
        BattleTag = battleTag,
        ChannelId = "chan1",
        MessageId = Guid.NewGuid().ToString(),
        AuthorBattleTag = "wolf#456",
        AuthorName = "Wolf",
        Excerpt = "hey",
        CreatedAt = createdAt,
        ExpiresAt = createdAt.AddDays(30),
    };

    private static async Task<MentionInboxEntry> InsertReturning(MentionInboxRepository repo, MentionInboxEntry entry)
    {
        await repo.Insert(entry);
        return entry;
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

using System;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Driver;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Mentions;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C6 Task 7 (D7) — repo-level coverage of the real <see cref="MentionInboxCleaner"/>: the batch
/// delete itself, and its no-op safety against empty/unknown id lists (the shape
/// <c>ChatHub.PurgeMessagesFromUser</c> routinely triggers — most purged messages were never
/// mentioned at all). The hub-level call-site behavior (exact ids passed, audit-before-cleaner
/// ordering) is covered end-to-end in <see cref="ModerationIntegrationTests"/>.
/// </summary>
public class MentionInboxCleanerTests : IntegrationTestBase
{
    private static MentionInboxEntry NewEntry(string battleTag, string messageId) => new()
    {
        BattleTag = battleTag,
        ChannelId = "chan1",
        MessageId = messageId,
        AuthorBattleTag = "wolf#456",
        AuthorName = "Wolf",
        Excerpt = "hey",
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(30),
    };

    private IMongoCollection<MentionInboxEntry> RawInbox() =>
        MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName).GetCollection<MentionInboxEntry>(ChatCollections.MentionInbox);

    [Test]
    public async Task RemoveForMessages_DeletesAllEntriesForListedMessages_LeavesOthers()
    {
        var repo = new MentionInboxRepository(MongoClient);
        // Two entries reference "msg1" (a message can mention several eligible targets — one entry
        // per target), one references "msg2" (also listed for removal), and one references "msg3"
        // (the untouched control — never listed).
        await repo.Insert(NewEntry("peter#123", "msg1"));
        await repo.Insert(NewEntry("wolf#456", "msg1"));
        await repo.Insert(NewEntry("peter#123", "msg2"));
        await repo.Insert(NewEntry("peter#123", "msg3"));

        var cleaner = new MentionInboxCleaner(MongoClient);
        await cleaner.RemoveForMessages(new[] { "msg1", "msg2" });

        var remaining = await RawInbox().Find(FilterDefinition<MentionInboxEntry>.Empty).ToListAsync();
        Assert.That(remaining.Select(e => e.MessageId), Is.EquivalentTo(new[] { "msg3" }),
            "every entry referencing a listed message id must be gone; the untouched control (msg3) must survive");
    }

    [Test]
    public async Task RemoveForMessages_EmptyOrUnknownIds_NoOp_NoThrow()
    {
        var repo = new MentionInboxRepository(MongoClient);
        await repo.Insert(NewEntry("peter#123", "msg-control"));

        var cleaner = new MentionInboxCleaner(MongoClient);

        // PurgeMessagesFromUser's common case: zero eligible ids (nothing to clean up at all).
        Assert.DoesNotThrowAsync(() => cleaner.RemoveForMessages(Array.Empty<string>()),
            "an empty id list must be a safe no-op, not a throw");

        // PurgeMessagesFromUser's OTHER common case: eligible ids that reference no mention at all —
        // most of a purged user's messages were never mentioned in.
        Assert.DoesNotThrowAsync(() => cleaner.RemoveForMessages(new[] { "never-mentioned-1", "never-mentioned-2" }),
            "a batch of ids matching no existing entry must be a safe no-op, not a throw");

        var remaining = await RawInbox().Find(FilterDefinition<MentionInboxEntry>.Empty).ToListAsync();
        Assert.That(remaining.Select(e => e.MessageId), Is.EquivalentTo(new[] { "msg-control" }),
            "neither no-op call may touch an entry it was never asked to remove");
    }
}

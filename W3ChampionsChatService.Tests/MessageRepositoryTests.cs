using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Messages;

namespace W3ChampionsChatService.Tests;

public class MessageRepositoryTests : IntegrationTestBase
{
    private static ChannelMessage NewMessage(string channelId, long seq, string sender = "Peter#123") => new()
    {
        ChannelId = channelId,
        Seq = seq,
        Sender = new MessageSender { BattleTag = sender, Name = sender.Split('#')[0] },
        Content = "hello",
        SentAt = DateTime.UtcNow,
    };

    [Test]
    public async Task Message_RoundTrips_WithSenderSnapshot()
    {
        var repo = new MessageRepository(MongoClient);
        var message = NewMessage("chan1", 1);
        message.ExpiresAt = DateTime.UtcNow.AddDays(30);

        await repo.Insert(message);
        var loaded = await repo.Load(message.Id);

        Assert.AreEqual("chan1", loaded.ChannelId);
        Assert.AreEqual(1L, loaded.Seq);
        Assert.AreEqual("Peter#123", loaded.Sender.BattleTag);
        Assert.AreEqual("Peter", loaded.Sender.Name);
        Assert.IsNull(loaded.Deleted);
        Assert.IsFalse(loaded.Shadow);
        Assert.IsTrue((loaded.ExpiresAt.Value - message.ExpiresAt.Value).Duration() < TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task DuplicateChannelSeq_IsRejectedByUniqueIndex()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var repo = new MessageRepository(MongoClient);
        await repo.Insert(NewMessage("chan1", 7));

        Assert.ThrowsAsync<MongoWriteException>(() => repo.Insert(NewMessage("chan1", 7)));
        await repo.Insert(NewMessage("chan2", 7)); // same seq in another channel is fine
    }

    [Test]
    public async Task MessageIndexes_AreCreated()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var db = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        var indexes = await (await db.GetCollection<ChannelMessage>(ChatCollections.Messages).Indexes.ListAsync()).ToListAsync();

        var unique = indexes.Single(i => i["name"] == "ux_channelId_seq");
        Assert.IsTrue(unique["unique"].AsBoolean);
        Assert.AreEqual(1, unique["key"]["ChannelId"].ToInt32());
        Assert.AreEqual(1, unique["key"]["Seq"].ToInt32());

        var ttl = indexes.Single(i => i["name"] == "ttl_expiresAt");
        Assert.AreEqual(0, ttl["expireAfterSeconds"].ToDouble());
    }

    [Test]
    public async Task Messages_HasCaseInsensitiveSenderIndex()
    {
        // D6: ix_sender_sentAt (case-SENSITIVE) is replaced by ix_sender_ci_sentAt, collated at
        // strength 2 ("en") — the collation LoadPurgeableBySender's query MUST match for Mongo to
        // actually use this index (D6 note). Ensure is idempotent even against a pre-migration
        // database that still has the old-named index (best-effort drop-by-name).
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var db = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        var indexes = await (await db.GetCollection<ChannelMessage>(ChatCollections.Messages).Indexes.ListAsync()).ToListAsync();

        var senderIndex = indexes.Single(i => i["name"] == "ix_sender_ci_sentAt");
        Assert.AreEqual(1, senderIndex["key"]["Sender.BattleTag"].ToInt32());
        Assert.AreEqual(1, senderIndex["key"]["SentAt"].ToInt32());
        Assert.AreEqual("en", senderIndex["collation"]["locale"].AsString);
        Assert.AreEqual(2, senderIndex["collation"]["strength"].ToInt32());

        Assert.IsFalse(indexes.Any(i => i["name"] == "ix_sender_sentAt"),
            "the superseded case-sensitive index must be gone after ensure");
    }

    [Test]
    public async Task Messages_CaseInsensitiveSenderIndex_EnsureIsIdempotent_AgainstPreMigrationDatabase()
    {
        // Simulate a database that still carries the OLD-named index from before this migration —
        // Ensure must best-effort drop it (swallowing IndexNotFound is the OTHER branch, covered by
        // the test above running against a fresh DB) and still converge on the new index, never throw.
        var messages = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName).GetCollection<ChannelMessage>(ChatCollections.Messages);
        await messages.Indexes.CreateOneAsync(new CreateIndexModel<ChannelMessage>(
            Builders<ChannelMessage>.IndexKeys.Ascending(m => m.Sender.BattleTag).Ascending(m => m.SentAt),
            new CreateIndexOptions { Name = "ix_sender_sentAt" }));

        Assert.DoesNotThrowAsync(() => ChatDomainIndexes.EnsureAllAsync(MongoClient));

        var indexes = await (await messages.Indexes.ListAsync()).ToListAsync();
        Assert.IsFalse(indexes.Any(i => i["name"] == "ix_sender_sentAt"), "the pre-existing old-named index must be dropped");
        Assert.IsTrue(indexes.Any(i => i["name"] == "ix_sender_ci_sentAt"), "the new collated index must exist");
    }

    [Test]
    public async Task UserRead_ExcludesDeleted_AndForeignShadow_ButShowsOwnShadow()
    {
        var repo = new MessageRepository(MongoClient);
        var normal = NewMessage("chan1", 1, "Peter#123");
        var deleted = NewMessage("chan1", 2, "Peter#123");
        var foreignShadow = NewMessage("chan1", 3, "Wolf#456");
        foreignShadow.Shadow = true;
        var ownShadow = NewMessage("chan1", 4, "Peter#123");
        ownShadow.Shadow = true;

        await repo.Insert(normal);
        await repo.Insert(deleted);
        await repo.Insert(foreignShadow);
        await repo.Insert(ownShadow);
        await repo.MarkDeleted(deleted.Id, "Mod#1", DateTime.UtcNow);

        var petersView = await repo.LoadForUser("chan1", "Peter#123");
        CollectionAssert.AreEqual(new[] { 1L, 4L }, petersView.Select(m => m.Seq).ToArray());

        var othersView = await repo.LoadForUser("chan1", "Other#999");
        CollectionAssert.AreEqual(new[] { 1L }, othersView.Select(m => m.Seq).ToArray());
    }

    // ── C6 T2 (D8, C5 handoff) — shadow-self disjunct becomes case-insensitive (visibility boundary) ──

    [Test]
    public async Task UserVisible_OwnShadowRow_VisibleRegardlessOfViewerCasing()
    {
        // The self-match leg is now an anchored case-insensitive regex: a viewer must see their OWN
        // shadow-banned messages even when their connection's casing differs from the sender snapshot
        // casing captured at send time. The foreign shadow row must remain hidden.
        var repo = new MessageRepository(MongoClient);
        var ownShadow = NewMessage("chan1", 1, "Peter#123");
        ownShadow.Shadow = true;
        var foreignShadow = NewMessage("chan1", 2, "Wolf#456");
        foreignShadow.Shadow = true;

        await repo.Insert(ownShadow);
        await repo.Insert(foreignShadow);

        var petersView = await repo.LoadForUser("chan1", "peter#123"); // differs in case from stored "Peter#123"

        CollectionAssert.AreEqual(new[] { 1L }, petersView.Select(m => m.Seq).ToArray(),
            "the viewer's own shadow row must be visible despite the casing mismatch; the foreign shadow row must stay hidden");
    }

    [Test]
    public async Task CountUserVisibleAfter_SameCasingRule()
    {
        var repo = new MessageRepository(MongoClient);
        var ownShadow = NewMessage("chan1", 1, "Peter#123");
        ownShadow.Shadow = true;
        var foreignShadow = NewMessage("chan1", 2, "Wolf#456");
        foreignShadow.Shadow = true;

        await repo.Insert(ownShadow);
        await repo.Insert(foreignShadow);

        var count = await repo.CountUserVisibleAfter("chan1", "peter#123", 0);

        Assert.AreEqual(1, count, "CountUserVisibleAfter must apply the same case-insensitive self-match rule as UserVisible");
    }

    [Test]
    public async Task UserVisible_DeletedAndForeignShadow_StillExcluded()
    {
        // Regression pin: the case-insensitive self-match fix must ONLY ever affect the viewer's own
        // shadow rows. It must never widen visibility into (a) soft-deleted rows or (b) another
        // sender's shadow rows — including the adversarial casing/prefix case below, which proves the
        // regex is EXACT-match anchored (^...$) rather than a bare prefix match.
        var repo = new MessageRepository(MongoClient);
        var normal = NewMessage("chan1", 1, "Peter#123");
        var deleted = NewMessage("chan1", 2, "Peter#123");
        var foreignShadow = NewMessage("chan1", 3, "Wolf#456");
        foreignShadow.Shadow = true;
        // Anchoring edge case: the viewer's tag "Peter#12" is a strict PREFIX of this shadow row's
        // sender "Peter#123" (a DIFFERENT user) — an unanchored (missing trailing $) regex would
        // incorrectly match here and leak a stranger's shadow row.
        var prefixCollisionShadow = NewMessage("chan1", 4, "Peter#123");
        prefixCollisionShadow.Shadow = true;

        await repo.Insert(normal);
        await repo.Insert(deleted);
        await repo.Insert(foreignShadow);
        await repo.Insert(prefixCollisionShadow);
        await repo.MarkDeleted(deleted.Id, "Mod#1", DateTime.UtcNow);

        var view = await repo.LoadForUser("chan1", "Peter#12");

        CollectionAssert.AreEqual(new[] { 1L }, view.Select(m => m.Seq).ToArray(),
            "a viewer whose tag is merely a prefix of another shadow sender's tag must not see it — the anchor must be exact");
    }

    [Test]
    public async Task UserVisible_ShadowSelfRegexResidual_StillUsesChannelIdSeqIndex_NotCollscan()
    {
        // Reviewer finding on C6 T2: ShadowVisibleToSelf's anchored case-insensitive regex is
        // deliberately a residual predicate (evaluated on the FETCH stage, after ux_channelId_seq has
        // already bounded the scan by ChannelId) rather than a query-level Collation — a Collation
        // would force Mongo to pick an index sharing that exact collation for every predicate, and
        // ux_channelId_seq has none, so the query would silently regress to a full COLLSCAN on this
        // hot read path (every message page load). This pins that index-selection guarantee so a
        // future "helpful" Collation fix for the casing issue regresses loudly instead of silently.
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var repo = new MessageRepository(MongoClient);
        for (var seq = 1; seq <= 20; seq++)
        {
            await repo.Insert(NewMessage("chan1", seq, seq % 5 == 0 ? "Wolf#456" : "Peter#123"));
        }

        // The exact filter UserVisible builds for a viewer whose casing differs from the stored
        // sender snapshot — i.e. the shape that exercises the anchored regex disjunct.
        var filter = MessageRepository.UserVisible("chan1", "peter#123");
        var registry = BsonSerializer.SerializerRegistry;
        var renderedFilter = filter.Render(new RenderArgs<ChannelMessage>(registry.GetSerializer<ChannelMessage>(), registry));

        var db = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        var explainCommand = new BsonDocument
        {
            { "explain", new BsonDocument
                {
                    { "find", ChatCollections.Messages },
                    { "filter", renderedFilter },
                }
            },
            { "verbosity", "queryPlanner" },
        };

        var explain = await db.RunCommandAsync<BsonDocument>(explainCommand);
        var winningPlan = explain["queryPlanner"]["winningPlan"].AsBsonDocument;

        Assert.AreEqual("FETCH", winningPlan["stage"].AsString,
            "the shadow-self regex disjunct must be applied as a residual FETCH-stage filter, not baked into index bounds");
        var inputStage = winningPlan["inputStage"].AsBsonDocument;
        Assert.AreEqual("IXSCAN", inputStage["stage"].AsString,
            "UserVisible must still be served by an index scan on this hot read path, never a full collection scan");
        Assert.AreEqual("ux_channelId_seq", inputStage["indexName"].AsString,
            "a future Collation-based fix for the shadow-self casing rule must not silently drop this query off ux_channelId_seq");
    }

    [Test]
    public async Task ModeratorRead_IncludesEverything_WithFlagsIntact()
    {
        var repo = new MessageRepository(MongoClient);
        var normal = NewMessage("chan1", 1);
        var deleted = NewMessage("chan1", 2);
        var shadow = NewMessage("chan1", 3, "Wolf#456");
        shadow.Shadow = true;

        await repo.Insert(normal);
        await repo.Insert(deleted);
        await repo.Insert(shadow);
        var deletedAt = DateTime.UtcNow;
        await repo.MarkDeleted(deleted.Id, "Mod#1", deletedAt);

        var modView = await repo.LoadForModerator("chan1");

        Assert.AreEqual(3, modView.Count);
        var flaggedDeleted = modView.Single(m => m.Seq == 2);
        Assert.AreEqual("Mod#1", flaggedDeleted.Deleted.By);
        Assert.IsTrue((flaggedDeleted.Deleted.At - deletedAt).Duration() < TimeSpan.FromSeconds(1));
        Assert.IsTrue(modView.Single(m => m.Seq == 3).Shadow);
    }

    [Test]
    public async Task MarkDeleted_LeavesTheDocumentInPlace_PhysicalRemovalIsTtlOnly()
    {
        var repo = new MessageRepository(MongoClient);
        var message = NewMessage("chan1", 1);
        await repo.Insert(message);

        await repo.MarkDeleted(message.Id, "Mod#1", DateTime.UtcNow);

        var stillThere = await repo.Load(message.Id);
        Assert.IsNotNull(stillThere, "soft delete must never remove the record — moderators need it");
        Assert.IsNotNull(stillThere.Deleted);
    }

    [Test]
    public async Task LoadPageBefore_ReturnsNewestFirstWindow_ExclusiveOfBeforeSeq()
    {
        var repo = new MessageRepository(MongoClient);
        for (var seq = 1; seq <= 10; seq++)
        {
            await repo.Insert(NewMessage("chan1", seq));
        }

        var page = await repo.LoadPageBefore("chan1", "Peter#123", 8, 3);

        CollectionAssert.AreEqual(new[] { 5L, 6L, 7L }, page.Select(m => m.Seq).ToArray());
    }

    [Test]
    public async Task LoadPageBefore_NullBeforeSeq_ReturnsLatestPage()
    {
        var repo = new MessageRepository(MongoClient);
        for (var seq = 1; seq <= 10; seq++)
        {
            await repo.Insert(NewMessage("chan1", seq));
        }

        var page = await repo.LoadPageBefore("chan1", "Peter#123", null, 3);

        CollectionAssert.AreEqual(new[] { 8L, 9L, 10L }, page.Select(m => m.Seq).ToArray());
    }

    [Test]
    public async Task LoadPageBefore_PagesBackwards_NoGapsNoDupes_AcrossConcurrentInsert()
    {
        var repo = new MessageRepository(MongoClient);
        for (var seq = 1; seq <= 10; seq++)
        {
            await repo.Insert(NewMessage("chan1", seq));
        }

        var firstPage = await repo.LoadPageBefore("chan1", "Peter#123", null, 5);
        CollectionAssert.AreEqual(new[] { 6L, 7L, 8L, 9L, 10L }, firstPage.Select(m => m.Seq).ToArray());

        // Simulate a send arriving while the client is still paging backwards.
        await repo.Insert(NewMessage("chan1", 11));

        var minSeqOfFirstPage = firstPage.Min(m => m.Seq);
        var secondPage = await repo.LoadPageBefore("chan1", "Peter#123", minSeqOfFirstPage, 5);
        CollectionAssert.AreEqual(new[] { 1L, 2L, 3L, 4L, 5L }, secondPage.Select(m => m.Seq).ToArray());

        var union = firstPage.Select(m => m.Seq).Concat(secondPage.Select(m => m.Seq)).OrderBy(s => s).ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(1, 10).Select(i => (long)i).ToArray(), union);
        Assert.AreEqual(10, union.Distinct().Count(), "pages must not overlap across the concurrent insert");
    }

    [Test]
    public async Task LoadPageAround_ReturnsTargetPlusWindowBothSides()
    {
        var repo = new MessageRepository(MongoClient);
        for (var seq = 1; seq <= 21; seq++)
        {
            await repo.Insert(NewMessage("chan1", seq));
        }

        var page = await repo.LoadPageAround("chan1", "Peter#123", 11, 10);

        CollectionAssert.AreEqual(Enumerable.Range(6, 11).Select(i => (long)i).ToArray(), page.Select(m => m.Seq).ToArray());
    }

    [Test]
    public async Task LoadPage_RespectsUserVisibleFilter()
    {
        var repo = new MessageRepository(MongoClient);
        var normal = NewMessage("chan1", 1, "Peter#123");
        var deleted = NewMessage("chan1", 2, "Peter#123");
        var foreignShadow = NewMessage("chan1", 3, "Wolf#456");
        foreignShadow.Shadow = true;
        var ownShadow = NewMessage("chan1", 4, "Peter#123");
        ownShadow.Shadow = true;

        await repo.Insert(normal);
        await repo.Insert(deleted);
        await repo.Insert(foreignShadow);
        await repo.Insert(ownShadow);
        await repo.MarkDeleted(deleted.Id, "Mod#1", DateTime.UtcNow);

        var before = await repo.LoadPageBefore("chan1", "Peter#123", null, 10);
        CollectionAssert.AreEqual(new[] { 1L, 4L }, before.Select(m => m.Seq).ToArray());

        var around = await repo.LoadPageAround("chan1", "Peter#123", 1, 10);
        CollectionAssert.AreEqual(new[] { 1L, 4L }, around.Select(m => m.Seq).ToArray());
    }

    [Test]
    public async Task LoadPageBefore_ClampsLimitToMessagePageSize()
    {
        var repo = new MessageRepository(MongoClient);
        var inserts = Enumerable.Range(1, ChatLimits.MessagePageSize + 5)
            .Select(seq => repo.Insert(NewMessage("chan1", seq)));
        await Task.WhenAll(inserts);

        var page = await repo.LoadPageBefore("chan1", "Peter#123", null, ChatLimits.MessagePageSize * 10);

        Assert.AreEqual(ChatLimits.MessagePageSize, page.Count);
    }

    [Test]
    public async Task LoadPageBefore_ClampsLimitToMinimumOne()
    {
        var repo = new MessageRepository(MongoClient);
        for (var seq = 1; seq <= 5; seq++)
        {
            await repo.Insert(NewMessage("chan1", seq));
        }

        // A limit of 0 would pass straight through to MongoDB.Driver's .Limit(0) — which means
        // "no limit" and returns every matching document — unless the floor clamps it to 1 first.
        var page = await repo.LoadPageBefore("chan1", "Peter#123", null, 0);

        Assert.AreEqual(1, page.Count);
    }

    [Test]
    public async Task LoadPageAround_TargetNearChannelStart_ReturnsAvailableWindowNoThrow()
    {
        var repo = new MessageRepository(MongoClient);
        for (var seq = 1; seq <= 5; seq++)
        {
            await repo.Insert(NewMessage("chan1", seq));
        }

        // Requested window (half=10 before/after) is far larger than the available history;
        // the window should simply shrink to what exists, not throw or gap.
        var page = await repo.LoadPageAround("chan1", "Peter#123", 2, 20);

        CollectionAssert.AreEqual(new[] { 1L, 2L, 3L, 4L, 5L }, page.Select(m => m.Seq).ToArray());
        Assert.AreEqual(5, page.Select(m => m.Id).Distinct().Count(), "no duplicate messages in the window");
    }

    // ── C4 Task 1 — moderator paging (D3/D6/D7 building blocks, consumed by later C4 tasks) ──

    [Test]
    public async Task LoadPageBeforeForModerator_IncludesDeletedAndShadow_FlagsIntact()
    {
        var repo = new MessageRepository(MongoClient);
        var normal = NewMessage("chan1", 1, "Peter#123");
        var deleted = NewMessage("chan1", 2, "Peter#123");
        var foreignShadow = NewMessage("chan1", 3, "Wolf#456");
        foreignShadow.Shadow = true;

        await repo.Insert(normal);
        await repo.Insert(deleted);
        await repo.Insert(foreignShadow);
        await repo.MarkDeleted(deleted.Id, "Mod#1", DateTime.UtcNow);

        var page = await repo.LoadPageBeforeForModerator("chan1", null, 10);

        CollectionAssert.AreEqual(new[] { 1L, 2L, 3L }, page.Select(m => m.Seq).ToArray());
        Assert.IsNotNull(page.Single(m => m.Seq == 2).Deleted, "moderator paging must include soft-deleted rows with the flag intact");
        Assert.IsTrue(page.Single(m => m.Seq == 3).Shadow, "moderator paging must include foreign shadow rows with the flag intact");
    }

    [Test]
    public async Task LoadPageAroundForModerator_IncludesDeletedAndShadow()
    {
        var repo = new MessageRepository(MongoClient);
        var normal = NewMessage("chan1", 1, "Peter#123");
        var deleted = NewMessage("chan1", 2, "Peter#123");
        var foreignShadow = NewMessage("chan1", 3, "Wolf#456");
        foreignShadow.Shadow = true;

        await repo.Insert(normal);
        await repo.Insert(deleted);
        await repo.Insert(foreignShadow);
        await repo.MarkDeleted(deleted.Id, "Mod#1", DateTime.UtcNow);

        var page = await repo.LoadPageAroundForModerator("chan1", 2, 10);

        CollectionAssert.AreEqual(new[] { 1L, 2L, 3L }, page.Select(m => m.Seq).ToArray());
        Assert.IsNotNull(page.Single(m => m.Seq == 2).Deleted);
        Assert.IsTrue(page.Single(m => m.Seq == 3).Shadow);
    }

    [Test]
    public async Task ModeratorPaging_SameSeqAnchoring_AsUserPaging()
    {
        // Page-walk parity: LoadPageBeforeForModerator must be seq-anchored exactly like
        // LoadPageBefore (immune to concurrent appends) — only the visibility filter differs.
        var repo = new MessageRepository(MongoClient);
        for (var seq = 1; seq <= 10; seq++)
        {
            await repo.Insert(NewMessage("chan1", seq));
        }

        var firstPage = await repo.LoadPageBeforeForModerator("chan1", null, 5);
        CollectionAssert.AreEqual(new[] { 6L, 7L, 8L, 9L, 10L }, firstPage.Select(m => m.Seq).ToArray());

        await repo.Insert(NewMessage("chan1", 11));

        var minSeqOfFirstPage = firstPage.Min(m => m.Seq);
        var secondPage = await repo.LoadPageBeforeForModerator("chan1", minSeqOfFirstPage, 5);
        CollectionAssert.AreEqual(new[] { 1L, 2L, 3L, 4L, 5L }, secondPage.Select(m => m.Seq).ToArray());

        var union = firstPage.Select(m => m.Seq).Concat(secondPage.Select(m => m.Seq)).OrderBy(s => s).ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(1, 10).Select(i => (long)i).ToArray(), union);
        Assert.AreEqual(10, union.Distinct().Count(), "pages must not overlap across the concurrent insert");
    }

    // ── C4 Task 1 (D7) — CountUserVisibleAfter, indexed range count for later unread math ────

    [Test]
    public async Task CountUserVisibleAfter_ExcludesForeignShadowAndDeleted_IncludesOwnShadow()
    {
        var repo = new MessageRepository(MongoClient);
        var normal = NewMessage("chan1", 1, "Peter#123");
        var deleted = NewMessage("chan1", 2, "Peter#123");
        var foreignShadow = NewMessage("chan1", 3, "Wolf#456");
        foreignShadow.Shadow = true;
        var ownShadow = NewMessage("chan1", 4, "Peter#123");
        ownShadow.Shadow = true;
        var trailing = NewMessage("chan1", 5, "Peter#123");

        await repo.Insert(normal);
        await repo.Insert(deleted);
        await repo.Insert(foreignShadow);
        await repo.Insert(ownShadow);
        await repo.Insert(trailing);
        await repo.MarkDeleted(deleted.Id, "Mod#1", DateTime.UtcNow);

        var count = await repo.CountUserVisibleAfter("chan1", "Peter#123", 0);

        // Visible-to-Peter rows with seq > 0: seq 1 (normal), 4 (own shadow), 5 (trailing) = 3.
        // Excluded: seq 2 (deleted), seq 3 (foreign shadow).
        Assert.AreEqual(3, count);
    }

    [Test]
    public async Task CountUserVisibleAfter_ZeroWhenCaughtUp()
    {
        var repo = new MessageRepository(MongoClient);
        for (var seq = 1; seq <= 5; seq++)
        {
            await repo.Insert(NewMessage("chan1", seq));
        }

        var count = await repo.CountUserVisibleAfter("chan1", "Peter#123", 5);

        Assert.AreEqual(0, count);
    }

    // ── 2026-08-05 PR36 feedback (Part 1) — CountUserVisibleAfterMany, one aggregation round-trip ────

    [Test]
    public async Task CountUserVisibleAfterMany_MatchesSingleVersion_AcrossMultipleChannels()
    {
        var repo = new MessageRepository(MongoClient);

        // chan1: same mix as CountUserVisibleAfter_ExcludesForeignShadowAndDeleted_IncludesOwnShadow —
        // exercises the SAME visibility predicates (deleted + foreign-shadow exclusion, own-shadow
        // inclusion) inside the batched $match.
        var normal = NewMessage("chan1", 1, "Peter#123");
        var deleted = NewMessage("chan1", 2, "Peter#123");
        var foreignShadow = NewMessage("chan1", 3, "Wolf#456");
        foreignShadow.Shadow = true;
        var ownShadow = NewMessage("chan1", 4, "Peter#123");
        ownShadow.Shadow = true;
        var trailing = NewMessage("chan1", 5, "Peter#123");
        await repo.Insert(normal);
        await repo.Insert(deleted);
        await repo.Insert(foreignShadow);
        await repo.Insert(ownShadow);
        await repo.Insert(trailing);
        await repo.MarkDeleted(deleted.Id, "Mod#1", DateTime.UtcNow);

        // chan2: caught up (a real zero-count channel with the cursor at the tip) — must be ABSENT from
        // the batched result, never a spurious zero entry.
        for (var seq = 1; seq <= 3; seq++)
        {
            await repo.Insert(NewMessage("chan2", seq));
        }

        // chan3: no messages at all — the "genuinely no rows ever" zero-count case.
        var cursors = new[]
        {
            new ChannelUnreadCursor("chan1", 0),
            new ChannelUnreadCursor("chan2", 3),
            new ChannelUnreadCursor("chan3", 0),
        };

        var batched = await repo.CountUserVisibleAfterMany(cursors, "Peter#123");

        var singleChan1 = await repo.CountUserVisibleAfter("chan1", "Peter#123", 0);
        var singleChan2 = await repo.CountUserVisibleAfter("chan2", "Peter#123", 3);
        var singleChan3 = await repo.CountUserVisibleAfter("chan3", "Peter#123", 0);

        Assert.AreEqual(singleChan1, batched.GetValueOrDefault("chan1", 0), "chan1 (mixed visible/deleted/shadow) must match the single-version count");
        Assert.AreEqual(0, singleChan2, "sanity: chan2 is caught up under the single version too");
        Assert.AreEqual(singleChan2, batched.GetValueOrDefault("chan2", 0), "chan2 (caught up) must match the single-version count");
        Assert.AreEqual(0, singleChan3, "sanity: chan3 has no rows under the single version too");
        Assert.AreEqual(singleChan3, batched.GetValueOrDefault("chan3", 0), "chan3 (no rows) must match the single-version count");

        CollectionAssert.AreEqual(new[] { "chan1" }, batched.Keys.ToList(),
            "a zero-count channel (caught up OR no rows) must be ABSENT from the dictionary, not present with value 0");
    }

    [Test]
    public async Task CountUserVisibleAfterMany_EmptyInput_ReturnsEmptyWithoutQuerying()
    {
        var repo = new MessageRepository(MongoClient);
        // Present so an unguarded `$or: []` (which Mongo treats as match-everything) would wrongly
        // surface this channel — proving the empty-input guard short-circuits before any query.
        await repo.Insert(NewMessage("chan1", 1, "Peter#123"));

        var result = await repo.CountUserVisibleAfterMany(Array.Empty<ChannelUnreadCursor>(), "Peter#123");

        Assert.AreEqual(0, result.Count,
            "empty input must short-circuit to an empty result, never match-all via an unguarded $or: []");
    }

    // ── C4 Task 1 (D6) — purge query building blocks (consumed by a later C4 task) ───────────

    [Test]
    public async Task LoadPurgeableBySender_ReturnsNonDeletedOnly()
    {
        var repo = new MessageRepository(MongoClient);
        var live1 = NewMessage("chan1", 1, "Peter#123");
        var live2 = NewMessage("chan2", 2, "Peter#123");
        var alreadyDeleted = NewMessage("chan1", 3, "Peter#123");
        var otherSender = NewMessage("chan1", 4, "Wolf#456");

        await repo.Insert(live1);
        await repo.Insert(live2);
        await repo.Insert(alreadyDeleted);
        await repo.Insert(otherSender);
        await repo.MarkDeleted(alreadyDeleted.Id, "Mod#1", DateTime.UtcNow);

        var purgeable = await repo.LoadPurgeableBySender("Peter#123");

        CollectionAssert.AreEquivalent(
            new[] { (live1.Id, live1.ChannelId), (live2.Id, live2.ChannelId) },
            purgeable.Select(p => (p.Id, p.ChannelId)).ToArray());
    }

    [Test]
    public async Task LoadPurgeableBySender_IsCaseInsensitive()
    {
        // LEGACY-gap fix (Chats/History.cs DeleteMessagesFromUser used case-SENSITIVE `==`): a
        // mixed-case argument must still match the stored sender casing.
        var repo = new MessageRepository(MongoClient);
        var message = NewMessage("chan1", 1, "Peter#123");

        await repo.Insert(message);

        var purgeable = await repo.LoadPurgeableBySender("PETER#123");

        Assert.AreEqual(1, purgeable.Count);
        Assert.AreEqual(message.Id, purgeable[0].Id);
    }

    [Test]
    public async Task MarkDeletedMany_SetsDeletedByAt_OnAllIds_LeavesOthers()
    {
        var repo = new MessageRepository(MongoClient);
        var target1 = NewMessage("chan1", 1, "Peter#123");
        var target2 = NewMessage("chan2", 2, "Peter#123");
        var untouched = NewMessage("chan1", 3, "Wolf#456");

        await repo.Insert(target1);
        await repo.Insert(target2);
        await repo.Insert(untouched);

        var deletedAt = DateTime.UtcNow;
        await repo.MarkDeletedMany([target1.Id, target2.Id], "Mod#1", deletedAt);

        var loaded1 = await repo.Load(target1.Id);
        var loaded2 = await repo.Load(target2.Id);
        var loadedUntouched = await repo.Load(untouched.Id);

        Assert.AreEqual("Mod#1", loaded1.Deleted.By);
        Assert.IsTrue((loaded1.Deleted.At - deletedAt).Duration() < TimeSpan.FromSeconds(1));
        Assert.AreEqual("Mod#1", loaded2.Deleted.By);
        Assert.IsNull(loadedUntouched.Deleted, "a message not in the id list must be left untouched");
    }

    [Test]
    public async Task MarkDeletedMany_DoesNotTouchExpiresAt()
    {
        var repo = new MessageRepository(MongoClient);
        var target = NewMessage("chan1", 1, "Peter#123");
        target.ExpiresAt = DateTime.UtcNow.AddDays(30);
        await repo.Insert(target);

        await repo.MarkDeletedMany([target.Id], "Mod#1", DateTime.UtcNow);

        var loaded = await repo.Load(target.Id);
        Assert.IsNotNull(loaded.ExpiresAt, "moderation must never touch the TTL field — physical removal stays TTL-only");
        Assert.IsTrue((loaded.ExpiresAt.Value - target.ExpiresAt.Value).Duration() < TimeSpan.FromSeconds(1));
    }

    // ── C4 Task 4 directive (a) — conditional soft-delete (closes the double-delete TOCTOU) ──────

    [Test]
    public async Task MarkDeleted_ReturnsTrue_WhenItModifiesARow()
    {
        var repo = new MessageRepository(MongoClient);
        var message = NewMessage("chan1", 1, "Peter#123");
        await repo.Insert(message);

        var modified = await repo.MarkDeleted(message.Id, "Mod#1", DateTime.UtcNow);

        Assert.IsTrue(modified, "a fresh soft-delete must report it modified the row");
    }

    [Test]
    public async Task MarkDeleted_ReturnsFalse_WhenAlreadyDeleted_PreservesOriginalAttribution()
    {
        // Directive (a): the write is now conditional on Deleted == null, so a concurrent double-delete
        // can never overwrite the FIRST moderator's attribution (or re-fire downstream side-effects).
        var repo = new MessageRepository(MongoClient);
        var message = NewMessage("chan1", 1, "Peter#123");
        await repo.Insert(message);
        var firstAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await repo.MarkDeleted(message.Id, "FirstMod#1", firstAt);

        var modified = await repo.MarkDeleted(message.Id, "SecondMod#2", DateTime.UtcNow);

        Assert.IsFalse(modified, "a second delete of an already-deleted row must report no modification");
        var reloaded = await repo.Load(message.Id);
        Assert.AreEqual("FirstMod#1", reloaded.Deleted.By, "the first moderator's attribution must be preserved");
        Assert.AreEqual(firstAt, reloaded.Deleted.At);
    }

    [Test]
    public async Task MarkDeletedMany_SkipsAlreadyDeleted_ReturnsNewlyModifiedCount()
    {
        // Directive (a): the bulk write filters Deleted == null, so a re-purge only newly-deletes and
        // the returned count reflects the ACTUAL modifications (the count the audit/UI is based on).
        var repo = new MessageRepository(MongoClient);
        var fresh1 = NewMessage("chan1", 1, "Peter#123");
        var fresh2 = NewMessage("chan1", 2, "Peter#123");
        var alreadyDeleted = NewMessage("chan1", 3, "Peter#123");
        await repo.Insert(fresh1);
        await repo.Insert(fresh2);
        await repo.Insert(alreadyDeleted);
        var firstAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await repo.MarkDeleted(alreadyDeleted.Id, "FirstMod#1", firstAt);

        var modifiedCount = await repo.MarkDeletedMany(
            [fresh1.Id, fresh2.Id, alreadyDeleted.Id], "Mod#2", DateTime.UtcNow);

        Assert.AreEqual(2, modifiedCount, "only the two non-deleted rows are newly soft-deleted");
        var reloadedAlready = await repo.Load(alreadyDeleted.Id);
        Assert.AreEqual("FirstMod#1", reloadedAlready.Deleted.By, "the already-deleted row keeps its original attribution");
        Assert.AreEqual(firstAt, reloadedAlready.Deleted.At);
    }

    // ── C7 Task 4 — channel-teardown hard purge (net-new; DISTINCT from moderation soft-delete) ──

    [Test]
    public async Task DeleteAllForChannel_RemovesOnlyThatChannelsMessages()
    {
        var repo = new MessageRepository(MongoClient);
        var target1 = NewMessage("chan1", 1);
        var target2 = NewMessage("chan1", 2);
        var otherChannel = NewMessage("chan2", 1);
        await repo.Insert(target1);
        await repo.Insert(target2);
        await repo.Insert(otherChannel);

        await repo.DeleteAllForChannel("chan1");

        Assert.IsNull(await repo.Load(target1.Id));
        Assert.IsNull(await repo.Load(target2.Id));
        Assert.IsNotNull(await repo.Load(otherChannel.Id), "a different channel's messages must be untouched");
    }

    [Test]
    public async Task DeleteAllForChannel_RemovesSoftDeletedAndShadowMessagesToo()
    {
        // This is a HARD purge (channel teardown), distinct from moderation soft-delete: soft-deleted
        // rows are still physically present pending the 30d TTL, and shadow-banned rows are ordinary
        // physical rows too — both must be removed just like any other row in the channel.
        var repo = new MessageRepository(MongoClient);
        var normal = NewMessage("chan1", 1);
        var softDeleted = NewMessage("chan1", 2);
        var shadow = NewMessage("chan1", 3, "Wolf#456");
        shadow.Shadow = true;
        await repo.Insert(normal);
        await repo.Insert(softDeleted);
        await repo.Insert(shadow);
        await repo.MarkDeleted(softDeleted.Id, "Mod#1", DateTime.UtcNow);

        await repo.DeleteAllForChannel("chan1");

        Assert.IsNull(await repo.Load(normal.Id));
        Assert.IsNull(await repo.Load(softDeleted.Id), "soft-deleted rows are still physical rows and must be hard-purged too");
        Assert.IsNull(await repo.Load(shadow.Id), "shadow-banned rows are still physical rows and must be hard-purged too");
    }
}

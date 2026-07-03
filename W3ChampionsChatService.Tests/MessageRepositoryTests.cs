using System;
using System.Linq;
using System.Threading.Tasks;
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

        var senderIndex = indexes.Single(i => i["name"] == "ix_sender_sentAt");
        Assert.AreEqual(1, senderIndex["key"]["Sender.BattleTag"].ToInt32());
        Assert.AreEqual(1, senderIndex["key"]["SentAt"].ToInt32());

        var ttl = indexes.Single(i => i["name"] == "ttl_expiresAt");
        Assert.AreEqual(0, ttl["expireAfterSeconds"].ToDouble());
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
}

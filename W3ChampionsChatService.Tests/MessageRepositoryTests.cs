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
}

using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Messages;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Post-game chat Plan A Task 1 — the system-message shape on <see cref="ChannelMessage"/>:
/// BSON round-trip, the legacy-document default, and the dedupe uniqueness guarantee.
/// </summary>
public class SystemMessageModelTests : IntegrationTestBase
{
    private MessageRepository _messages;

    [SetUp]
    public void SetupBeforeEach() => _messages = new MessageRepository(MongoClient);

    private static ChannelMessage NewSystemMessage(string channelId, long seq, string dedupeKey = null) => new()
    {
        ChannelId = channelId,
        Seq = seq,
        Kind = MessageKind.System,
        SystemMessage = new SystemMessageBody
        {
            Key = "match_intro",
            Params = new Dictionary<string, string> { ["map"] = "Amazonia" },
            ListParams = new Dictionary<string, List<string>> { ["players"] = ["Grubby#2136", "Happy#2233"] },
            FallbackText = "Match on Amazonia — Grubby#2136, Happy#2233",
        },
        DedupeKey = dedupeKey,
        SentAt = System.DateTime.UtcNow,
    };

    [Test]
    public async Task SystemMessage_RoundTripsThroughMongo_WithNullSenderAndContent()
    {
        var written = NewSystemMessage("chan-1", 1);
        await _messages.Insert(written);

        var read = await _messages.Load(written.Id);

        Assert.That(read, Is.Not.Null);
        Assert.That(read.Kind, Is.EqualTo(MessageKind.System), "Kind survives the round-trip");
        Assert.That(read.Sender, Is.Null, "a system message has no sender snapshot");
        Assert.That(read.Content, Is.Null, "a system message carries no free-form content");
        Assert.That(read.SystemMessage.Key, Is.EqualTo("match_intro"));
        Assert.That(read.SystemMessage.Params["map"], Is.EqualTo("Amazonia"));
        Assert.That(read.SystemMessage.ListParams["players"], Is.EqualTo(new[] { "Grubby#2136", "Happy#2233" }));
        Assert.That(read.SystemMessage.FallbackText, Does.Contain("Amazonia"));
    }

    [Test]
    public async Task LegacyDocumentWithoutKind_DeserializesAsUser()
    {
        // A pre-migration document: no `kind`, no `systemMessage`, no `dedupeKey`.
        var raw = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId().ToString(),
            ["ChannelId"] = "chan-legacy",
            ["Seq"] = 7L,
            ["Sender"] = new BsonDocument { ["BattleTag"] = "Peter#123", ["Name"] = "Peter" },
            ["Content"] = "hello",
            ["SentAt"] = System.DateTime.UtcNow,
            ["Shadow"] = false,
        };
        await MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName)
            .GetCollection<BsonDocument>(ChatCollections.Messages)
            .InsertOneAsync(raw);

        var read = await _messages.Load(raw["_id"].AsString);

        Assert.That(read.Kind, Is.EqualTo(MessageKind.User),
            "existing documents must deserialize as User with NO migration — Kind defaults");
        Assert.That(read.SystemMessage, Is.Null);
        Assert.That(read.DedupeKey, Is.Null);
    }

    [Test]
    public async Task DedupeKey_IsUniquePerChannel_ButFreeAcrossChannels()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);

        await _messages.Insert(NewSystemMessage("chan-1", 1, "match_intro"));
        await _messages.Insert(NewSystemMessage("chan-2", 1, "match_intro"));

        Assert.ThrowsAsync<MongoWriteException>(
            async () => await _messages.Insert(NewSystemMessage("chan-1", 2, "match_intro")),
            "ux_channelId_dedupeKey makes a duplicate (channel, dedupeKey) a hard write error");
    }

    [Test]
    public async Task UserMessagesWithoutDedupeKey_AreNotConstrained()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);

        await _messages.Insert(new ChannelMessage
        {
            ChannelId = "chan-1",
            Seq = 1,
            Sender = new MessageSender { BattleTag = "A#1", Name = "A" },
            Content = "one",
            SentAt = System.DateTime.UtcNow,
        });

        Assert.DoesNotThrowAsync(async () => await _messages.Insert(new ChannelMessage
        {
            ChannelId = "chan-1",
            Seq = 2,
            Sender = new MessageSender { BattleTag = "B#1", Name = "B" },
            Content = "two",
            SentAt = System.DateTime.UtcNow,
        }), "the partial index must not constrain ordinary messages, which have no dedupeKey at all");
    }
}

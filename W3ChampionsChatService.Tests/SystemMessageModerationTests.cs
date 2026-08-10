using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Messages;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Post-game chat Plan A Task 5 — moderation must treat a sender-less message safely. The purge and
/// visibility legs are PINS on existing behaviour (they already work by construction); the
/// DeleteMessage leg is a real crash fix.
/// </summary>
public class SystemMessageModerationTests : IntegrationTestBase
{
    private MessageRepository _messages;

    [SetUp]
    public void SetupBeforeEach() => _messages = new MessageRepository(MongoClient);

    private async Task<ChannelMessage> SeedSystemMessage(string channelId, long seq)
    {
        var message = new ChannelMessage
        {
            ChannelId = channelId,
            Seq = seq,
            Kind = MessageKind.System,
            SystemMessage = new SystemMessageBody { Key = "match_intro", FallbackText = "Match on Amazonia" },
            SentAt = DateTime.UtcNow,
        };
        await _messages.Insert(message);
        return message;
    }

    [Test]
    public async Task PurgeBySender_NeverTargetsSystemMessages()
    {
        await SeedSystemMessage("chan-1", 1);
        await _messages.Insert(new ChannelMessage
        {
            ChannelId = "chan-1",
            Seq = 2,
            Sender = new MessageSender { BattleTag = "Griefer#1", Name = "Griefer" },
            Content = "spam",
            SentAt = DateTime.UtcNow,
        });

        var targets = await _messages.LoadPurgeableBySender("Griefer#1");

        Assert.That(targets, Has.Count.EqualTo(1),
            "a sender-less system message can never be a purge target — it has no battleTag to match");
    }

    [Test]
    public async Task SystemMessages_AreUserVisible()
    {
        var seeded = await SeedSystemMessage("chan-1", 1);

        var visible = await _messages.LoadForUser("chan-1", "Alice#1");

        Assert.That(visible.Select(m => m.Id), Does.Contain(seeded.Id),
            "UserVisible's Shadow==false disjunct matches a system message (Shadow defaults false), so the sender-regex leg is never needed");
    }

    [Test]
    public async Task SystemMessages_AreVisibleInModeratorHistory()
    {
        var seeded = await SeedSystemMessage("chan-1", 1);

        var page = await _messages.LoadPageBeforeForModerator("chan-1", beforeSeq: null, limit: 50);

        Assert.That(page.Select(m => m.Id), Does.Contain(seeded.Id),
            "moderator history must include the system message row alongside ordinary user messages");
        Assert.That(page.Single(m => m.Id == seeded.Id).SystemMessage.FallbackText, Is.EqualTo("Match on Amazonia"),
            "the moderator projection must carry the structured SystemMessage body through unchanged");
    }
}

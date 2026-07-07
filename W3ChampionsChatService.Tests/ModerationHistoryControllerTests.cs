using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C4 Task 7 (D9): the paged moderation-history REST surface — GET /api/moderation/channels (the
/// channelId-resolution list the website-backend's moderation proxy needs; the OLD ChatHistory-backed
/// GET /api/chat/{chatroom} never needed one because it took room NAMEs directly) and
/// GET /api/moderation/channels/{channelId}/messages (the real durable moderator history read — deleted
/// and shadow rows included, flags intact, never filtered like a user read). Both actions share the
/// SAME <see cref="ChannelModeration.IsModeratable"/> scope wall as ChatHub.DeleteMessage/
/// PurgeMessagesFromUser.
/// <para>
/// Controller instantiated directly against REAL Mongo (IntegrationTestBase), mirroring
/// AuthSessionControllerTests/MuteReconciliationTests' direct-construction idiom. The
/// [UserHasPermission(Moderation)] gate itself is a generic attribute-driven MVC IFilterFactory the
/// framework only wires at request time — exercised here by reflection (mirrors
/// ChatHubPermissionFilterTests' RealChatHub_ModeratorOnlyMethods_DeclareTheAttribute idiom for the hub).
/// </para>
/// </summary>
public class ModerationHistoryControllerTests : IntegrationTestBase
{
    private ChannelRepository _channelRepository;
    private MessageRepository _messageRepository;
    private ModerationHistoryController _controller;

    [SetUp]
    public void SetupBeforeEach()
    {
        _channelRepository = new ChannelRepository(MongoClient);
        _messageRepository = new MessageRepository(MongoClient);
        _controller = new ModerationHistoryController(_channelRepository, _messageRepository);
    }

    private async Task<ChatChannel> InsertChannel(ChannelType type, SystemChannelKind? kind = null, DateTime? lastMessageAt = null)
    {
        var channel = new ChatChannel { Type = type, SystemKind = kind, LastMessageAt = lastMessageAt };
        await _channelRepository.Insert(channel);
        return channel;
    }

    private async Task<ChannelMessage> SeedMessage(string channelId, string senderBattleTag, string content, bool shadow = false)
    {
        var seq = await _channelRepository.AllocateSeq(channelId, DateTime.UtcNow);
        var message = new ChannelMessage
        {
            ChannelId = channelId,
            Seq = seq,
            Sender = new MessageSender { BattleTag = senderBattleTag, Name = senderBattleTag.Split('#')[0] },
            Content = content,
            SentAt = DateTime.UtcNow,
            Shadow = shadow,
        };
        await _messageRepository.Insert(message);
        return message;
    }

    private static void AssertForbidden(IActionResult result)
    {
        Assert.IsInstanceOf<StatusCodeResult>(result, "an ineligible channel type must reject with a plain 403, not a body");
        Assert.AreEqual(403, ((StatusCodeResult)result).StatusCode);
    }

    // ── GET /api/moderation/channels ───────────────────────────────────────────────────────

    [Test]
    public async Task Channels_ReturnsOnlyEligibleTypes_SortedByLastMessageAt()
    {
        var now = DateTime.UtcNow;
        var oldestEligible = await InsertChannel(ChannelType.Public, lastMessageAt: now.AddMinutes(-30));
        var newestEligible = await InsertChannel(ChannelType.SemiPublic, lastMessageAt: now);
        var midEligible = await InsertChannel(ChannelType.System, SystemChannelKind.Match, lastMessageAt: now.AddMinutes(-10));
        await InsertChannel(ChannelType.Dm, lastMessageAt: now.AddMinutes(1));
        await InsertChannel(ChannelType.GroupDm, lastMessageAt: now.AddMinutes(1));
        await InsertChannel(ChannelType.System, SystemChannelKind.Clan, lastMessageAt: now.AddMinutes(1));
        await InsertChannel(ChannelType.System, SystemChannelKind.Lobby, lastMessageAt: now.AddMinutes(1));

        var result = await _controller.GetModeratableChannels(limit: 100) as OkObjectResult;
        var channels = result.Value as List<ModerationChannelDto>;

        Assert.IsNotNull(channels);
        Assert.AreEqual(3, channels.Count, "only Public/SemiPublic/System+Match are eligible");
        CollectionAssert.AreEqual(
            new[] { newestEligible.Id, midEligible.Id, oldestEligible.Id },
            channels.Select(c => c.Id).ToArray(),
            "sorted by LastMessageAt DESCENDING (most recently active first)");
    }

    [Test]
    public async Task Channels_LimitClamped()
    {
        var now = DateTime.UtcNow;
        // Batched (not one big Task.WhenAll over all 505) — a single wave of 500+ concurrent single-doc
        // inserts can exhaust the driver's connection-pool wait queue; batching keeps this a realistic,
        // reliable seed rather than a driver stress test.
        foreach (var batch in Enumerable.Range(0, ChatLimits.ModerationChannelsPageSize + 5).Chunk(100))
        {
            await Task.WhenAll(batch.Select(i => _channelRepository.Insert(new ChatChannel { Type = ChannelType.Public, LastMessageAt = now.AddSeconds(-i) })));
        }

        var overCapped = await _controller.GetModeratableChannels(limit: ChatLimits.ModerationChannelsPageSize * 10) as OkObjectResult;
        Assert.AreEqual(ChatLimits.ModerationChannelsPageSize, (overCapped.Value as List<ModerationChannelDto>).Count,
            "a limit above the cap must clamp DOWN to ModerationChannelsPageSize, never return everything");

        var zeroLimit = await _controller.GetModeratableChannels(limit: 0) as OkObjectResult;
        Assert.AreEqual(1, (zeroLimit.Value as List<ModerationChannelDto>).Count,
            "a limit of 0 must clamp to the floor of 1, never MongoDB's 'no limit' semantics");
    }

    // ── GET /api/moderation/channels/{channelId}/messages ──────────────────────────────────

    [Test]
    public async Task Messages_ReturnsFlaggedModerationDtos_Paged_Ascending_WithNextBeforeSeq()
    {
        var channel = await InsertChannel(ChannelType.Public);
        var m1 = await SeedMessage(channel.Id, "author#1", "one");
        var m2 = await SeedMessage(channel.Id, "author#1", "two", shadow: true);
        var m3 = await SeedMessage(channel.Id, "author#1", "three");
        var m4 = await SeedMessage(channel.Id, "author#1", "four");
        var m5 = await SeedMessage(channel.Id, "author#1", "five");

        var firstPage = await _controller.GetChannelMessages(channel.Id, beforeSeq: null, limit: 3) as OkObjectResult;
        var firstDto = firstPage.Value as ModerationMessagePageDto;

        Assert.IsNotNull(firstDto);
        Assert.AreEqual(channel.Id, firstDto.ChannelId);
        CollectionAssert.AreEqual(new[] { m3.Seq, m4.Seq, m5.Seq }, firstDto.Messages.Select(m => m.Seq).ToArray(),
            "ASCENDING seq order within the page");
        Assert.AreEqual(m3.Seq, firstDto.NextBeforeSeq, "cursor for the next OLDER page is the page's min seq");

        var secondPage = await _controller.GetChannelMessages(channel.Id, beforeSeq: firstDto.NextBeforeSeq, limit: 3) as OkObjectResult;
        var secondDto = secondPage.Value as ModerationMessagePageDto;
        CollectionAssert.AreEqual(new[] { m1.Seq, m2.Seq }, secondDto.Messages.Select(m => m.Seq).ToArray());
        Assert.IsNull(secondDto.NextBeforeSeq, "an under-full page (2 of a requested 3) signals no further older page");

        var shadowRow = secondDto.Messages.Single(m => m.Seq == m2.Seq);
        Assert.IsTrue(shadowRow.Shadow, "the moderator projection carries the REAL shadow flag (never filtered like a user read)");
    }

    [Test]
    public async Task Messages_DeletedRow_CarriesDeletedByAt()
    {
        var channel = await InsertChannel(ChannelType.Public);
        var message = await SeedMessage(channel.Id, "author#1", "spam");
        var deletedAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        await _messageRepository.MarkDeleted(message.Id, "mod#1", deletedAt);

        var page = await _controller.GetChannelMessages(channel.Id, beforeSeq: null, limit: 50) as OkObjectResult;
        var dto = (page.Value as ModerationMessagePageDto).Messages.Single();

        Assert.IsTrue(dto.Deleted);
        Assert.AreEqual("mod#1", dto.DeletedBy);
        Assert.AreEqual(deletedAt, dto.DeletedAt);
    }

    [Test]
    public async Task Messages_UnknownChannel_404()
    {
        var result = await _controller.GetChannelMessages("does-not-exist", beforeSeq: null, limit: 50);

        Assert.IsInstanceOf<NotFoundResult>(result, "an unresolvable channel must 404 — a moderator must never learn whether a private channel exists");
    }

    [Test]
    public async Task Messages_DmChannel_403()
    {
        var channel = await InsertChannel(ChannelType.Dm);

        var result = await _controller.GetChannelMessages(channel.Id, beforeSeq: null, limit: 50);

        AssertForbidden(result);
    }

    [Test]
    public async Task Messages_GroupDm_403()
    {
        var channel = await InsertChannel(ChannelType.GroupDm);

        var result = await _controller.GetChannelMessages(channel.Id, beforeSeq: null, limit: 50);

        AssertForbidden(result);
    }

    [Test]
    public async Task Messages_Clan_403()
    {
        var channel = await InsertChannel(ChannelType.System, SystemChannelKind.Clan);

        var result = await _controller.GetChannelMessages(channel.Id, beforeSeq: null, limit: 50);

        AssertForbidden(result);
    }

    [Test]
    public async Task Messages_Lobby_403()
    {
        var channel = await InsertChannel(ChannelType.System, SystemChannelKind.Lobby);

        var result = await _controller.GetChannelMessages(channel.Id, beforeSeq: null, limit: 50);

        AssertForbidden(result);
    }

    // ── Permission gate wiring ──────────────────────────────────────────────────────────────

    [TestCase(nameof(ModerationHistoryController.GetModeratableChannels))]
    [TestCase(nameof(ModerationHistoryController.GetChannelMessages))]
    public void Endpoints_DeclareModerationPermissionAttribute(string methodName)
    {
        // Mirrors ChatHubPermissionFilterTests.RealChatHub_ModeratorOnlyMethods_DeclareTheAttribute_...:
        // drives the ACTUAL controller method metadata — the attribute must be present, since the MVC
        // UserHasPermissionFilter enforces exactly what the attribute declares.
        var method = typeof(ModerationHistoryController).GetMethod(methodName);
        var attrs = method.GetCustomAttributes(typeof(Authentication.UserHasPermissionAttribute), true);

        Assert.IsNotEmpty(attrs, $"{methodName} must declare [UserHasPermission] (the filter enforces what it declares)");
        var attribute = (Authentication.UserHasPermissionAttribute)attrs[0];
        Assert.AreEqual(Authentication.EPermission.Moderation, attribute.Permission);
    }
}

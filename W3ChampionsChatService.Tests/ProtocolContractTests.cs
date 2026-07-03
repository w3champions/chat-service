using System;
using System.Text.Json;
using NUnit.Framework;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C3 Task 1 — protocol vocabulary contract guards: result-enum exact set (program contract §1),
/// pinned server→client event names, and the plan-decision ChatLimits constants it introduces.
/// </summary>
public class ProtocolContractTests
{
    [Test]
    public void ChatResultCode_HasExactPinnedMembers()
    {
        var pinned = new[] { "Ok", "Throttled", "NotMember", "Muted", "TooLong", "NotFound", "PermissionDenied" };
        var actual = Enum.GetNames(typeof(ChatResultCode));

        Assert.AreEqual(pinned.Length, actual.Length);
        CollectionAssert.AreEquivalent(pinned, actual);
    }

    [Test]
    public void ChatResultCode_SerializesAsStringName()
    {
        var json = JsonSerializer.Serialize(ChatResultCode.NotMember);

        Assert.AreEqual("\"NotMember\"", json);
    }

    [Test]
    public void ChatEvents_DefinesPinnedServerEventNames()
    {
        Assert.AreEqual("SessionState", ChatEvents.SessionState);
        Assert.AreEqual("MessageReceived", ChatEvents.MessageReceived);
        Assert.AreEqual("ChannelActivity", ChatEvents.ChannelActivity);
        Assert.AreEqual("ViewersChanged", ChatEvents.ViewersChanged);
        Assert.AreEqual("ChannelAdded", ChatEvents.ChannelAdded);
        Assert.AreEqual("ChannelRemoved", ChatEvents.ChannelRemoved);
        Assert.AreEqual("MessageDeleted", ChatEvents.MessageDeleted);
        Assert.AreEqual("BulkMessagesDeleted", ChatEvents.BulkMessagesDeleted);
        Assert.AreEqual("PlayerBannedFromChat", ChatEvents.PlayerBannedFromChat);
        Assert.AreEqual("ConnectionDisplaced", ChatEvents.ConnectionDisplaced);
        Assert.AreEqual("ThrottleNotice", ChatEvents.ThrottleNotice);
    }

    [Test]
    public void ChatLimits_MessagePageSize_Is100()
    {
        Assert.AreEqual(100, ChatLimits.MessagePageSize);
    }

    [Test]
    public void ChatLimits_AutoThrottle_Constants()
    {
        // Plan decision (C3-plan.md Task 1 / Open question 3) — spec §13 pins only "60s automatic
        // throttle"; the escalation trigger threshold/window are NOT spec-pinned, XML-doc'd as such
        // at the declaration.
        Assert.AreEqual(5, ChatLimits.AutoThrottleViolationThreshold);
        Assert.AreEqual(TimeSpan.FromSeconds(60), ChatLimits.AutoThrottleWindow);
        Assert.AreEqual(TimeSpan.FromSeconds(60), ChatLimits.AutoThrottleDuration);
    }

    [Test]
    public void SendMessageResult_Ok_CarriesMessageIdAndSeq_NoRetryAfter()
    {
        var result = new SendMessageResult(ChatResultCode.Ok, MessageId: "msg1", Seq: 5);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.AreEqual("msg1", result.MessageId);
        Assert.AreEqual(5, result.Seq);
        Assert.IsNull(result.RetryAfterSeconds);
    }

    [Test]
    public void SendMessageResult_Throttled_CarriesRetryAfterSeconds_NoMessageIdOrSeq()
    {
        var result = new SendMessageResult(ChatResultCode.Throttled, RetryAfterSeconds: 2.5);

        Assert.AreEqual(ChatResultCode.Throttled, result.Code);
        Assert.AreEqual(2.5, result.RetryAfterSeconds);
        Assert.IsNull(result.MessageId);
        Assert.IsNull(result.Seq);
    }

    [Test]
    public void JoinChannelResult_CarriesChannelAndMembership()
    {
        var channel = new ChatChannel { Id = "c1" };
        var membership = new ChannelMembership { Id = "m1" };

        var result = new JoinChannelResult(ChatResultCode.Ok, Channel: channel, Membership: membership);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.AreSame(channel, result.Channel);
        Assert.AreSame(membership, result.Membership);
    }

    [Test]
    public void FocusChannelResult_CarriesViewerRoster()
    {
        var viewers = new[] { new ChannelViewerDto("Peter#123", "Peter") };

        var result = new FocusChannelResult(ChatResultCode.Ok, Viewers: viewers);

        Assert.AreEqual(1, result.Viewers.Count);
        Assert.AreEqual("Peter#123", result.Viewers[0].BattleTag);
        Assert.AreEqual("Peter", result.Viewers[0].Name);
    }

    [Test]
    public void GetMessagesResult_CarriesMessagesWithModeratorFlagSlots()
    {
        var sender = new MessageSender { BattleTag = "Peter#123", Name = "Peter" };
        var message = new MessageDto("m1", "c1", 1, sender, "hi", DateTime.UtcNow, Deleted: false, Shadow: false);

        var result = new GetMessagesResult(ChatResultCode.Ok, Messages: new[] { message });

        Assert.AreEqual(1, result.Messages.Count);
        Assert.IsFalse(result.Messages[0].Deleted);
        Assert.IsFalse(result.Messages[0].Shadow);
    }

    [Test]
    public void ChannelOperationResult_DefaultsRetryAfterSecondsToNull()
    {
        var result = new ChannelOperationResult(ChatResultCode.NotMember);

        Assert.AreEqual(ChatResultCode.NotMember, result.Code);
        Assert.IsNull(result.RetryAfterSeconds);
    }

    // ── C4 Task 1 (D3/D6) — moderator projections + purge result shape pins ───────────────

    [Test]
    public void MessageDto_ForModerator_PreservesFlags()
    {
        var deletedMessage = new ChannelMessage
        {
            Id = "m1",
            ChannelId = "chan1",
            Seq = 2,
            Sender = new MessageSender { BattleTag = "Peter#123", Name = "Peter" },
            Content = "hello",
            SentAt = DateTime.UtcNow,
            Deleted = new MessageDeletion { By = "Mod#1", At = DateTime.UtcNow },
        };
        var shadowMessage = new ChannelMessage
        {
            Id = "m2",
            ChannelId = "chan1",
            Seq = 3,
            Sender = new MessageSender { BattleTag = "Wolf#456", Name = "Wolf" },
            Content = "hi",
            SentAt = DateTime.UtcNow,
            Shadow = true,
        };

        var deletedDto = MessageDto.ForModerator("chan1", deletedMessage);
        var shadowDto = MessageDto.ForModerator("chan1", shadowMessage);

        Assert.IsTrue(deletedDto.Deleted, "a moderator projection must expose the real Deleted flag");
        Assert.IsFalse(deletedDto.Shadow);
        Assert.IsFalse(shadowDto.Deleted);
        Assert.IsTrue(shadowDto.Shadow, "a moderator projection must expose the real Shadow flag");
    }

    [Test]
    public void MessageDto_ForUserDelivery_StillForcesFlagsFalse_RegressionAgainstForModerator()
    {
        // Regression pair for the test above: ForUserDelivery must NEVER be weakened by adding
        // ForModerator — the shadow-illusion (C3-plan.md decision 7) still forces both flags false.
        var deletedShadowMessage = new ChannelMessage
        {
            Id = "m3",
            ChannelId = "chan1",
            Seq = 4,
            Sender = new MessageSender { BattleTag = "Peter#123", Name = "Peter" },
            Content = "hello",
            SentAt = DateTime.UtcNow,
            Deleted = new MessageDeletion { By = "Mod#1", At = DateTime.UtcNow },
            Shadow = true,
        };

        var dto = MessageDto.ForUserDelivery("chan1", deletedShadowMessage);

        Assert.IsFalse(dto.Deleted);
        Assert.IsFalse(dto.Shadow);
    }

    [Test]
    public void ModerationMessageDto_FromChannelMessage_MapsDeletionFields()
    {
        var deletedAt = DateTime.UtcNow;
        var message = new ChannelMessage
        {
            Id = "m1",
            ChannelId = "chan1",
            Seq = 5,
            Sender = new MessageSender { BattleTag = "Peter#123", Name = "Peter" },
            Content = "hello",
            SentAt = DateTime.UtcNow,
            Deleted = new MessageDeletion { By = "Mod#1", At = deletedAt },
            Shadow = false,
        };

        var dto = ModerationMessageDto.FromChannelMessage("chan1", message);

        Assert.AreEqual("m1", dto.Id);
        Assert.AreEqual("chan1", dto.ChannelId);
        Assert.AreEqual(5, dto.Seq);
        Assert.AreEqual("Peter#123", dto.SenderBattleTag);
        Assert.AreEqual("Peter", dto.SenderName);
        Assert.AreEqual("hello", dto.Content);
        Assert.IsTrue(dto.Deleted);
        Assert.AreEqual("Mod#1", dto.DeletedBy);
        Assert.AreEqual(deletedAt, dto.DeletedAt);
        Assert.IsFalse(dto.Shadow);
    }

    [Test]
    public void ModerationMessageDto_FromChannelMessage_NotDeleted_NullDeletionFields()
    {
        var message = new ChannelMessage
        {
            Id = "m2",
            ChannelId = "chan1",
            Seq = 6,
            Sender = new MessageSender { BattleTag = "Peter#123", Name = "Peter" },
            Content = "hello",
            SentAt = DateTime.UtcNow,
        };

        var dto = ModerationMessageDto.FromChannelMessage("chan1", message);

        Assert.IsFalse(dto.Deleted);
        Assert.IsNull(dto.DeletedBy);
        Assert.IsNull(dto.DeletedAt);
    }

    [Test]
    public void PurgeMessagesResult_CarriesCodeAndDeletedCount()
    {
        var result = new PurgeMessagesResult(ChatResultCode.Ok, MessagesDeleted: 3);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.AreEqual(3, result.MessagesDeleted);
    }
}

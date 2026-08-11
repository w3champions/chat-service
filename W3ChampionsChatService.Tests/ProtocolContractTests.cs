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
        var pinned = new[] { "Ok", "Throttled", "NotMember", "Muted", "TooLong", "NotFound", "PermissionDenied", "UnsupportedCommand" };
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
        Assert.AreEqual("RequestReceived", ChatEvents.RequestReceived);
        Assert.AreEqual("MentionNotified", ChatEvents.MentionNotified);
        Assert.AreEqual("PresenceChanged", ChatEvents.PresenceChanged);
        Assert.AreEqual("FriendPresenceChanged", ChatEvents.FriendPresenceChanged);
    }

    [Test]
    public void ChatLimits_MessagePageSize_Is100()
    {
        Assert.AreEqual(100, ChatLimits.MessagePageSize);
    }

    [Test]
    public void ChatLimits_AutoThrottle_Constants()
    {
        // Plan decision (C3-plan.md Task 1 / Open question 3): the escalation trigger
        // threshold/window are NOT spec-pinned, XML-doc'd as such at the declaration. The escalating
        // tier durations (10s/30s/60s cap) and the 10-minute decay are pinned by the 2026-08-04
        // follow-up spec §1.
        Assert.AreEqual(5, ChatLimits.AutoThrottleViolationThreshold);
        Assert.AreEqual(TimeSpan.FromSeconds(60), ChatLimits.AutoThrottleWindow);
        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60) },
            ChatLimits.AutoThrottleTierDurations);
        Assert.AreEqual(TimeSpan.FromMinutes(10), ChatLimits.AutoThrottleTierDecay);
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

    // ── C5 Task 2 (D17/D18) — DM/group domain + repository foundation pins ───────────────

    [Test]
    public void ChatLimits_C5NewConstants_MatchPlanD17()
    {
        // Spec-pinned (§4): decline is soft + temporal, 24h suppression.
        Assert.AreEqual(TimeSpan.FromHours(24), ChatLimits.DmDeclineSuppression);
        // Spec-pinned (§4: "3–100 members") — the floor half of the existing MaxGroupSize ceiling.
        Assert.AreEqual(3, ChatLimits.GroupMinSize);
        // Plan decisions (C5 T2) — not spec §13 text; hard-coded, adjust here only.
        Assert.AreEqual(64, ChatLimits.GroupNameMaxLength);
        Assert.AreEqual(120, ChatLimits.DmPreviewExcerptLength);
    }

    [Test]
    public void OpenDmResult_CarriesChannelAndMembership()
    {
        var channel = new ChatChannel { Id = "c1", Type = ChannelType.Dm };
        var membership = new ChannelMembership { Id = "m1" };

        var result = new OpenDmResult(ChatResultCode.Ok, Channel: channel, Membership: membership);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.AreSame(channel, result.Channel);
        Assert.AreSame(membership, result.Membership);
        Assert.IsNull(result.RetryAfterSeconds);
    }

    [Test]
    public void OpenDmResult_Throttled_CarriesRetryAfterSeconds_NoChannelOrMembership()
    {
        var result = new OpenDmResult(ChatResultCode.Throttled, RetryAfterSeconds: 30);

        Assert.AreEqual(ChatResultCode.Throttled, result.Code);
        Assert.AreEqual(30, result.RetryAfterSeconds);
        Assert.IsNull(result.Channel);
        Assert.IsNull(result.Membership);
    }

    [Test]
    public void CreateGroupResult_CarriesChannelAndCreatorMembership()
    {
        var channel = new ChatChannel { Id = "g1", Type = ChannelType.GroupDm };
        var membership = new ChannelMembership { Id = "m1", Role = MembershipRole.Owner };

        var result = new CreateGroupResult(ChatResultCode.Ok, Channel: channel, Membership: membership);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.AreSame(channel, result.Channel);
        Assert.AreEqual(MembershipRole.Owner, result.Membership.Role);
    }

    [Test]
    public void PendingDmRequestDto_CarriesChannelInitiatorAndTimestamp()
    {
        var requestedAt = DateTime.UtcNow;
        var dto = new PendingDmRequestDto("c1", "Peter#123", requestedAt);

        Assert.AreEqual("c1", dto.ChannelId);
        Assert.AreEqual("Peter#123", dto.FromBattleTag);
        Assert.AreEqual(requestedAt, dto.RequestedAt);
    }

    [Test]
    public void MembershipDto_NeverSerializesDeclinedUntil()
    {
        // D3 leak pin: DeclinedUntil lives on the RECIPIENT's membership row and must never reach
        // the wire — MembershipDto.From is an explicit projection, so this must hold structurally
        // even though the domain type itself carries the field.
        var membership = new ChannelMembership
        {
            ChannelId = "chan1",
            BattleTag = "Peter#123",
            JoinedAt = DateTime.UtcNow,
            DeclinedUntil = DateTime.UtcNow.AddHours(24),
        };

        var dto = MembershipDto.From(membership);
        var json = JsonSerializer.Serialize(dto);

        StringAssert.DoesNotContain("Declined", json);
        StringAssert.DoesNotContain("declined", json);
    }

    // ── C6 Task 1 (D14) — mention/presence protocol vocabulary const pins ────────────────

    [Test]
    public void ChatLimits_C6NewConstants_MatchPlanD14()
    {
        // Spec-pinned (§7: "lastSeenAt ≥ now−90d") — applies to Tier 3 (directory) search only.
        Assert.AreEqual(TimeSpan.FromDays(90), ChatLimits.MentionCandidateActivityWindow);
        // Plan decisions (C6 T1) — not spec §13 text; hard-coded, adjust here only.
        Assert.AreEqual(20, ChatLimits.MentionSearchMaxResults);
        Assert.AreEqual(100, ChatLimits.MentionAckBatchMax);
        Assert.AreEqual(200, ChatLimits.MentionInboxMaxEntries);
        Assert.AreEqual(200, ChatLimits.PresenceQueryMaxBattleTags);
    }

    [Test]
    public void OpenDmResult_NeverSerializesDeclinedUntil_OnRawMembership()
    {
        // D3 leak-wall pin, entity level: OpenDmResult/CreateGroupResult/JoinChannelResult carry the
        // RAW ChannelMembership (not the MembershipDto projection). Even though these results only ever
        // carry the CALLER'S OWN membership, DeclinedUntil is server-only state and must never reach the
        // wire via System.Text.Json — the same serializer SignalR's default hub protocol uses.
        var membership = new ChannelMembership
        {
            ChannelId = "chan1",
            BattleTag = "Peter#123",
            JoinedAt = DateTime.UtcNow,
            DeclinedUntil = DateTime.UtcNow.AddHours(24),
        };
        var result = new OpenDmResult(ChatResultCode.Ok, Membership: membership);

        var json = JsonSerializer.Serialize(result);

        StringAssert.DoesNotContain("Declined", json);
        StringAssert.DoesNotContain("declined", json);
    }

    // ── 2026-08-05 reconciliation plan Task 1 (D6) — assertion-state leak wall ───────────────

    [Test]
    public void ChatChannel_AssertionState_IsNeverSerializedToClients()
    {
        // D6: AssertEpoch/AssertSeq/Detached are mm<->chat reconciliation bookkeeping, never client
        // protocol — the raw entity rides ChannelAddedDto.Channel / ChannelDto.Channel to clients.
        var channel = new ChatChannel
        {
            Id = "c1",
            AssertEpoch = "e1",
            AssertSeq = 5,
            Detached = true,
        };

        var json = JsonSerializer.Serialize(channel);

        StringAssert.DoesNotContain("assertEpoch", json);
        StringAssert.DoesNotContain("AssertEpoch", json);
        StringAssert.DoesNotContain("assertSeq", json);
        StringAssert.DoesNotContain("AssertSeq", json);
        StringAssert.DoesNotContain("detached", json);
        StringAssert.DoesNotContain("Detached", json);
        // Positive control — proves the object really did serialize.
        StringAssert.Contains("Id", json);
    }
}

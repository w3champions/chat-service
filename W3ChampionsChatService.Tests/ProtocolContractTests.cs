using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using NUnit.Framework;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
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
    public void MessageKind_SerializesAsStringName()
    {
        // Same property, same reason, as ChatResultCode above: there is no global
        // JsonStringEnumConverter (ChatJsonProtocol.Configure only sets DefaultIgnoreCondition), so
        // without MessageKind's own [JsonConverter] the discriminator rides as an undocumented ordinal
        // and a client ends up writing `if (msg.kind === 1)`.
        Assert.AreEqual("\"System\"", JsonSerializer.Serialize(MessageKind.System));
        Assert.AreEqual("\"User\"", JsonSerializer.Serialize(MessageKind.User));
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
        // Explicit Profile (Finding 3: ChannelViewerDto's Profile ctor arg no longer defaults to
        // null — every construction site states its intent). Also asserts Profile itself rides the
        // roster (Finding 4): a regression that stopped populating it would previously leave this
        // wire-shape contract test green.
        var profile = new ChatProfile
        {
            ProfilePicture = new ProfilePicture { Race = AvatarCategory.HU, PictureId = 3, IsClassic = true },
        };
        var viewers = new[] { new ChannelViewerDto("Peter#123", "Peter", profile) };

        var result = new FocusChannelResult(ChatResultCode.Ok, Viewers: viewers);

        Assert.AreEqual(1, result.Viewers.Count);
        Assert.AreEqual("Peter#123", result.Viewers[0].BattleTag);
        Assert.AreEqual("Peter", result.Viewers[0].Name);
        Assert.AreSame(profile, result.Viewers[0].Profile);
        Assert.AreEqual(AvatarCategory.HU, result.Viewers[0].Profile.ProfilePicture.Race);
        Assert.AreEqual(3, result.Viewers[0].Profile.ProfilePicture.PictureId);
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
        // Ladder joins them: it is the mm-declared ladder-vs-custom classification the send-path mute
        // gate reads server-side, not something a client is told or could act on.
        var channel = new ChatChannel
        {
            Id = "c1",
            AssertEpoch = "e1",
            AssertSeq = 5,
            Detached = true,
            Ladder = true,
        };

        var json = JsonSerializer.Serialize(channel);

        StringAssert.DoesNotContain("assertEpoch", json);
        StringAssert.DoesNotContain("AssertEpoch", json);
        StringAssert.DoesNotContain("assertSeq", json);
        StringAssert.DoesNotContain("AssertSeq", json);
        StringAssert.DoesNotContain("detached", json);
        StringAssert.DoesNotContain("Detached", json);
        StringAssert.DoesNotContain("ladder", json);
        StringAssert.DoesNotContain("Ladder", json);
        // Positive control — proves the object really did serialize.
        StringAssert.Contains("Id", json);
    }

    // ── Post-game chat Plan A Task 2 — MessageDto projects the system-message fields ────────

    [Test]
    public void ForUserDelivery_CarriesSystemKindAndBody()
    {
        var systemMessage = new ChannelMessage
        {
            Id = "m1",
            ChannelId = "chan1",
            Seq = 3,
            Kind = MessageKind.System,
            SystemMessage = new SystemMessageBody
            {
                Key = "match_intro",
                Params = new Dictionary<string, string> { ["map"] = "Amazonia" },
                FallbackText = "Match on Amazonia",
            },
            SentAt = DateTime.UtcNow,
        };

        var dto = MessageDto.ForUserDelivery("chan1", systemMessage);

        Assert.That(dto.Kind, Is.EqualTo(MessageKind.System), "the client needs the discriminator to pick a renderer");
        Assert.That(dto.SystemMessage.Key, Is.EqualTo("match_intro"), "key is the client's catalogue lookup token");
        Assert.That(dto.SystemMessage.FallbackText, Is.EqualTo("Match on Amazonia"), "fallback text must survive the projection");
        Assert.That(dto.Sender, Is.Null, "a system message has no sender snapshot on the wire either");
        Assert.That(dto.Content, Is.Null, "a system message carries no free-form content on the wire either");
    }

    [Test]
    public void ForModerator_CarriesSystemKindAndBody()
    {
        var systemMessage = new ChannelMessage
        {
            Id = "m1",
            ChannelId = "chan1",
            Seq = 3,
            Kind = MessageKind.System,
            SystemMessage = new SystemMessageBody { Key = "match_intro", FallbackText = "Match on Amazonia" },
            SentAt = DateTime.UtcNow,
        };

        var dto = MessageDto.ForModerator("chan1", systemMessage);

        Assert.That(dto.Kind, Is.EqualTo(MessageKind.System), "the moderator projection needs the discriminator too");
        Assert.That(dto.SystemMessage.FallbackText, Is.EqualTo("Match on Amazonia"),
            "moderation history renders fallbackText — it has no i18n catalogue");
    }

    [Test]
    public void UserMessageProjection_DefaultsToUserKindWithNoSystemBody()
    {
        var userMessage = new ChannelMessage
        {
            Id = "m2",
            ChannelId = "chan1",
            Seq = 4,
            Sender = new MessageSender { BattleTag = "A#1", Name = "A" },
            Content = "gg",
            SentAt = DateTime.UtcNow,
        };

        var dto = MessageDto.ForUserDelivery("chan1", userMessage);

        Assert.That(dto.Kind, Is.EqualTo(MessageKind.User), "the client needs the discriminator to pick the ordinary-message renderer, not the system one");
        Assert.That(dto.SystemMessage, Is.Null, "a populated body here would make the client try to render system content for a normal chat line");
    }

    [Test]
    public void MessageDto_WireShape_KindAlwaysEmittedAsString_SystemBodyOmittedWhenAbsent()
    {
        // The three cases above assert on the C# record's PROPERTIES; this one asserts on the bytes a
        // client actually receives, through the hub's real serializer options (the same
        // ChatJsonProtocol.Configure that ChatJsonProtocolTests pins). Without it the wire shape of
        // `kind` and `systemMessage` — the two fields Plan C's renderer branches on — is unpinned.
        var options = ConfiguredHubOptions();

        var systemJson = JsonSerializer.Serialize(
            MessageDto.ForUserDelivery("chan1", new ChannelMessage
            {
                Id = "m1",
                ChannelId = "chan1",
                Seq = 3,
                Kind = MessageKind.System,
                SystemMessage = new SystemMessageBody { Key = "match_intro", FallbackText = "Match on Amazonia" },
                SentAt = DateTime.UtcNow,
            }),
            options);

        Assert.That(systemJson, Does.Contain("\"kind\":\"System\""),
            "the discriminator must ride as a self-describing string, never as an ordinal a client has to guess");
        Assert.That(systemJson, Does.Contain("\"systemMessage\""), "the structured body is the only thing a system message has to render");
        Assert.That(systemJson, Does.Contain("\"fallbackText\":\"Match on Amazonia\""), "a client that does not know the key renders fallbackText");
        Assert.That(systemJson, Does.Not.Contain("\"sender\""), "a system message has no sender — the null must be omitted, not sent as an explicit null");
        Assert.That(systemJson, Does.Not.Contain("\"content\""), "a system message has no free-form content");

        var userJson = JsonSerializer.Serialize(
            MessageDto.ForUserDelivery("chan1", new ChannelMessage
            {
                Id = "m2",
                ChannelId = "chan1",
                Seq = 4,
                Sender = new MessageSender { BattleTag = "A#1", Name = "A" },
                Content = "gg",
                SentAt = DateTime.UtcNow,
            }),
            options);

        // Deliberately NOT shrunk with WhenWritingDefault: a discriminator that vanishes on the common
        // case invites `msg.kind === undefined` bugs in the client, and ChatResultCode sets the
        // always-emit precedent. See MessageKind's own doc comment.
        Assert.That(userJson, Does.Contain("\"kind\":\"User\""),
            "kind must be emitted on ORDINARY messages too — a discriminator present only sometimes is one a client cannot branch on");
        Assert.That(userJson, Does.Not.Contain("systemMessage"), "a user message's null system body must not occupy wire bytes");
    }

    // ── Final review M2 — ChannelType/SystemChannelKind ordinals are a notification-routing
    // discriminator, not just a wire curiosity ──────────────────────────────────────────────────

    [Test]
    public void ChannelType_HasExactPinnedNumericValues()
    {
        // ActivityPreviewDto rides ChannelType as its ORDINAL, deliberately (see that DTO's own doc
        // comment) so a client can compare activityPreview.channelType directly against a channel's
        // own `type` — post-game chat's one-time nudge gate is decided by that comparison. The existing
        // wire test (ChatJsonProtocolTests.Configure_MatchChannelActivity_PreviewCarriesItsChannelClassOnTheWire)
        // only asserts the KEY is present, not its value, so reordering this enum would silently reroute
        // every client's notification routing with a fully green suite. Pin every member's NUMERIC
        // VALUE — not just ChatResultCode_HasExactPinnedMembers's name-only check above — and every
        // member, not only System/Dm which the nudge reads today, since a reorder anywhere shifts every
        // value after it.
        Assert.AreEqual(5, Enum.GetValues(typeof(ChannelType)).Length);
        Assert.AreEqual(0, (int)ChannelType.Public);
        Assert.AreEqual(1, (int)ChannelType.SemiPublic);
        Assert.AreEqual(2, (int)ChannelType.System);
        Assert.AreEqual(3, (int)ChannelType.Dm);
        Assert.AreEqual(4, (int)ChannelType.GroupDm);
    }

    [Test]
    public void SystemChannelKind_HasExactPinnedNumericValues()
    {
        // Same load-bearing reason as ChannelType_HasExactPinnedNumericValues above — SystemChannelKind
        // is the OTHER half of the ordinal pair ActivityPreviewDto rides on the wire.
        Assert.AreEqual(3, Enum.GetValues(typeof(SystemChannelKind)).Length);
        Assert.AreEqual(0, (int)SystemChannelKind.Lobby);
        Assert.AreEqual(1, (int)SystemChannelKind.Match);
        Assert.AreEqual(2, (int)SystemChannelKind.Clan);
    }

    // The hub's REAL payload serializer options, so a wire-shape assertion above pins what a client
    // actually receives rather than System.Text.Json's defaults (which would emit nulls).
    private static JsonSerializerOptions ConfiguredHubOptions()
    {
        var options = new JsonHubProtocolOptions();
        ChatJsonProtocol.Configure(options);
        return options.PayloadSerializerOptions;
    }
}

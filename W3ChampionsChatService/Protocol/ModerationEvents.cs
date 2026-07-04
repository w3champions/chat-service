using System;
using System.Collections.Generic;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Messages;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// Server→client push for a single moderation-deleted message, SCOPED BY CHANNEL. The legacy hub
/// (<c>Chats/ChatHub.cs:408</c>, <c>DeleteMessage</c>) emits a bare <c>messageId</c> string via
/// <c>Clients.AllExcept(authorConnectionIds).SendAsync("MessageDeleted", deletedMessage.Id)</c> —
/// but the NEW client model is per-channel, so this shape carries the channel alongside the message
/// id.
/// <para>
/// FINAL (C4 Task 1, confirming C3 Task 18's channel-scoped payload): this shape is the one C4–C7
/// emit through <see cref="ChatEvents.MessageDeleted"/>. C4's refinement over the legacy trio is
/// DELIVERY — who receives the push and when (later C4 tasks 3/4) — not this payload shape.
/// </para>
/// </summary>
public record MessageDeletedDto(string ChannelId, string MessageId);

/// <summary>
/// Server→client push for a moderator's bulk purge of a user's messages, SCOPED BY CHANNEL. The
/// legacy hub (<c>Chats/ChatHub.cs:422</c>, <c>PurgeMessagesFromUser</c>) emits a bare
/// <c>List&lt;string&gt;</c> of message ids via
/// <c>Clients.AllExcept(authorConnectionIds).SendAsync("BulkMessageDeleted", ...)</c> — note the OLD
/// event name is SINGULAR (<c>"BulkMessageDeleted"</c>); the NEW pinned name is PLURAL,
/// <see cref="ChatEvents.BulkMessagesDeleted"/> (see that constant's doc comment). This shape carries
/// the channel alongside the message ids for the same per-channel-client-model reason as
/// <see cref="MessageDeletedDto"/>.
/// <para>
/// FINAL (C4 Task 1, confirming C3 Task 18's channel-scoped payload): this shape is the one C4–C7
/// emit through <see cref="ChatEvents.BulkMessagesDeleted"/>. C4's refinement over the legacy trio is
/// DELIVERY — who receives the push and when (later C4 tasks 3/4) — not this payload shape.
/// </para>
/// </summary>
public record BulkMessagesDeletedDto(string ChannelId, IReadOnlyList<string> MessageIds);

/// <summary>
/// REST moderator-read projection of a <see cref="ChannelMessage"/> (D3): unlike
/// <see cref="MessageDto.ForModerator"/> (the hub-facing projection, which reuses the shared
/// <see cref="MessageSender"/> snapshot), this DTO flattens the sender fields for the REST moderation
/// surface and additionally exposes WHO deleted a row and WHEN — ban <c>reason</c>/<c>author</c> are
/// NOT message fields and must never appear here.
/// </summary>
public record ModerationMessageDto(
    string Id,
    string ChannelId,
    long Seq,
    string SenderBattleTag,
    string SenderName,
    string Content,
    DateTime SentAt,
    bool Deleted,
    string DeletedBy,
    DateTime? DeletedAt,
    bool Shadow)
{
    public static ModerationMessageDto FromChannelMessage(string channelId, ChannelMessage message) =>
        new(
            Id: message.Id,
            ChannelId: channelId,
            Seq: message.Seq,
            SenderBattleTag: message.Sender.BattleTag,
            SenderName: message.Sender.Name,
            Content: message.Content,
            SentAt: message.SentAt,
            Deleted: message.Deleted != null,
            DeletedBy: message.Deleted?.By,
            DeletedAt: message.Deleted?.At,
            Shadow: message.Shadow);
}

/// <summary>
/// REST channel-list projection of a <see cref="ChatChannel"/> (C4 Task 7, D9): backs
/// GET /api/moderation/channels — the channelId-resolution surface the website-backend's moderation
/// proxy needs (the OLD ChatHistory-backed GET /api/chat/{chatroom} took room NAMEs directly).
/// </summary>
public record ModerationChannelDto(
    string Id,
    string Name,
    ChannelType Type,
    SystemChannelKind? SystemKind,
    string SystemRef,
    long LastSeq,
    DateTime? LastMessageAt)
{
    public static ModerationChannelDto FromChannel(ChatChannel channel) =>
        new(
            Id: channel.Id,
            Name: channel.Name,
            Type: channel.Type,
            SystemKind: channel.SystemKind,
            SystemRef: channel.SystemRef,
            LastSeq: channel.LastSeq,
            LastMessageAt: channel.LastMessageAt);
}

/// <summary>
/// REST response envelope for GET /api/moderation/channels/{channelId}/messages (C4 Task 7, D9):
/// <see cref="Messages"/> is ASCENDING seq order (oldest to newest within the page);
/// <see cref="NextBeforeSeq"/> is the cursor for the next OLDER page — the min seq returned, or null
/// when the page was not full (no older rows remain).
/// </summary>
public record ModerationMessagePageDto(
    string ChannelId,
    IReadOnlyList<ModerationMessageDto> Messages,
    long? NextBeforeSeq);

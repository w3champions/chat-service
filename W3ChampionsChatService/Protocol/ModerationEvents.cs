using System.Collections.Generic;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// Server→client push for a single moderation-deleted message, SCOPED BY CHANNEL. The legacy hub
/// (<c>Chats/ChatHub.cs:408</c>, <c>DeleteMessage</c>) emits a bare <c>messageId</c> string via
/// <c>Clients.AllExcept(authorConnectionIds).SendAsync("MessageDeleted", deletedMessage.Id)</c> —
/// but the NEW client model is per-channel, so this shape carries the channel alongside the message
/// id.
/// <para>
/// SHAPE-ONLY (C3 Task 18, contract completeness): defined here so C4–C7 share one payload shape for
/// <see cref="ChatEvents.MessageDeleted"/>, but C3 provides no emit helper and no caller — C4 owns
/// the trigger when it ports the legacy moderation deletes onto <see cref="ChatEvents.MessageDeleted"/>,
/// and MAY refine this shape at that point (e.g. if the moderation flow needs more than the bare id).
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
/// SHAPE-ONLY (C3 Task 18, contract completeness): defined here so C4–C7 share one payload shape for
/// <see cref="ChatEvents.BulkMessagesDeleted"/>, but C3 provides no emit helper and no caller — C4
/// owns the trigger when it ports the legacy moderation deletes onto
/// <see cref="ChatEvents.BulkMessagesDeleted"/>, and MAY refine this shape at that point.
/// </para>
/// </summary>
public record BulkMessagesDeletedDto(string ChannelId, IReadOnlyList<string> MessageIds);

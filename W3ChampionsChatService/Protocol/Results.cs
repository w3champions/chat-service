using System.Collections.Generic;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Memberships;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// Per-method result DTOs (program contract §1, C3-plan.md decision 5). Every hub method returns
/// one of these instead of throwing/silently dropping — mapping decisions (empty-after-trim →
/// TooLong, focused-set cap → PermissionDenied, malformed arg combos → HubException, etc.) are
/// documented at each hub method's implementation, not here.
/// <see cref="FocusChannelResult.Viewers"/> and <see cref="GetMessagesResult.Messages"/> reference
/// <see cref="ChannelViewerDto"/> and <see cref="MessageDto"/> respectively, which live in their
/// own files (<c>ChannelViewerDto.cs</c>/<c>MessageDto.cs</c>) since later tasks (9, 12) own further
/// wiring against them; mirrors how <see cref="JoinChannelResult"/> reuses the domain types
/// <see cref="ChatChannel"/>/<see cref="ChannelMembership"/> directly instead of inventing parallel
/// DTOs.
/// </summary>
public record SendMessageResult(
    ChatResultCode Code,
    double? RetryAfterSeconds = null,
    string MessageId = null,
    long? Seq = null);

public record JoinChannelResult(
    ChatResultCode Code,
    double? RetryAfterSeconds = null,
    ChatChannel Channel = null,
    ChannelMembership Membership = null);

public record FocusChannelResult(
    ChatResultCode Code,
    IReadOnlyList<ChannelViewerDto> Viewers = null);

public record GetMessagesResult(
    ChatResultCode Code,
    IReadOnlyList<MessageDto> Messages = null);

/// <summary>Leave/SetNotificationLevel/MarkRead/Unfocus — no result payload beyond the code.</summary>
public record ChannelOperationResult(
    ChatResultCode Code,
    double? RetryAfterSeconds = null);

/// <summary>PurgeMessagesFromUser (D6, later C4 task) — count of soft-deleted rows for audit/UI feedback.</summary>
public record PurgeMessagesResult(
    ChatResultCode Code,
    int MessagesDeleted);

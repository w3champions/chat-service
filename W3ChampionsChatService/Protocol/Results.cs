using System;
using System.Collections.Generic;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Memberships;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// Per-method result DTOs (program contract §1, C3-plan.md decision 5). Every hub method returns
/// one of these instead of throwing/silently dropping — mapping decisions (empty-after-trim →
/// TooLong, focused-set cap → PermissionDenied, malformed arg combos → HubException, etc.) are
/// documented at each hub method's implementation, not here.
/// <see cref="ChannelViewerDto"/>/<see cref="MessageDto"/>/<see cref="MessageSenderDto"/> are
/// forward references to shapes that Task 9 (FocusChannel roster) and Task 12 (fan-out MessageDto)
/// own the wiring for; they are defined here — rather than deferred — because
/// <see cref="FocusChannelResult"/>/<see cref="GetMessagesResult"/> need a concrete type now
/// (Task 1 has no dependencies and runs first). Later tasks may relocate/extend them; the field
/// shapes already match what those tasks' briefs pin verbatim.
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

/// <summary>Active-viewer roster entry for <see cref="FocusChannelResult"/> (Task 9 shape).</summary>
public record ChannelViewerDto(string BattleTag, string Name);

/// <summary>Immutable sender snapshot on a wire-facing message (Task 12 shape).</summary>
public record MessageSenderDto(string BattleTag, string Name, ChatProfile Flair);

/// <summary>
/// Wire-facing message projection for <see cref="GetMessagesResult"/> and focused
/// <c>MessageReceived</c> pushes (Task 12 shape). <see cref="Deleted"/>/<see cref="Shadow"/> are
/// user-facing flag slots defined now, populated by C4 — always false until then, including on a
/// shadow author's own echo (the load-bearing illusion, C3-plan.md decision 7).
/// </summary>
public record MessageDto(
    string Id,
    string ChannelId,
    long Seq,
    MessageSenderDto Sender,
    string Content,
    DateTime SentAt,
    bool Deleted,
    bool Shadow);

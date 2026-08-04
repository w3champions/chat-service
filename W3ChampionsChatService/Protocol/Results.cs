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

/// <summary>OpenDm (C5 D18) — same shape as <see cref="JoinChannelResult"/>: the caller's channel +
/// own membership on success, nothing on a typed reject.</summary>
public record OpenDmResult(
    ChatResultCode Code,
    double? RetryAfterSeconds = null,
    ChatChannel Channel = null,
    ChannelMembership Membership = null);

/// <summary>CreateGroup (C5 D18) — the new group channel + the creator's own (Owner) membership.</summary>
public record CreateGroupResult(
    ChatResultCode Code,
    double? RetryAfterSeconds = null,
    ChatChannel Channel = null,
    ChannelMembership Membership = null);

/// <summary>GetMentionInbox (C6 D6) — newest-first, capped at
/// <see cref="Domain.ChatLimits.MentionInboxMaxEntries"/>.</summary>
public record GetMentionInboxResult(
    ChatResultCode Code,
    IReadOnlyList<MentionInboxEntryDto> Entries = null);

/// <summary>SearchMentionCandidates (C6 D10) — tiered viewer/online/directory search results,
/// deduped across tiers and capped at <see cref="Domain.ChatLimits.MentionSearchMaxResults"/>.</summary>
public record SearchMentionCandidatesResult(
    ChatResultCode Code,
    IReadOnlyList<MentionCandidateDto> Candidates = null);

/// <summary>GetPresence (C6 D12) — one-shot, ungated online-bool reads.</summary>
public record GetPresenceResult(
    ChatResultCode Code,
    IReadOnlyList<PresenceStatusDto> Statuses = null);

/// <summary>GetPresenceDetails (C6 D12) — one-shot reads with friend-gated
/// <see cref="PresenceDetailsDto.LastSeenAt"/>.</summary>
public record GetPresenceDetailsResult(
    ChatResultCode Code,
    IReadOnlyList<PresenceDetailsDto> Details = null);

/// <summary>GetConversations (2026-08-04 follow-up spec §6) — one page of the caller's OLDER 1:1 Dm
/// shells, newest-first by (LastMessageAt, ChannelId). Reuses ChannelDto so a paged conversation is
/// byte-shaped like a SessionState.Channels entry. The client derives the next cursor from the last
/// element (channel.LastMessageAt, channel.Id) and detects the end by Count &lt; limit.</summary>
public record GetConversationsResult(
    ChatResultCode Code,
    IReadOnlyList<ChannelDto> Conversations = null);

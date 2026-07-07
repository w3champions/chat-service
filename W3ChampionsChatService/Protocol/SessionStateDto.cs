using System;
using System.Collections.Generic;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Memberships;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// The full session snapshot pushed on every (re)connect (spec acceptance 8) — the single source of
/// truth the client rebuilds its whole UI from. Built by <see cref="SessionStateAssembler"/>.
/// <para>
/// BOUNDARY-PRIVACY CRITICAL (C2 amendment): this type and everything it references must never
/// carry the raw <c>W3CUserAuthentication</c> permission snapshot, any HubCallerContext/Identity
/// object, or the mute shadow flag — see <see cref="OwnProfileDto"/> and <see cref="MuteStateDto"/>.
/// </para>
/// <see cref="PublicCatalog"/> is always present (decision 1: client fallback) EXCEPT on a FULL-ban
/// connect, where the assembler filters it to empty (the full-ban room-scope rule — a full-banned
/// user's public rooms stay hidden). <see cref="PendingDmRequests"/> is the C5 T6 request tray — one
/// entry per PENDING 1:1 Dm the connecting user is the RECIPIENT of (never the initiator's own outgoing
/// requests), minus any the recipient has decline-suppressed within the 24h window; the same
/// pending-recipient channels ALSO ride <see cref="Channels"/> (D4 dual-listing). <see cref="MentionUnreadCount"/>
/// (C6 Task 6, D6) is the live count of the caller's OWN unread <c>mention_inbox</c> entries
/// (<c>ReadAt == null</c>) — <see cref="SessionStateAssembler"/> reads it via
/// <see cref="Mentions.MentionInboxRepository.CountUnread"/> on every (re)connect.
/// </summary>
public record SessionStateDto(
    IReadOnlyList<ChannelDto> Channels,
    IReadOnlyList<ChatChannel> PublicCatalog,
    IReadOnlyList<PendingDmRequestDto> PendingDmRequests,
    int MentionUnreadCount,
    OwnProfileDto OwnProfile,
    MuteStateDto MuteState);

/// <summary>
/// One entry of the caller's channel list: channel metadata (the raw <see cref="ChatChannel"/> —
/// nothing on it is boundary-private, mirroring the existing <c>JoinChannelResult</c> precedent of
/// reusing domain types directly in a wire DTO) plus this user's own membership projection and
/// computed unread state. D7 (Amendment 3): <see cref="UnreadCount"/> is the COUNT of USER-VISIBLE rows
/// after the member's read cursor (<c>MessageRepository.CountUserVisibleAfter</c>) — NOT the raw
/// channel.LastSeq − membership.LastReadSeq delta, which would count invisible foreign-author shadow
/// rows and soft-deleted rows and so generate phantom unread on reconnect. <see cref="HasUnread"/> =
/// UnreadCount &gt; 0.
/// </summary>
public record ChannelDto(
    ChatChannel Channel,
    MembershipDto Membership,
    long UnreadCount,
    bool HasUnread);

/// <summary>
/// Projection of <see cref="W3ChampionsChatService.Memberships.ChannelMembership"/> — only the
/// fields the client needs to render the channel list (no Id/ChannelId/BattleTag/JoinedAt).
/// </summary>
public record MembershipDto(
    NotificationLevel NotificationLevel,
    long LastReadSeq,
    MembershipRole Role)
{
    /// <summary>
    /// The single shared mapper from the persisted <see cref="ChannelMembership"/> down to this wire
    /// shape — used by both <see cref="SessionStateAssembler"/> (the Channels list on every
    /// (re)connect) and <see cref="FanOut.FanOutEngine.PushChannelAdded"/> (the live
    /// <c>ChannelAdded</c> push), so the two call sites can never drift on which membership fields
    /// are client-visible.
    /// </summary>
    public static MembershipDto From(ChannelMembership membership) =>
        new(membership.NotificationLevel, membership.LastReadSeq, membership.Role);
}

/// <summary>
/// The caller's own profile. BOUNDARY-PRIVATE PROJECTION (C2 amendment): <see cref="Permissions"/>
/// is an explicit allow-list of chat-relevant permission NAMES (currently only "Moderation") —
/// never the raw <c>IReadOnlySet&lt;EPermission&gt;</c> identity snapshot — and this type carries no
/// Identity/Context object of any kind.
/// </summary>
public record OwnProfileDto(
    string BattleTag,
    string Name,
    bool IsAdmin,
    ChatProfile Flair,
    IReadOnlyList<string> Permissions);

/// <summary>
/// FULL bans only, exposed as expiry alone — mirrors <c>MuteReconciliationService</c>'s
/// <c>PlayerBannedFromChat</c> payload shape (endDate, nothing else). A SHADOW ban or no active mute
/// means <see cref="SessionStateDto.MuteState"/> is null: the shadow flag must NEVER reach the
/// client (spec §12 / <c>MuteReconciliationService.cs:88-94</c> policy).
/// </summary>
public record MuteStateDto(DateTime EndDate);

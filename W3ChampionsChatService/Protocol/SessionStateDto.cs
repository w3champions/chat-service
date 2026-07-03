using System;
using System.Collections.Generic;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;

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
/// connect, where the assembler filters it to empty (carries the legacy
/// <c>LoginAsAuthenticated</c> room-scope semantics — a full-banned user's public rooms stay
/// hidden). <see cref="PendingDmRequests"/>/<see cref="MentionUnreadCount"/> are C3 stubs — always
/// empty/zero until the DM and mention-inbox features land.
/// </summary>
public record SessionStateDto(
    IReadOnlyList<ChannelDto> Channels,
    IReadOnlyList<ChatChannel> PublicCatalog,
    IReadOnlyList<object> PendingDmRequests,
    int MentionUnreadCount,
    OwnProfileDto OwnProfile,
    MuteStateDto MuteState);

/// <summary>
/// One entry of the caller's channel list: channel metadata (the raw <see cref="ChatChannel"/> —
/// nothing on it is boundary-private, mirroring the existing <c>JoinChannelResult</c> precedent of
/// reusing domain types directly in a wire DTO) plus this user's own membership projection and
/// computed unread state. <see cref="UnreadCount"/> = max(0, channel.LastSeq -
/// membership.LastReadSeq); <see cref="HasUnread"/> = UnreadCount &gt; 0.
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
    MembershipRole Role);

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

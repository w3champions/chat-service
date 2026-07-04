using System;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// One <c>GetPresence</c> result row (C6-plan.md D12) — a one-shot, UNGATED online-bool read; the
/// same observability a viewer roster or focused-set already gives, so no relationship check applies.
/// </summary>
public record PresenceStatusDto(string BattleTag, bool Online);

/// <summary>
/// One <c>GetPresenceDetails</c> result row (C6-plan.md D12) — same guards as
/// <see cref="PresenceStatusDto"/>, but <see cref="LastSeenAt"/> is populated ONLY for tags that
/// are the caller's FRIENDS per their cached relationship snapshot. Non-friends (and callers whose
/// snapshot is unavailable) come back with a null <see cref="LastSeenAt"/> by design — the
/// sensitive datum fails closed while <see cref="Online"/> stays honest for everyone.
/// </summary>
public record PresenceDetailsDto(string BattleTag, bool Online, DateTime? LastSeenAt);

/// <summary>
/// Payload for <see cref="ChatEvents.PresenceChanged"/> (C6-plan.md D11) — pushed ONLY to
/// connections with derived interest (a focused Dm/GroupDm containing <see cref="BattleTag"/>);
/// interest is revoked on unfocus, watcher disconnect, or membership change. There is no
/// presence-subscribe API — interest is derived, never requested.
/// </summary>
public record PresenceChangedDto(string BattleTag, bool Online);

/// <summary>
/// Payload for <see cref="ChatEvents.FriendPresenceChanged"/> (C6-plan.md D13) — pushed to a user's
/// online friends on a true online/offline transition, replacing the wb <c>FriendOnlineStatus</c>
/// push. <see cref="BattleTag"/> is the SUBJECT who transitioned (display casing), not the recipient.
/// </summary>
public record FriendPresenceChangedDto(string BattleTag, bool Online);

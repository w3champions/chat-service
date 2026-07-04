using System;
using System.Collections.Generic;
using System.Linq;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Relationships;

/// <summary>
/// An immutable point-in-time view of one player's friends + blocked lists, read from the website
/// backend (C5/D1). <see cref="Friends"/>/<see cref="Blocked"/> are always non-null and compared
/// case-insensitively — battleTags are stored lowercased server-side but arrive with live casing — so
/// <see cref="IsFriendWith"/>/<see cref="HasBlocked"/> AND any direct <c>Contains</c> check a consumer
/// makes (e.g. the group friends-gate) are OrdinalIgnoreCase regardless of how this snapshot was built.
/// <see cref="FetchedAt"/> anchors freshness (<see cref="IsFresh"/>); a stale snapshot is still a valid
/// last-known fallback (spec §14) — the provider decides per call site whether staleness is acceptable.
/// </summary>
public sealed record RelationshipSnapshot
{
    public RelationshipSnapshot(
        string battleTag, IReadOnlySet<string> friends, IReadOnlySet<string> blocked, DateTime fetchedAt)
    {
        BattleTag = battleTag;
        Friends = ToCaseInsensitiveSet(friends);
        Blocked = ToCaseInsensitiveSet(blocked);
        FetchedAt = fetchedAt;
    }

    public string BattleTag { get; }
    public IReadOnlySet<string> Friends { get; }
    public IReadOnlySet<string> Blocked { get; }
    public DateTime FetchedAt { get; }

    /// <summary>True while this snapshot is within <see cref="ChatLimits.RelationshipCacheTtl"/> of
    /// <see cref="FetchedAt"/>. Stranger-DM initiation and group friend-checks require freshness; the
    /// 1:1 delivery block-check accepts any snapshot (last-known fallback included).</summary>
    public bool IsFresh(DateTime now) => now - FetchedAt <= ChatLimits.RelationshipCacheTtl;

    /// <summary>OrdinalIgnoreCase membership test against the friends list.</summary>
    public bool IsFriendWith(string other) => other is not null && Friends.Contains(other);

    /// <summary>OrdinalIgnoreCase membership test against the blocked list.</summary>
    public bool HasBlocked(string other) => other is not null && Blocked.Contains(other);

    // Copies into an OrdinalIgnoreCase set so the predicates above (and consumer Contains calls) are
    // case-insensitive no matter what comparer the caller's set used; drops null/whitespace entries so a
    // sloppy source payload can never seed a bogus friend/block edge. Null in => empty set (never null).
    private static IReadOnlySet<string> ToCaseInsensitiveSet(IReadOnlySet<string> values) =>
        values is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                values.Where(v => !string.IsNullOrWhiteSpace(v)), StringComparer.OrdinalIgnoreCase);
}

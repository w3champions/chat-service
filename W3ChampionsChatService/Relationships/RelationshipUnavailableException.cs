using System;

namespace W3ChampionsChatService.Relationships;

/// <summary>
/// Thrown by <see cref="RelationshipProvider.GetSnapshotAsync"/> when a snapshot cannot be produced and
/// there is NOTHING cached to fall back to (C5/D1, the fail-closed root). Relationship-gated call sites
/// map this to a typed RETRIABLE reject (a <c>Throttled</c> carrying
/// <see cref="Domain.ChatLimits.RelationshipRetryAfterSeconds"/>) — NEVER to a silent "no block / no
/// friend" decision. Distinct from a stale-but-present snapshot, which is returned as a last-known
/// fallback rather than throwing.
/// </summary>
public sealed class RelationshipUnavailableException(string battleTag, Exception innerException)
    : Exception($"Relationship snapshot unavailable for '{battleTag}' and no cached fallback exists.", innerException)
{
    /// <summary>The battleTag whose snapshot could not be produced.</summary>
    public string BattleTag { get; } = battleTag;
}

using System.Threading.Tasks;

namespace W3ChampionsChatService.Relationships;

/// <summary>
/// Cache + fail-closed policy over <see cref="IRelationshipSource"/> (C5/D1) — the block-enforcement
/// foundation. Gating call sites (C5 later tasks, C6) read snapshots through here and decide per site
/// whether a stale snapshot is acceptable (initiation/friend-checks require freshness; the 1:1 delivery
/// block-check accepts any snapshot).
/// </summary>
public interface IRelationshipProvider
{
    /// <summary>
    /// Returns the cached snapshot if it is fresh (within
    /// <see cref="Domain.ChatLimits.RelationshipCacheTtl"/>); otherwise fetches via the source and caches
    /// it. On a fetch FAILURE, returns the STALE cached snapshot if one exists (last-known fallback,
    /// spec §14); if nothing is cached at all, throws <see cref="RelationshipUnavailableException"/>.
    /// NEVER returns null — callers must not read a missing snapshot as "no friend / no block".
    /// </summary>
    Task<RelationshipSnapshot> GetSnapshotAsync(string battleTag);

    /// <summary>
    /// Drops the cache entry for <paramref name="battleTag"/> so the next read refetches — C7's
    /// change-ping seam. Thread-safe, synchronous, O(1); a no-op if nothing is cached.
    /// </summary>
    void Invalidate(string battleTag);
}

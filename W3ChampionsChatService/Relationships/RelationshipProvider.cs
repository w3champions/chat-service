using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace W3ChampionsChatService.Relationships;

/// <summary>
/// In-memory, lock-guarded cache over an <see cref="IRelationshipSource"/> implementing the C5/D1
/// three-tier fail-closed policy:
/// <list type="number">
/// <item>a FRESH cached snapshot (within <see cref="Domain.ChatLimits.RelationshipCacheTtl"/>) is served
/// without touching the source;</item>
/// <item>otherwise the source is fetched and the result cached;</item>
/// <item>on a fetch FAILURE the STALE cached snapshot is returned as a last-known fallback (spec §14) if
/// one exists, else <see cref="RelationshipUnavailableException"/> is thrown (the fail-closed root).</item>
/// </list>
/// Cache keyed by battleTag OrdinalIgnoreCase (mirroring <see cref="Sessions.SessionRegistry"/>). Singleton
/// (Startup) so every hub invocation shares one cache. The source is awaited OUTSIDE the lock and the
/// result published UNDER it, so a slow wb read never blocks other tags and concurrent reads for the same
/// tag never tear cache state. A per-provider version stamp makes <see cref="Invalidate"/> authoritative:
/// a fetch that started before an invalidation can never re-publish the pre-change snapshot over it (C7's
/// change-ping must win). No negative caching (a failed fetch is never cached) — kept simple; a future
/// option if wb outages prove costly.
/// </summary>
public sealed class RelationshipProvider(IRelationshipSource source, TimeProvider timeProvider) : IRelationshipProvider
{
    private readonly IRelationshipSource _source = source;
    private readonly TimeProvider _timeProvider = timeProvider;

    private readonly Dictionary<string, RelationshipSnapshot> _cache =
        new Dictionary<string, RelationshipSnapshot>(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new object();

    // Bumped on every Invalidate. A fetch captures the version before it starts and only publishes if the
    // version is unchanged when it completes — so an Invalidate landing mid-fetch drops the in-flight
    // (pre-change) result instead of resurrecting it as "fresh".
    private long _version;

    /// <inheritdoc />
    public async Task<RelationshipSnapshot> GetSnapshotAsync(string battleTag)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Tier 1 — a fresh cached snapshot wins without touching the source (cached data always beats an
        // outage; friend-cache hits proceed). Capture the version under the same lock for the publish guard.
        long versionAtStart;
        lock (_lock)
        {
            if (_cache.TryGetValue(battleTag, out var cached) && cached.IsFresh(now))
            {
                return cached;
            }
            versionAtStart = _version;
        }

        // Tier 2 — stale or absent: fetch OUTSIDE the lock so one slow wb read never blocks other tags'
        // reads, then publish UNDER the lock. A null result is treated as a failure (fail-closed), never
        // cached or returned — the "never returns null" guarantee does not trust the swappable source.
        try
        {
            var fetched = await _source.FetchAsync(battleTag, now)
                ?? throw new InvalidOperationException("relationship source returned a null snapshot");
            lock (_lock)
            {
                // Only publish if no Invalidate (C7 change-ping) landed while this fetch was in flight —
                // the post-change state must win, never be overwritten by the snapshot we were fetching.
                if (_version == versionAtStart)
                {
                    _cache[battleTag] = fetched;
                }
            }
            return fetched;
        }
        catch (Exception ex)
        {
            // Tier 3 — fail closed. Prefer the last-known snapshot (usable by the 1:1 delivery block-check;
            // initiation/friend-check call sites re-check IsFresh and reject a stale one). With NOTHING
            // cached this is the ONLY place the provider throws — callers map it to a typed retriable
            // reject and MUST NOT read it as "no friend / no block".
            lock (_lock)
            {
                if (_cache.TryGetValue(battleTag, out var stale))
                {
                    return stale;
                }
            }

            throw new RelationshipUnavailableException(battleTag, ex);
        }
    }

    /// <inheritdoc />
    public void Invalidate(string battleTag)
    {
        lock (_lock)
        {
            // Bump first so any fetch already in flight (captured the old version) refuses to re-publish.
            _version++;
            _cache.Remove(battleTag);
        }
    }
}

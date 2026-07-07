using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using W3ChampionsChatService.Relationships;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Test double for <see cref="IRelationshipSource"/> — counts fetches, can be switched to fault, exposes
/// a first-fetch signal so a test can observe the fire-and-forget connect prefetch without racing it, and
/// (optionally) a <see cref="ReleaseGate"/> that holds each fetch open so a test can control timing (e.g.
/// Invalidate-mid-fetch, or prove the prefetch does not block connect). <see cref="FetchAsync"/> is
/// genuinely async (it yields), so concurrent provider calls actually interleave. NEVER performs HTTP.
/// </summary>
internal sealed class FakeRelationshipSource(Func<string, DateTime, RelationshipSnapshot> snapshotFactory = null)
    : IRelationshipSource
{
    private readonly Func<string, DateTime, RelationshipSnapshot> _snapshotFactory = snapshotFactory;
    private readonly TaskCompletionSource<string> _firstFetch =
        new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _fetchCount;

    /// <summary>When true, every <see cref="FetchAsync"/> records the call then faults (simulating a wb
    /// outage) so the provider must fall back or fail closed.</summary>
    public bool ShouldThrow { get; set; }

    /// <summary>When set, each fetch awaits this before producing its result — lets a test hold a fetch
    /// in flight (recorded as started) to drive timing-sensitive scenarios. Null => the fetch just yields.</summary>
    public Task ReleaseGate { get; set; }

    public int FetchCount => Volatile.Read(ref _fetchCount);

    /// <summary>Completes with the battleTag of the FIRST fetch (recorded BEFORE any gate/yield) — lets a
    /// test await the fire-and-forget connect prefetch deterministically instead of polling.</summary>
    public Task<string> FirstFetch => _firstFetch.Task;

    public async Task<RelationshipSnapshot> FetchAsync(string battleTag, DateTime now)
    {
        Interlocked.Increment(ref _fetchCount);
        _firstFetch.TrySetResult(battleTag);

        if (ReleaseGate != null)
        {
            await ReleaseGate;
        }
        else
        {
            await Task.Yield();
        }

        if (ShouldThrow)
        {
            throw new InvalidOperationException("relationship source unavailable (test)");
        }

        return _snapshotFactory?.Invoke(battleTag, now)
            ?? new RelationshipSnapshot(
                battleTag,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                now);
    }
}

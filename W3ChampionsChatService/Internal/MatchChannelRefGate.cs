using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace W3ChampionsChatService.Internal;

/// <summary>
/// Serializes every mutating operation on a single match channel by its systemRef (plan D5, 2026-08-05
/// reconciliation spec). WHY IT EXISTS: <see cref="Channels.ChannelRepository.TryAdvanceAssertion"/>
/// admits an <c>(epoch, seq)</c> assertion atomically, but the MEMBERSHIP DIFF that follows it is a
/// sequence of independent writes — two admitted assertions (seq 6 and seq 7) could otherwise interleave
/// their diffs and leave the channel doc claiming seq 7 while the membership rows still reflect seq 6.
/// This gate closes that gap by making the whole "admit, diff, converge" operation atomic per ref, not
/// just the admission check.
/// <para>
/// A COMPLETE guard, not merely a best-effort one — the service runs single-instance by design (already
/// documented on <see cref="Channels.ChannelRepository.AllocateSeq"/>). The durable
/// <c>TryAdvanceAssertion</c> CAS remains the backstop if that assumption is ever broken; only the diff
/// interleaving above would then be unguarded, which is the pre-existing residual already called out on
/// <c>MatchChannelService</c>'s class doc.
/// </para>
/// <para>
/// EVICTION: match refs are unbounded over time — one per lobby, forever — so a semaphore-per-ref map
/// that only ever grows is not acceptable. Entries are reference-counted and removed the instant the last
/// holder releases, so the map only ever holds refs with a genuinely in-flight operation.
/// </para>
/// <para>
/// Only one gate is ever held at a time by a caller (no nesting, no lock-ordering hazard) — the mutating
/// service methods this guards never call each other, so there is no re-entrancy deadlock. Callers MUST
/// acquire via <c>using var _ = await AcquireAsync(...);</c>: <c>using</c> lowers to a <c>try/finally</c>,
/// so a throwing guarded body still releases — but nothing here recovers a holder that never calls
/// <see cref="IDisposable.Dispose"/> at all, which would wedge every future caller for that ref
/// permanently.
/// </para>
/// </summary>
internal sealed class MatchChannelRefGate
{
    // StringComparer.Ordinal: systemRef is already validated to [A-Za-z0-9_-]{1,64} and is an exact Mongo
    // key elsewhere in the codebase — no case folding is meaningful or wanted here.
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    // Guards ONLY the dictionary + ref-counts below; no async work (in particular, no
    // SemaphoreSlim.WaitAsync) ever happens while this is held.
    private readonly object _lock = new();

    /// <summary>
    /// Acquires the exclusive gate for <paramref name="systemRef"/>, awaiting the current holder (if any)
    /// first. Returns a releaser — wrap the call as <c>using var _ = await AcquireAsync(systemRef);</c>;
    /// see this class's doc for why the caller, not the gate, owns exception-safety here.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(string systemRef)
    {
        Entry entry;
        lock (_lock)
        {
            if (!_entries.TryGetValue(systemRef, out entry))
            {
                entry = new Entry();
                _entries[systemRef] = entry;
            }

            // Bumped BEFORE the wait below, under the same lock as every other ref-count mutation — this
            // is what keeps a contended entry alive while a second caller is queued on its semaphore (see
            // Release: eviction only fires once RefCount reaches zero).
            entry.RefCount++;
        }

        // The wait itself happens OUTSIDE the lock: SemaphoreSlim.WaitAsync can genuinely suspend, and a
        // plain `lock` cannot span an `await`. Holding the dictionary lock across the wait would also
        // serialize acquires for every OTHER ref on one global lock, defeating the point of a keyed,
        // per-ref gate.
        await entry.Semaphore.WaitAsync();

        return new Releaser(this, systemRef, entry);
    }

    // Test seam (assembly has InternalsVisibleTo, mirrors ReadRateLimiter.TrackedUserCount) — proves
    // eviction: 0 once every acquired holder has released.
    internal int TrackedRefCount
    {
        get { lock (_lock) { return _entries.Count; } }
    }

    // Invoked at most once per Releaser (guarded by its own Interlocked idempotency flag — see below).
    // Releases the semaphore FIRST so a queued waiter can proceed immediately, then removes and disposes
    // the entry under the lock only once its ref-count reaches zero. A queued waiter already bumped
    // RefCount before it started waiting (AcquireAsync above), so a contended entry can never be evicted
    // out from under a pending acquire.
    private void Release(string systemRef, Entry entry)
    {
        entry.Semaphore.Release();

        lock (_lock)
        {
            entry.RefCount--;
            if (entry.RefCount == 0)
            {
                _entries.Remove(systemRef);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int RefCount;
    }

    // Idempotent by construction: `_released` flips from 0 to 1 via a single Interlocked.Exchange, so a
    // second (or racing concurrent) Dispose observes a nonzero result and is a guaranteed no-op — a
    // `using` nested inside a `try/finally`, or any other double-release shape, can never over-release the
    // underlying semaphore and let two holders in at once.
    private sealed class Releaser(MatchChannelRefGate gate, string systemRef, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            gate.Release(systemRef, entry);
        }
    }
}

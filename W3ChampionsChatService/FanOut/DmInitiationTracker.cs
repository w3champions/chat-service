using System;
using System.Collections.Generic;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.FanOut;

/// <summary>
/// In-memory, lock-guarded tracker of unaccepted stranger-DM initiations, enforcing the
/// <see cref="ChatLimits.StrangerDmInitiationCap"/>-per-<see cref="ChatLimits.StrangerDmInitiationWindow"/>
/// cap (C5/D7). Each NEW stranger-shell creation is <see cref="Record"/>ed as a
/// (initiator, targetNormalized, at) event; the cap is applied by the caller against
/// <see cref="CountActive"/>. Events age out at the 8h window (<see cref="CountActive"/>/<see cref="RetryAfterSeconds"/>
/// prune on read), and an <see cref="MarkAccepted"/> frees a pair's slots INSTANTLY (spec "accepted frees
/// capacity"). It is deliberately decision-agnostic — it cannot see WHY an initiation happened, so a
/// blocked-target or later-declined initiation counts identically until it ages out ("blocked count",
/// "declined still count", D7).
/// <para>
/// Structurally mirrors <see cref="ChannelCreationRateLimiter"/>: a single <see cref="Dictionary{TKey,TValue}"/>
/// keyed by initiator (OrdinalIgnoreCase — battleTags arrive with live casing but are stored lowercased),
/// one lock, and an opportunistic stale-event purge on every call so the map cannot grow unbounded. The
/// clock is always passed in (<c>now</c>), never read internally, so the whole thing is deterministic
/// under a FakeTimeProvider. Singleton (Startup) — a transient would fragment each initiator's counter.
/// It is an EVENT model, not a live-pending-shell count: an expired shell's event ages out and a fresh
/// initiation to the same pair re-counts (D7 / OQ-4).
/// </para>
/// </summary>
public class DmInitiationTracker
{
    // initiator -> its outstanding initiation events (target it initiated + when).
    private readonly Dictionary<string, List<(string Target, DateTime At)>> _eventsByInitiator =
        new Dictionary<string, List<(string Target, DateTime At)>>(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new object();

    /// <summary>Records a NEW stranger-shell creation by <paramref name="initiator"/> to
    /// <paramref name="targetNormalized"/> at <paramref name="now"/>. Only genuine new shells are recorded
    /// (the hub skips this for an already-existing conversation and for a concurrent-upsert that returned
    /// an existing doc).
    /// <para>
    /// TEST-SUPPORT API: no production caller. Production <c>OpenDm</c> uses the atomic
    /// <see cref="TryRecord"/> (which folds the check-and-record into one critical section); this method
    /// is kept as a direct seed primitive for unit tests that need to pre-populate events without going
    /// through the cap check.
    /// </para>
    /// </summary>
    public void Record(string initiator, string targetNormalized, DateTime now)
    {
        lock (_lock)
        {
            PruneNoLock(now);
            if (!_eventsByInitiator.TryGetValue(initiator, out var events))
            {
                events = new List<(string, DateTime)>();
                _eventsByInitiator[initiator] = events;
            }
            events.Add((targetNormalized, now));
        }
    }

    /// <summary>ATOMIC check-and-record of a NEW stranger-shell initiation under ONE lock (C5 FIX 2): prunes,
    /// and if <paramref name="initiator"/> is already at/over <paramref name="cap"/> active initiations
    /// returns <c>false</c> WITHOUT appending; otherwise appends the (<paramref name="targetNormalized"/>,
    /// <paramref name="now"/>) event and returns <c>true</c>. Collapsing the former separate
    /// <see cref="CountActive"/>-then-<see cref="Record"/> into a single critical section closes the TOCTOU
    /// that let concurrent same-caller opens slip past the cap — "≤cap genuinely-new stranger initiations
    /// admitted" now holds structurally, not just by the single-connection-per-battleTag invariant.</summary>
    public bool TryRecord(string initiator, string targetNormalized, DateTime now, int cap)
    {
        lock (_lock)
        {
            PruneNoLock(now);
            var count = _eventsByInitiator.TryGetValue(initiator, out var events) ? events.Count : 0;
            if (count >= cap)
            {
                return false;
            }

            if (events == null)
            {
                events = new List<(string, DateTime)>();
                _eventsByInitiator[initiator] = events;
            }
            events.Add((targetNormalized, now));
            return true;
        }
    }

    /// <summary>Frees the (<paramref name="initiator"/>, <paramref name="targetNormalized"/>) pair's slot
    /// INSTANTLY on accept — removes EVERY event for that pair (case-insensitively), so an accepted
    /// conversation stops counting well before the 8h window. Called by the reply-accept / AcceptRequest
    /// transitions (T4/T6).</summary>
    public void MarkAccepted(string initiator, string targetNormalized)
    {
        lock (_lock)
        {
            if (_eventsByInitiator.TryGetValue(initiator, out var events))
            {
                events.RemoveAll(e => string.Equals(e.Target, targetNormalized, StringComparison.OrdinalIgnoreCase));
                if (events.Count == 0)
                {
                    _eventsByInitiator.Remove(initiator);
                }
            }
        }
    }

    /// <summary>The number of <paramref name="initiator"/>'s unaccepted initiations still inside the 8h
    /// window at <paramref name="now"/> (aged-out events are pruned first). The caller rejects a new
    /// initiation when this is <c>&gt;= <see cref="ChatLimits.StrangerDmInitiationCap"/></c>.
    /// <para>
    /// TEST-SUPPORT / observability API: no production caller. Production <c>OpenDm</c> gates on the
    /// atomic <see cref="TryRecord"/> return value instead of calling this then <see cref="Record"/>
    /// separately (that two-step was the pre-C5-FIX-2 TOCTOU). Kept as a mutation-free read accessor for
    /// direct unit-test assertions on tracker state.
    /// </para>
    /// </summary>
    public int CountActive(string initiator, DateTime now)
    {
        lock (_lock)
        {
            PruneNoLock(now);
            return _eventsByInitiator.TryGetValue(initiator, out var events) ? events.Count : 0;
        }
    }

    /// <summary>Seconds until <paramref name="initiator"/>'s OLDEST still-active event ages out of the 8h
    /// window (i.e. when a capped initiator regains a slot) — 0 when there are no active events. Surfaced
    /// on the typed <c>Throttled</c> reject so the client retries after a slot frees.</summary>
    public double RetryAfterSeconds(string initiator, DateTime now)
    {
        lock (_lock)
        {
            PruneNoLock(now);
            if (!_eventsByInitiator.TryGetValue(initiator, out var events) || events.Count == 0)
            {
                return 0;
            }

            var oldest = events[0].At;
            for (var i = 1; i < events.Count; i++)
            {
                if (events[i].At < oldest)
                {
                    oldest = events[i].At;
                }
            }

            var retryAfter = (oldest + ChatLimits.StrangerDmInitiationWindow - now).TotalSeconds;
            return retryAfter > 0 ? retryAfter : 0;
        }
    }

    // Caller must already hold _lock. Drops every event that has reached/passed the 8h window (age >=
    // window) across ALL initiators so the map can't grow unbounded, and removes now-empty initiator keys.
    private void PruneNoLock(DateTime now)
    {
        var cutoff = now - ChatLimits.StrangerDmInitiationWindow;
        List<string> emptyKeys = null;
        foreach (var kvp in _eventsByInitiator)
        {
            kvp.Value.RemoveAll(e => e.At <= cutoff);
            if (kvp.Value.Count == 0)
            {
                (emptyKeys ??= new List<string>()).Add(kvp.Key);
            }
        }

        if (emptyKeys == null)
        {
            return;
        }
        foreach (var key in emptyKeys)
        {
            _eventsByInitiator.Remove(key);
        }
    }
}

using System;
using System.Collections.Generic;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Sessions;

/// <summary>
/// Generic keyed fixed-window rate limiter, in-memory and single-instance by design — mirrors
/// TicketStore's node-local placement (spec §2 state placement — no Mongo for this). Key-agnostic:
/// the caller composes keys with a prefix discipline (e.g. "bt:{battleTag}" / "ip:{ip}") so one
/// instance can serve multiple independent limits (per-battleTag, per-IP) without their windows
/// colliding. Concurrency idiom mirrors Chats/ConnectionMapping.cs and Sessions/TicketStore.cs:
/// a private Dictionary guarded by a single lock object, with every public method doing its work
/// inside that lock.
/// </summary>
public class MintRateLimiter
{
    private readonly Dictionary<string, (DateTime WindowStart, int Count)> _windows =
        new Dictionary<string, (DateTime WindowStart, int Count)>();

    private readonly object _lock = new object();

    // Purge test seam — internals visible to W3ChampionsChatService.Tests (see Chats/ChatHub.cs).
    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _windows.Count;
            }
        }
    }

    /// <summary>
    /// Fixed-window acquire for <paramref name="key"/>: allows up to <paramref name="limit"/> calls
    /// within a live ChatLimits.TicketMintWindow, then denies until the window rolls over. Purges
    /// stale windows (across all keys) opportunistically on every call so per-IP keys can't grow
    /// unbounded. Still the per-battleTag mint cap's mechanism (AuthSessionController) — unchanged.
    /// </summary>
    public bool TryAcquire(string key, int limit, DateTime now)
    {
        lock (_lock)
        {
            PurgeStaleNoLock(now);

            if (_windows.TryGetValue(key, out var window) && window.WindowStart + ChatLimits.TicketMintWindow > now)
            {
                if (window.Count >= limit)
                {
                    return false;
                }

                _windows[key] = (window.WindowStart, window.Count + 1);
                return true;
            }

            _windows[key] = (now, 1);
            return true;
        }
    }

    /// <summary>
    /// PURE READ (no mutation, no purge): returns true iff a live (non-expired) window exists for
    /// <paramref name="key"/> whose Count has already reached <paramref name="limit"/>. This is the
    /// pre-validation half of the F1 per-IP mint shield: the caller asks, BEFORE doing any expensive
    /// RSA validation, whether this source IP has already burned its REJECTION budget for the current
    /// window. Deliberately does NOT purge — a stale window reads as absent (not at limit) but is only
    /// physically removed by a later mutating call (<see cref="Record"/>/<see cref="TryAcquire"/>), so
    /// this stays a side-effect-free read (a limiter that mutated on every read couldn't distinguish a
    /// "just checking" caller from a real charge).
    /// </summary>
    public bool IsAtLimit(string key, int limit, DateTime now)
    {
        lock (_lock)
        {
            return _windows.TryGetValue(key, out var window)
                && window.WindowStart + ChatLimits.TicketMintWindow > now
                && window.Count >= limit;
        }
    }

    /// <summary>
    /// Charges one unit against <paramref name="key"/> UNCONDITIONALLY (no limit check, no return):
    /// creates a fresh window if none exists, rolls a stale one over, or bumps a live one. Purges stale
    /// windows (across all keys) opportunistically, exactly like <see cref="TryAcquire"/>, so per-IP
    /// keys can't grow unbounded. This is the mutating half of the F1 per-IP mint shield: only a
    /// REJECTED mint attempt (auth failure or per-battleTag throttle) calls this. A SUCCESSFUL mint
    /// never does, so a valid-JWT reconnect storm of many distinct battleTags behind one shared proxy
    /// IP never charges the IP budget and is never IP-throttled (see AuthSessionController.MintTicket).
    /// </summary>
    public void Record(string key, DateTime now)
    {
        lock (_lock)
        {
            PurgeStaleNoLock(now);

            if (_windows.TryGetValue(key, out var window) && window.WindowStart + ChatLimits.TicketMintWindow > now)
            {
                _windows[key] = (window.WindowStart, window.Count + 1);
                return;
            }

            _windows[key] = (now, 1);
        }
    }

    // Caller must already hold _lock.
    private void PurgeStaleNoLock(DateTime now)
    {
        var staleKeys = new List<string>();
        foreach (var kvp in _windows)
        {
            if (kvp.Value.WindowStart + ChatLimits.TicketMintWindow <= now)
            {
                staleKeys.Add(kvp.Key);
            }
        }

        foreach (var staleKey in staleKeys)
        {
            _windows.Remove(staleKey);
        }
    }
}

using System;
using System.Collections.Generic;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.FanOut;

/// <summary>
/// The outcome of a single <see cref="ChannelCreationRateLimiter.TryAcquire"/> call.
/// </summary>
/// <param name="Allowed">True when the implicit semiPublic creation may proceed.</param>
/// <param name="RetryAfterSeconds">
/// When throttled, seconds remaining until the caller's fixed window resets — JoinChannel's
/// <c>Throttled</c> result carries this straight through. Null when <paramref name="Allowed"/> is true.
/// </param>
public readonly record struct ChannelCreationDecision(bool Allowed, double? RetryAfterSeconds);

/// <summary>
/// Fixed-window per-battleTag rate limiter guarding IMPLICIT semiPublic channel creation from
/// <c>ChatHub.JoinChannel</c> (<see cref="ChatLimits.ChannelCreationPerHour"/> over
/// <see cref="ChatLimits.ChannelCreationWindow"/> — C3 Task 10).
/// <para>
/// Structurally mirrors <see cref="Sessions.MintRateLimiter"/> EXACTLY (same
/// <c>Dictionary&lt;key, (WindowStart, Count)&gt;</c> + single-lock + opportunistic stale-window purge
/// idiom), but is deliberately a SEPARATE class rather than a second <see cref="Sessions.MintRateLimiter"/>
/// instance: that class hardcodes its window to <see cref="ChatLimits.TicketMintWindow"/> (1 minute)
/// inside <c>TryAcquire</c>/<c>PurgeStaleNoLock</c>, so pointing a second instance of it at this
/// per-HOUR cap would silently apply the wrong (1-minute) window — the "reuse if the API is general
/// enough" building-block note resolves to "no" for that reason.
/// </para>
/// <para>
/// Only ACTUAL creations are metered: JoinChannel calls <see cref="TryAcquire"/> ONLY on the
/// implicit-create path (no existing channel by that normalized name) — never on a join of an
/// existing public/semiPublic channel, and never on the ACL-type-rejected or idempotent-already-member
/// paths.
/// </para>
/// <para>
/// Singleton by design: a transient registration would fragment each battleTag's creation counter
/// across hub invocations, defeating the cap entirely. Registered in Startup.cs; see
/// StartupDependencyInjectionTests for the DI guard.
/// </para>
/// </summary>
public class ChannelCreationRateLimiter
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
    /// Fixed-window acquire for <paramref name="battleTag"/>: allows up to
    /// <see cref="ChatLimits.ChannelCreationPerHour"/> implicit creations within a live
    /// <see cref="ChatLimits.ChannelCreationWindow"/>, then denies — with the seconds remaining until
    /// the window resets — until it rolls over. Purges stale windows (across all keys) opportunistically
    /// on every call so the map can't grow unbounded.
    /// </summary>
    public ChannelCreationDecision TryAcquire(string battleTag, DateTime now)
    {
        lock (_lock)
        {
            PurgeStaleNoLock(now);

            if (_windows.TryGetValue(battleTag, out var window) && window.WindowStart + ChatLimits.ChannelCreationWindow > now)
            {
                if (window.Count >= ChatLimits.ChannelCreationPerHour)
                {
                    var retryAfterSeconds = (window.WindowStart + ChatLimits.ChannelCreationWindow - now).TotalSeconds;
                    return new ChannelCreationDecision(false, retryAfterSeconds);
                }

                _windows[battleTag] = (window.WindowStart, window.Count + 1);
                return new ChannelCreationDecision(true, null);
            }

            _windows[battleTag] = (now, 1);
            return new ChannelCreationDecision(true, null);
        }
    }

    // Caller must already hold _lock.
    private void PurgeStaleNoLock(DateTime now)
    {
        var staleKeys = new List<string>();
        foreach (var kvp in _windows)
        {
            if (kvp.Value.WindowStart + ChatLimits.ChannelCreationWindow <= now)
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

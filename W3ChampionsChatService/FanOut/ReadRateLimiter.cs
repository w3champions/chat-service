using System;
using System.Collections.Generic;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.FanOut;

/// <summary>
/// The outcome of a single <see cref="ReadRateLimiter.TryAcquire"/> call. Deliberately simpler than
/// <see cref="RateLimitDecision"/>: there is no escalation signal because this limiter has no violation
/// ladder (see <see cref="ReadRateLimiter"/>'s class doc).
/// </summary>
/// <param name="Allowed">True when the read may proceed (a token was consumed from the bucket).</param>
/// <param name="RetryAfterSeconds">
/// When throttled, seconds until the caller could read again — strictly positive. Null when
/// <paramref name="Allowed"/> is true.
/// </param>
public readonly record struct ReadRateLimitDecision(bool Allowed, double? RetryAfterSeconds);

/// <summary>
/// Pure, deterministic abuse guard for READ-shaped hub methods (2026-08-05 PR36 feedback, Part 3 —
/// Marco: server protection, NOT UX pacing). <c>GetConversations</c> and <c>GetMessages</c> (the
/// GetMessages scope decision + its supporting evidence are recorded in the task report) share ONE
/// per-battleTag token bucket — capacity <see cref="ChatLimits.ReadBurst"/>, refilling
/// <see cref="ChatLimits.ReadRefillPerSecond"/> tokens/second (burst 60, sustained 5/s — sized for the
/// CONNECT FAN-OUT shape, not a per-user-action handful of loads; see <see cref="ChatLimits.ReadBurst"/>'s
/// doc comment for the full rationale, fix round 1 finding F1).
/// <para>
/// Deliberately simpler than <see cref="MessageRateLimiter"/>: a single bucket, no per-channel
/// dimension, NO violation ladder / hard auto-throttle escalation, and NO
/// <see cref="Protocol.ChatEvents.ThrottleNotice"/> push. A denial is just the existing typed
/// <c>Throttled</c> result code with a retry-after — the SAME shape these methods already use for their
/// relationship-outage fail-closed path, so callers have nothing new to special-case.
/// </para>
/// <para>
/// DELIBERATE DUPLICATION (do not refactor to share code with <see cref="MessageRateLimiter"/>): both
/// implement a continuous token bucket, but the two limiters' semantics genuinely differ — dual-bucket-
/// plus-escalation-ladder vs a single bucket with none of that — and forcing a shared abstraction across
/// that split would cost more than the ~30 lines of duplicated <c>TokenBucket</c> logic it would save.
/// </para>
/// <para>
/// NO timers and NO wall-clock reads: every decision takes an explicit <c>now</c> and refills are
/// derived from elapsed time, so it is fully testable without sleeping. Singleton, hit concurrently by
/// many connections — mirrors <see cref="MessageRateLimiter"/>'s single-lock-guards-all-state idiom.
/// </para>
/// </summary>
public class ReadRateLimiter
{
    private readonly Dictionary<string, UserBucket> _byUser = new Dictionary<string, UserBucket>();

    private readonly object _lock = new object();

    // Last time PruneQuiescentNoLock actually swept the map — time-gated so the hot path never pays for
    // an O(n) sweep on every call (mirrors MessageRateLimiter.PruneQuiescentNoLock).
    private DateTime _lastPruneAt;

    /// <summary>
    /// Attempts to consume one read for <paramref name="battleTag"/> as of <paramref name="now"/>. State
    /// is keyed by LOWERCASED battleTag (mirrors <see cref="MessageRateLimiter"/>) so the budget is
    /// shared across every guarded method and every connection/casing a caller might use. Entries
    /// quiescent past <see cref="ChatLimits.ReadRateLimiterPruneHorizon"/> are pruned opportunistically,
    /// bounding the map to roughly the users active within one prune window.
    /// <para><paramref name="now"/> MUST be a trusted server-side clock read (never client-supplied); the
    /// bucket fails safe on a backwards clock but assumes a monotone one.</para>
    /// </summary>
    public ReadRateLimitDecision TryAcquire(string battleTag, DateTime now)
    {
        var key = battleTag.ToLowerInvariant();

        lock (_lock)
        {
            PruneQuiescentNoLock(now);
            var bucket = GetOrCreateBucket(key, now);
            bucket.LastTouchedAt = now;
            bucket.Tokens.Refill(now);

            if (bucket.Tokens.HasToken)
            {
                bucket.Tokens.Consume();
                return new ReadRateLimitDecision(true, null);
            }

            return new ReadRateLimitDecision(false, bucket.Tokens.SecondsUntilToken());
        }
    }

    // Test seam (assembly has InternalsVisibleTo): number of tracked user entries — asserts the
    // quiescent prune keeps the map bounded. Mirrors MessageRateLimiter.TrackedUserCount.
    internal int TrackedUserCount
    {
        get { lock (_lock) { return _byUser.Count; } }
    }

    // Caller must already hold _lock.
    private UserBucket GetOrCreateBucket(string key, DateTime now)
    {
        if (!_byUser.TryGetValue(key, out var bucket))
        {
            bucket = new UserBucket(now);
            _byUser[key] = bucket;
        }
        return bucket;
    }

    // Caller must already hold _lock. Sweeps at most once per ReadRateLimiterPruneHorizon: an entry idle
    // that long is guaranteed to hold a full bucket again regardless of how empty it was (the pinned
    // invariant on ChatLimits.ReadRateLimiterPruneHorizon), so removing it and letting a later call
    // recreate it fresh is behaviour-preserving. Gated by time (not run on every call) because TryAcquire
    // is the hot path — a full-map sweep on every read would be O(n) per call.
    private void PruneQuiescentNoLock(DateTime now)
    {
        if (now - _lastPruneAt < ChatLimits.ReadRateLimiterPruneHorizon)
        {
            return;
        }
        _lastPruneAt = now;

        List<string> quiescent = null;
        foreach (var kvp in _byUser)
        {
            if (now - kvp.Value.LastTouchedAt >= ChatLimits.ReadRateLimiterPruneHorizon)
            {
                (quiescent ??= new List<string>()).Add(kvp.Key);
            }
        }
        if (quiescent == null)
        {
            return;
        }
        foreach (var key in quiescent)
        {
            _byUser.Remove(key);
        }
    }

    /// <summary>One user's read-bucket state: the token bucket itself plus when it was last touched (the
    /// quiescent-prune anchor). Mutated only under the limiter's lock, so plain mutable fields (mirrors
    /// MessageRateLimiter.UserState).</summary>
    private sealed class UserBucket
    {
        internal readonly TokenBucket Tokens;
        internal DateTime LastTouchedAt;

        internal UserBucket(DateTime now)
        {
            Tokens = new TokenBucket(
                ChatLimits.ReadBurst,
                TimeSpan.FromSeconds(1.0 / ChatLimits.ReadRefillPerSecond),
                now);
            LastTouchedAt = now;
        }
    }

    /// <summary>
    /// A continuous token bucket: starts full (burst available immediately) and regenerates one token
    /// every <c>refillPerToken</c> of elapsed time, capped at capacity. Deterministic — all motion is
    /// driven by the <c>now</c> handed to <see cref="Refill"/>. Deliberately duplicated from
    /// <see cref="MessageRateLimiter"/>'s private nested type of the same name (see this file's class
    /// doc) rather than shared — independently owned so the two limiters can diverge freely.
    /// </summary>
    private sealed class TokenBucket
    {
        private readonly double _capacity;
        private readonly double _refillPerTokenSeconds;
        private double _tokens;
        private DateTime _lastRefill;

        internal TokenBucket(double capacity, TimeSpan refillPerToken, DateTime now)
        {
            _capacity = capacity;
            _refillPerTokenSeconds = refillPerToken.TotalSeconds;
            _tokens = capacity;
            _lastRefill = now;
        }

        internal bool HasToken => _tokens >= 1.0;

        internal void Refill(DateTime now)
        {
            // Guard non-monotonic / same-instant calls: never subtract, never over-count.
            if (now <= _lastRefill)
            {
                return;
            }

            var elapsedSeconds = (now - _lastRefill).TotalSeconds;
            _tokens = Math.Min(_capacity, _tokens + elapsedSeconds / _refillPerTokenSeconds);
            _lastRefill = now;
        }

        internal void Consume() => _tokens -= 1.0;

        /// <summary>Seconds until this bucket next holds a whole token (0 if it already does).</summary>
        internal double SecondsUntilToken()
        {
            if (_tokens >= 1.0)
            {
                return 0;
            }
            return (1.0 - _tokens) * _refillPerTokenSeconds;
        }
    }
}

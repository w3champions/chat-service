using System;
using System.Collections.Generic;
using Serilog;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.FanOut;

/// <summary>
/// The outcome of a single <see cref="MessageRateLimiter.TryAcquire"/> call.
/// <para>
/// A value type (readonly record struct): it is produced on every send and never serialized/stored,
/// so avoiding a heap allocation on the hot path is worthwhile, and value equality keeps the tests
/// terse. The hub (Task 11) maps it onto <see cref="Protocol.SendMessageResult"/> —
/// <see cref="RetryAfterSeconds"/> is a <see cref="double"/> so it drops straight into
/// <c>SendMessageResult.RetryAfterSeconds</c> without conversion.
/// </para>
/// </summary>
/// <param name="Allowed">True when the send may proceed (a token was consumed from BOTH buckets).</param>
/// <param name="RetryAfterSeconds">
/// When throttled, seconds until the caller could send again — strictly positive and bounded by the
/// blocking window. Null when <paramref name="Allowed"/> is true.
/// </param>
/// <param name="JustAutoThrottled">
/// True on the SINGLE decision that transitions the connection into hard auto-throttle — the hub's
/// cue to push exactly one <see cref="Protocol.ChatEvents.ThrottleNotice"/>. False on every other
/// decision, including the denials that follow while the connection stays hard-throttled.
/// </param>
public readonly record struct RateLimitDecision(
    bool Allowed,
    double? RetryAfterSeconds,
    bool JustAutoThrottled);

/// <summary>
/// Pure, deterministic send-path abuse control (spec §13, C3 Task 6). Two classic token buckets per
/// connection:
/// <list type="bullet">
/// <item>a per-(connection, channel) bucket — capacity <see cref="ChatLimits.PerChannelBurst"/>,
/// refilling one token every <see cref="ChatLimits.PerChannelSustainedInterval"/> (5 immediate, then
/// 1/sec); and</item>
/// <item>a per-connection GLOBAL bucket — capacity <see cref="ChatLimits.GlobalMessageBurst"/> over
/// <see cref="ChatLimits.GlobalMessageWindow"/>, enforced across every channel (10 per 5s, i.e. one
/// token every 0.5s).</item>
/// </list>
/// A send consumes one token from BOTH buckets; if either is short, the send is throttled and no
/// token is consumed. Repeated throttles escalate to an escalating hard auto-throttle
/// (<see cref="ChatLimits.AutoThrottleTierDurations"/> 10s→30s→60s cap, ladder decaying after
/// <see cref="ChatLimits.AutoThrottleTierDecay"/>).
/// <para>
/// NO timers and NO wall-clock reads: every decision takes an explicit <c>now</c> and refills are
/// derived from elapsed time, so it is fully testable without sleeping. The limiter is PURE domain
/// logic — it never touches SignalR/<c>IHubContext</c>; it emits its own moderation log line but only
/// SIGNALS the ThrottleNotice via <see cref="RateLimitDecision.JustAutoThrottled"/>.
/// </para>
/// <para>
/// Singleton, hit concurrently by many connections. Concurrency idiom mirrors
/// <see cref="Sessions.SessionRegistry"/> / <see cref="FocusRegistry"/>: a single private lock with
/// ALL state mutation done inside it. The one exception is the moderation <see cref="Log.Warning"/>,
/// deliberately emitted AFTER releasing the lock (no I/O under lock) — exactly-once is guaranteed by
/// the state transition happening once inside the lock, not by where the line is written.
/// </para>
/// </summary>
public class MessageRateLimiter
{
    private readonly Dictionary<string, ConnectionState> _byConnection =
        new Dictionary<string, ConnectionState>();

    private readonly object _lock = new object();

    /// <summary>
    /// Attempts to consume one send for <paramref name="connectionId"/> on <paramref name="channelId"/>
    /// as of <paramref name="now"/>. Allowed only when the per-channel AND global buckets each hold a
    /// token; otherwise throttled with a positive retry-after. A throttle counts as a violation, and
    /// crossing the escalation threshold hard-throttles the whole connection for the auto-throttle
    /// duration (all channels), logging one moderation line and signalling the notice once.
    /// <para><paramref name="now"/> MUST be a trusted server-side clock read (never derived from
    /// client-supplied data); the buckets fail safe on a backwards clock but assume a monotone one.</para>
    /// </summary>
    public RateLimitDecision TryAcquire(string connectionId, string channelId, DateTime now)
    {
        RateLimitDecision decision;
        TimeSpan? applied = null;

        lock (_lock)
        {
            var state = GetOrCreateState(connectionId, now);

            // 1) Hard auto-throttle gate: while serving the penalty, deny EVERYTHING for this
            //    connection (no bucket work, no new violation) with the remaining time as retry-after.
            if (state.HardThrottleUntil is DateTime until)
            {
                if (now < until)
                {
                    return new RateLimitDecision(false, (until - now).TotalSeconds, false);
                }

                // Penalty served — reset and fall through to a fresh evaluation.
                state.HardThrottleUntil = null;
                state.Violations.Clear();
            }

            // 2) Continuous refill from elapsed time, then require a token in BOTH buckets. Idle
            //    per-channel buckets are dropped opportunistically so the map can't grow unbounded.
            var channelBucket = state.GetOrCreateChannelBucket(channelId, now);
            channelBucket.Refill(now);
            state.GlobalBucket.Refill(now);
            state.PruneIdleChannelBuckets(channelId, now);

            if (channelBucket.HasToken && state.GlobalBucket.HasToken)
            {
                channelBucket.Consume();
                state.GlobalBucket.Consume();
                return new RateLimitDecision(true, null, false);
            }

            // 3) Throttled by a bucket. Retry-after = time until the caller could send again, i.e.
            //    until BOTH buckets hold a token → the max of the two per-bucket waits.
            var retryAfter = Math.Max(channelBucket.SecondsUntilToken(), state.GlobalBucket.SecondsUntilToken());

            // 4) Record the violation in the rolling window; escalate if it crosses the threshold.
            applied = RecordViolationAndCheckEscalation(state, now);
            decision = applied is TimeSpan escalated
                ? new RateLimitDecision(false, escalated.TotalSeconds, true)
                : new RateLimitDecision(false, retryAfter, false);
        }

        // Emit the moderation line outside the lock. The transition it reports happened exactly once
        // (inside the lock), so this fires exactly once per hard-throttle episode.
        if (applied is TimeSpan d)
        {
            Log.Warning(
                "Auto-throttling chat connection {ConnectionId} for {DurationSeconds}s after {ViolationThreshold} rate-limit violations within {WindowSeconds}s",
                connectionId,
                d.TotalSeconds,
                ChatLimits.AutoThrottleViolationThreshold,
                ChatLimits.AutoThrottleWindow.TotalSeconds);
        }

        return decision;
    }

    /// <summary>Drops all bucket, violation, and hard-throttle state for a connection (on disconnect).</summary>
    public void RemoveConnection(string connectionId)
    {
        lock (_lock)
        {
            _byConnection.Remove(connectionId);
        }
    }

    // Test seam (assembly has InternalsVisibleTo): number of live per-channel buckets a connection is
    // tracking. Used to assert the idle-bucket purge keeps that map bounded. Mirrors MintRateLimiter.Count.
    internal int TrackedChannelCount(string connectionId)
    {
        lock (_lock)
        {
            return _byConnection.TryGetValue(connectionId, out var state) ? state.ChannelBucketCount : 0;
        }
    }

    // Caller must already hold _lock.
    private ConnectionState GetOrCreateState(string connectionId, DateTime now)
    {
        if (!_byConnection.TryGetValue(connectionId, out var state))
        {
            state = new ConnectionState(now);
            _byConnection[connectionId] = state;
        }
        return state;
    }

    // Caller must already hold _lock. Records a throttle violation at now, ages out ones older than
    // the rolling window, and — when the count crosses the threshold — escalates into the NEXT tier
    // (10s → 30s → 60s cap; the ladder resets first if AutoThrottleTierDecay has passed since the
    // last trigger), returning the applied duration. Null for a plain non-escalating throttle.
    private static TimeSpan? RecordViolationAndCheckEscalation(ConnectionState state, DateTime now)
    {
        state.Violations.Add(now);
        var cutoff = now - ChatLimits.AutoThrottleWindow;
        state.Violations.RemoveAll(v => v < cutoff);

        if (state.Violations.Count < ChatLimits.AutoThrottleViolationThreshold)
        {
            return null;
        }

        // Tier decay: 10 clean minutes since the last trigger reset the ladder to the first tier.
        if (state.LastAutoThrottleAt is DateTime last && now - last >= ChatLimits.AutoThrottleTierDecay)
        {
            state.TierLevel = 0;
        }

        var tierIndex = Math.Min(state.TierLevel, ChatLimits.AutoThrottleTierDurations.Count - 1);
        var duration = ChatLimits.AutoThrottleTierDurations[tierIndex];
        state.HardThrottleUntil = now + duration;
        state.LastAutoThrottleAt = now;
        // Saturate rather than grow unbounded: TierLevel only ever needs to reach the tier count
        // (any further increments would be equivalent to the cap anyway, via the Math.Min above).
        state.TierLevel = Math.Min(state.TierLevel + 1, ChatLimits.AutoThrottleTierDurations.Count);
        state.Violations.Clear();
        return duration;
    }

    /// <summary>
    /// All rate-limiting state for a single connection: its per-channel buckets, the shared global
    /// bucket, the rolling violation timestamps, and an optional hard-throttle deadline. Mutated only
    /// under the limiter's lock, so plain mutable fields (mirrors the in-place-under-lock idiom of the
    /// sibling registries).
    /// </summary>
    private sealed class ConnectionState
    {
        internal readonly TokenBucket GlobalBucket;
        internal readonly Dictionary<string, TokenBucket> ChannelBuckets =
            new Dictionary<string, TokenBucket>();
        internal readonly List<DateTime> Violations = new List<DateTime>();
        internal DateTime? HardThrottleUntil;
        // Follow-up spec §1: completed auto-throttle triggers (the ladder position) + when the last one
        // fired (anchors the 10-minute decay). Mutated only under the limiter's lock, like every sibling.
        internal int TierLevel;
        internal DateTime? LastAutoThrottleAt;

        internal ConnectionState(DateTime now)
        {
            // Global bucket: 10 tokens over 5s ⇒ one token every 0.5s. Derived from the constants so
            // the "per 5s" window and the burst stay in one place.
            GlobalBucket = new TokenBucket(
                ChatLimits.GlobalMessageBurst,
                ChatLimits.GlobalMessageWindow / ChatLimits.GlobalMessageBurst,
                now);
        }

        internal int ChannelBucketCount => ChannelBuckets.Count;

        internal TokenBucket GetOrCreateChannelBucket(string channelId, DateTime now)
        {
            if (!ChannelBuckets.TryGetValue(channelId, out var bucket))
            {
                bucket = new TokenBucket(ChatLimits.PerChannelBurst, ChatLimits.PerChannelSustainedInterval, now);
                ChannelBuckets[channelId] = bucket;
            }
            return bucket;
        }

        /// <summary>
        /// Drops every per-channel bucket (except <paramref name="keepChannelId"/>) that has sat idle
        /// long enough to be guaranteed full: such a bucket is indistinguishable from a fresh one, so
        /// removing it is behaviour-preserving and bounds the map to the channels a connection is
        /// actively sending in. Mirrors <see cref="Sessions.MintRateLimiter"/>'s opportunistic
        /// stale-window purge — without it a connection blasting a new channel per message (which
        /// never trips the per-channel bucket) could grow this map without limit until disconnect.
        /// </summary>
        internal void PruneIdleChannelBuckets(string keepChannelId, DateTime now)
        {
            List<string> idle = null;
            foreach (var kvp in ChannelBuckets)
            {
                if (kvp.Key != keepChannelId && kvp.Value.IsGuaranteedFullAt(now))
                {
                    (idle ??= new List<string>()).Add(kvp.Key);
                }
            }

            if (idle == null)
            {
                return;
            }

            foreach (var channelId in idle)
            {
                ChannelBuckets.Remove(channelId);
            }
        }
    }

    /// <summary>
    /// A continuous token bucket: starts full (burst available immediately) and regenerates one token
    /// every <c>refillPerToken</c> of elapsed time, capped at capacity. Deterministic — all motion is
    /// driven by the <c>now</c> handed to <see cref="Refill"/>.
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

        /// <summary>
        /// True once enough time has elapsed since the last touch that the bucket MUST be at capacity
        /// regardless of how empty it was then (capacity × refill-per-token). A pure time check — no
        /// refill needed — so it is cheap to evaluate for every tracked bucket on each call.
        /// </summary>
        internal bool IsGuaranteedFullAt(DateTime now) =>
            (now - _lastRefill).TotalSeconds >= _capacity * _refillPerTokenSeconds;
    }
}

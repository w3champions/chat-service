using System;
using System.Collections.Generic;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Unit tests for <see cref="MessageRateLimiter"/> — the pure, deterministic send-path abuse control
/// (C3 Task 6; re-keyed to battleTag by the 2026-08-04 follow-up spec §1). Every decision takes an
/// explicit <c>DateTime now</c>; refills are derived from elapsed time, so these tests never sleep and
/// never read the wall clock. Two token buckets (per-(user, channel) burst-then-sustained, and a
/// per-user global window) plus an escalation to a 60s hard auto-throttle after repeated violations.
/// </summary>
public class MessageRateLimiterTests
{
    private MessageRateLimiter _limiter;
    private DateTime _t0;
    private const string User = "peter#123";
    private const string Channel = "channel-a";

    [SetUp]
    public void SetUp()
    {
        _limiter = new MessageRateLimiter();
        _t0 = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);
    }

    [Test]
    public void Burst5_Allowed_SixthWithin1s_Throttled_WithRetryAfter()
    {
        for (var i = 0; i < ChatLimits.PerChannelBurst; i++)
        {
            var allowed = _limiter.TryAcquire(User, Channel, _t0);
            Assert.IsTrue(allowed.Allowed, $"burst message {i + 1} within capacity must be allowed");
            Assert.IsNull(allowed.RetryAfterSeconds, "allowed sends carry no retry-after");
        }

        // 6th message well inside the sustained interval — burst is spent, no full token yet.
        var sixth = _limiter.TryAcquire(User, Channel, _t0.AddMilliseconds(500));

        Assert.IsFalse(sixth.Allowed, "the 6th message inside 1s exceeds the per-channel burst");
        Assert.IsNotNull(sixth.RetryAfterSeconds);
        Assert.Greater(sixth.RetryAfterSeconds.Value, 0);
        Assert.LessOrEqual(sixth.RetryAfterSeconds.Value, ChatLimits.PerChannelSustainedInterval.TotalSeconds);
    }

    [Test]
    public void SustainedRate_1PerSecond_AllowedAfterInterval()
    {
        for (var i = 0; i < ChatLimits.PerChannelBurst; i++)
        {
            _limiter.TryAcquire(User, Channel, _t0);
        }
        // Burst spent; an immediate retry is throttled.
        Assert.IsFalse(_limiter.TryAcquire(User, Channel, _t0).Allowed);

        // One sustained interval later, exactly one token has regenerated.
        var afterOne = _limiter.TryAcquire(User, Channel, _t0 + ChatLimits.PerChannelSustainedInterval);
        Assert.IsTrue(afterOne.Allowed, "one token regenerates per sustained interval");

        // That single token is spent again — another immediate send is throttled.
        Assert.IsFalse(_limiter.TryAcquire(User, Channel, _t0 + ChatLimits.PerChannelSustainedInterval).Allowed);

        // Two intervals in, the next token is available — steady state of 1 per interval.
        var afterTwo = _limiter.TryAcquire(
            User,
            Channel,
            _t0 + ChatLimits.PerChannelSustainedInterval + ChatLimits.PerChannelSustainedInterval);
        Assert.IsTrue(afterTwo.Allowed, "sustained rate holds at 1 per interval");
    }

    [Test]
    public void GlobalBucket_10Per5s_EnforcedAcrossChannels()
    {
        // One message across 10 DISTINCT channels: no per-channel bucket is ever the binding
        // constraint (each has capacity 5), so only the per-user global bucket can throttle.
        for (var i = 0; i < ChatLimits.GlobalMessageBurst; i++)
        {
            var d = _limiter.TryAcquire(User, $"channel-{i}", _t0);
            Assert.IsTrue(d.Allowed, $"global send {i + 1} within the global burst must be allowed");
        }

        // 11th send on yet another fresh channel: its per-channel bucket is full, but the global
        // bucket is exhausted → throttled by the global limit, not the per-channel one.
        var overflow = _limiter.TryAcquire(User, "channel-overflow", _t0);

        Assert.IsFalse(overflow.Allowed, "the global 10/5s cap is enforced across all channels");
        Assert.IsNotNull(overflow.RetryAfterSeconds);
        Assert.Greater(overflow.RetryAfterSeconds.Value, 0);
        Assert.LessOrEqual(overflow.RetryAfterSeconds.Value, ChatLimits.GlobalMessageWindow.TotalSeconds);
    }

    [Test]
    public void RetryAfter_IsPositive_AndBoundedByWindow()
    {
        for (var i = 0; i < ChatLimits.PerChannelBurst; i++)
        {
            _limiter.TryAcquire(User, Channel, _t0);
        }

        var throttled = _limiter.TryAcquire(User, Channel, _t0);

        Assert.IsFalse(throttled.Allowed);
        Assert.IsNotNull(throttled.RetryAfterSeconds);
        Assert.Greater(throttled.RetryAfterSeconds.Value, 0, "retry-after must be strictly positive when throttled");
        Assert.LessOrEqual(
            throttled.RetryAfterSeconds.Value,
            ChatLimits.PerChannelSustainedInterval.TotalSeconds,
            "retry-after for a per-channel throttle must not exceed the sustained interval");
    }

    // Drives one full auto-throttle trigger for User on Channel at `at`: spends the burst, then lands
    // AutoThrottleViolationThreshold violations — returns the escalation decision (JustAutoThrottled true).
    private RateLimitDecision TriggerAutoThrottle(DateTime at)
    {
        for (var i = 0; i < ChatLimits.PerChannelBurst; i++)
        {
            Assert.IsTrue(_limiter.TryAcquire(User, Channel, at).Allowed, "burst send must be allowed");
        }
        RateLimitDecision escalation = default;
        for (var v = 0; v < ChatLimits.AutoThrottleViolationThreshold; v++)
        {
            escalation = _limiter.TryAcquire(User, Channel, at);
            Assert.IsFalse(escalation.Allowed, "every violation-loop send must be denied, including the escalating one");
        }
        Assert.IsTrue(escalation.JustAutoThrottled, "the threshold-th violation must escalate");
        return escalation;
    }

    [Test]
    public void FirstAutoThrottle_Lasts10Seconds()
    {
        var first = TriggerAutoThrottle(_t0);
        Assert.AreEqual(ChatLimits.AutoThrottleTierDurations[0].TotalSeconds, first.RetryAfterSeconds.Value, 0.001);
        Assert.AreEqual(10, first.RetryAfterSeconds.Value, 0.001, "spec pin: the FIRST tier is exactly 10s");

        // Still denied 9s in; recovered right after the 10s tier elapses (fresh burst).
        Assert.IsFalse(_limiter.TryAcquire(User, Channel, _t0.AddSeconds(9)).Allowed);

        // The hard throttle is user-wide, not per-channel: a SECOND, different channel for the
        // same user is denied too while the penalty is active, and it's not a fresh escalation.
        var otherChannelDenial = _limiter.TryAcquire(User, "channel-b", _t0.AddSeconds(9));
        Assert.IsFalse(otherChannelDenial.Allowed, "hard auto-throttle blocks every channel for the user");
        Assert.IsFalse(otherChannelDenial.JustAutoThrottled, "a denial during an active penalty is not a new escalation");

        Assert.IsTrue(_limiter.TryAcquire(User, Channel, _t0 + ChatLimits.AutoThrottleTierDurations[0] + TimeSpan.FromSeconds(11)).Allowed,
            "after serving 10s (plus bucket refill time) the user recovers");
    }

    [Test]
    public void SecondAutoThrottle_Escalates_To30Seconds()
    {
        TriggerAutoThrottle(_t0);
        // Well after the first penalty (buckets refilled), still inside the 10-minute decay window.
        var second = TriggerAutoThrottle(_t0.AddSeconds(60));
        Assert.AreEqual(30, second.RetryAfterSeconds.Value, 0.001, "spec pin: the SECOND tier is exactly 30s");
    }

    [Test]
    public void ThirdAndLaterAutoThrottles_CapAt60Seconds()
    {
        TriggerAutoThrottle(_t0);
        TriggerAutoThrottle(_t0.AddSeconds(60));
        var third = TriggerAutoThrottle(_t0.AddSeconds(150));
        Assert.AreEqual(60, third.RetryAfterSeconds.Value, 0.001, "spec pin: the THIRD tier caps at 60s");
        var fourth = TriggerAutoThrottle(_t0.AddSeconds(300));
        Assert.AreEqual(60, fourth.RetryAfterSeconds.Value, 0.001, "the cap holds for every later trigger");
    }

    [Test]
    public void TierLadder_ResetsAfterTenCleanMinutes()
    {
        TriggerAutoThrottle(_t0);
        TriggerAutoThrottle(_t0.AddSeconds(60)); // now at tier 2 (30s served)

        // 10 clean minutes (no trigger) after the SECOND trigger → the ladder resets to the first tier.
        var afterDecay = _t0.AddSeconds(60) + ChatLimits.AutoThrottleTierDecay + TimeSpan.FromSeconds(1);
        var reset = TriggerAutoThrottle(afterDecay);
        Assert.AreEqual(10, reset.RetryAfterSeconds.Value, 0.001,
            "10 clean minutes without a trigger must reset the ladder to the 10s first tier");
    }

    [Test]
    public void TierLadder_ResetsAtExactlyTheDecayBoundary()
    {
        // Pins the `>=` comparison in RecordViolationAndCheckEscalation's tier-decay check: at EXACTLY
        // AutoThrottleTierDecay since the last trigger, the ladder MUST already have reset (the
        // boundary is inclusive).
        TriggerAutoThrottle(_t0);
        TriggerAutoThrottle(_t0.AddSeconds(60)); // tier 2 (30s served); LastAutoThrottleAt = t0+60s

        var atBoundary = _t0.AddSeconds(60) + ChatLimits.AutoThrottleTierDecay;
        var reset = TriggerAutoThrottle(atBoundary);
        Assert.AreEqual(10, reset.RetryAfterSeconds.Value, 0.001,
            "exactly AutoThrottleTierDecay since the last trigger must reset the ladder");
    }

    [Test]
    public void TierLadder_DoesNotReset_JustUnderTheDecayBoundary()
    {
        // One second short of AutoThrottleTierDecay: the ladder must NOT reset — the trigger
        // continues the ladder from tier 2 (index 2, the 60s cap), not back down to 10s.
        TriggerAutoThrottle(_t0);
        TriggerAutoThrottle(_t0.AddSeconds(60)); // tier 2 (30s served); LastAutoThrottleAt = t0+60s

        var justUnder = _t0.AddSeconds(60) + ChatLimits.AutoThrottleTierDecay - TimeSpan.FromSeconds(1);
        var notReset = TriggerAutoThrottle(justUnder);
        Assert.AreEqual(60, notReset.RetryAfterSeconds.Value, 0.001,
            "just under AutoThrottleTierDecay since the last trigger must NOT reset the ladder");
    }

    [Test]
    public void AutoThrottle_EmitsOneModerationLogLine()
    {
        var capturedWarnings = new List<string>();
        var sink = new DelegatingLogSink(evt =>
        {
            if (evt.Level == Serilog.Events.LogEventLevel.Warning)
            {
                capturedWarnings.Add(evt.RenderMessage());
            }
        });
        var originalLogger = Serilog.Log.Logger;
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            for (var i = 0; i < ChatLimits.PerChannelBurst; i++)
            {
                _limiter.TryAcquire(User, Channel, _t0);
            }
            for (var v = 0; v < ChatLimits.AutoThrottleViolationThreshold; v++)
            {
                _limiter.TryAcquire(User, Channel, _t0);
            }
            // Further denied sends inside the hard-throttle window must NOT emit more log lines.
            _limiter.TryAcquire(User, Channel, _t0.AddSeconds(1));
            _limiter.TryAcquire(User, Channel, _t0.AddSeconds(2));

            Assert.AreEqual(1, capturedWarnings.Count, "auto-throttle must log exactly one moderation line");
            // The line logs the LOWERCASED battleTag key, not the raw arg — User is already all-lowercase,
            // but assert against the normalized form so this stays correct if User ever gains mixed casing.
            StringAssert.Contains(User.ToLowerInvariant(), capturedWarnings[0], "the moderation line must identify the battleTag");
        }
        finally
        {
            Serilog.Log.Logger = originalLogger;
            (Serilog.Log.Logger as IDisposable)?.Dispose();
        }
    }

    [Test]
    public void Buckets_AreIndependent_PerUser()
    {
        // User A exhausts their per-channel burst on a shared channel.
        for (var i = 0; i < ChatLimits.PerChannelBurst; i++)
        {
            Assert.IsTrue(_limiter.TryAcquire("userA#1", Channel, _t0).Allowed);
        }
        Assert.IsFalse(_limiter.TryAcquire("userA#1", Channel, _t0).Allowed, "userA's own burst is spent");

        // User B on the SAME channel is unaffected — buckets are per (user, channel).
        for (var i = 0; i < ChatLimits.PerChannelBurst; i++)
        {
            Assert.IsTrue(
                _limiter.TryAcquire("userB#2", Channel, _t0).Allowed,
                $"userB message {i + 1} must have its own independent burst");
        }
    }

    [Test]
    public void HardThrottle_SurvivesReconnect_BecauseStateIsKeyedByBattleTag()
    {
        TriggerAutoThrottle(_t0);

        // A relaunch/reconnect produces a NEW connectionId but the SAME battleTag — there is no
        // RemoveConnection any more, and TryAcquire keys on the tag, so the penalty holds.
        var afterReconnect = _limiter.TryAcquire(User, Channel, _t0.AddSeconds(2));
        Assert.IsFalse(afterReconnect.Allowed, "the hard throttle must survive reconnect (battleTag-keyed)");
        Assert.IsFalse(afterReconnect.JustAutoThrottled, "no re-signal while serving the penalty");
    }

    [Test]
    public void BattleTagKey_IsCaseInsensitive()
    {
        TriggerAutoThrottle(_t0);
        var differentCasing = _limiter.TryAcquire("PETER#123", Channel, _t0.AddSeconds(2));
        Assert.IsFalse(differentCasing.Allowed, "casing variants of one battleTag share one throttle state");
    }

    [Test]
    public void QuiescentEntries_ArePruned_BoundingMemory()
    {
        // 200 distinct users each send once at t0 — 200 live entries.
        for (var i = 0; i < 200; i++)
        {
            Assert.IsTrue(_limiter.TryAcquire($"user{i}#1", Channel, _t0).Allowed);
        }

        // One send far past the decay/prune horizon sweeps every quiescent entry.
        var later = _t0 + ChatLimits.AutoThrottleTierDecay + TimeSpan.FromSeconds(1);
        Assert.IsTrue(_limiter.TryAcquire(User, Channel, later).Allowed);

        Assert.LessOrEqual(_limiter.TrackedUserCount, 2,
            "entries idle past AutoThrottleTierDecay must be pruned (only the sweeping caller may remain)");
    }

    [Test]
    public void QuiescentPrune_IsSelective_DoesNotEvictARecentlyTouchedEntry()
    {
        // Many quiescent users seeded once at t0 — idle past the decay horizon by the time of the sweep.
        for (var i = 0; i < 50; i++)
        {
            Assert.IsTrue(_limiter.TryAcquire($"stale{i}#1", Channel, _t0).Allowed);
        }

        // One user keeps sending right up to (just under) the decay horizon — this entry must NOT be
        // swept away even though the map-wide sweep timer (anchored on the very first t0 call) fires.
        const string ActiveUser = "active#1";
        var justUnderDecay = _t0 + ChatLimits.AutoThrottleTierDecay - TimeSpan.FromSeconds(1);
        Assert.IsTrue(_limiter.TryAcquire(ActiveUser, Channel, justUnderDecay).Allowed);

        // A distinct caller just past the decay horizon (relative to t0) triggers the time-gated sweep.
        var sweepTime = _t0 + ChatLimits.AutoThrottleTierDecay + TimeSpan.FromSeconds(1);
        Assert.IsTrue(_limiter.TryAcquire(User, Channel, sweepTime).Allowed);

        // The 50 stale users (idle since t0, well past decay) are gone. ActiveUser (touched only 2s
        // before the sweep) and the sweeping caller (User) survive — proving the sweep evaluates each
        // entry's OWN idle time rather than clearing everything whenever the map-wide timer fires.
        Assert.LessOrEqual(_limiter.TrackedUserCount, 2,
            "a recently-touched entry must survive a sweep that evicts other, genuinely-quiescent entries");

        // A count assertion alone can't distinguish "selective pruning" from a buggy "clear everyone
        // once the gate opens" sweep (both would leave <=2 entries here) — directly confirm ActiveUser
        // specifically is still tracked, not merely that the total count is small.
        Assert.AreEqual(1, _limiter.TrackedChannelCount(ActiveUser),
            "ActiveUser's own per-channel bucket state must have survived the sweep, not been evicted");
    }

    [Test]
    public void ViolationsOutsideRollingWindow_DoNotEscalate()
    {
        // One violation per epoch, epochs spaced beyond the auto-throttle window: each old violation
        // ages out before the next lands, so the rolling count never reaches the escalation threshold.
        var spacing = ChatLimits.AutoThrottleWindow + TimeSpan.FromSeconds(1);
        var epoch = _t0;
        for (var e = 0; e < ChatLimits.AutoThrottleViolationThreshold * 2; e++)
        {
            // The bucket has fully refilled across the >window gap → a fresh burst each epoch.
            for (var i = 0; i < ChatLimits.PerChannelBurst; i++)
            {
                Assert.IsTrue(_limiter.TryAcquire(User, Channel, epoch).Allowed);
            }
            var throttled = _limiter.TryAcquire(User, Channel, epoch);
            Assert.IsFalse(throttled.Allowed, "the over-burst send is throttled");
            Assert.IsFalse(
                throttled.JustAutoThrottled,
                "violations spaced beyond the rolling window must never accumulate into an escalation");
            epoch += spacing;
        }
    }

    [Test]
    public void IdleChannelBuckets_ArePurged_BoundingMemory()
    {
        // Send once to a brand-new channel every second, indefinitely — traffic that never trips the
        // per-channel bucket (each new channel starts full) and stays under the global cap. Without a
        // purge the per-channel map would grow one entry per message forever; the idle purge keeps it
        // bounded once each channel sits idle long enough to be guaranteed full again.
        var now = _t0;
        for (var i = 0; i < 1000; i++)
        {
            Assert.IsTrue(_limiter.TryAcquire(User, $"channel-{i}", now).Allowed);
            now = now.AddSeconds(1);
        }

        Assert.LessOrEqual(
            _limiter.TrackedChannelCount(User),
            ChatLimits.PerChannelBurst + 1,
            "idle per-channel buckets must be purged so the map cannot grow unboundedly");
    }

    /// <summary>A Serilog sink that forwards each event to a callback (for asserting log content).</summary>
    private sealed class DelegatingLogSink(Action<Serilog.Events.LogEvent> onEmit) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent) => onEmit(logEvent);
    }
}

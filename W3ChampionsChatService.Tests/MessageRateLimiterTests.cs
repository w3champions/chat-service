using System;
using System.Collections.Generic;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Unit tests for <see cref="MessageRateLimiter"/> — the pure, deterministic send-path abuse control
/// (C3 Task 6). Every decision takes an explicit <c>DateTime now</c>; refills are derived from
/// elapsed time, so these tests never sleep and never read the wall clock. Two token buckets
/// (per-(connection, channel) burst-then-sustained, and a per-connection global window) plus an
/// escalation to a 60s hard auto-throttle after repeated violations.
/// </summary>
public class MessageRateLimiterTests
{
    private MessageRateLimiter _limiter;
    private DateTime _t0;
    private const string Conn = "conn-1";
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
            var allowed = _limiter.TryAcquire(Conn, Channel, _t0);
            Assert.IsTrue(allowed.Allowed, $"burst message {i + 1} within capacity must be allowed");
            Assert.IsNull(allowed.RetryAfterSeconds, "allowed sends carry no retry-after");
        }

        // 6th message well inside the sustained interval — burst is spent, no full token yet.
        var sixth = _limiter.TryAcquire(Conn, Channel, _t0.AddMilliseconds(500));

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
            _limiter.TryAcquire(Conn, Channel, _t0);
        }
        // Burst spent; an immediate retry is throttled.
        Assert.IsFalse(_limiter.TryAcquire(Conn, Channel, _t0).Allowed);

        // One sustained interval later, exactly one token has regenerated.
        var afterOne = _limiter.TryAcquire(Conn, Channel, _t0 + ChatLimits.PerChannelSustainedInterval);
        Assert.IsTrue(afterOne.Allowed, "one token regenerates per sustained interval");

        // That single token is spent again — another immediate send is throttled.
        Assert.IsFalse(_limiter.TryAcquire(Conn, Channel, _t0 + ChatLimits.PerChannelSustainedInterval).Allowed);

        // Two intervals in, the next token is available — steady state of 1 per interval.
        var afterTwo = _limiter.TryAcquire(
            Conn,
            Channel,
            _t0 + ChatLimits.PerChannelSustainedInterval + ChatLimits.PerChannelSustainedInterval);
        Assert.IsTrue(afterTwo.Allowed, "sustained rate holds at 1 per interval");
    }

    [Test]
    public void GlobalBucket_10Per5s_EnforcedAcrossChannels()
    {
        // One message across 10 DISTINCT channels: no per-channel bucket is ever the binding
        // constraint (each has capacity 5), so only the per-connection global bucket can throttle.
        for (var i = 0; i < ChatLimits.GlobalMessageBurst; i++)
        {
            var d = _limiter.TryAcquire(Conn, $"channel-{i}", _t0);
            Assert.IsTrue(d.Allowed, $"global send {i + 1} within the global burst must be allowed");
        }

        // 11th send on yet another fresh channel: its per-channel bucket is full, but the global
        // bucket is exhausted → throttled by the global limit, not the per-channel one.
        var overflow = _limiter.TryAcquire(Conn, "channel-overflow", _t0);

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
            _limiter.TryAcquire(Conn, Channel, _t0);
        }

        var throttled = _limiter.TryAcquire(Conn, Channel, _t0);

        Assert.IsFalse(throttled.Allowed);
        Assert.IsNotNull(throttled.RetryAfterSeconds);
        Assert.Greater(throttled.RetryAfterSeconds.Value, 0, "retry-after must be strictly positive when throttled");
        Assert.LessOrEqual(
            throttled.RetryAfterSeconds.Value,
            ChatLimits.PerChannelSustainedInterval.TotalSeconds,
            "retry-after for a per-channel throttle must not exceed the sustained interval");
    }

    // Drives one full auto-throttle trigger for Conn on Channel at `at`: spends the burst, then lands
    // AutoThrottleViolationThreshold violations — returns the escalation decision (JustAutoThrottled true).
    private RateLimitDecision TriggerAutoThrottle(DateTime at)
    {
        for (var i = 0; i < ChatLimits.PerChannelBurst; i++)
        {
            Assert.IsTrue(_limiter.TryAcquire(Conn, Channel, at).Allowed, "burst send must be allowed");
        }
        RateLimitDecision escalation = default;
        for (var v = 0; v < ChatLimits.AutoThrottleViolationThreshold; v++)
        {
            escalation = _limiter.TryAcquire(Conn, Channel, at);
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
        Assert.IsFalse(_limiter.TryAcquire(Conn, Channel, _t0.AddSeconds(9)).Allowed);
        Assert.IsTrue(_limiter.TryAcquire(Conn, Channel, _t0 + ChatLimits.AutoThrottleTierDurations[0] + TimeSpan.FromSeconds(11)).Allowed,
            "after serving 10s (plus bucket refill time) the connection recovers");
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
                _limiter.TryAcquire(Conn, Channel, _t0);
            }
            for (var v = 0; v < ChatLimits.AutoThrottleViolationThreshold; v++)
            {
                _limiter.TryAcquire(Conn, Channel, _t0);
            }
            // Further denied sends inside the hard-throttle window must NOT emit more log lines.
            _limiter.TryAcquire(Conn, Channel, _t0.AddSeconds(1));
            _limiter.TryAcquire(Conn, Channel, _t0.AddSeconds(2));

            Assert.AreEqual(1, capturedWarnings.Count, "auto-throttle must log exactly one moderation line");
            StringAssert.Contains(Conn, capturedWarnings[0], "the moderation line must identify the connection");
        }
        finally
        {
            Serilog.Log.Logger = originalLogger;
            (Serilog.Log.Logger as IDisposable)?.Dispose();
        }
    }

    [Test]
    public void Buckets_AreIndependent_PerConnection()
    {
        // Connection A exhausts its per-channel burst on a shared channel.
        for (var i = 0; i < ChatLimits.PerChannelBurst; i++)
        {
            Assert.IsTrue(_limiter.TryAcquire("conn-A", Channel, _t0).Allowed);
        }
        Assert.IsFalse(_limiter.TryAcquire("conn-A", Channel, _t0).Allowed, "conn-A's own burst is spent");

        // Connection B on the SAME channel is unaffected — buckets are per (connection, channel).
        for (var i = 0; i < ChatLimits.PerChannelBurst; i++)
        {
            Assert.IsTrue(
                _limiter.TryAcquire("conn-B", Channel, _t0).Allowed,
                $"conn-B message {i + 1} must have its own independent burst");
        }
    }

    [Test]
    public void RemoveConnection_DropsState()
    {
        for (var i = 0; i < ChatLimits.PerChannelBurst; i++)
        {
            _limiter.TryAcquire(Conn, Channel, _t0);
        }
        Assert.IsFalse(_limiter.TryAcquire(Conn, Channel, _t0).Allowed, "burst is spent before removal");

        _limiter.RemoveConnection(Conn);

        // After removal the connection is brand-new: full burst available at the SAME instant.
        var afterRemoval = _limiter.TryAcquire(Conn, Channel, _t0);
        Assert.IsTrue(afterRemoval.Allowed, "RemoveConnection drops all bucket/violation state");
        Assert.IsNull(afterRemoval.RetryAfterSeconds);
    }

    [Test]
    public void RemoveConnection_ClearsActiveHardThrottle()
    {
        for (var i = 0; i < ChatLimits.PerChannelBurst; i++)
        {
            _limiter.TryAcquire(Conn, Channel, _t0);
        }
        RateLimitDecision escalation = default;
        for (var v = 0; v < ChatLimits.AutoThrottleViolationThreshold; v++)
        {
            escalation = _limiter.TryAcquire(Conn, Channel, _t0);
        }
        Assert.IsTrue(escalation.JustAutoThrottled, "precondition: the connection is now hard-throttled");
        Assert.IsFalse(_limiter.TryAcquire(Conn, Channel, _t0.AddSeconds(1)).Allowed, "still inside the penalty");

        _limiter.RemoveConnection(Conn);

        // The same connectionId is a clean slate even mid-penalty (e.g. reconnect reusing the id).
        var afterRemoval = _limiter.TryAcquire(Conn, Channel, _t0.AddSeconds(1));
        Assert.IsTrue(afterRemoval.Allowed, "RemoveConnection clears an active hard-throttle");
        Assert.IsFalse(afterRemoval.JustAutoThrottled);
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
                Assert.IsTrue(_limiter.TryAcquire(Conn, Channel, epoch).Allowed);
            }
            var throttled = _limiter.TryAcquire(Conn, Channel, epoch);
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
            Assert.IsTrue(_limiter.TryAcquire(Conn, $"channel-{i}", now).Allowed);
            now = now.AddSeconds(1);
        }

        Assert.LessOrEqual(
            _limiter.TrackedChannelCount(Conn),
            ChatLimits.PerChannelBurst + 1,
            "idle per-channel buckets must be purged so the map cannot grow unboundedly");
    }

    /// <summary>A Serilog sink that forwards each event to a callback (for asserting log content).</summary>
    private sealed class DelegatingLogSink(Action<Serilog.Events.LogEvent> onEmit) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent) => onEmit(logEvent);
    }
}

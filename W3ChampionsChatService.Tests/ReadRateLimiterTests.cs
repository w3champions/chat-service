using System;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Unit tests for <see cref="ReadRateLimiter"/> — the pure, deterministic abuse guard for read-shaped
/// hub methods (2026-08-05 PR36 feedback, Part 3). Every decision takes an explicit <c>DateTime now</c>;
/// refills are derived from elapsed time, so these tests never sleep and never read the wall clock.
/// Deliberately simpler than <see cref="MessageRateLimiter"/>: ONE token bucket per battleTag, no
/// per-channel dimension, no violation ladder — mirrors <see cref="MessageRateLimiterTests"/>'s
/// clock-injection and prune-invariant patterns without the escalation-ladder tests that don't apply here.
/// </summary>
public class ReadRateLimiterTests
{
    private ReadRateLimiter _limiter;
    private DateTime _t0;
    private const string User = "peter#123";

    [SetUp]
    public void SetUp()
    {
        _limiter = new ReadRateLimiter();
        _t0 = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);
    }

    // Drains User's bucket back to zero tokens AS OF `at` (refilling first, same as a real TryAcquire
    // would) — used by the F3 logging tests below to force a genuine denial at an arbitrary later time,
    // since the bucket's full-refill time (12s) is far shorter than ReadRateLimiterDenyLogInterval (60s)
    // and would otherwise have long since refilled by the time a suppression probe runs.
    private void ExhaustBurstAt(DateTime at)
    {
        for (var i = 0; i < ChatLimits.ReadBurst; i++)
        {
            _limiter.TryAcquire(User, at);
        }
    }

    [Test]
    public void Burst_AllowsReadBurstImmediateCalls_NextIsDenied_WithRetryAfter()
    {
        for (var i = 0; i < ChatLimits.ReadBurst; i++)
        {
            var allowed = _limiter.TryAcquire(User, _t0);
            Assert.IsTrue(allowed.Allowed, $"burst read {i + 1} within capacity must be allowed");
            Assert.IsNull(allowed.RetryAfterSeconds, "allowed reads carry no retry-after");
        }

        var overflow = _limiter.TryAcquire(User, _t0);

        Assert.IsFalse(overflow.Allowed, $"the read past ReadBurst ({ChatLimits.ReadBurst}) must be denied");
        Assert.IsNotNull(overflow.RetryAfterSeconds);
        Assert.Greater(overflow.RetryAfterSeconds.Value, 0, "retry-after must be strictly positive when throttled");
    }

    [Test]
    public void Refill_AfterEnoughElapsedTime_ReadsAreAllowedAgain()
    {
        for (var i = 0; i < ChatLimits.ReadBurst; i++)
        {
            _limiter.TryAcquire(User, _t0);
        }
        // Burst spent; an immediate retry is throttled.
        Assert.IsFalse(_limiter.TryAcquire(User, _t0).Allowed);

        // One sustained interval later (1 / ReadRefillPerSecond), exactly one token has regenerated.
        var interval = TimeSpan.FromSeconds(1.0 / ChatLimits.ReadRefillPerSecond);
        var afterOne = _limiter.TryAcquire(User, _t0 + interval);
        Assert.IsTrue(afterOne.Allowed, "one token regenerates per 1/ReadRefillPerSecond interval");

        // That single token is spent again — another immediate read is throttled.
        Assert.IsFalse(_limiter.TryAcquire(User, _t0 + interval).Allowed);

        // A full burst worth of elapsed time fully refills the bucket back to capacity.
        var fullyRefilled = _t0 + TimeSpan.FromSeconds((double)ChatLimits.ReadBurst / ChatLimits.ReadRefillPerSecond) + interval;
        for (var i = 0; i < ChatLimits.ReadBurst; i++)
        {
            Assert.IsTrue(_limiter.TryAcquire(User, fullyRefilled).Allowed,
                $"read {i + 1} after a full refill window must be allowed — the bucket is back at capacity");
        }
    }

    [Test]
    public void RetryAfter_IsPositive_AndBoundedByFullRefillWindow()
    {
        for (var i = 0; i < ChatLimits.ReadBurst; i++)
        {
            _limiter.TryAcquire(User, _t0);
        }

        var throttled = _limiter.TryAcquire(User, _t0);

        Assert.IsFalse(throttled.Allowed);
        Assert.IsNotNull(throttled.RetryAfterSeconds);
        Assert.Greater(throttled.RetryAfterSeconds.Value, 0, "retry-after must be strictly positive when throttled");
        Assert.LessOrEqual(
            throttled.RetryAfterSeconds.Value,
            1.0 / ChatLimits.ReadRefillPerSecond,
            "retry-after for an empty bucket must not exceed one refill interval (a single missing token)");
    }

    [Test]
    public void Buckets_AreIndependent_PerBattleTag()
    {
        // User A exhausts their own burst.
        for (var i = 0; i < ChatLimits.ReadBurst; i++)
        {
            Assert.IsTrue(_limiter.TryAcquire("userA#1", _t0).Allowed);
        }
        Assert.IsFalse(_limiter.TryAcquire("userA#1", _t0).Allowed, "userA's own burst is spent");

        // User B is completely unaffected — independent per-battleTag buckets.
        for (var i = 0; i < ChatLimits.ReadBurst; i++)
        {
            Assert.IsTrue(
                _limiter.TryAcquire("userB#2", _t0).Allowed,
                $"userB read {i + 1} must have its own independent burst");
        }
    }

    [Test]
    public void BattleTagKey_IsCaseInsensitive()
    {
        for (var i = 0; i < ChatLimits.ReadBurst; i++)
        {
            Assert.IsTrue(_limiter.TryAcquire(User, _t0).Allowed);
        }
        var differentCasing = _limiter.TryAcquire("PETER#123", _t0);
        Assert.IsFalse(differentCasing.Allowed, "casing variants of one battleTag share one bucket");
    }

    [Test]
    public void SharedBudget_AcrossGuardedMethods_OneBattleTagBucket()
    {
        // Deliverable 4: "all acquisitions share one bucket per battleTag" — simulated here by simply
        // calling TryAcquire repeatedly for the same battleTag (the hub wires the SAME limiter instance
        // into both GetConversations and GetMessages, so from the limiter's own perspective there is no
        // way to distinguish "method A's call" from "method B's call" — it is the same call shape either
        // way). Spending the WHOLE burst must deny the very next call regardless of which "method" it
        // represents.
        for (var i = 0; i < ChatLimits.ReadBurst; i++)
        {
            Assert.IsTrue(_limiter.TryAcquire(User, _t0).Allowed, $"call {i + 1} within the shared budget must be allowed");
        }
        Assert.IsFalse(_limiter.TryAcquire(User, _t0).Allowed, "the shared per-battleTag budget is exhausted regardless of which guarded method spent it");
    }

    [Test]
    public void Deny_LogsAtMostOncePerDenyLogInterval_PerUser()
    {
        // Fix round 1, finding F3: denials were previously invisible to operators. There is no
        // log-capture harness in this test suite (see FanOut/ReadRateLimiter.cs's class doc for why —
        // the log call is deliberately outside the lock), so this pins the once-per-
        // ChatLimits.ReadRateLimiterDenyLogInterval logging decision directly via the internal test seam
        // (LastDenyLoggedAtFor) rather than captured log output.
        ExhaustBurstAt(_t0);
        Assert.IsNull(_limiter.LastDenyLoggedAtFor(User), "no denial has happened yet — nothing logged");

        // First denial at t0: stamps LastDenyLoggedAt.
        var firstDenial = _limiter.TryAcquire(User, _t0);
        Assert.IsFalse(firstDenial.Allowed, "the bucket is already fully drained by ExhaustBurstAt");
        Assert.AreEqual(_t0, _limiter.LastDenyLoggedAtFor(User), "the first denial stamps the log time");

        // A denial well within ReadRateLimiterDenyLogInterval must NOT refresh the stamp (suppressed).
        // Re-exhaust first: by 30s later the bucket has long since fully refilled (full-refill time is
        // only 12s), so the probe call needs a fresh drain to be a genuine denial rather than a hit.
        var withinWindow = _t0 + TimeSpan.FromSeconds(30);
        ExhaustBurstAt(withinWindow);
        var secondDenial = _limiter.TryAcquire(User, withinWindow);
        Assert.IsFalse(secondDenial.Allowed);
        Assert.AreEqual(_t0, _limiter.LastDenyLoggedAtFor(User),
            "a denial within the log interval must not refresh the stamp — logging stays suppressed");

        // A denial once the interval has fully elapsed refreshes the stamp.
        var afterWindow = _t0 + ChatLimits.ReadRateLimiterDenyLogInterval + TimeSpan.FromSeconds(1);
        ExhaustBurstAt(afterWindow);
        var thirdDenial = _limiter.TryAcquire(User, afterWindow);
        Assert.IsFalse(thirdDenial.Allowed);
        Assert.AreEqual(afterWindow, _limiter.LastDenyLoggedAtFor(User),
            "a denial once the log interval has elapsed must refresh the stamp — logging resumes");
    }

    [Test]
    public void Allow_NeverStampsLastDenyLoggedAt()
    {
        // The stamp must only ever move on a DENIAL, never on an allowed read.
        Assert.IsTrue(_limiter.TryAcquire(User, _t0).Allowed);
        Assert.IsNull(_limiter.LastDenyLoggedAtFor(User), "an allowed read must never stamp LastDenyLoggedAt");
    }

    [Test]
    public void QuiescentEntries_ArePruned_BoundingMemory()
    {
        // 200 distinct users each read once at t0 — 200 live entries.
        for (var i = 0; i < 200; i++)
        {
            Assert.IsTrue(_limiter.TryAcquire($"user{i}#1", _t0).Allowed);
        }

        // One read far past the prune horizon sweeps every quiescent entry.
        var later = _t0 + ChatLimits.ReadRateLimiterPruneHorizon + TimeSpan.FromSeconds(1);
        Assert.IsTrue(_limiter.TryAcquire(User, later).Allowed);

        // The sweep (PruneQuiescentNoLock) runs BEFORE the sweeping call's own entry is created, so all
        // 200 prior entries — idle since _t0, past the prune horizon — are evicted and exactly ONE entry
        // (User, just created) remains.
        Assert.AreEqual(1, _limiter.TrackedUserCount,
            "entries idle past ReadRateLimiterPruneHorizon must be pruned (only the sweeping caller remains)");
    }

    [Test]
    public void QuiescentPrune_IsSelective_DoesNotEvictARecentlyTouchedEntry()
    {
        // Many quiescent users seeded once at t0 — idle past the prune horizon by the time of the sweep.
        for (var i = 0; i < 50; i++)
        {
            Assert.IsTrue(_limiter.TryAcquire($"stale{i}#1", _t0).Allowed);
        }

        // One user keeps reading right up to (just under) the prune horizon — this entry must NOT be
        // swept away even though the map-wide sweep timer (anchored on the very first t0 call) fires.
        const string ActiveUser = "active#1";
        var justUnderHorizon = _t0 + ChatLimits.ReadRateLimiterPruneHorizon - TimeSpan.FromSeconds(1);
        Assert.IsTrue(_limiter.TryAcquire(ActiveUser, justUnderHorizon).Allowed);

        // A distinct caller just past the prune horizon (relative to t0) triggers the time-gated sweep.
        var sweepTime = _t0 + ChatLimits.ReadRateLimiterPruneHorizon + TimeSpan.FromSeconds(1);
        Assert.IsTrue(_limiter.TryAcquire(User, sweepTime).Allowed);

        // The 50 stale users (idle since t0, well past the horizon) are gone. ActiveUser (touched only 2s
        // before the sweep) and the sweeping caller (User) survive. Unlike MessageRateLimiterTests' analog
        // (which needs a separate TrackedChannelCount probe to rule out a "clear everyone" bug landing on
        // the same bound), an EXACT count of 2 here already distinguishes all three failure modes given
        // this scenario's fixed shape (52 total, 1 active, 1 sweeper): "no prune" would leave 52, a
        // "clear-everyone" bug would leave 1 (only the sweeping caller), and correct selective pruning
        // leaves exactly 2 — there is no per-key auxiliary state left for a 4th, more specific probe.
        Assert.AreEqual(2, _limiter.TrackedUserCount,
            "a recently-touched entry must survive a sweep that evicts other, genuinely-quiescent entries");
    }
}

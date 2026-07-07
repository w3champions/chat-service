using System;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Sessions;

namespace W3ChampionsChatService.Tests;

public class MintRateLimiterTests
{
    [Test]
    public void TryAcquire_AllowsExactlyLimit_ThenDenies()
    {
        var limiter = new MintRateLimiter();
        var now = DateTime.UtcNow;
        var key = "bt:peter#123";

        for (var i = 0; i < 10; i++)
        {
            Assert.IsTrue(limiter.TryAcquire(key, 10, now), $"call {i + 1} should be allowed");
        }

        Assert.IsFalse(limiter.TryAcquire(key, 10, now));
    }

    [Test]
    public void TryAcquire_NewWindow_ResetsTheCounter()
    {
        var limiter = new MintRateLimiter();
        var t0 = DateTime.UtcNow;
        var key = "bt:peter#123";

        for (var i = 0; i < 10; i++)
        {
            limiter.TryAcquire(key, 10, t0);
        }
        Assert.IsFalse(limiter.TryAcquire(key, 10, t0));

        var result = limiter.TryAcquire(key, 10, t0 + ChatLimits.TicketMintWindow + TimeSpan.FromSeconds(1));

        Assert.IsTrue(result);
    }

    [Test]
    public void TryAcquire_IndependentKeys_DoNotInterfere()
    {
        var limiter = new MintRateLimiter();
        var now = DateTime.UtcNow;

        for (var i = 0; i < 10; i++)
        {
            limiter.TryAcquire("bt:a#1", 10, now);
        }
        Assert.IsFalse(limiter.TryAcquire("bt:a#1", 10, now));
        Assert.IsTrue(limiter.TryAcquire("bt:b#2", 10, now));

        // Prefix discipline: same suffix, different prefix must be distinct keys.
        for (var i = 0; i < 30; i++)
        {
            limiter.TryAcquire("ip:1.2.3.4", 30, now);
        }
        Assert.IsFalse(limiter.TryAcquire("ip:1.2.3.4", 30, now));
        Assert.IsTrue(limiter.TryAcquire("bt:1.2.3.4", 10, now));
    }

    [Test]
    public void TryAcquire_PurgesStaleWindows()
    {
        var limiter = new MintRateLimiter();
        var t0 = DateTime.UtcNow;

        limiter.TryAcquire("ip:1.2.3.4", 30, t0);
        limiter.TryAcquire("ip:5.6.7.8", 30, t0);
        Assert.AreEqual(2, limiter.Count);

        // Advance past the window and acquire a new key -> stale entries purged.
        limiter.TryAcquire("ip:9.9.9.9", 30, t0 + ChatLimits.TicketMintWindow + TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, limiter.Count);
    }

    // ── F1 per-IP mint shield: IsAtLimit (pure read) + Record (charge) ─────────────────────────────
    //
    // The per-IP budget now counts ONLY REJECTED mint attempts (charged via Record) and is queried
    // pre-validation via IsAtLimit — a pure read that must never mutate. TryAcquire stays the
    // per-battleTag cap's mechanism. These pin the split so they can't regress into each other.

    [Test]
    public void IsAtLimit_IsPureRead_NeverCreatesOrMutatesWindows()
    {
        var limiter = new MintRateLimiter();
        var now = DateTime.UtcNow;
        var key = "ip:1.2.3.4";

        // Reading a key that was never recorded must NOT create a window as a side effect.
        Assert.IsFalse(limiter.IsAtLimit(key, 30, now));
        Assert.AreEqual(0, limiter.Count, "IsAtLimit must not create a window");

        // Charge the key to exactly the limit, then read it repeatedly.
        for (var i = 0; i < 30; i++)
        {
            limiter.Record(key, now);
        }
        Assert.AreEqual(1, limiter.Count);

        for (var i = 0; i < 5; i++)
        {
            Assert.IsTrue(limiter.IsAtLimit(key, 30, now), "an at-limit key reads as at-limit");
        }
        Assert.AreEqual(1, limiter.Count, "repeated IsAtLimit reads must not add or roll windows");
    }

    [Test]
    public void Record_ChargesUpToLimit_ThenIsAtLimitTrips()
    {
        var limiter = new MintRateLimiter();
        var now = DateTime.UtcNow;
        var key = "ip:1.2.3.4";

        for (var i = 0; i < 30; i++)
        {
            Assert.IsFalse(limiter.IsAtLimit(key, 30, now), $"before record {i + 1}, count {i} is below the limit");
            limiter.Record(key, now);
        }

        Assert.IsTrue(limiter.IsAtLimit(key, 30, now), "after 30 records the per-IP budget is at limit");
    }

    [Test]
    public void Record_NewWindow_RollsTheCounterOver()
    {
        var limiter = new MintRateLimiter();
        var t0 = DateTime.UtcNow;
        var key = "ip:1.2.3.4";

        for (var i = 0; i < 30; i++)
        {
            limiter.Record(key, t0);
        }
        Assert.IsTrue(limiter.IsAtLimit(key, 30, t0));

        // A record in a fresh window rolls the counter back to 1 → no longer at limit.
        var t1 = t0 + ChatLimits.TicketMintWindow + TimeSpan.FromSeconds(1);
        limiter.Record(key, t1);
        Assert.IsFalse(limiter.IsAtLimit(key, 30, t1), "a record in a new window resets the counter to 1");
    }

    [Test]
    public void IsAtLimit_StaleWindow_ReadsAsNotAtLimit_WithoutPurging()
    {
        var limiter = new MintRateLimiter();
        var t0 = DateTime.UtcNow;
        var key = "ip:1.2.3.4";

        for (var i = 0; i < 30; i++)
        {
            limiter.Record(key, t0);
        }
        Assert.AreEqual(1, limiter.Count);

        // Past the window: IsAtLimit treats the stale window as absent (false) but, being a PURE read,
        // must NOT physically purge it — only a mutating call (Record/TryAcquire) purges stale windows.
        var t1 = t0 + ChatLimits.TicketMintWindow + TimeSpan.FromSeconds(1);
        Assert.IsFalse(limiter.IsAtLimit(key, 30, t1), "a stale window must read as not-at-limit");
        Assert.AreEqual(1, limiter.Count, "IsAtLimit is a pure read — it must not purge the stale window");
    }

    [Test]
    public void Record_PurgesStaleWindows_LikeTryAcquire()
    {
        var limiter = new MintRateLimiter();
        var t0 = DateTime.UtcNow;

        limiter.Record("ip:1.2.3.4", t0);
        limiter.Record("ip:5.6.7.8", t0);
        Assert.AreEqual(2, limiter.Count);

        // A record in a new window past the old one purges the stale keys opportunistically.
        limiter.Record("ip:9.9.9.9", t0 + ChatLimits.TicketMintWindow + TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, limiter.Count, "Record must purge stale windows so per-IP keys can't grow unbounded");
    }
}

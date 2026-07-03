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
}

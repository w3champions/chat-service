using System;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Unit coverage for the fixed-window per-battleTag limiter backing JoinChannel's implicit-creation
/// throttle (C3 Task 10). Mirrors MintRateLimiterTests.cs's shape — same fixed-window idiom — plus a
/// case specific to this class: a denial carries a positive retry-after (JoinChannel's Throttled
/// result surfaces it directly).
/// </summary>
public class ChannelCreationRateLimiterTests
{
    [Test]
    public void TryAcquire_AllowsExactlyLimit_ThenDenies()
    {
        var limiter = new ChannelCreationRateLimiter();
        var now = DateTime.UtcNow;

        for (var i = 0; i < ChatLimits.ChannelCreationPerHour; i++)
        {
            Assert.IsTrue(limiter.TryAcquire("peter#123", now).Allowed, $"call {i + 1} should be allowed");
        }

        Assert.IsFalse(limiter.TryAcquire("peter#123", now).Allowed);
    }

    [Test]
    public void TryAcquire_Denied_ReturnsPositiveRetryAfterSeconds()
    {
        var limiter = new ChannelCreationRateLimiter();
        var t0 = DateTime.UtcNow;

        for (var i = 0; i < ChatLimits.ChannelCreationPerHour; i++)
        {
            limiter.TryAcquire("peter#123", t0);
        }

        var denied = limiter.TryAcquire("peter#123", t0 + TimeSpan.FromMinutes(10));

        Assert.IsFalse(denied.Allowed);
        Assert.IsNotNull(denied.RetryAfterSeconds);
        Assert.That(denied.RetryAfterSeconds, Is.GreaterThan(0));
        Assert.That(denied.RetryAfterSeconds, Is.LessThanOrEqualTo(ChatLimits.ChannelCreationWindow.TotalSeconds));
    }

    [Test]
    public void TryAcquire_Allowed_HasNullRetryAfterSeconds()
    {
        var limiter = new ChannelCreationRateLimiter();

        var decision = limiter.TryAcquire("peter#123", DateTime.UtcNow);

        Assert.IsTrue(decision.Allowed);
        Assert.IsNull(decision.RetryAfterSeconds);
    }

    [Test]
    public void TryAcquire_NewWindow_ResetsTheCounter()
    {
        var limiter = new ChannelCreationRateLimiter();
        var t0 = DateTime.UtcNow;

        for (var i = 0; i < ChatLimits.ChannelCreationPerHour; i++)
        {
            limiter.TryAcquire("peter#123", t0);
        }
        Assert.IsFalse(limiter.TryAcquire("peter#123", t0).Allowed);

        var result = limiter.TryAcquire("peter#123", t0 + ChatLimits.ChannelCreationWindow + TimeSpan.FromSeconds(1));

        Assert.IsTrue(result.Allowed);
    }

    [Test]
    public void TryAcquire_IndependentBattleTags_DoNotInterfere()
    {
        var limiter = new ChannelCreationRateLimiter();
        var now = DateTime.UtcNow;

        for (var i = 0; i < ChatLimits.ChannelCreationPerHour; i++)
        {
            limiter.TryAcquire("peter#123", now);
        }
        Assert.IsFalse(limiter.TryAcquire("peter#123", now).Allowed);
        Assert.IsTrue(limiter.TryAcquire("alice#456", now).Allowed);
    }
}

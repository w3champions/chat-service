using System;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Smoke test for the <c>Microsoft.Extensions.TimeProvider.Testing</c> package pulled in for C3's
/// injectable-clock foundation. Every timer-driven fan-out task (13, 14, 15) will construct its
/// service under test with a <see cref="FakeTimeProvider"/> instead of the production
/// <see cref="TimeProvider.System"/> singleton, then call <see cref="FakeTimeProvider.Advance"/> to
/// deterministically move time forward without real delays. This test only proves the package
/// itself behaves as expected — no production code under test.
/// </summary>
public class TimeProviderTests
{
    [Test]
    public void FakeTimeProvider_Advance_MovesGetUtcNow()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var fakeTimeProvider = new FakeTimeProvider(start);

        Assert.AreEqual(start, fakeTimeProvider.GetUtcNow(),
            "FakeTimeProvider must start at the seeded time");

        fakeTimeProvider.Advance(TimeSpan.FromSeconds(90));

        Assert.AreEqual(start + TimeSpan.FromSeconds(90), fakeTimeProvider.GetUtcNow(),
            "Advance must deterministically move GetUtcNow forward by exactly the given span");
    }
}

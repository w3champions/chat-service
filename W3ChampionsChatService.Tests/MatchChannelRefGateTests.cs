using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using W3ChampionsChatService.Internal;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Unit tests for <see cref="MatchChannelRefGate"/> — the per-<c>systemRef</c> keyed async mutex that
/// plan D5 requires every mutating match-channel path to hold for its whole operation (2026-08-05
/// reconciliation spec, Task 2). Plain in-memory logic, deliberately **not** an
/// <c>IntegrationTestBase</c> suite (no Mongo, no channel/membership state) — mirrors
/// <see cref="ReadRateLimiterTests"/>'s shape: no <c>Thread.Sleep</c>, every interleaving proven
/// deterministically via <see cref="TaskCompletionSource"/> and awaited tasks with generous timeouts.
/// </summary>
public class MatchChannelRefGateTests
{
    // Long enough that a correctly-blocked task never accidentally completes within it, short enough
    // that a genuinely wedged gate fails the test promptly instead of hanging the run.
    private static readonly TimeSpan ShortWait = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private MatchChannelRefGate _gate;

    [SetUp]
    public void SetUp()
    {
        _gate = new MatchChannelRefGate();
    }

    [Test]
    public async Task SameRef_SecondAcquire_BlocksUntilFirstReleases()
    {
        var first = await _gate.AcquireAsync("a");

        var secondTask = _gate.AcquireAsync("a");

        // The second acquire for the SAME ref must not complete while the first holder is still live —
        // proven deterministically by racing it against a short delay rather than sleeping and hoping.
        var completedFirst = await Task.WhenAny(secondTask, Task.Delay(ShortWait));
        Assert.AreNotSame(secondTask, completedFirst, "the second acquire must still be blocked");
        Assert.IsFalse(secondTask.IsCompleted, "the second acquire must not have completed yet");

        first.Dispose();

        var second = await secondTask.WaitAsync(Timeout);
        Assert.IsNotNull(second, "the second acquire must complete once the first holder releases");
        second.Dispose();
    }

    [Test]
    public async Task DifferentRefs_DoNotBlockEachOther()
    {
        using var first = await _gate.AcquireAsync("a");

        var secondTask = _gate.AcquireAsync("b");
        var second = await secondTask.WaitAsync(Timeout);

        Assert.IsTrue(secondTask.IsCompletedSuccessfully, "a distinct ref must never be blocked by another ref's holder");
        second.Dispose();
    }

    [Test]
    public async Task Entries_AreEvictedWhenAllHoldersRelease()
    {
        using (await _gate.AcquireAsync("a"))
        {
        }
        using (await _gate.AcquireAsync("b"))
        {
        }

        // A contended ref too: two sequential holders on the same key still end up fully released.
        var first = await _gate.AcquireAsync("c");
        var secondTask = _gate.AcquireAsync("c");
        first.Dispose();
        var second = await secondTask.WaitAsync(Timeout);
        second.Dispose();

        Assert.AreEqual(0, _gate.TrackedRefCount, "every entry must be evicted once its holders all release");
    }

    [Test]
    public async Task ContendedEntry_IsNotEvictedWhileAWaiterIsPending()
    {
        var first = await _gate.AcquireAsync("a");
        var secondTask = _gate.AcquireAsync("a");

        // Give the second acquire a chance to register as a waiter before asserting the entry survives.
        await Task.WhenAny(secondTask, Task.Delay(ShortWait));

        Assert.AreEqual(1, _gate.TrackedRefCount, "an entry with a queued waiter must not be evicted early");

        first.Dispose();
        var second = await secondTask.WaitAsync(Timeout);
        second.Dispose();
    }

    [Test]
    public async Task Release_IsIdempotent()
    {
        var first = await _gate.AcquireAsync("a");
        first.Dispose();
        first.Dispose(); // A second Dispose must be a no-op, not a spurious extra Release.

        var second = await _gate.AcquireAsync("a").WaitAsync(Timeout);

        var thirdTask = _gate.AcquireAsync("a");
        var completedThird = await Task.WhenAny(thirdTask, Task.Delay(ShortWait));

        Assert.AreNotSame(thirdTask, completedThird, "the gate must still be exclusive after a double-Dispose");
        Assert.IsFalse(thirdTask.IsCompleted, "a double-released holder must not have let two holders in");

        second.Dispose();
        var third = await thirdTask.WaitAsync(Timeout);
        third.Dispose();
    }

    [Test]
    public async Task BodyThrows_StillReleases()
    {
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var _ = await _gate.AcquireAsync("a");
            throw new InvalidOperationException("boom");
        });

        var reacquired = await _gate.AcquireAsync("a").WaitAsync(Timeout);
        Assert.IsNotNull(reacquired, "a throwing body must still release the gate via using/Dispose");
        reacquired.Dispose();
    }

    [Test]
    public async Task ManyConcurrentAcquires_SameRef_NeverOverlap()
    {
        const int concurrency = 50;
        var sharedCounter = 0;
        var overlapDetected = false;
        var tasks = new List<Task>(concurrency);

        for (var i = 0; i < concurrency; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                using var _ = await _gate.AcquireAsync("shared");
                var value = System.Threading.Interlocked.Increment(ref sharedCounter);
                if (value != 1)
                {
                    overlapDetected = true;
                }
                await Task.Yield();
                System.Threading.Interlocked.Decrement(ref sharedCounter);
            }));
        }

        await Task.WhenAll(tasks).WaitAsync(Timeout);

        Assert.IsFalse(overlapDetected, "no two concurrent acquires of the SAME ref may ever overlap");
        Assert.AreEqual(0, _gate.TrackedRefCount, "every entry must be evicted once all 50 holders release");
    }
}

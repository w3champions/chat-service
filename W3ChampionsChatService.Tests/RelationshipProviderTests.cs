using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Relationships;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C5 (Task 1, D1): the relationship provider's cache + three-tier fail-closed policy and the snapshot's
/// case-insensitive predicates. Pure unit tests — a <see cref="FakeRelationshipSource"/> stands in for wb
/// (never any HTTP) and a <see cref="FakeTimeProvider"/> drives the TTL clock. NUnit constraint style.
/// </summary>
[TestFixture]
public class RelationshipProviderTests
{
    private const string Tag = "peter#123";
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    private static RelationshipSnapshot Snapshot(
        string battleTag, DateTime now, IEnumerable<string> friends = null, IEnumerable<string> blocked = null) =>
        new RelationshipSnapshot(
            battleTag,
            new HashSet<string>(friends ?? Enumerable.Empty<string>()),
            new HashSet<string>(blocked ?? Enumerable.Empty<string>()),
            now);

    [Test]
    public async Task GetSnapshot_CachedWithinTtl_DoesNotRefetch()
    {
        var time = new FakeTimeProvider(FixedNow);
        var source = new FakeRelationshipSource();
        var provider = new RelationshipProvider(source, time);

        await provider.GetSnapshotAsync(Tag);
        time.Advance(ChatLimits.RelationshipCacheTtl - TimeSpan.FromSeconds(1)); // still fresh
        await provider.GetSnapshotAsync(Tag);

        Assert.That(source.FetchCount, Is.EqualTo(1),
            "a snapshot still within its TTL must be served from cache without refetching");
    }

    [Test]
    public async Task GetSnapshot_AfterTtl_Refetches()
    {
        var time = new FakeTimeProvider(FixedNow);
        var source = new FakeRelationshipSource();
        var provider = new RelationshipProvider(source, time);

        await provider.GetSnapshotAsync(Tag);
        time.Advance(ChatLimits.RelationshipCacheTtl + TimeSpan.FromSeconds(1)); // now stale
        await provider.GetSnapshotAsync(Tag);

        Assert.That(source.FetchCount, Is.EqualTo(2), "a snapshot past its TTL must be refetched");
    }

    [Test]
    public async Task GetSnapshot_FetchFails_WithStaleCache_ReturnsStaleSnapshot()
    {
        var time = new FakeTimeProvider(FixedNow);
        var fetchedAt = FixedNow.UtcDateTime;
        var source = new FakeRelationshipSource((tag, now) => Snapshot(tag, now, friends: new[] { "ally#1" }));
        var provider = new RelationshipProvider(source, time);

        await provider.GetSnapshotAsync(Tag);                                  // caches the T0 snapshot
        time.Advance(ChatLimits.RelationshipCacheTtl + TimeSpan.FromMinutes(1)); // stale
        source.ShouldThrow = true;

        var stale = await provider.GetSnapshotAsync(Tag);

        Assert.That(stale.FetchedAt, Is.EqualTo(fetchedAt),
            "on fetch failure the last-known snapshot must be returned (last-known fallback, spec §14)");
        Assert.That(stale.IsFresh(time.GetUtcNow().UtcDateTime), Is.False,
            "the fallback snapshot is explicitly stale");
        Assert.That(stale.IsFriendWith("ally#1"), Is.True, "the stale snapshot retains its data");
    }

    [Test]
    public void GetSnapshot_FetchFails_NoCache_ThrowsRelationshipUnavailable()
    {
        var time = new FakeTimeProvider(FixedNow);
        var source = new FakeRelationshipSource { ShouldThrow = true };
        var provider = new RelationshipProvider(source, time);

        Assert.That(async () => await provider.GetSnapshotAsync(Tag),
            Throws.TypeOf<RelationshipUnavailableException>(),
            "no cache + fetch failure must fail closed (never return null, never a silent no-block)");
    }

    [Test]
    public async Task Invalidate_DropsEntry_NextReadRefetches()
    {
        var time = new FakeTimeProvider(FixedNow);
        var source = new FakeRelationshipSource();
        var provider = new RelationshipProvider(source, time);

        await provider.GetSnapshotAsync(Tag);
        provider.Invalidate(Tag);
        await provider.GetSnapshotAsync(Tag); // within TTL, but the entry was dropped

        Assert.That(source.FetchCount, Is.EqualTo(2),
            "Invalidate (C7's change-ping seam) must force the next read to refetch");
    }

    [Test]
    public async Task Invalidate_DuringInFlightFetch_DoesNotResurrectStaleEntry()
    {
        // Security: a fetch that started BEFORE an Invalidate (a C7 change-ping, e.g. a new block) must
        // NOT re-publish the pre-change snapshot over the invalidation — otherwise a just-blocked user
        // could pass a freshness gate for up to a full TTL. The version guard drops the racing publish.
        var time = new FakeTimeProvider(FixedNow);
        var gate = new TaskCompletionSource();
        var source = new FakeRelationshipSource { ReleaseGate = gate.Task };
        var provider = new RelationshipProvider(source, time);

        var inFlight = provider.GetSnapshotAsync(Tag); // starts a fetch, then blocks on the gate
        await source.FirstFetch;                       // the fetch has started (nothing cached yet)
        provider.Invalidate(Tag);                      // change-ping lands while the fetch is in flight
        gate.SetResult();                              // let the pre-change fetch complete
        await inFlight;                                // provider tries to publish → dropped by the guard

        source.ReleaseGate = null;                     // subsequent fetches complete promptly
        await provider.GetSnapshotAsync(Tag);

        Assert.That(source.FetchCount, Is.EqualTo(2),
            "a fetch completing after an Invalidate must not resurrect the cache entry — the next read refetches");
    }

    [Test]
    public void Snapshot_IsFriendWith_And_HasBlocked_AreCaseInsensitive()
    {
        var snapshot = Snapshot(Tag, FixedNow.UtcDateTime, friends: new[] { "Ally#1" }, blocked: new[] { "Foe#2" });

        Assert.That(snapshot.IsFriendWith("ally#1"), Is.True);
        Assert.That(snapshot.IsFriendWith("ALLY#1"), Is.True);
        Assert.That(snapshot.HasBlocked("foe#2"), Is.True);
        Assert.That(snapshot.HasBlocked("FOE#2"), Is.True);
        Assert.That(snapshot.IsFriendWith("foe#2"), Is.False, "a blocked tag is not a friend");
        Assert.That(snapshot.HasBlocked("ally#1"), Is.False, "a friend tag is not blocked");
    }

    [Test]
    public async Task Provider_ConcurrentGetsForSameTag_SingleCacheEntry()
    {
        var time = new FakeTimeProvider(FixedNow);
        var source = new FakeRelationshipSource();
        var provider = new RelationshipProvider(source, time);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => provider.GetSnapshotAsync(Tag)));

        Assert.That(results, Is.All.Not.Null, "every concurrent get must return a usable snapshot, never tear");
        var fetchesDuringBurst = source.FetchCount;

        await provider.GetSnapshotAsync(Tag); // within TTL — proves a single populated cache entry exists
        Assert.That(source.FetchCount, Is.EqualTo(fetchesDuringBurst),
            "after concurrent gets settle there is exactly one cached entry — a subsequent read does not refetch");
    }
}

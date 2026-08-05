using System;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C7 Task 4: System-channel persistence primitives — idempotent find-or-create keyed by
/// (SystemKind, SystemRef), wiring the C1-amendment <see cref="ExpiryCalculator.ForChannelShell"/>
/// 24h Match expiry (previously INERT — no production code built a System channel before this task).
/// New file — mirrors <see cref="DmChannelRepositoryTests"/>'s shape verbatim (Mongo-integration,
/// IntegrationTestBase — ephemeral testcontainer, never the remote default).
/// </summary>
public class ChannelRepositorySystemTests : IntegrationTestBase
{
    private static readonly DateTime Now = new(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task FindOrCreateSystem_CreatesWithMatchKind_RefAndExpiry24h()
    {
        var repo = new ChannelRepository(MongoClient);

        var channel = await repo.FindOrCreateSystem(SystemChannelKind.Match, "match-1", "Match Chat", Now);

        Assert.That(channel.Type, Is.EqualTo(ChannelType.System));
        Assert.That(channel.SystemKind, Is.EqualTo(SystemChannelKind.Match));
        Assert.That(channel.SystemRef, Is.EqualTo("match-1"));
        Assert.That(channel.Name, Is.EqualTo("Match Chat"));
        Assert.That(channel.LastSeq, Is.EqualTo(0L));
        Assert.That(channel.ExpiresAt, Is.Not.Null);
        // Must be wired through ExpiryCalculator.ForChannelShell (the C1-amendment this task closes),
        // not a hardcoded 24h literal — RetentionPeriods.MatchChannel is the single source of truth.
        Assert.That((channel.ExpiresAt.Value - (Now + RetentionPeriods.MatchChannel)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task FindOrCreateSystem_SecondCall_ReturnsExistingWithoutResettingExpiry()
    {
        var repo = new ChannelRepository(MongoClient);
        var first = await repo.FindOrCreateSystem(SystemChannelKind.Match, "match-1", "Match Chat", Now);

        // one hour later, with a different display name — must resolve to the SAME channel and must
        // NOT push the expiry forward or overwrite the original name.
        var second = await repo.FindOrCreateSystem(SystemChannelKind.Match, "match-1", "Match Chat (renamed)", Now.AddHours(1));

        Assert.That(second.Id, Is.EqualTo(first.Id), "second call must return the SAME channel, not create a new one");
        Assert.That(second.ExpiresAt, Is.EqualTo(first.ExpiresAt), "the second call must not reset the original expiry");
        Assert.That(second.Name, Is.EqualTo("Match Chat"), "the second call must not overwrite the existing name");
        var all = await repo.LoadAllOfType(ChannelType.System);
        Assert.That(all.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task FindOrCreateSystem_ConcurrentCalls_YieldOneChannel()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var repo = new ChannelRepository(MongoClient);
        // Genuine race — 8 parallel calls against a NOT-yet-existing (kind, ref) — mirrors
        // FindOrCreateDm_ConcurrentCalls_YieldOneDocument / FindOrCreateSemiPublic_ConcurrentCalls_YieldOneChannel
        // verbatim. Exactly one of the 8 upserts wins the insert half; the other 7 hit the unique
        // partial index ux_systemKind_systemRef, surface as a duplicate-key MongoCommandException, and
        // must resolve via the retry-once path in ChannelRepository.FindOrCreateSystem's catch block —
        // unlike the pre-inserted-doc variant this replaces, the insert half is actually attempted here.
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => repo.FindOrCreateSystem(SystemChannelKind.Match, "match-1", "Match Chat", Now)));
        var results = await Task.WhenAll(tasks);

        var distinctIds = results.Select(c => c.Id).Distinct().ToList();
        Assert.That(distinctIds.Count, Is.EqualTo(1), "concurrent find-or-creates for the same (kind, ref) must resolve to exactly one document");

        var all = await repo.LoadAllOfType(ChannelType.System);
        Assert.That(all.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task FindOrCreateSystem_ClanKind_OmitsExpiresAt()
    {
        var repo = new ChannelRepository(MongoClient);

        var channel = await repo.FindOrCreateSystem(SystemChannelKind.Clan, "clan-1", "Clan Chat", Now);

        Assert.That(channel.ExpiresAt, Is.Null);

        // Raw-BSON assertion (mirrors ChannelRepositoryTests.Channel_RoundTrips_WithEnumsAsStrings, lines
        // 36-40): a permanent System kind (Clan) must leave ExpiresAt genuinely ABSENT from the BSON
        // document, not written as an explicit null — FindOrCreateSystem's `if (expiresAt.HasValue)`
        // guard is load-bearing for this, since an unconditional $setOnInsert of a null value would
        // write the field instead of omitting it, breaking the TTL-absence convention.
        var raw = await MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName)
            .GetCollection<BsonDocument>(ChatCollections.Channels)
            .Find(new BsonDocument("_id", channel.Id)).FirstAsync();
        Assert.That(raw.Contains("ExpiresAt"), Is.False, "Clan (permanent) System channels must never have ExpiresAt written, not even as null");
    }

    [Test]
    public async Task LoadBySystemRef_ReturnsNullForUnknownRef()
    {
        var repo = new ChannelRepository(MongoClient);

        Assert.That(await repo.LoadBySystemRef(SystemChannelKind.Match, "does-not-exist"), Is.Null);
    }

    [Test]
    public async Task LoadBySystemRef_DoesNotMatchAcrossKinds()
    {
        var repo = new ChannelRepository(MongoClient);
        var created = await repo.FindOrCreateSystem(SystemChannelKind.Match, "shared-ref", "Match Chat", Now);

        var wrongKind = await repo.LoadBySystemRef(SystemChannelKind.Lobby, "shared-ref");
        Assert.That(wrongKind, Is.Null, "a lookup for a DIFFERENT SystemKind must not match the same SystemRef");

        var rightKind = await repo.LoadBySystemRef(SystemChannelKind.Match, "shared-ref");
        Assert.That(rightKind, Is.Not.Null);
        Assert.That(rightKind.Id, Is.EqualTo(created.Id));
    }

    [Test]
    public async Task UniqueIndex_RejectsSecondSystemDocWithSameKindRef()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var repo = new ChannelRepository(MongoClient);

        await repo.Insert(new ChatChannel { Type = ChannelType.System, SystemKind = SystemChannelKind.Match, SystemRef = "match-1" });
        Assert.That(
            async () => await repo.Insert(new ChatChannel { Type = ChannelType.System, SystemKind = SystemChannelKind.Match, SystemRef = "match-1" }),
            Throws.TypeOf<MongoWriteException>(),
            "ux_systemKind_systemRef must reject a second System doc with the same (SystemKind, SystemRef)");

        // a different SystemKind with the SAME SystemRef is fine — compound key
        await repo.Insert(new ChatChannel { Type = ChannelType.System, SystemKind = SystemChannelKind.Lobby, SystemRef = "match-1" });

        // non-System channels never populate SystemKind/SystemRef and are unaffected by the partial index
        await repo.Insert(new ChatChannel { Type = ChannelType.Public, Name = "A", NormalizedName = "a" });
        await repo.Insert(new ChatChannel { Type = ChannelType.Public, Name = "B", NormalizedName = "b" });
    }

    [Test]
    public async Task ChannelIndexes_Include_UxSystemKindSystemRef()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        await ChatDomainIndexes.EnsureAllAsync(MongoClient); // idempotent — second run must not throw

        var db = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        var indexes = await (await db.GetCollection<ChatChannel>(ChatCollections.Channels).Indexes.ListAsync()).ToListAsync();

        var index = indexes.Single(i => i["name"] == "ux_systemKind_systemRef");
        Assert.That(index["unique"].AsBoolean, Is.True);
        Assert.That(index["key"]["SystemKind"].ToInt32(), Is.EqualTo(1));
        Assert.That(index["key"]["SystemRef"].ToInt32(), Is.EqualTo(1));
        Assert.That(index["partialFilterExpression"]["Type"].AsString, Is.EqualTo("System"));
    }

    // ── 2026-08-05 reconciliation plan Task 1 (D3/D4/D8) — (epoch, seq) assertion admission +
    // detach latch + epoch-sync repository primitives ────────────────────────────────────────

    [Test]
    public async Task TryAdvanceAssertion_NoEpochStored_Accepts_AndStamps()
    {
        var repo = new ChannelRepository(MongoClient);
        var channel = await repo.FindOrCreateSystem(SystemChannelKind.Match, "match-1", "Match Chat", Now);

        var accepted = await repo.TryAdvanceAssertion(channel.Id, "e1", 1);

        Assert.That(accepted, Is.True, "a channel with no stored epoch must admit the first assertion");
        var reloaded = await repo.Load(channel.Id);
        Assert.That(reloaded.AssertEpoch, Is.EqualTo("e1"));
        Assert.That(reloaded.AssertSeq, Is.EqualTo(1L));
    }

    [Test]
    public async Task TryAdvanceAssertion_SameEpochHigherSeq_Accepts_AndAdvances()
    {
        var repo = new ChannelRepository(MongoClient);
        var channel = await repo.FindOrCreateSystem(SystemChannelKind.Match, "match-1", "Match Chat", Now);
        await repo.TryAdvanceAssertion(channel.Id, "e1", 3);

        var accepted = await repo.TryAdvanceAssertion(channel.Id, "e1", 4);

        Assert.That(accepted, Is.True);
        var reloaded = await repo.Load(channel.Id);
        Assert.That(reloaded.AssertEpoch, Is.EqualTo("e1"));
        Assert.That(reloaded.AssertSeq, Is.EqualTo(4L));
    }

    [Test]
    public async Task TryAdvanceAssertion_SameEpochEqualSeq_Rejects_AndLeavesStampUnchanged()
    {
        // Pins equal-seq REJECTION (the observable boolean/stamp), not the ModifiedCount vs MatchedCount
        // choice — the strict Lt and the ModifiedCount check mutually mask each other here; neither is
        // independently distinguished by this test alone (see ChannelRepository.TryAdvanceAssertion).
        var repo = new ChannelRepository(MongoClient);
        var channel = await repo.FindOrCreateSystem(SystemChannelKind.Match, "match-1", "Match Chat", Now);
        await repo.TryAdvanceAssertion(channel.Id, "e1", 3);

        var accepted = await repo.TryAdvanceAssertion(channel.Id, "e1", 3);

        Assert.That(accepted, Is.False);
        var reloaded = await repo.Load(channel.Id);
        Assert.That(reloaded.AssertEpoch, Is.EqualTo("e1"));
        Assert.That(reloaded.AssertSeq, Is.EqualTo(3L));
    }

    [Test]
    public async Task TryAdvanceAssertion_SameEpochLowerSeq_Rejects_AndLeavesStampUnchanged()
    {
        var repo = new ChannelRepository(MongoClient);
        var channel = await repo.FindOrCreateSystem(SystemChannelKind.Match, "match-1", "Match Chat", Now);
        await repo.TryAdvanceAssertion(channel.Id, "e1", 5);

        var accepted = await repo.TryAdvanceAssertion(channel.Id, "e1", 4);

        Assert.That(accepted, Is.False);
        var reloaded = await repo.Load(channel.Id);
        Assert.That(reloaded.AssertEpoch, Is.EqualTo("e1"));
        Assert.That(reloaded.AssertSeq, Is.EqualTo(5L));
    }

    [Test]
    public async Task TryAdvanceAssertion_DifferentEpoch_Accepts_AndReAnchors()
    {
        // D3(c): epochs are opaque and unordered, so a mismatch is accepted and re-anchored rather
        // than discarded (a discard rule could permanently wedge a channel after an mm restart).
        var repo = new ChannelRepository(MongoClient);
        var channel = await repo.FindOrCreateSystem(SystemChannelKind.Match, "match-1", "Match Chat", Now);
        await repo.TryAdvanceAssertion(channel.Id, "e1", 9);

        var accepted = await repo.TryAdvanceAssertion(channel.Id, "e2", 1);

        Assert.That(accepted, Is.True);
        var reloaded = await repo.Load(channel.Id);
        Assert.That(reloaded.AssertEpoch, Is.EqualTo("e2"));
        Assert.That(reloaded.AssertSeq, Is.EqualTo(1L));
    }

    [Test]
    public async Task TryAdvanceAssertion_DetachedChannel_Rejects()
    {
        var repo = new ChannelRepository(MongoClient);
        var channel = await repo.FindOrCreateSystem(SystemChannelKind.Match, "match-1", "Match Chat", Now);
        await repo.TryAdvanceAssertion(channel.Id, "e1", 1);
        await repo.SetDetached(channel.Id);

        var accepted = await repo.TryAdvanceAssertion(channel.Id, "e1", 99);

        Assert.That(accepted, Is.False, "a detached channel must never re-admit an assertion, even with a strictly greater seq");
        var reloaded = await repo.Load(channel.Id);
        Assert.That(reloaded.AssertSeq, Is.EqualTo(1L));
    }

    [Test]
    public async Task TryAdvanceAssertion_UnknownChannelId_Rejects()
    {
        var repo = new ChannelRepository(MongoClient);

        var accepted = await repo.TryAdvanceAssertion("does-not-exist", "e1", 1);

        Assert.That(accepted, Is.False);
    }

    [Test]
    public async Task SetDetached_IsIdempotent_AndPersists()
    {
        var repo = new ChannelRepository(MongoClient);
        var channel = await repo.FindOrCreateSystem(SystemChannelKind.Match, "match-1", "Match Chat", Now);

        await repo.SetDetached(channel.Id);
        await repo.SetDetached(channel.Id);

        var reloaded = await repo.Load(channel.Id);
        Assert.That(reloaded.Detached, Is.True);
    }

    [Test]
    public async Task LoadNonDetachedMatchChannels_ReturnsOnlyNonDetachedAssertionStampedSystemMatch()
    {
        var repo = new ChannelRepository(MongoClient);
        var liveMatch = await repo.FindOrCreateSystem(SystemChannelKind.Match, "match-live", "Live Match", Now);
        await repo.TryAdvanceAssertion(liveMatch.Id, "e1", 1);
        var detachedMatch = await repo.FindOrCreateSystem(SystemChannelKind.Match, "match-detached", "Detached Match", Now);
        await repo.TryAdvanceAssertion(detachedMatch.Id, "e1", 1);
        await repo.SetDetached(detachedMatch.Id);
        // 2026-08-05 fix wave (final review H1, plan D8 amendment): a match channel that has NEVER been
        // stamped by the assertion protocol — exactly the shape of a channel minted only by the
        // deprecated delta path during the transition window — must be excluded too, same as a detached
        // one, not just left to chance alongside the non-match seeds below.
        await repo.FindOrCreateSystem(SystemChannelKind.Match, "match-unstamped", "Unstamped Match", Now);
        await repo.FindOrCreateSystem(SystemChannelKind.Clan, "clan-1", "Clan Chat", Now);
        await repo.Insert(new ChatChannel { Type = ChannelType.Public, Name = "Pub", NormalizedName = "pub" });
        await repo.Insert(new ChatChannel { Type = ChannelType.Dm, PairKey = "alice#1|bob#2" });
        // Pins the Type == System clause (Task 1 review r1, mutation M8): SystemKind == Match alone
        // would otherwise admit this non-System doc. The unique ux_systemKind_systemRef index is
        // partial on Type == System, so a Public doc with a SystemKind cannot collide with it.
        await repo.Insert(new ChatChannel { Type = ChannelType.Public, Name = "Impostor", NormalizedName = "impostor", SystemKind = SystemChannelKind.Match, SystemRef = "match-impostor" });

        var result = await repo.LoadNonDetachedMatchChannels();

        Assert.That(result.Select(c => c.Id).ToList(), Is.EquivalentTo(new[] { liveMatch.Id }));
    }

    [Test]
    public async Task StampAssertionEpoch_SetsEpoch_AndResetsSeqToZeroSentinel()
    {
        var repo = new ChannelRepository(MongoClient);
        var channel = await repo.FindOrCreateSystem(SystemChannelKind.Match, "match-1", "Match Chat", Now);
        await repo.TryAdvanceAssertion(channel.Id, "e1", 9);

        await repo.StampAssertionEpoch(channel.Id, "e2");

        var reloaded = await repo.Load(channel.Id);
        Assert.That(reloaded.AssertEpoch, Is.EqualTo("e2"));
        Assert.That(reloaded.AssertSeq, Is.EqualTo(0L));

        // proves the 0 sentinel (never $unset) does not wedge the channel against the new epoch.
        var accepted = await repo.TryAdvanceAssertion(channel.Id, "e2", 1);
        Assert.That(accepted, Is.True);
    }

    [Test]
    public async Task LegacyChannelDocument_WithoutAssertionFields_Deserializes_AndIsAdmissible()
    {
        // Backward-compatibility pin: a channel created via the legacy create/delta path never wrote
        // the three new fields — every read must tolerate absence, and the CAS must still admit.
        var repo = new ChannelRepository(MongoClient);
        var channel = await repo.FindOrCreateSystem(SystemChannelKind.Match, "match-1", "Match Chat", Now);

        Assert.That(channel.Detached, Is.False);
        Assert.That(channel.AssertSeq, Is.EqualTo(0L));
        Assert.That(channel.AssertEpoch, Is.Null);

        var accepted = await repo.TryAdvanceAssertion(channel.Id, "e1", 1);
        Assert.That(accepted, Is.True);
    }
}

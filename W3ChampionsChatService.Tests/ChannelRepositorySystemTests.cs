using System;
using System.Linq;
using System.Threading.Tasks;
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
    public async Task FindOrCreateSystem_ConcurrentDuplicateKey_RetriesOnce()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var repo = new ChannelRepository(MongoClient);
        // Simulates the losing half of a genuine race: a doc already exists under the same
        // (SystemKind, SystemRef) by the time the upsert's own insert half is attempted, which must
        // surface as a duplicate-key MongoCommandException on ux_systemKind_systemRef and be retried
        // once (mirroring FindOrCreateSemiPublic/FindOrCreateDm's retry idiom).
        var preInserted = new ChatChannel
        {
            Type = ChannelType.System,
            SystemKind = SystemChannelKind.Match,
            SystemRef = "match-1",
            Name = "Pre-existing",
            LastSeq = 0,
        };
        await repo.Insert(preInserted);

        var result = await repo.FindOrCreateSystem(SystemChannelKind.Match, "match-1", "Match Chat", Now);

        Assert.That(result.Id, Is.EqualTo(preInserted.Id));
        var all = await repo.LoadAllOfType(ChannelType.System);
        Assert.That(all.Count, Is.EqualTo(1));
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
}

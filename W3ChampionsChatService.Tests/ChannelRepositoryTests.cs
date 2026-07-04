using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Tests;

public class ChannelRepositoryTests : IntegrationTestBase
{
    [Test]
    public async Task Channel_RoundTrips_WithEnumsAsStrings()
    {
        var repo = new ChannelRepository(MongoClient);
        var channel = new ChatChannel
        {
            Type = ChannelType.Dm,
            PairKey = DmPairKey.For("Peter#123", "Wolf#456"),
            RequestState = DmRequestState.Pending,
            LastSeq = 0,
        };

        await repo.Insert(channel);
        var loaded = await repo.Load(channel.Id);

        Assert.AreEqual(ChannelType.Dm, loaded.Type);
        Assert.AreEqual("peter#123|wolf#456", loaded.PairKey);
        Assert.AreEqual(DmRequestState.Pending, loaded.RequestState);
        Assert.IsNull(loaded.ExpiresAt);

        // enum stored as readable string, and stored in the spec-named collection
        var raw = await MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName)
            .GetCollection<BsonDocument>(ChatCollections.Channels)
            .Find(new BsonDocument("_id", channel.Id)).FirstAsync();
        Assert.AreEqual("Dm", raw["Type"].AsString);
        Assert.IsFalse(raw.Contains("ExpiresAt"), "null ExpiresAt must be omitted so TTL never sees it");
    }

    [Test]
    public async Task LoadByNormalizedName_FindsPublicChannel()
    {
        var repo = new ChannelRepository(MongoClient);
        await repo.Insert(new ChatChannel { Type = ChannelType.Public, Name = "W3C Lounge", NormalizedName = "w3c lounge" });

        var loaded = await repo.LoadByNormalizedName(ChannelType.Public, "w3c lounge");

        Assert.IsNotNull(loaded);
        Assert.AreEqual("W3C Lounge", loaded.Name);
    }

    [Test]
    public async Task ChannelIndexes_AreCreated_UniquePairKeyPartial_AndTtl()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        await ChatDomainIndexes.EnsureAllAsync(MongoClient); // idempotent — second run must not throw

        var db = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        var indexes = await (await db.GetCollection<ChatChannel>(ChatCollections.Channels).Indexes.ListAsync()).ToListAsync();

        var pairKey = indexes.Single(i => i["name"] == "ux_pairKey_dm");
        Assert.IsTrue(pairKey["unique"].AsBoolean);
        Assert.AreEqual("Dm", pairKey["partialFilterExpression"]["Type"].AsString);

        var ttl = indexes.Single(i => i["name"] == "ttl_expiresAt");
        Assert.AreEqual(0, ttl["expireAfterSeconds"].ToDouble());
        Assert.AreEqual(1, ttl["key"]["ExpiresAt"].ToInt32());
    }

    [Test]
    public async Task DuplicateDmPairKey_IsRejected_ButNonDmChannelsIgnoreTheIndex()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var repo = new ChannelRepository(MongoClient);
        var pairKey = DmPairKey.For("Peter#123", "Wolf#456");

        await repo.Insert(new ChatChannel { Type = ChannelType.Dm, PairKey = pairKey });
        Assert.ThrowsAsync<MongoWriteException>(
            () => repo.Insert(new ChatChannel { Type = ChannelType.Dm, PairKey = pairKey }));

        // two public channels without PairKey are fine (partial index only covers Type == Dm)
        await repo.Insert(new ChatChannel { Type = ChannelType.Public, Name = "A", NormalizedName = "a" });
        await repo.Insert(new ChatChannel { Type = ChannelType.Public, Name = "B", NormalizedName = "b" });
    }

    [Test]
    public async Task AllocateSeq_ReturnsSequentialValues()
    {
        var repo = new ChannelRepository(MongoClient);
        var channel = new ChatChannel { Type = ChannelType.Public, Name = "Seq", NormalizedName = "seq" };
        await repo.Insert(channel);

        Assert.AreEqual(1L, await repo.AllocateSeq(channel.Id, DateTime.UtcNow));
        Assert.AreEqual(2L, await repo.AllocateSeq(channel.Id, DateTime.UtcNow));
        Assert.AreEqual(3L, await repo.AllocateSeq(channel.Id, DateTime.UtcNow));
    }

    [Test]
    public async Task AllocateSeq_IsStrictlyMonotonic_UnderConcurrentAllocation()
    {
        var repo = new ChannelRepository(MongoClient);
        var channel = new ChatChannel { Type = ChannelType.Public, Name = "Seq", NormalizedName = "seq" };
        await repo.Insert(channel);

        var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(() => repo.AllocateSeq(channel.Id, DateTime.UtcNow)));
        var seqs = await Task.WhenAll(tasks);

        // 100 parallel allocations: no duplicates, no gaps, counter lands on exactly 100
        CollectionAssert.AreEquivalent(Enumerable.Range(1, 100).Select(i => (long)i).ToList(), seqs);
        Assert.AreEqual(100L, (await repo.Load(channel.Id)).LastSeq);
    }

    [Test]
    public void AllocateSeq_UnknownChannel_Throws()
    {
        var repo = new ChannelRepository(MongoClient);
        Assert.ThrowsAsync<InvalidOperationException>(() => repo.AllocateSeq("does-not-exist", DateTime.UtcNow));
    }

    [Test]
    public async Task AllocateSeq_AlsoStampsLastMessageAt()
    {
        var repo = new ChannelRepository(MongoClient);
        var channel = new ChatChannel { Type = ChannelType.Public, Name = "Seq", NormalizedName = "seq" };
        await repo.Insert(channel);
        Assert.IsNull(channel.LastMessageAt);

        var t1 = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);
        await repo.AllocateSeq(channel.Id, t1);
        var afterFirst = await repo.Load(channel.Id);
        Assert.IsTrue((afterFirst.LastMessageAt.Value - t1).Duration() < TimeSpan.FromSeconds(1));

        var t2 = t1.AddMinutes(5);
        await repo.AllocateSeq(channel.Id, t2);
        var afterSecond = await repo.Load(channel.Id);
        Assert.IsTrue((afterSecond.LastMessageAt.Value - t2).Duration() < TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task FindOrCreateSemiPublic_CreatesOnFirstJoin_ReturnsExistingAfter()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var repo = new ChannelRepository(MongoClient);
        var now = DateTime.UtcNow;

        var created = await repo.FindOrCreateSemiPublic("Clan War Room", now);

        Assert.AreEqual(ChannelType.SemiPublic, created.Type);
        Assert.AreEqual("clan war room", created.NormalizedName);
        Assert.AreEqual("Clan War Room", created.Name);
        Assert.AreEqual(0L, created.LastSeq);
        Assert.IsTrue((created.LastMessageAt.Value - now).Duration() < TimeSpan.FromSeconds(1));

        var again = await repo.FindOrCreateSemiPublic("clan war room", now.AddMinutes(1)); // different case/time

        Assert.AreEqual(created.Id, again.Id, "second call must return the SAME channel, not create a new one");
        var all = await repo.LoadAllOfType(ChannelType.SemiPublic);
        Assert.AreEqual(1, all.Count);
    }

    [Test]
    public async Task FindOrCreateSemiPublic_ConcurrentCalls_YieldOneChannel()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var repo = new ChannelRepository(MongoClient);
        var now = DateTime.UtcNow;

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => repo.FindOrCreateSemiPublic("Race Room", now)));
        var results = await Task.WhenAll(tasks);

        var distinctIds = results.Select(c => c.Id).Distinct().ToList();
        Assert.AreEqual(1, distinctIds.Count, "all 8 concurrent find-or-creates must resolve to exactly one channel");

        var all = await repo.LoadAllOfType(ChannelType.SemiPublic);
        Assert.AreEqual(1, all.Count);
    }

    [Test]
    public async Task Channels_UniqueIndex_TypeNormalizedName_ForNameJoinableTypes()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        await ChatDomainIndexes.EnsureAllAsync(MongoClient); // idempotent — second run must not throw

        var db = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        var indexes = await (await db.GetCollection<ChatChannel>(ChatCollections.Channels).Indexes.ListAsync()).ToListAsync();
        var index = indexes.Single(i => i["name"] == "ux_type_normalizedName");
        Assert.IsTrue(index["unique"].AsBoolean);
        Assert.AreEqual(1, index["key"]["Type"].ToInt32());
        Assert.AreEqual(1, index["key"]["NormalizedName"].ToInt32());

        var repo = new ChannelRepository(MongoClient);

        await repo.Insert(new ChatChannel { Type = ChannelType.Public, Name = "Dup", NormalizedName = "dup" });
        Assert.ThrowsAsync<MongoWriteException>(
            () => repo.Insert(new ChatChannel { Type = ChannelType.Public, Name = "Dup2", NormalizedName = "dup" }));

        await repo.Insert(new ChatChannel { Type = ChannelType.SemiPublic, Name = "SemiDup", NormalizedName = "semidup" });
        Assert.ThrowsAsync<MongoWriteException>(
            () => repo.Insert(new ChatChannel { Type = ChannelType.SemiPublic, Name = "SemiDup2", NormalizedName = "semidup" }));

        // same normalized name across DIFFERENT name-joinable types is fine — compound key
        await repo.Insert(new ChatChannel { Type = ChannelType.Public, Name = "cross", NormalizedName = "cross" });
        await repo.Insert(new ChatChannel { Type = ChannelType.SemiPublic, Name = "cross", NormalizedName = "cross" });

        // non-name-joinable types are excluded by the partial filter — proves the partial
        // filter (not the base field) drives uniqueness (System channels don't set
        // NormalizedName in practice; this exercises the filter boundary directly)
        await repo.Insert(new ChatChannel { Type = ChannelType.System, SystemKind = SystemChannelKind.Match, SystemRef = "m1", NormalizedName = "dup" });
        await repo.Insert(new ChatChannel { Type = ChannelType.System, SystemKind = SystemChannelKind.Match, SystemRef = "m2", NormalizedName = "dup" });
    }

    [Test]
    public async Task LoadByIds_ReturnsRequestedChannels()
    {
        var repo = new ChannelRepository(MongoClient);
        var a = new ChatChannel { Type = ChannelType.Public, Name = "A", NormalizedName = "a" };
        var b = new ChatChannel { Type = ChannelType.Public, Name = "B", NormalizedName = "b" };
        var c = new ChatChannel { Type = ChannelType.Public, Name = "C", NormalizedName = "c" };
        await repo.Insert(a);
        await repo.Insert(b);
        await repo.Insert(c);

        var loaded = await repo.LoadByIds(new[] { a.Id, c.Id });

        Assert.AreEqual(2, loaded.Count);
        CollectionAssert.AreEquivalent(new[] { a.Id, c.Id }, loaded.Select(x => x.Id).ToList());
    }

    [Test]
    public async Task LoadAnyByNormalizedName_FindsAcrossTypes()
    {
        var repo = new ChannelRepository(MongoClient);
        await repo.Insert(new ChatChannel { Type = ChannelType.Public, Name = "Public One", NormalizedName = "public one" });
        var semi = new ChatChannel { Type = ChannelType.SemiPublic, Name = "Semi One", NormalizedName = "semi one" };
        await repo.Insert(semi);

        var found = await repo.LoadAnyByNormalizedName("semi one");

        Assert.IsNotNull(found);
        Assert.AreEqual(semi.Id, found.Id);
        Assert.AreEqual(ChannelType.SemiPublic, found.Type);
    }

    // C4 Task 7 review finding: the moderation "scope wall" — {Public, SemiPublic, System+Match} — now
    // exists as TWO independent expressions: ChannelModeration.IsModeratable (a C# predicate, used by
    // ChatHub.DeleteMessage/PurgeMessagesFromUser + the REST message read) and LoadModeratableChannels'
    // Mongo filter (used by the REST channel-listing endpoint), because a C# predicate can't be pushed
    // into a query. They agree today but could silently drift if only one is ever edited, which would
    // let the moderator channel LIST leak clan/lobby/dm channel metadata that the message read still
    // correctly rejects. This test seeds one channel per every ChannelType x SystemChannelKind
    // combination and asserts the two expressions agree EXACTLY on which of them are moderatable, so any
    // future one-sided edit fails the build instead of silently drifting.
    [Test]
    public async Task LoadModeratableChannels_ExactlyMatches_ChannelModerationIsModeratable_AcrossAllTypeKindCombinations()
    {
        var repo = new ChannelRepository(MongoClient);
        var now = DateTime.UtcNow;

        var allChannels = new List<ChatChannel>
        {
            new() { Type = ChannelType.Public, LastMessageAt = now },
            new() { Type = ChannelType.SemiPublic, LastMessageAt = now },
            new() { Type = ChannelType.System, SystemKind = SystemChannelKind.Match, LastMessageAt = now },
            new() { Type = ChannelType.System, SystemKind = SystemChannelKind.Clan, LastMessageAt = now },
            new() { Type = ChannelType.System, SystemKind = SystemChannelKind.Lobby, LastMessageAt = now },
            new() { Type = ChannelType.System, SystemKind = null, LastMessageAt = now },
            new() { Type = ChannelType.Dm, LastMessageAt = now },
            new() { Type = ChannelType.GroupDm, LastMessageAt = now },
        };

        foreach (var channel in allChannels)
        {
            await repo.Insert(channel);
        }

        var expectedIds = allChannels.Where(ChannelModeration.IsModeratable).Select(c => c.Id).ToHashSet();
        var actualIds = (await repo.LoadModeratableChannels(allChannels.Count)).Select(c => c.Id).ToHashSet();

        CollectionAssert.AreEquivalent(expectedIds, actualIds,
            "LoadModeratableChannels' Mongo filter must select EXACTLY the channels ChannelModeration.IsModeratable " +
            "agrees are moderatable — the two scope-wall expressions must never drift apart");
    }
}

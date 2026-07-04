using System;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Driver;
using NUnit.Framework;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Memberships;

namespace W3ChampionsChatService.Tests;

public class MembershipRepositoryTests : IntegrationTestBase
{
    [Test]
    public async Task Membership_RoundTrips_WithDefaults()
    {
        var repo = new MembershipRepository(MongoClient, new ChannelRepository(MongoClient));
        var membership = new ChannelMembership
        {
            ChannelId = "chan1",
            BattleTag = "Peter#123",
            JoinedAt = DateTime.UtcNow,
        };

        await repo.Insert(membership);
        var loaded = await repo.Load("chan1", "Peter#123");

        Assert.AreEqual(MembershipRole.Member, loaded.Role);
        Assert.AreEqual(NotificationLevel.All, loaded.NotificationLevel);
        Assert.AreEqual(0L, loaded.LastReadSeq);
    }

    [Test]
    public async Task LoadForUser_ReturnsAllChannelsOfThatUser_ViaBattleTagIndex()
    {
        var repo = new MembershipRepository(MongoClient, new ChannelRepository(MongoClient));
        await repo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });
        await repo.Insert(new ChannelMembership { ChannelId = "chan2", BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });
        await repo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "Wolf#456", JoinedAt = DateTime.UtcNow });

        var mine = await repo.LoadForUser("Peter#123");

        Assert.AreEqual(2, mine.Count);
        CollectionAssert.AreEquivalent(new[] { "chan1", "chan2" }, mine.Select(m => m.ChannelId).ToList());
    }

    [Test]
    public async Task DuplicateMembership_IsRejectedByUniqueIndex()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var repo = new MembershipRepository(MongoClient, new ChannelRepository(MongoClient));
        await repo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });

        Assert.ThrowsAsync<MongoWriteException>(
            () => repo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow }));
    }

    [Test]
    public async Task MembershipIndexes_AreCreated()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var db = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        var indexes = await (await db.GetCollection<ChannelMembership>(ChatCollections.ChannelMemberships).Indexes.ListAsync()).ToListAsync();

        var unique = indexes.Single(i => i["name"] == "ux_channelId_battleTag");
        Assert.IsTrue(unique["unique"].AsBoolean);
        Assert.AreEqual(1, unique["key"]["ChannelId"].ToInt32());
        Assert.AreEqual(1, unique["key"]["BattleTag"].ToInt32());

        var byUser = indexes.Single(i => i["name"] == "ix_battleTag");
        Assert.AreEqual(1, byUser["key"]["BattleTag"].ToInt32());
    }

    [Test]
    public async Task UpdateLastReadSeq_IsMonotonicMax()
    {
        var repo = new MembershipRepository(MongoClient, new ChannelRepository(MongoClient));
        await repo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow, LastReadSeq = 5 });

        await repo.UpdateLastReadSeq("chan1", "Peter#123", 10);
        Assert.AreEqual(10L, (await repo.Load("chan1", "Peter#123")).LastReadSeq);

        await repo.UpdateLastReadSeq("chan1", "Peter#123", 3); // lower — must NOT regress
        Assert.AreEqual(10L, (await repo.Load("chan1", "Peter#123")).LastReadSeq);

        await repo.UpdateLastReadSeq("chan1", "Peter#123", 15);
        Assert.AreEqual(15L, (await repo.Load("chan1", "Peter#123")).LastReadSeq);
    }

    [Test]
    public async Task SetNotificationLevel_Persists()
    {
        var repo = new MembershipRepository(MongoClient, new ChannelRepository(MongoClient));
        await repo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });
        Assert.AreEqual(NotificationLevel.All, (await repo.Load("chan1", "Peter#123")).NotificationLevel);

        await repo.SetNotificationLevel("chan1", "Peter#123", NotificationLevel.Mentions);

        Assert.AreEqual(NotificationLevel.Mentions, (await repo.Load("chan1", "Peter#123")).NotificationLevel);
    }

    [Test]
    public async Task CountNameJoinableMembershipsForUser_CountsOnlyPublicAndSemiPublic()
    {
        var channelRepo = new ChannelRepository(MongoClient);
        var pub = new ChatChannel { Type = ChannelType.Public, Name = "Pub", NormalizedName = "pub" };
        var semi = new ChatChannel { Type = ChannelType.SemiPublic, Name = "Semi", NormalizedName = "semi" };
        var sys = new ChatChannel { Type = ChannelType.System, SystemKind = SystemChannelKind.Match, SystemRef = "m1" };
        var dm = new ChatChannel { Type = ChannelType.Dm, PairKey = DmPairKey.For("Peter#123", "Wolf#456") };
        await channelRepo.Insert(pub);
        await channelRepo.Insert(semi);
        await channelRepo.Insert(sys);
        await channelRepo.Insert(dm);

        var membershipRepo = new MembershipRepository(MongoClient, channelRepo);
        await membershipRepo.Insert(new ChannelMembership { ChannelId = pub.Id, BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });
        await membershipRepo.Insert(new ChannelMembership { ChannelId = semi.Id, BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });
        await membershipRepo.Insert(new ChannelMembership { ChannelId = sys.Id, BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });
        await membershipRepo.Insert(new ChannelMembership { ChannelId = dm.Id, BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });

        var count = await membershipRepo.CountNameJoinableMembershipsForUser("Peter#123");

        Assert.AreEqual(2, count);
    }

    // ── C5 Task 2 — DM/group repository foundation additions ─────────────────────────────

    [Test]
    public async Task LoadForChannel_And_CountForChannel_ReturnAllMembersOfOneChannel()
    {
        var repo = new MembershipRepository(MongoClient, new ChannelRepository(MongoClient));
        await repo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });
        await repo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "Wolf#456", JoinedAt = DateTime.UtcNow });
        await repo.Insert(new ChannelMembership { ChannelId = "chan2", BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });

        var members = await repo.LoadForChannel("chan1");
        var count = await repo.CountForChannel("chan1");

        Assert.AreEqual(2, members.Count);
        // The durable membership battleTag key is stored lowercased (C5 T4 — casing-agnostic key
        // convention, see MembershipRepository's class doc), so the persisted rows read back lowercased.
        CollectionAssert.AreEquivalent(new[] { "peter#123", "wolf#456" }, members.Select(m => m.BattleTag).ToList());
        Assert.AreEqual(2, count);
    }

    [Test]
    public async Task SetRole_Persists()
    {
        var repo = new MembershipRepository(MongoClient, new ChannelRepository(MongoClient));
        await repo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });
        Assert.AreEqual(MembershipRole.Member, (await repo.Load("chan1", "Peter#123")).Role);

        await repo.SetRole("chan1", "Peter#123", MembershipRole.Owner);

        Assert.AreEqual(MembershipRole.Owner, (await repo.Load("chan1", "Peter#123")).Role);
    }

    [Test]
    public async Task SetDeclinedUntil_And_ClearDeclinedUntil_RoundTrip()
    {
        var repo = new MembershipRepository(MongoClient, new ChannelRepository(MongoClient));
        await repo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });
        Assert.IsNull((await repo.Load("chan1", "Peter#123")).DeclinedUntil);

        var declinedUntil = DateTime.UtcNow.AddHours(24);
        await repo.SetDeclinedUntil("chan1", "Peter#123", declinedUntil);
        var declined = await repo.Load("chan1", "Peter#123");
        Assert.IsNotNull(declined.DeclinedUntil);
        Assert.IsTrue((declined.DeclinedUntil.Value - declinedUntil).Duration() < TimeSpan.FromSeconds(1));

        await repo.ClearDeclinedUntil("chan1", "Peter#123");
        Assert.IsNull((await repo.Load("chan1", "Peter#123")).DeclinedUntil);
    }

    [Test]
    public async Task DeleteAllForChannel_RemovesEveryMembershipRow_LeavesOtherChannelsIntact()
    {
        var repo = new MembershipRepository(MongoClient, new ChannelRepository(MongoClient));
        await repo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });
        await repo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "Wolf#456", JoinedAt = DateTime.UtcNow });
        await repo.Insert(new ChannelMembership { ChannelId = "chan2", BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });

        await repo.DeleteAllForChannel("chan1");

        Assert.AreEqual(0, (await repo.LoadForChannel("chan1")).Count);
        Assert.AreEqual(1, (await repo.LoadForUser("Peter#123")).Count);
    }
}

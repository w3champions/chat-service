using System;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Driver;
using NUnit.Framework;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Memberships;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C5 Task 2 (D3/D10): the DM/group persistence primitives — pair-key find-or-create, shell-expiry
/// wiring, the accept-race guard, and the idempotent membership upsert. New file — NUnit constraint
/// style throughout (Assert.That), mirroring <c>ChatHubSendMessageTests.cs</c>. Mongo-integration
/// (IntegrationTestBase — ephemeral testcontainer, never the remote default).
/// </summary>
public class DmChannelRepositoryTests : IntegrationTestBase
{
    private static readonly DateTime Now = new(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task FindOrCreateDm_CreatesPendingShell_WithPairKeyInitiatorAndExpiry()
    {
        var repo = new ChannelRepository(MongoClient);

        var channel = await repo.FindOrCreateDm("Peter#123", "Wolf#456", "Peter#123", DmRequestState.Pending, Now);

        Assert.That(channel.Type, Is.EqualTo(ChannelType.Dm));
        Assert.That(channel.PairKey, Is.EqualTo(DmPairKey.For("Peter#123", "Wolf#456")));
        Assert.That(channel.RequestState, Is.EqualTo(DmRequestState.Pending));
        Assert.That(channel.RequestInitiatedBy, Is.EqualTo("Peter#123"));
        Assert.That(channel.LastSeq, Is.EqualTo(0L));
        Assert.That(channel.LastMessageAt, Is.Not.Null);
        Assert.That((channel.LastMessageAt.Value - Now).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)));
        Assert.That(channel.ExpiresAt, Is.Not.Null);
        Assert.That((channel.ExpiresAt.Value - Now.AddDays(30)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task FindOrCreateDm_FriendVariant_CreatesAcceptedShell()
    {
        var repo = new ChannelRepository(MongoClient);

        var channel = await repo.FindOrCreateDm("Peter#123", "Wolf#456", "Peter#123", DmRequestState.Accepted, Now);

        Assert.That(channel.RequestState, Is.EqualTo(DmRequestState.Accepted));
        Assert.That((channel.ExpiresAt.Value - Now.AddDays(365)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task FindOrCreateDm_SecondCall_ReturnsSameChannel_EitherArgumentOrder()
    {
        var repo = new ChannelRepository(MongoClient);

        var first = await repo.FindOrCreateDm("Peter#123", "Wolf#456", "Peter#123", DmRequestState.Pending, Now);
        // reversed order, mixed case + whitespace — pair-key symmetry
        var second = await repo.FindOrCreateDm(" WOLF#456 ", " peter#123 ", "Wolf#456", DmRequestState.Pending, Now.AddMinutes(1));

        Assert.That(second.Id, Is.EqualTo(first.Id), "either argument order/casing must resolve to the SAME channel");
        Assert.That(second.RequestInitiatedBy, Is.EqualTo("Peter#123"), "the SECOND call must not overwrite the original initiator");
        var all = await repo.LoadAllOfType(ChannelType.Dm);
        Assert.That(all.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task FindOrCreateDm_ConcurrentCalls_YieldOneDocument()
    {
        var repo = new ChannelRepository(MongoClient);

        var tasks = Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => repo.FindOrCreateDm("Peter#123", "Wolf#456", i % 2 == 0 ? "Peter#123" : "Wolf#456", DmRequestState.Pending, Now)));
        var results = await Task.WhenAll(tasks);

        var distinctIds = results.Select(c => c.Id).Distinct().ToList();
        Assert.That(distinctIds.Count, Is.EqualTo(1), "concurrent find-or-creates for the same pair must resolve to exactly one document");

        var all = await repo.LoadAllOfType(ChannelType.Dm);
        Assert.That(all.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task UxPairKeyDm_Index_RejectsDuplicateDmPairKey_AllowsNonDm()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var repo = new ChannelRepository(MongoClient);
        var pairKey = DmPairKey.For("Peter#123", "Wolf#456");

        await repo.Insert(new ChatChannel { Type = ChannelType.Dm, PairKey = pairKey });
        Assert.That(
            async () => await repo.Insert(new ChatChannel { Type = ChannelType.Dm, PairKey = pairKey }),
            Throws.TypeOf<MongoWriteException>(),
            "ux_pairKey_dm must reject a second Dm channel with the same pair key");

        // non-Dm channels never populate PairKey and are unaffected by the partial index
        await repo.Insert(new ChatChannel { Type = ChannelType.GroupDm });
        await repo.Insert(new ChatChannel { Type = ChannelType.GroupDm });
    }

    [Test]
    public async Task AllocateSeq_WithShellExpiry_SetsExpiresAtAtomically()
    {
        var repo = new ChannelRepository(MongoClient);
        var channel = await repo.FindOrCreateDm("Peter#123", "Wolf#456", "Peter#123", DmRequestState.Accepted, Now);

        var shellExpiry = Now.AddDays(365).AddHours(1);
        await repo.AllocateSeq(channel.Id, Now.AddMinutes(1), shellExpiresAt: shellExpiry);

        var reloaded = await repo.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(1L));
        Assert.That((reloaded.ExpiresAt.Value - shellExpiry).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task AllocateSeq_NullShellExpiry_LeavesExpiresAtUntouched()
    {
        // Regression pin (C5 D10): public/semiPublic/System sends never pass shellExpiresAt — the
        // pre-existing overload behavior (no ExpiresAt write at all) must be completely unchanged.
        var repo = new ChannelRepository(MongoClient);
        var channel = new ChatChannel { Type = ChannelType.Public, Name = "Lounge", NormalizedName = "lounge" };
        await repo.Insert(channel);
        Assert.That(channel.ExpiresAt, Is.Null);

        await repo.AllocateSeq(channel.Id, Now);

        var reloaded = await repo.Load(channel.Id);
        Assert.That(reloaded.ExpiresAt, Is.Null, "a null shellExpiresAt must leave ExpiresAt untouched");
    }

    [Test]
    public async Task SetRequestAccepted_FlipsOnlyPending_ReturnsFalseWhenAlreadyAccepted()
    {
        var repo = new ChannelRepository(MongoClient);
        var channel = await repo.FindOrCreateDm("Peter#123", "Wolf#456", "Peter#123", DmRequestState.Pending, Now);

        var firstAccept = await repo.SetRequestAccepted(channel.Id, Now.AddMinutes(5));
        Assert.That(firstAccept, Is.True);

        var reloaded = await repo.Load(channel.Id);
        Assert.That(reloaded.RequestState, Is.EqualTo(DmRequestState.Accepted));
        Assert.That((reloaded.ExpiresAt.Value - Now.AddMinutes(5).AddDays(365)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)));

        // accept-race guard: a second accept attempt on an already-Accepted channel is a no-op
        var secondAccept = await repo.SetRequestAccepted(channel.Id, Now.AddDays(10));
        Assert.That(secondAccept, Is.False);
        var stillReloaded = await repo.Load(channel.Id);
        Assert.That((stillReloaded.ExpiresAt.Value - Now.AddMinutes(5).AddDays(365)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "a no-op accept attempt must not touch ExpiresAt again");
    }

    [Test]
    public async Task LoadByPairKey_FindsExistingChannel_NullWhenAbsent()
    {
        var repo = new ChannelRepository(MongoClient);
        Assert.That(await repo.LoadByPairKey("Peter#123", "Wolf#456"), Is.Null);

        var created = await repo.FindOrCreateDm("Peter#123", "Wolf#456", "Peter#123", DmRequestState.Pending, Now);

        var found = await repo.LoadByPairKey("Wolf#456", "Peter#123"); // reversed order
        Assert.That(found, Is.Not.Null);
        Assert.That(found.Id, Is.EqualTo(created.Id));
    }

    [Test]
    public async Task Delete_RemovesTheChannelDoc()
    {
        var repo = new ChannelRepository(MongoClient);
        var channel = await repo.FindOrCreateDm("Peter#123", "Wolf#456", "Peter#123", DmRequestState.Accepted, Now);

        await repo.Delete(channel.Id);

        Assert.That(await repo.Load(channel.Id), Is.Null);
    }

    [Test]
    public void FindOrCreateDm_NullOrEmptyArgs_Throws()
    {
        var repo = new ChannelRepository(MongoClient);

        Assert.That(async () => await repo.FindOrCreateDm(null, "Wolf#456", "Wolf#456", DmRequestState.Pending, Now),
            Throws.InstanceOf<ArgumentException>());
        Assert.That(async () => await repo.FindOrCreateDm("Peter#123", "", "Peter#123", DmRequestState.Pending, Now),
            Throws.InstanceOf<ArgumentException>());
        Assert.That(async () => await repo.FindOrCreateDm("Peter#123", "Wolf#456", "  ", DmRequestState.Pending, Now),
            Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public async Task InsertIfAbsent_ConcurrentMaterialization_YieldsOneMembership()
    {
        var channelRepo = new ChannelRepository(MongoClient);
        var membershipRepo = new MembershipRepository(MongoClient, channelRepo);
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var channel = await channelRepo.FindOrCreateDm("Peter#123", "Wolf#456", "Peter#123", DmRequestState.Accepted, Now);

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => membershipRepo.InsertIfAbsent(new ChannelMembership
        {
            ChannelId = channel.Id,
            BattleTag = "Wolf#456",
            JoinedAt = Now,
        })));
        var results = await Task.WhenAll(tasks);

        Assert.That(results.Select(m => m.Id).Distinct().Count(), Is.EqualTo(1),
            "concurrent InsertIfAbsent calls for the same (channel, battleTag) must resolve to exactly one membership row");

        var loaded = await membershipRepo.LoadForChannel(channel.Id);
        Assert.That(loaded.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task InsertIfAbsent_SecondCall_ReturnsExistingRow_DoesNotOverwriteFields()
    {
        var channelRepo = new ChannelRepository(MongoClient);
        var membershipRepo = new MembershipRepository(MongoClient, channelRepo);
        var channel = await channelRepo.FindOrCreateDm("Peter#123", "Wolf#456", "Peter#123", DmRequestState.Accepted, Now);

        var first = await membershipRepo.InsertIfAbsent(new ChannelMembership
        {
            ChannelId = channel.Id,
            BattleTag = "Wolf#456",
            Role = MembershipRole.Owner,
            JoinedAt = Now,
        });
        var second = await membershipRepo.InsertIfAbsent(new ChannelMembership
        {
            ChannelId = channel.Id,
            BattleTag = "Wolf#456",
            Role = MembershipRole.Member, // must be ignored — the row already exists
            JoinedAt = Now.AddMinutes(5),
        });

        Assert.That(second.Id, Is.EqualTo(first.Id));
        Assert.That(second.Role, Is.EqualTo(MembershipRole.Owner), "the SECOND call must not overwrite the existing row's fields");
    }
}

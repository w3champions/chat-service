using System;
using System.Threading.Tasks;
using NUnit.Framework;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

public class CleanupJobsTests : IntegrationTestBase
{
    [Test]
    public async Task SemiPublicGc_DeletesOnlyChannelsWithNoMembersAndNoMessages()
    {
        var channelRepo = new ChannelRepository(MongoClient);
        var membershipRepo = new MembershipRepository(MongoClient, channelRepo);
        var messageRepo = new MessageRepository(MongoClient);

        var empty = new ChatChannel { Type = ChannelType.SemiPublic, Name = "dead", NormalizedName = "dead" };
        var withMember = new ChatChannel { Type = ChannelType.SemiPublic, Name = "member", NormalizedName = "member" };
        var withMessage = new ChatChannel { Type = ChannelType.SemiPublic, Name = "msg", NormalizedName = "msg" };
        var publicChannel = new ChatChannel { Type = ChannelType.Public, Name = "W3C Lounge", NormalizedName = "w3c lounge" };
        await channelRepo.Insert(empty);
        await channelRepo.Insert(withMember);
        await channelRepo.Insert(withMessage);
        await channelRepo.Insert(publicChannel);

        await membershipRepo.Insert(new ChannelMembership { ChannelId = withMember.Id, BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });
        await messageRepo.Insert(new ChannelMessage
        {
            ChannelId = withMessage.Id,
            Seq = 1,
            Sender = new MessageSender { BattleTag = "Peter#123", Name = "Peter" },
            Content = "still here",
            SentAt = DateTime.UtcNow,
        });

        var deleted = await new CleanupJobs(MongoClient).DeleteEmptySemiPublicChannels();

        Assert.AreEqual(1, deleted);
        Assert.IsNull(await channelRepo.Load(empty.Id));
        Assert.IsNotNull(await channelRepo.Load(withMember.Id));
        Assert.IsNotNull(await channelRepo.Load(withMessage.Id));
        Assert.IsNotNull(await channelRepo.Load(publicChannel.Id), "public channels are never GC'd");
    }

    [Test]
    public async Task IdleMembershipPruning_RemovesMembershipsOfUsersIdleOverOneYear()
    {
        var membershipRepo = new MembershipRepository(MongoClient, new ChannelRepository(MongoClient));
        var directoryRepo = new UserDirectoryRepository(MongoClient);
        var now = DateTime.UtcNow;

        await directoryRepo.Upsert(new UserDirectoryEntry { BattleTag = "Idle#1", NormalizedName = "idle#1", LastSeenAt = now.AddDays(-400) });
        await directoryRepo.Upsert(new UserDirectoryEntry { BattleTag = "Active#2", NormalizedName = "active#2", LastSeenAt = now.AddDays(-5) });
        await membershipRepo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "Idle#1", JoinedAt = now.AddDays(-500) });
        await membershipRepo.Insert(new ChannelMembership { ChannelId = "chan2", BattleTag = "Idle#1", JoinedAt = now.AddDays(-500) });
        await membershipRepo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "Active#2", JoinedAt = now.AddDays(-500) });
        await membershipRepo.Insert(new ChannelMembership { ChannelId = "chan1", BattleTag = "NoDirectoryEntry#3", JoinedAt = now.AddDays(-500) });

        var pruned = await new CleanupJobs(MongoClient).PruneIdleMemberships(now);

        Assert.AreEqual(2, pruned);
        Assert.AreEqual(0, (await membershipRepo.LoadForUser("Idle#1")).Count);
        Assert.AreEqual(1, (await membershipRepo.LoadForUser("Active#2")).Count);
        Assert.AreEqual(1, (await membershipRepo.LoadForUser("NoDirectoryEntry#3")).Count,
            "users unknown to the directory are conservatively kept");
    }
}

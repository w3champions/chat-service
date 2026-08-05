using System;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Driver;
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

    // Fix round 1 (F6): PruneIdleMemberships also sweeps the SAME idle users' NotificationPreference rows
    // — that carrier has no TTL of its own (PR36 D2 — deliberately durable across an ordinary leave/
    // rejoin), so this job is its ONLY GC path and must not let it outlive every other trace of the user.
    [Test]
    public async Task IdleMembershipPruning_AlsoRemovesNotificationPreferencesOfIdleUsers()
    {
        var directoryRepo = new UserDirectoryRepository(MongoClient);
        var prefsRepo = new NotificationPreferenceRepository(MongoClient);
        var now = DateTime.UtcNow;

        await directoryRepo.Upsert(new UserDirectoryEntry { BattleTag = "Idle#1", NormalizedName = "idle#1", LastSeenAt = now.AddDays(-400) });
        await directoryRepo.Upsert(new UserDirectoryEntry { BattleTag = "Active#2", NormalizedName = "active#2", LastSeenAt = now.AddDays(-5) });
        await prefsRepo.Upsert("Idle#1", "chan1", NotificationLevel.None, now.AddDays(-500));
        await prefsRepo.Upsert("Active#2", "chan1", NotificationLevel.None, now.AddDays(-500));

        await new CleanupJobs(MongoClient).PruneIdleMemberships(now);

        Assert.IsNull(await prefsRepo.Load("Idle#1", "chan1"), "the idle user's persisted preference must be pruned too");
        Assert.IsNotNull(await prefsRepo.Load("Active#2", "chan1"), "the active user's preference must survive");
    }

    // ---------------------------------------------------------------------------------------------
    // Match-channel-hygiene brief (2026-08-05), Part 2 — orphan sweep: membership rows referencing a
    // channel id that no longer exists in the channels collection (TTL'd System match channel, or a lost
    // mm→chat member-removal) are DB-hygiene GC'd on the same weekly cadence, catching users who never
    // reconnect (the connect-time self-heal, Part 1, only fires for someone who DOES reconnect).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task OrphanSweep_DeletesOnlyMembershipsOfMissingChannels_AcrossUsersAndTypes()
    {
        var channelRepo = new ChannelRepository(MongoClient);
        var membershipRepo = new MembershipRepository(MongoClient, channelRepo);

        var publicChannel = new ChatChannel { Type = ChannelType.Public, Name = "W3C Lounge", NormalizedName = "w3c lounge" };
        var systemChannel = new ChatChannel { Type = ChannelType.System, SystemKind = SystemChannelKind.Match, SystemRef = "match-1", Name = "Match 1" };
        await channelRepo.Insert(publicChannel);
        await channelRepo.Insert(systemChannel);

        // Live memberships — their channel docs exist, must survive the sweep untouched.
        await membershipRepo.Insert(new ChannelMembership { ChannelId = publicChannel.Id, BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });
        await membershipRepo.Insert(new ChannelMembership { ChannelId = systemChannel.Id, BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });

        // Orphaned memberships — channel ids with NO matching channel document at all (TTL'd/deleted),
        // spanning two different users, one of the ids shared by both (mirrors a stale System match
        // channel two participants were both still a member of).
        await membershipRepo.Insert(new ChannelMembership { ChannelId = "gone-match-channel", BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });
        await membershipRepo.Insert(new ChannelMembership { ChannelId = "gone-match-channel", BattleTag = "Wolf#456", JoinedAt = DateTime.UtcNow });
        await membershipRepo.Insert(new ChannelMembership { ChannelId = "gone-public-channel", BattleTag = "Wolf#456", JoinedAt = DateTime.UtcNow });

        var deleted = await new CleanupJobs(MongoClient).SweepOrphanedMemberships();

        Assert.AreEqual(3, deleted);
        var peterChannelIds = (await membershipRepo.LoadForUser("Peter#123")).Select(m => m.ChannelId).ToList();
        CollectionAssert.AreEquivalent(new[] { publicChannel.Id, systemChannel.Id }, peterChannelIds,
            "Peter keeps only the two memberships whose channels still exist");
        Assert.AreEqual(0, (await membershipRepo.LoadForUser("Wolf#456")).Count,
            "Wolf's only two memberships were both orphaned");
    }

    [Test]
    public async Task OrphanSweep_ProcessesMoreThanOneBatch_AndDeletesAllOrphansAcrossBatches()
    {
        var channelRepo = new ChannelRepository(MongoClient);
        var membershipRepo = new MembershipRepository(MongoClient, channelRepo);
        var db = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        var membershipsCollection = db.GetCollection<ChannelMembership>(ChatCollections.ChannelMemberships);

        var liveChannel = new ChatChannel { Type = ChannelType.Public, Name = "W3C Lounge", NormalizedName = "w3c lounge" };
        await channelRepo.Insert(liveChannel);
        await membershipRepo.Insert(new ChannelMembership { ChannelId = liveChannel.Id, BattleTag = "Peter#123", JoinedAt = DateTime.UtcNow });

        // Strictly more distinct orphaned channel ids than a single OrphanSweepBatchSize round, so the
        // sweep must loop across at least two batches to reap every one of them — one membership row per
        // distinct (nonexistent) channel id, each for a different user so ux_channelId_battleTag never
        // collides.
        var orphanCount = CleanupJobs.OrphanSweepBatchSize + 1;
        var orphanRows = Enumerable.Range(0, orphanCount)
            .Select(i => new ChannelMembership { ChannelId = $"gone-{i}", BattleTag = $"orphan{i}#1", JoinedAt = DateTime.UtcNow })
            .ToList();
        await membershipsCollection.InsertManyAsync(orphanRows);

        var deleted = await new CleanupJobs(MongoClient).SweepOrphanedMemberships();

        Assert.AreEqual(orphanCount, deleted, "every orphan must be reaped, not just the first batch's worth");
        Assert.AreEqual(0L, await membershipsCollection.CountDocumentsAsync(m => m.ChannelId != liveChannel.Id),
            "no orphaned row may survive across the batch boundary");
        Assert.IsNotNull(await membershipRepo.Load(liveChannel.Id, "Peter#123"), "the live membership must survive untouched");
    }

    // Fix round 1 (finding F7): the multi-batch test above only ever exercises the ORPHAN half of the
    // per-page existence-check $in — every id in its batches is missing. This test crosses the SAME
    // pagination boundary with EXISTING channels instead, so the existence check itself is proven
    // correct at full OrphanSweepBatchSize width, not merely "an empty $in never matches".
    [Test]
    public async Task OrphanSweep_ExistingChannelsAtFullBatchWidth_AllSurvive_OnlyTheGenuineOrphanIsDeleted()
    {
        var db = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        var channelsCollection = db.GetCollection<ChatChannel>(ChatCollections.Channels);
        var membershipsCollection = db.GetCollection<ChannelMembership>(ChatCollections.ChannelMemberships);
        var membershipRepo = new MembershipRepository(MongoClient, new ChannelRepository(MongoClient));

        // Strictly more than one batch's worth of EXISTING channels, each with exactly one membership —
        // a discovery page's distinct-ChannelId batch composed ENTIRELY (or almost entirely) of ids that
        // must all resolve "exists" via the existence-check $in, at full OrphanSweepBatchSize width.
        var liveCount = CleanupJobs.OrphanSweepBatchSize + 1;
        var liveChannels = Enumerable.Range(0, liveCount)
            .Select(i => new ChatChannel { Type = ChannelType.SemiPublic, Name = $"room-{i}", NormalizedName = $"room-{i}" })
            .ToList();
        await channelsCollection.InsertManyAsync(liveChannels);
        var liveMemberships = liveChannels
            .Select((c, i) => new ChannelMembership { ChannelId = c.Id, BattleTag = $"liveuser{i}#1", JoinedAt = DateTime.UtcNow })
            .ToList();
        await membershipsCollection.InsertManyAsync(liveMemberships);

        // One genuine orphan alongside the live set, so the sweep still does REAL work in the same run —
        // not merely a no-op pass over an all-live dataset.
        await membershipRepo.Insert(new ChannelMembership { ChannelId = "gone-channel", BattleTag = "orphan#1", JoinedAt = DateTime.UtcNow });

        var deleted = await new CleanupJobs(MongoClient).SweepOrphanedMemberships();

        Assert.AreEqual(1, deleted, "only the single genuine orphan is deleted — none of the live channels' memberships");
        var liveChannelIds = liveChannels.Select(c => c.Id).ToList();
        Assert.AreEqual((long)liveCount, await membershipsCollection.CountDocumentsAsync(
            Builders<ChannelMembership>.Filter.In(m => m.ChannelId, liveChannelIds)),
            "every live channel's membership must survive — the existence check must resolve all of them correctly even at full batch width");
        Assert.IsNull(await membershipRepo.Load("gone-channel", "orphan#1"), "the genuine orphan must still be reaped");
    }

    // Fix round 1 (finding F3): RunOnce's wiring of the orphan sweep was untested — deleting the
    // SweepOrphanedMemberships() call from RunOnce would survive the full suite (only the method in
    // isolation was pinned). This proves RunOnce itself reaps an orphan, not just the method directly.
    [Test]
    public async Task RunOnce_AlsoSweepsOrphanedMemberships()
    {
        var channelRepo = new ChannelRepository(MongoClient);
        var membershipRepo = new MembershipRepository(MongoClient, channelRepo);
        var now = DateTime.UtcNow;

        var liveChannel = new ChatChannel { Type = ChannelType.Public, Name = "W3C Lounge", NormalizedName = "w3c lounge" };
        await channelRepo.Insert(liveChannel);
        await membershipRepo.Insert(new ChannelMembership { ChannelId = liveChannel.Id, BattleTag = "Peter#123", JoinedAt = now });
        await membershipRepo.Insert(new ChannelMembership { ChannelId = "gone-channel", BattleTag = "Peter#123", JoinedAt = now });

        await new CleanupJobs(MongoClient).RunOnce(now);

        Assert.IsNull(await membershipRepo.Load("gone-channel", "Peter#123"),
            "RunOnce must invoke the orphan sweep — an undeleted orphaned row here means the wiring, not just the method, regressed");
        Assert.IsNotNull(await membershipRepo.Load(liveChannel.Id, "Peter#123"), "the live membership must survive RunOnce");
    }
}

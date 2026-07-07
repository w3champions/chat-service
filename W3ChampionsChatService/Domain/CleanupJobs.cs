using System;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Domain;

/// <summary>
/// Weekly maintenance (driven by WeeklyCleanupService):
/// (a) semi-public channel GC — a semiPublic channel with zero memberships AND zero stored
///     messages is dead (message TTL already removed anything older than retention);
/// (b) membership pruning for users idle &gt; 1 year per user_directory.lastSeenAt.
/// Group/DM shells need no job — their ExpiresAt TTL handles them.
/// </summary>
public class CleanupJobs(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient)
{
    public async Task RunOnce(DateTime now)
    {
        await DeleteEmptySemiPublicChannels();
        await PruneIdleMemberships(now);
    }

    public async Task<long> DeleteEmptySemiPublicChannels()
    {
        var db = CreateClient();
        var channels = db.GetCollection<ChatChannel>(ChatCollections.Channels);
        var memberships = db.GetCollection<ChannelMembership>(ChatCollections.ChannelMemberships);
        var messages = db.GetCollection<ChannelMessage>(ChatCollections.Messages);

        var semiPublicChannels = await channels.Find(c => c.Type == ChannelType.SemiPublic).ToListAsync();
        long deleted = 0;
        foreach (var channel in semiPublicChannels)
        {
            var hasMembers = await memberships.Find(m => m.ChannelId == channel.Id).AnyAsync();
            if (hasMembers) continue;
            var hasMessages = await messages.Find(m => m.ChannelId == channel.Id).AnyAsync();
            if (hasMessages) continue;

            var result = await channels.DeleteOneAsync(c => c.Id == channel.Id);
            deleted += result.DeletedCount;
        }

        return deleted;
    }

    public async Task<long> PruneIdleMemberships(DateTime now)
    {
        var db = CreateClient();
        var directory = db.GetCollection<UserDirectoryEntry>(ChatCollections.UserDirectory);
        var memberships = db.GetCollection<ChannelMembership>(ChatCollections.ChannelMemberships);

        var cutoff = now - RetentionPeriods.IdleMembership;
        var idleBattleTags = await directory
            .Find(e => e.LastSeenAt < cutoff)
            .Project(e => e.BattleTag)
            .ToListAsync();
        if (idleBattleTags.Count == 0) return 0;

        // Both stores key on battleTag, and both now store it LOWERCASED: the durable membership key
        // (C5 T4 — see MembershipRepository's class doc) and, since C6 T2's D8 re-keying, the directory
        // _id/BattleTag too (the original JWT casing now lives on UserDirectoryEntry.DisplayBattleTag, not
        // on the projected BattleTag). The projected idle tags are therefore already lowercased and this
        // .ToLowerInvariant() is a no-op on them — kept as a defensive boundary normalization so a legacy
        // pre-D8 (mixed-case) directory _id still lingering in the collection (entries are kept, never
        // TTL'd) can't silently match no membership row and no-op the prune.
        var idleMembershipKeys = idleBattleTags.Select(t => t.ToLowerInvariant()).ToList();
        var result = await memberships.DeleteManyAsync(
            Builders<ChannelMembership>.Filter.In(m => m.BattleTag, idleMembershipKeys));
        return result.DeletedCount;
    }
}

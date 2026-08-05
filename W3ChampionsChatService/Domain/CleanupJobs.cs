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
/// (b) membership pruning for users idle &gt; 1 year per user_directory.lastSeenAt — fix round 1 (F6)
///     extends this to ALSO delete the same idle users' <see cref="Memberships.NotificationPreference"/>
///     rows, so that carrier doesn't outlive the membership it was written to survive. The pref
///     collection has no TTL of its own (PR36 D2 — it must persist indefinitely across an ordinary
///     leave/rejoin), so this job is its ONLY GC path.
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
        var notificationPreferences = db.GetCollection<NotificationPreference>(ChatCollections.NotificationPreferences);

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
        // TTL'd) can't silently match no membership row and no-op the prune. NotificationPreference stores
        // its BattleTag lowercased too (see that repository's class doc), so the SAME key list serves it.
        var idleMembershipKeys = idleBattleTags.Select(t => t.ToLowerInvariant()).ToList();
        var result = await memberships.DeleteManyAsync(
            Builders<ChannelMembership>.Filter.In(m => m.BattleTag, idleMembershipKeys));

        // Fix round 1 (F6): the persisted NotificationPreference carrier (PR36 D2) has no TTL of its own —
        // it is deliberately durable across an ordinary leave/rejoin — so it must be swept here alongside
        // the membership rows of the SAME idle users, or it would outlive every other trace of them.
        await notificationPreferences.DeleteManyAsync(
            Builders<NotificationPreference>.Filter.In(p => p.BattleTag, idleMembershipKeys));

        return result.DeletedCount;
    }
}

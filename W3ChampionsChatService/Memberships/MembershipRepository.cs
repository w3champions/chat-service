using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Memberships;

// CS9107: mongoClient is intentionally re-used below to compose ChannelRepository for
// CountNameJoinableMembershipsForUser's LoadByIds reuse — MongoClient is a cheap, thread-safe
// connection-pool handle, safe to hold via both this class and the base.
#pragma warning disable CS9107
public class MembershipRepository(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient)
#pragma warning restore CS9107
{
    private IMongoCollection<ChannelMembership> Memberships =>
        CreateCollection<ChannelMembership>(ChatCollections.ChannelMemberships);

    public Task Insert(ChannelMembership membership) => Memberships.InsertOneAsync(membership);

    public Task<ChannelMembership> Load(string channelId, string battleTag) =>
        Memberships.Find(m => m.ChannelId == channelId && m.BattleTag == battleTag).FirstOrDefaultAsync();

    public Task<List<ChannelMembership>> LoadForUser(string battleTag) =>
        Memberships.Find(m => m.BattleTag == battleTag).ToListAsync();

    public Task Delete(string channelId, string battleTag) =>
        Memberships.DeleteOneAsync(m => m.ChannelId == channelId && m.BattleTag == battleTag);

    /// <summary>Monotonic read-state advance ($max) — a lower/stale seq from an out-of-order
    /// or duplicate MarkRead call never regresses LastReadSeq.</summary>
    public Task UpdateLastReadSeq(string channelId, string battleTag, long seq) =>
        Memberships.UpdateOneAsync(
            m => m.ChannelId == channelId && m.BattleTag == battleTag,
            Builders<ChannelMembership>.Update.Max(m => m.LastReadSeq, seq));

    public Task SetNotificationLevel(string channelId, string battleTag, NotificationLevel level) =>
        Memberships.UpdateOneAsync(
            m => m.ChannelId == channelId && m.BattleTag == battleTag,
            Builders<ChannelMembership>.Update.Set(m => m.NotificationLevel, level));

    /// <summary>
    /// Membership cap gate (acceptance 10) — counts only name-joinable (Public + SemiPublic)
    /// memberships; System/Dm/GroupDm never count against the cap. KISS at realistic
    /// per-user scale (a user's channel count is bounded, in practice far under 50):
    /// LoadForUser + ChannelRepository.LoadByIds type filter, no new aggregation pipeline.
    /// </summary>
    public async Task<int> CountNameJoinableMembershipsForUser(string battleTag)
    {
        var memberships = await LoadForUser(battleTag);
        if (memberships.Count == 0) return 0;

        // Composes ChannelRepository so the type filter reuses LoadByIds rather than
        // duplicating its query (see CS9107 suppression note on the class declaration).
        var channelRepository = new ChannelRepository(mongoClient);
        var channels = await channelRepository.LoadByIds(memberships.Select(m => m.ChannelId));
        var nameJoinableChannelIds = channels
            .Where(c => c.Type == ChannelType.Public || c.Type == ChannelType.SemiPublic)
            .Select(c => c.Id)
            .ToHashSet();

        return memberships.Count(m => nameJoinableChannelIds.Contains(m.ChannelId));
    }
}

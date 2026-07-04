using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Memberships;

public class MembershipRepository(MongoClient mongoClient, ChannelRepository channelRepository) : MongoDbRepositoryBase(mongoClient)
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

        // Reuses the injected ChannelRepository so the type filter reuses LoadByIds rather than
        // duplicating its query.
        var channels = await channelRepository.LoadByIds(memberships.Select(m => m.ChannelId));
        var nameJoinableChannelIds = channels
            .Where(c => c.Type == ChannelType.Public || c.Type == ChannelType.SemiPublic)
            .Select(c => c.Id)
            .ToHashSet();

        return memberships.Count(m => nameJoinableChannelIds.Contains(m.ChannelId));
    }

    /// <summary>All memberships of one channel (C5 D12) — legitimate here: the never-enumerate-
    /// channel→users guardrail on <see cref="ChannelMembership"/> is about PUBLIC channels; groups
    /// are ACL-bound and capped at <see cref="ChatLimits.MaxGroupSize"/>, so enumerating a group's
    /// members (roster, owner lookups, auto-promotion) is the intended access pattern.</summary>
    public Task<List<ChannelMembership>> LoadForChannel(string channelId) =>
        Memberships.Find(m => m.ChannelId == channelId).ToListAsync();

    /// <summary>Member count for a single channel (C5 D12 — group size bounds, last-member-leaves
    /// detection). Uses the same ux_channelId_battleTag-backed collection scan as
    /// <see cref="LoadForChannel"/> but returns a bare count.</summary>
    public async Task<int> CountForChannel(string channelId) =>
        (int)await Memberships.CountDocumentsAsync(m => m.ChannelId == channelId);

    public Task SetRole(string channelId, string battleTag, MembershipRole role) =>
        Memberships.UpdateOneAsync(
            m => m.ChannelId == channelId && m.BattleTag == battleTag,
            Builders<ChannelMembership>.Update.Set(m => m.Role, role));

    /// <summary>C5 D3: stamps the RECIPIENT's own decline-suppression window. Never touches the
    /// channel doc or any other member's row.</summary>
    public Task SetDeclinedUntil(string channelId, string battleTag, DateTime declinedUntil) =>
        Memberships.UpdateOneAsync(
            m => m.ChannelId == channelId && m.BattleTag == battleTag,
            Builders<ChannelMembership>.Update.Set(m => m.DeclinedUntil, declinedUntil));

    /// <summary>C5 D3/T4: clears a resolved decline window — called when the suppression period has
    /// elapsed and a fresh request is about to resurface, or when the conversation is accepted.</summary>
    public Task ClearDeclinedUntil(string channelId, string battleTag) =>
        Memberships.UpdateOneAsync(
            m => m.ChannelId == channelId && m.BattleTag == battleTag,
            Builders<ChannelMembership>.Update.Unset(m => m.DeclinedUntil));

    /// <summary>Residual-row cleanup when a channel is deleted (C5 D12 — last group member leaves).</summary>
    public Task DeleteAllForChannel(string channelId) =>
        Memberships.DeleteManyAsync(m => m.ChannelId == channelId);

    /// <summary>
    /// Idempotent membership upsert (C5 T2) — mirrors <see cref="Channels.ChannelRepository.FindOrCreateSemiPublic"/>'s
    /// $setOnInsert-upsert + duplicate-key-retry-once idiom, backed by the unique
    /// <c>ux_channelId_battleTag</c> index. Used for lazy recipient materialization (a DM's recipient
    /// membership is created on first successfully-delivered message, D4) where a genuine race —
    /// e.g. two concurrent sends both trying to materialize the same recipient — must resolve to
    /// exactly one row rather than surfacing a raw duplicate-key write exception.
    /// </summary>
    public async Task<ChannelMembership> InsertIfAbsent(ChannelMembership membership)
    {
        var filter = Builders<ChannelMembership>.Filter.Where(m =>
            m.ChannelId == membership.ChannelId && m.BattleTag == membership.BattleTag);
        var update = Builders<ChannelMembership>.Update
            .SetOnInsert(m => m.Id, membership.Id ?? ObjectId.GenerateNewId().ToString())
            .SetOnInsert(m => m.ChannelId, membership.ChannelId)
            .SetOnInsert(m => m.BattleTag, membership.BattleTag)
            .SetOnInsert(m => m.Role, membership.Role)
            .SetOnInsert(m => m.NotificationLevel, membership.NotificationLevel)
            .SetOnInsert(m => m.LastReadSeq, membership.LastReadSeq)
            .SetOnInsert(m => m.JoinedAt, membership.JoinedAt);
        var options = new FindOneAndUpdateOptions<ChannelMembership>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After,
        };

        try
        {
            return await Memberships.FindOneAndUpdateAsync(filter, update, options);
        }
        catch (MongoCommandException ex) when (IsDuplicateKey(ex))
        {
            return await Memberships.FindOneAndUpdateAsync(filter, update, options);
        }
    }

    private static bool IsDuplicateKey(MongoCommandException ex) => ex.Code == 11000;
}

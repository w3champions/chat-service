using System;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Memberships;

/// <summary>
/// PR36 follow-up (D2): durable (battleTag, channelId) → last-explicitly-set <see cref="NotificationLevel"/>
/// store — see <see cref="NotificationPreference"/>'s class doc for the write/seed/consult wiring.
/// <para>
/// BATTLETAG KEY CONVENTION: mirrors <see cref="MembershipRepository"/> and
/// <see cref="Mentions.MentionInboxRepository"/> — the persisted <see cref="NotificationPreference.BattleTag"/>
/// is ALWAYS stored lowercased, and every read/write below lowercases its incoming <c>battleTag</c>
/// argument before building the Mongo filter, so a caller may pass the JWT-cased identity battleTag
/// straight through.
/// </para>
/// </summary>
public class NotificationPreferenceRepository(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient)
{
    private IMongoCollection<NotificationPreference> Prefs =>
        CreateCollection<NotificationPreference>(ChatCollections.NotificationPreferences);

    /// <summary>Lowercases a battleTag to the durable notification-preference key convention (see the class doc).</summary>
    private static string NormalizeTag(string battleTag) => battleTag.ToLowerInvariant();

    /// <summary>The last explicitly-set level for (battleTag, channelId), or null if one was never set
    /// (or was set and the collection has since been pruned — callers must treat null as "no opinion",
    /// never as an implicit None).</summary>
    public Task<NotificationPreference> Load(string battleTag, string channelId)
    {
        var tag = NormalizeTag(battleTag);
        return Prefs.Find(p => p.BattleTag == tag && p.ChannelId == channelId).FirstOrDefaultAsync();
    }

    /// <summary>
    /// Idempotent last-write-wins upsert — <c>ChatHub.SetNotificationLevel</c>'s sole write path. Backed
    /// by the unique <c>ux_battleTag_channelId</c> index (<see cref="Domain.ChatDomainIndexes"/>), so a
    /// repeated set for the same (battleTag, channelId) overwrites the existing row rather than
    /// accumulating duplicates.
    /// <para>
    /// Fix round 1 (F1): a plain <c>$setOnInsert</c> + <c>IsUpsert</c> races against the unique index —
    /// two callers upserting the same not-yet-existing (battleTag, channelId) at the same instant can
    /// make the LOSING call's insert half violate <c>ux_battleTag_channelId</c>. Mirrors
    /// <see cref="MembershipRepository.InsertIfAbsent"/> / <c>ChannelRepository.FindOrCreate*</c>'s
    /// established fix for this exact race: use the findAndModify form (<c>FindOneAndUpdateAsync</c>,
    /// NOT <c>UpdateOneAsync</c>) wrapped in <see cref="MongoDbRepositoryBase.RetryOnceOnDuplicateKey{T}"/>
    /// — that helper only catches the <see cref="MongoCommandException"/>{Code==11000} shape findAndModify
    /// throws, not the <see cref="MongoWriteException"/> a plain <c>UpdateOneAsync</c> would throw instead,
    /// so the write MUST go through <c>FindOneAndUpdateAsync</c> for the retry to actually catch the race.
    /// </para>
    /// </summary>
    // virtual: a test seam (mirroring MembershipRepository.LoadForChannel / MentionInboxRepository.Insert)
    // so a test double can simulate a write failure here — exercises the ChatHub.SetNotificationLevel
    // best-effort posture (fix round 1, F5) without needing a real Mongo fault.
    public virtual Task<NotificationPreference> Upsert(string battleTag, string channelId, NotificationLevel level, DateTime now)
    {
        var tag = NormalizeTag(battleTag);
        var filter = Builders<NotificationPreference>.Filter.Where(p => p.BattleTag == tag && p.ChannelId == channelId);
        var update = Builders<NotificationPreference>.Update
            .Set(p => p.NotificationLevel, level)
            .Set(p => p.UpdatedAt, now)
            .SetOnInsert(p => p.BattleTag, tag)
            .SetOnInsert(p => p.ChannelId, channelId)
            // Explicit string _id on insert (mirrors MembershipRepository.InsertIfAbsent) — an upsert
            // that never names _id would otherwise let Mongo assign its own native ObjectId, which the
            // string-typed Id property here cannot deserialize back (a BsonSerializationException on the
            // very next Load).
            .SetOnInsert(p => p.Id, ObjectId.GenerateNewId().ToString());
        var options = new FindOneAndUpdateOptions<NotificationPreference> { IsUpsert = true };

        return RetryOnceOnDuplicateKey(() => Prefs.FindOneAndUpdateAsync(filter, update, options));
    }
}

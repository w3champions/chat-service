using System;
using System.Threading.Tasks;
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
    /// </summary>
    public Task Upsert(string battleTag, string channelId, NotificationLevel level, DateTime now)
    {
        var tag = NormalizeTag(battleTag);
        var filter = Builders<NotificationPreference>.Filter.Where(p => p.BattleTag == tag && p.ChannelId == channelId);
        var update = Builders<NotificationPreference>.Update
            .Set(p => p.NotificationLevel, level)
            .Set(p => p.UpdatedAt, now)
            .SetOnInsert(p => p.BattleTag, tag)
            .SetOnInsert(p => p.ChannelId, channelId);

        return Prefs.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    }
}

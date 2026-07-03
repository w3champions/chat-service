using System;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Memberships;

namespace W3ChampionsChatService.Domain;

/// <summary>
/// Code-managed index definitions for all chat domain collections. Called at startup
/// (ChatDomainBootstrap) and from tests. Idempotent: deterministic names + CreateManyAsync
/// no-ops when an identical index already exists.
/// TTL convention: expireAfterSeconds 0 — ExpiresAt IS the absolute expiry instant, and the
/// field is omitted entirely (BsonIgnoreIfNull) on permanent documents.
/// </summary>
public static class ChatDomainIndexes
{
    public static async Task EnsureAllAsync(MongoClient mongoClient)
    {
        var db = mongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        await EnsureChannelIndexes(db);
        await EnsureMembershipIndexes(db);
        // Tasks 5/7 extend: messages, mention_inbox
    }

    private static async Task EnsureChannelIndexes(IMongoDatabase db)
    {
        var channels = db.GetCollection<ChatChannel>(ChatCollections.Channels);
        await channels.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<ChatChannel>(
                Builders<ChatChannel>.IndexKeys.Ascending(c => c.PairKey),
                new CreateIndexOptions<ChatChannel>
                {
                    Name = "ux_pairKey_dm",
                    Unique = true,
                    PartialFilterExpression = Builders<ChatChannel>.Filter.Eq(c => c.Type, ChannelType.Dm),
                }),
            new CreateIndexModel<ChatChannel>(
                Builders<ChatChannel>.IndexKeys.Ascending(c => c.ExpiresAt),
                new CreateIndexOptions { Name = "ttl_expiresAt", ExpireAfter = TimeSpan.Zero }),
        ]);
    }

    private static async Task EnsureMembershipIndexes(IMongoDatabase db)
    {
        var memberships = db.GetCollection<ChannelMembership>(ChatCollections.ChannelMemberships);
        await memberships.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<ChannelMembership>(
                Builders<ChannelMembership>.IndexKeys.Ascending(m => m.ChannelId).Ascending(m => m.BattleTag),
                new CreateIndexOptions { Name = "ux_channelId_battleTag", Unique = true }),
            new CreateIndexModel<ChannelMembership>(
                Builders<ChannelMembership>.IndexKeys.Ascending(m => m.BattleTag),
                new CreateIndexOptions { Name = "ix_battleTag" }),
        ]);
    }
}

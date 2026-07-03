using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Messages;

public class MessageRepository(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient)
{
    private IMongoCollection<ChannelMessage> Messages =>
        CreateCollection<ChannelMessage>(ChatCollections.Messages);

    public Task Insert(ChannelMessage message) => Messages.InsertOneAsync(message);

    public Task<ChannelMessage> Load(string id) => Messages.Find(m => m.Id == id).FirstOrDefaultAsync();

    /// <summary>
    /// User-facing visibility: soft-deleted messages excluded; shadow messages visible
    /// only to their author. ({Deleted: null} also matches documents without the field.)
    /// </summary>
    public static FilterDefinition<ChannelMessage> UserVisible(string channelId, string viewerBattleTag)
    {
        var filter = Builders<ChannelMessage>.Filter;
        return filter.And(
            filter.Eq(m => m.ChannelId, channelId),
            filter.Eq(m => m.Deleted, null),
            filter.Or(
                filter.Eq(m => m.Shadow, false),
                filter.Eq(m => m.Sender.BattleTag, viewerBattleTag)));
    }

    public Task<List<ChannelMessage>> LoadForUser(string channelId, string viewerBattleTag) =>
        Messages.Find(UserVisible(channelId, viewerBattleTag)).SortBy(m => m.Seq).ToListAsync();

    /// <summary>Moderator view: deleted and shadow messages included, flags intact.</summary>
    public Task<List<ChannelMessage>> LoadForModerator(string channelId) =>
        Messages.Find(m => m.ChannelId == channelId).SortBy(m => m.Seq).ToListAsync();

    /// <summary>Soft delete: sets deleted{by,at}. Physical removal happens ONLY via TTL.</summary>
    public Task MarkDeleted(string messageId, string deletedBy, DateTime deletedAt) =>
        Messages.UpdateOneAsync(
            m => m.Id == messageId,
            Builders<ChannelMessage>.Update.Set(m => m.Deleted, new MessageDeletion { By = deletedBy, At = deletedAt }));
}

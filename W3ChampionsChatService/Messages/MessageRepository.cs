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

    /// <summary>
    /// Newest-first page strictly older than <paramref name="beforeSeq"/> (null = latest page),
    /// returned in ascending seq order. Seq-anchored: paging with the previous page's minimum seq
    /// is immune to concurrent appends — a later insert can never gap or dupe an already-fetched page.
    /// </summary>
    public async Task<List<ChannelMessage>> LoadPageBefore(string channelId, string viewerBattleTag, long? beforeSeq, int limit)
    {
        var effectiveLimit = ClampLimit(limit);
        var filterBuilder = Builders<ChannelMessage>.Filter;
        var filter = UserVisible(channelId, viewerBattleTag);
        if (beforeSeq.HasValue)
        {
            filter = filterBuilder.And(filter, filterBuilder.Lt(m => m.Seq, beforeSeq.Value));
        }

        var page = await Messages.Find(filter).SortByDescending(m => m.Seq).Limit(effectiveLimit).ToListAsync();
        page.Reverse();
        return page;
    }

    /// <summary>
    /// Window centered on <paramref name="aroundSeq"/>: up to limit/2 messages strictly before it,
    /// plus the target (if visible) and up to limit/2 after — two queries, merged ascending.
    /// </summary>
    public async Task<List<ChannelMessage>> LoadPageAround(string channelId, string viewerBattleTag, long aroundSeq, int limit)
    {
        var effectiveLimit = ClampLimit(limit);
        var half = effectiveLimit / 2;
        var filterBuilder = Builders<ChannelMessage>.Filter;
        var baseFilter = UserVisible(channelId, viewerBattleTag);

        var before = new List<ChannelMessage>();
        if (half > 0)
        {
            before = await Messages
                .Find(filterBuilder.And(baseFilter, filterBuilder.Lt(m => m.Seq, aroundSeq)))
                .SortByDescending(m => m.Seq)
                .Limit(half)
                .ToListAsync();
            before.Reverse();
        }

        var targetAndAfter = await Messages
            .Find(filterBuilder.And(baseFilter, filterBuilder.Gte(m => m.Seq, aroundSeq)))
            .SortBy(m => m.Seq)
            .Limit(half + 1)
            .ToListAsync();

        before.AddRange(targetAndAfter);
        return before;
    }

    /// <summary>
    /// Requested limits above the page-size cap are clamped down, never rejected. The lower
    /// bound of 1 is load-bearing: MongoDB.Driver's <c>.Limit(0)</c> means "no limit" (returns
    /// every matching document), so a limit of 0 must never reach <c>.Limit()</c> unchanged.
    /// Do not simplify this to <see cref="Math.Min"/> — that would silently reintroduce the
    /// unbounded-return footgun for a caller-supplied limit of 0 or less.
    /// </summary>
    private static int ClampLimit(int limit) => Math.Clamp(limit, 1, ChatLimits.MessagePageSize);
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Messages;

/// <summary>(Id, ChannelId) projection for a moderator bulk-purge target list (D6, consumed by a later C4 task).</summary>
public record PurgeTarget(string Id, string ChannelId);

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

    /// <summary>
    /// Soft delete: sets deleted{by,at}. Physical removal happens ONLY via TTL. The write is
    /// CONDITIONAL on <c>Deleted == null</c> (C4 Task 4 directive (a)) so a concurrent double-delete
    /// can never overwrite the first moderator's attribution nor re-fire the caller's downstream
    /// side-effects (cleanup/event/audit) — closing the load-then-write TOCTOU. Returns <c>true</c>
    /// iff this call actually flipped the row (<c>ModifiedCount == 1</c>); <c>false</c> means the row
    /// was already deleted (or vanished), and the caller must treat it as an idempotent no-op.
    /// </summary>
    public async Task<bool> MarkDeleted(string messageId, string deletedBy, DateTime deletedAt)
    {
        var filter = Builders<ChannelMessage>.Filter.And(
            Builders<ChannelMessage>.Filter.Eq(m => m.Id, messageId),
            Builders<ChannelMessage>.Filter.Eq(m => m.Deleted, null));
        var update = Builders<ChannelMessage>.Update.Set(m => m.Deleted, new MessageDeletion { By = deletedBy, At = deletedAt });

        var result = await Messages.UpdateOneAsync(filter, update);
        return result.ModifiedCount == 1;
    }

    /// <summary>
    /// D6 bulk soft delete (the moderator purge): sets deleted{by,at} in ONE write on every id in
    /// <paramref name="messageIds"/>. The filter is CONDITIONAL on <c>Deleted == null</c> (C4 Task 4
    /// directive (a)) so a re-purge only newly-deletes (never overwriting a prior attribution), and the
    /// returned <c>ModifiedCount</c> is the ACTUAL number of rows this call flipped — the count the
    /// caller's audit line and UI feedback are based on. Documents not in the id list, already-deleted
    /// rows, and every OTHER field on the matched documents (notably ExpiresAt/TTL) are left untouched —
    /// physical removal stays TTL-only, exactly like the single-message <see cref="MarkDeleted"/>.
    /// </summary>
    public async Task<long> MarkDeletedMany(IReadOnlyCollection<string> messageIds, string deletedBy, DateTime deletedAt)
    {
        var filter = Builders<ChannelMessage>.Filter.And(
            Builders<ChannelMessage>.Filter.In(m => m.Id, messageIds),
            Builders<ChannelMessage>.Filter.Eq(m => m.Deleted, null));
        var update = Builders<ChannelMessage>.Update.Set(m => m.Deleted, new MessageDeletion { By = deletedBy, At = deletedAt });

        var result = await Messages.UpdateManyAsync(filter, update);
        return result.ModifiedCount;
    }

    /// <summary>
    /// D6 purge query (a later C4 task's moderator purge): every NON-deleted row sent by
    /// <paramref name="battleTag"/>, projected to just (Id, ChannelId) — already-deleted rows are
    /// excluded so re-running a purge is idempotent. Runs under
    /// <see cref="ChatDomainIndexes.SenderCaseInsensitiveCollation"/> so a mixed-case argument still
    /// matches the stored sender casing — fixing the legacy case-SENSITIVE bug in
    /// <c>Chats/History.cs</c>'s <c>DeleteMessagesFromUser</c> (plain <c>==</c> comparison). The
    /// collation MUST match <c>ix_sender_ci_sentAt</c> exactly or Mongo silently collection-scans
    /// instead of using it.
    /// </summary>
    public Task<List<PurgeTarget>> LoadPurgeableBySender(string battleTag)
    {
        var filterBuilder = Builders<ChannelMessage>.Filter;
        var filter = filterBuilder.And(
            filterBuilder.Eq(m => m.Sender.BattleTag, battleTag),
            filterBuilder.Eq(m => m.Deleted, null));

        return Messages
            .Find(filter, new FindOptions { Collation = ChatDomainIndexes.SenderCaseInsensitiveCollation })
            .Project(m => new PurgeTarget(m.Id, m.ChannelId))
            .ToListAsync();
    }

    /// <summary>
    /// D7 (consumed by a later C4 task's unread math): count of rows in <paramref name="channelId"/>
    /// visible to <paramref name="viewerBattleTag"/> (same rule as <see cref="UserVisible"/>) with
    /// <c>Seq &gt; afterSeq</c>. The filter leads with ChannelId equality + a Seq range so the
    /// count is an INDEXED RANGE COUNT bounded by <c>ux_channelId_seq</c> — never a full-collection
    /// scan.
    /// </summary>
    public Task<long> CountUserVisibleAfter(string channelId, string viewerBattleTag, long afterSeq)
    {
        var filterBuilder = Builders<ChannelMessage>.Filter;
        var filter = filterBuilder.And(
            filterBuilder.Eq(m => m.ChannelId, channelId),
            filterBuilder.Gt(m => m.Seq, afterSeq),
            filterBuilder.Eq(m => m.Deleted, null),
            filterBuilder.Or(
                filterBuilder.Eq(m => m.Shadow, false),
                filterBuilder.Eq(m => m.Sender.BattleTag, viewerBattleTag)));

        return Messages.CountDocumentsAsync(filter);
    }

    /// <summary>
    /// Newest-first page strictly older than <paramref name="beforeSeq"/> (null = latest page),
    /// returned in ascending seq order. Seq-anchored: paging with the previous page's minimum seq
    /// is immune to concurrent appends — a later insert can never gap or dupe an already-fetched page.
    /// </summary>
    public Task<List<ChannelMessage>> LoadPageBefore(string channelId, string viewerBattleTag, long? beforeSeq, int limit) =>
        LoadPageBeforeCore(UserVisible(channelId, viewerBattleTag), beforeSeq, limit);

    /// <summary>
    /// Moderator counterpart of <see cref="LoadPageBefore"/> (D3/D6, consumed by later C4 tasks):
    /// same seq-anchored shape and clamp, but channel-filter only — deleted and shadow rows come
    /// back with their flags intact instead of being excluded by <see cref="UserVisible"/>.
    /// </summary>
    public Task<List<ChannelMessage>> LoadPageBeforeForModerator(string channelId, long? beforeSeq, int limit) =>
        LoadPageBeforeCore(Builders<ChannelMessage>.Filter.Eq(m => m.ChannelId, channelId), beforeSeq, limit);

    private async Task<List<ChannelMessage>> LoadPageBeforeCore(FilterDefinition<ChannelMessage> baseFilter, long? beforeSeq, int limit)
    {
        var effectiveLimit = ClampLimit(limit);
        var filterBuilder = Builders<ChannelMessage>.Filter;
        var filter = baseFilter;
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
    public Task<List<ChannelMessage>> LoadPageAround(string channelId, string viewerBattleTag, long aroundSeq, int limit) =>
        LoadPageAroundCore(UserVisible(channelId, viewerBattleTag), aroundSeq, limit);

    /// <summary>
    /// Moderator counterpart of <see cref="LoadPageAround"/> (D3/D6, consumed by later C4 tasks):
    /// same seq-anchored shape and clamp, but channel-filter only — deleted and shadow rows come
    /// back with their flags intact instead of being excluded by <see cref="UserVisible"/>.
    /// </summary>
    public Task<List<ChannelMessage>> LoadPageAroundForModerator(string channelId, long aroundSeq, int limit) =>
        LoadPageAroundCore(Builders<ChannelMessage>.Filter.Eq(m => m.ChannelId, channelId), aroundSeq, limit);

    private async Task<List<ChannelMessage>> LoadPageAroundCore(FilterDefinition<ChannelMessage> baseFilter, long aroundSeq, int limit)
    {
        var effectiveLimit = ClampLimit(limit);
        var half = effectiveLimit / 2;
        var filterBuilder = Builders<ChannelMessage>.Filter;

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
    internal static int ClampLimit(int limit) => Math.Clamp(limit, 1, ChatLimits.MessagePageSize);
}

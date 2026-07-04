using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Channels;

public class ChannelRepository(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient)
{
    private IMongoCollection<ChatChannel> Channels => CreateCollection<ChatChannel>(ChatCollections.Channels);

    public Task Insert(ChatChannel channel) => Channels.InsertOneAsync(channel);

    public Task<ChatChannel> Load(string id) => Channels.Find(c => c.Id == id).FirstOrDefaultAsync();

    public Task<List<ChatChannel>> LoadByIds(IEnumerable<string> ids) =>
        Channels.Find(Builders<ChatChannel>.Filter.In(c => c.Id, ids.ToList())).ToListAsync();

    public Task<ChatChannel> LoadByNormalizedName(ChannelType type, string normalizedName) =>
        Channels.Find(c => c.Type == type && c.NormalizedName == normalizedName).FirstOrDefaultAsync();

    /// <summary>
    /// Finds a name-joinable channel by normalized name across ALL types (not scoped to one
    /// ChannelType) — backs the join resolution order: a name match on an existing
    /// non-name-joinable type (e.g. System) must be distinguishable from "no match" so the
    /// caller can reject with PermissionDenied instead of falling through to implicit create.
    /// </summary>
    public Task<ChatChannel> LoadAnyByNormalizedName(string normalizedName) =>
        Channels.Find(c => c.NormalizedName == normalizedName).FirstOrDefaultAsync();

    public Task<List<ChatChannel>> LoadAllOfType(ChannelType type) =>
        Channels.Find(c => c.Type == type).ToListAsync();

    /// <summary>
    /// Atomically allocates the next per-channel sequence number via findOneAndUpdate $inc
    /// on the channel doc, also stamping LastMessageAt (C1 amendment: lastSeq + lastMessageAt
    /// maintained on every message-insert path). Strictly monotonic under concurrency —
    /// guaranteed by MongoDB single-document $inc atomicity, so it holds regardless of
    /// service-instance count (the service also runs single-instance by design).
    /// </summary>
    public async Task<long> AllocateSeq(string channelId, DateTime now)
    {
        var updated = await Channels.FindOneAndUpdateAsync<ChatChannel>(
            c => c.Id == channelId,
            Builders<ChatChannel>.Update
                .Inc(c => c.LastSeq, 1)
                .Set(c => c.LastMessageAt, now),
            new FindOneAndUpdateOptions<ChatChannel> { ReturnDocument = ReturnDocument.After });

        if (updated == null)
        {
            throw new InvalidOperationException($"Cannot allocate seq: channel {channelId} does not exist");
        }

        return updated.LastSeq;
    }

    /// <summary>
    /// Implicit find-or-create for semiPublic channels (join resolution — acceptance 9a):
    /// $setOnInsert upsert keyed (Type=SemiPublic, NormalizedName), mirroring
    /// PublicChannelSeeder's idempotent pattern. Backed by the unique partial index
    /// ux_type_normalizedName (ChatDomainIndexes.EnsureChannelIndexes). A genuine concurrent
    /// race — two joiners implicitly creating the same brand-new name at once — can make the
    /// losing upsert's insert half violate that index (surfaces as MongoCommandException,
    /// Code 11000/"DuplicateKey" — findAndModify is a single command, not a bulk-write op, so
    /// this is NOT MongoWriteException, which only wraps the insert/update/delete write-command
    /// family); retried once, after which the winner's row is visible and the retry resolves
    /// as a plain match.
    /// </summary>
    public async Task<ChatChannel> FindOrCreateSemiPublic(string name, DateTime now)
    {
        var normalized = ChannelNames.Normalize(name);
        var filter = Builders<ChatChannel>.Filter.Where(c => c.Type == ChannelType.SemiPublic && c.NormalizedName == normalized);
        var update = Builders<ChatChannel>.Update
            .SetOnInsert(c => c.Id, ObjectId.GenerateNewId().ToString())
            .SetOnInsert(c => c.Name, name)
            .SetOnInsert(c => c.LastSeq, 0L)
            .SetOnInsert(c => c.LastMessageAt, now);
        var options = new FindOneAndUpdateOptions<ChatChannel>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After,
        };

        try
        {
            return await Channels.FindOneAndUpdateAsync(filter, update, options);
        }
        catch (MongoCommandException ex) when (IsDuplicateKey(ex))
        {
            return await Channels.FindOneAndUpdateAsync(filter, update, options);
        }
    }

    private static bool IsDuplicateKey(MongoCommandException ex) => ex.Code == 11000;

    /// <summary>
    /// C4 Task 7 (D9): the eligible-channel list backing GET /api/moderation/channels — the
    /// channelId-resolution surface the website-backend's moderation proxy needs (the OLD
    /// ChatHistory-backed GET /api/chat/{chatroom} took room NAMEs directly; channels are the new unit).
    /// Eligible types mirror <see cref="ChannelModeration.IsModeratable"/> EXACTLY (Public / SemiPublic /
    /// System+Match) — expressed here as an explicit Mongo filter (a C# predicate can't be pushed into a
    /// query), so keep both definitions in sync if the scope wall ever changes. Sorted by LastMessageAt
    /// DESCENDING (most recently active first); <paramref name="limit"/> is clamped to
    /// [1, <see cref="ChatLimits.ModerationChannelsPageSize"/>] — never MongoDB's Limit(0) "no limit".
    /// </summary>
    public Task<List<ChatChannel>> LoadModeratableChannels(int limit)
    {
        var effectiveLimit = Math.Clamp(limit, 1, ChatLimits.ModerationChannelsPageSize);
        var filterBuilder = Builders<ChatChannel>.Filter;
        var filter = filterBuilder.Or(
            filterBuilder.Eq(c => c.Type, ChannelType.Public),
            filterBuilder.Eq(c => c.Type, ChannelType.SemiPublic),
            filterBuilder.And(
                filterBuilder.Eq(c => c.Type, ChannelType.System),
                filterBuilder.Eq(c => c.SystemKind, SystemChannelKind.Match)));

        return Channels.Find(filter).SortByDescending(c => c.LastMessageAt).Limit(effectiveLimit).ToListAsync();
    }
}

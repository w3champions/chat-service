using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Channels;

public class ChannelRepository(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient)
{
    private IMongoCollection<ChatChannel> Channels => CreateCollection<ChatChannel>(ChatCollections.Channels);

    public Task Insert(ChatChannel channel) => Channels.InsertOneAsync(channel);

    public Task<ChatChannel> Load(string id) => Channels.Find(c => c.Id == id).FirstOrDefaultAsync();

    public Task<ChatChannel> LoadByNormalizedName(ChannelType type, string normalizedName) =>
        Channels.Find(c => c.Type == type && c.NormalizedName == normalizedName).FirstOrDefaultAsync();

    public Task<List<ChatChannel>> LoadAllOfType(ChannelType type) =>
        Channels.Find(c => c.Type == type).ToListAsync();

    /// <summary>
    /// Atomically allocates the next per-channel sequence number via findOneAndUpdate $inc
    /// on the channel doc. Strictly monotonic under concurrency (single-document atomicity);
    /// single service instance by design.
    /// </summary>
    public async Task<long> AllocateSeq(string channelId)
    {
        var updated = await Channels.FindOneAndUpdateAsync<ChatChannel>(
            c => c.Id == channelId,
            Builders<ChatChannel>.Update.Inc(c => c.LastSeq, 1),
            new FindOneAndUpdateOptions<ChatChannel> { ReturnDocument = ReturnDocument.After });

        if (updated == null)
        {
            throw new InvalidOperationException($"Cannot allocate seq: channel {channelId} does not exist");
        }

        return updated.LastSeq;
    }
}

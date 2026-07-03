using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Channels;

/// <summary>
/// Seeds/upserts the hardcoded public catalog (DefaultChatRooms.Rooms) into `channels` at
/// startup. Rows are needed for lastSeq counters and message storage. Idempotent: keyed on
/// (Type, NormalizedName); $setOnInsert only, so LastSeq/LastMessageAt survive restarts.
/// Catalog changes remain deploy-only by explicit product decision.
/// </summary>
public class PublicChannelSeeder(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient)
{
    public async Task SeedPublicChannels()
    {
        var channels = CreateCollection<ChatChannel>(ChatCollections.Channels);

        foreach (var room in DefaultChatRooms.Rooms)
        {
            var normalized = ChannelNames.Normalize(room);
            var update = Builders<ChatChannel>.Update
                .SetOnInsert(c => c.Id, ObjectId.GenerateNewId().ToString())
                .SetOnInsert(c => c.Name, room)
                .SetOnInsert(c => c.LastSeq, 0L);

            await channels.UpdateOneAsync(
                c => c.Type == ChannelType.Public && c.NormalizedName == normalized,
                update,
                new UpdateOptions { IsUpsert = true });
        }
    }
}

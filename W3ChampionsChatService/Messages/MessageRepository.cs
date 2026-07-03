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
}

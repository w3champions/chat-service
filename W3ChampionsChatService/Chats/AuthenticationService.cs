using System.Threading.Tasks;
using MongoDB.Driver;

namespace W3ChampionsChatService.Chats
{
    public class ChatAuthenticationService : MongoDbRepositoryBase
    {
        public ChatAuthenticationService(MongoClient mongoClient) : base(mongoClient)
        {
        }

        public async Task<ChatUser> GetUser(string battleTag)
        {
            return new ChatUser(battleTag);
        }
    }
}
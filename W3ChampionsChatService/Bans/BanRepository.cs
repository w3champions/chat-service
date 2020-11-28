using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Driver;

namespace W3ChampionsChatService.Bans
{
    public class BanRepository : MongoDbRepositoryBase
    {
        public BanRepository(MongoClient mongoClient) : base(mongoClient)
        {
        }

        public Task<ChatBan> Load(string battleTag)
        {
            return LoadFirst<ChatBan>(battleTag);
        }

        public Task<List<ChatBan>> LoadAll()
        {
            return LoadAll<ChatBan>();
        }

        public Task Delete(string battleTag)
        {
            return Delete<ChatBan>(battleTag);
        }
    }
}
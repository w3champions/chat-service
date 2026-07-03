using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Users;

public class UserDirectoryRepository(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient)
{
    private IMongoCollection<UserDirectoryEntry> Directory =>
        CreateCollection<UserDirectoryEntry>(ChatCollections.UserDirectory);

    public Task Upsert(UserDirectoryEntry entry) =>
        Directory.ReplaceOneAsync(e => e.BattleTag == entry.BattleTag, entry, new ReplaceOptions { IsUpsert = true });

    public Task<UserDirectoryEntry> Load(string battleTag) =>
        Directory.Find(e => e.BattleTag == battleTag).FirstOrDefaultAsync();
}

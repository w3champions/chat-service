using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Users;

public class UserSettingsRepository(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient)
{
    private IMongoCollection<UserSettings> Settings =>
        CreateCollection<UserSettings>(ChatCollections.UserSettings);

    public Task Upsert(UserSettings settings) =>
        Settings.ReplaceOneAsync(s => s.BattleTag == settings.BattleTag, settings, new ReplaceOptions { IsUpsert = true });

    public async Task<UserSettings> LoadOrDefault(string battleTag) =>
        await Settings.Find(s => s.BattleTag == battleTag).FirstOrDefaultAsync()
        ?? new UserSettings { BattleTag = battleTag };
}

using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Memberships;

public class MembershipRepository(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient)
{
    private IMongoCollection<ChannelMembership> Memberships =>
        CreateCollection<ChannelMembership>(ChatCollections.ChannelMemberships);

    public Task Insert(ChannelMembership membership) => Memberships.InsertOneAsync(membership);

    public Task<ChannelMembership> Load(string channelId, string battleTag) =>
        Memberships.Find(m => m.ChannelId == channelId && m.BattleTag == battleTag).FirstOrDefaultAsync();

    public Task<List<ChannelMembership>> LoadForUser(string battleTag) =>
        Memberships.Find(m => m.BattleTag == battleTag).ToListAsync();

    public Task Delete(string channelId, string battleTag) =>
        Memberships.DeleteOneAsync(m => m.ChannelId == channelId && m.BattleTag == battleTag);
}

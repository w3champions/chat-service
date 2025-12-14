using System;
using System.Threading.Tasks;
using MongoDB.Driver;
using System.Collections.Generic;

namespace W3ChampionsChatService.Mutes;

public class MuteRepository(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient)
{
    public Task AddLoungeMute(LoungeMuteRequest loungeMuteRequest)
    {
        LoungeMute loungeMute = new LoungeMute();
        loungeMute.battleTag = loungeMuteRequest.battleTag.ToLower();
        loungeMute.author = loungeMuteRequest.author;
        loungeMute.reason = loungeMuteRequest.reason;
        loungeMute.insertDate = DateTime.UtcNow;
        loungeMute.endDate = DateTime.Parse(loungeMuteRequest.endDate, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        loungeMute.isShadowBan = loungeMuteRequest.isShadowBan;
        return Upsert(loungeMute);
    }

    public Task<LoungeMute> GetMutedPlayer(string battleTag)
    {
        return LoadFirst<LoungeMute>(battleTag.ToLower());
    }

    public Task<List<LoungeMute>> GetLoungeMutes()
    {
        return LoadAll<LoungeMute>();
    }

    public Task<DeleteResult> DeleteLoungeMute(string battleTag)
    {
        return Delete<LoungeMute>(c => c.battleTag == battleTag);
    }
}

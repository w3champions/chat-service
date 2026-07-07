using System;
using System.Threading.Tasks;
using MongoDB.Driver;
using System.Collections.Generic;

namespace W3ChampionsChatService.Mutes;

public class MuteRepository(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient), IMuteRepository
{
    public Task AddLoungeMute(LoungeMuteRequest loungeMuteRequest)
    {
        LoungeMute loungeMute = new LoungeMute();
        // Store the battleTag in its ORIGINAL (moderator-entered) casing so the admin mute list shows the
        // real display tag instead of a lowercased one. The document's identity is NOT this field: the
        // Mongo _id is LoungeMute.Id => battleTag.ToLowerInvariant(), so the Upsert's identity filter
        // (x.Id == obj.Id → { _id: <lowercased> }) matches an existing row for the same player REGARDLESS
        // of the entered casing — a legacy all-lowercase row OR a previously original-cased one. That means
        // a re-mute REPLACES the same document (no duplicate) and never changes _id across the replace, so
        // MongoDB's immutable-_id rule is never tripped.
        loungeMute.battleTag = loungeMuteRequest.battleTag;
        loungeMute.author = loungeMuteRequest.author;
        loungeMute.reason = loungeMuteRequest.reason;
        loungeMute.insertDate = DateTime.UtcNow;
        loungeMute.endDate = DateTime.Parse(loungeMuteRequest.endDate, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        loungeMute.isShadowBan = loungeMuteRequest.isShadowBan;
        return Upsert(loungeMute);
    }

    public Task<LoungeMute> GetMutedPlayer(string battleTag)
    {
        // Keyed on the lowercased _id (LoadFirst<LoungeMute>(id) renders { _id: id }): matches BOTH legacy
        // all-lowercase rows AND new rows that keep the moderator's display casing in the battleTag field,
        // because _id is the lowercased tag for both — the case-insensitive match.
        return LoadFirst<LoungeMute>(battleTag.ToLowerInvariant());
    }

    public Task<List<LoungeMute>> GetLoungeMutes()
    {
        return LoadAll<LoungeMute>();
    }

    public Task<DeleteResult> DeleteLoungeMute(string battleTag)
    {
        // Key the delete on the lowercased _id (c => c.Id renders { _id: ... }), NOT the battleTag field.
        // _id is the case-insensitive match key, so a delete under any casing removes the right document —
        // whether the row is a legacy all-lowercase one or a new one storing the display casing in
        // battleTag (which no longer necessarily equals the lowercased key).
        return Delete<LoungeMute>(c => c.Id == battleTag.ToLowerInvariant());
    }
}

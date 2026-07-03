using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Users;

/// <summary>Replaces the old ChatSettings (hard cutover — old collection untouched, no migration).</summary>
public class UserSettings
{
    [BsonId]
    public string BattleTag { get; set; }

    [BsonRepresentation(BsonType.String)]
    public DmPrivacy DmPrivacy { get; set; } = DmPrivacy.Everyone;

    [BsonRepresentation(BsonType.String)]
    public NotificationLevel DefaultNotificationLevel { get; set; } = NotificationLevel.All;

    public bool SoundsEnabled { get; set; } = true;
}

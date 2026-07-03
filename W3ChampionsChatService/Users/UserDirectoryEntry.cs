using System;
using MongoDB.Bson.Serialization.Attributes;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Users;

/// <summary>
/// Directory of every user chat has seen. Upserted at connect AND disconnect (C6);
/// serves mention search, the 90d activity gate, and last-online. Entries are kept —
/// the 90d gate is applied at query time, not via TTL.
/// </summary>
public class UserDirectoryEntry
{
    [BsonId]
    public string BattleTag { get; set; }

    public string NormalizedName { get; set; }

    public DateTime LastSeenAt { get; set; }

    [BsonIgnoreIfNull]
    public ChatProfile Profile { get; set; }
}

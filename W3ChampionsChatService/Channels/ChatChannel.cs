using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Channels;

public class ChatChannel
{
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    [BsonRepresentation(BsonType.String)]
    public ChannelType Type { get; set; }

    [BsonIgnoreIfNull]
    public string Name { get; set; }

    [BsonIgnoreIfNull]
    public string NormalizedName { get; set; }

    [BsonRepresentation(BsonType.String)]
    [BsonIgnoreIfNull]
    public SystemChannelKind? SystemKind { get; set; }

    /// <summary>lobbyId / matchId / clanId, depending on SystemKind.</summary>
    [BsonIgnoreIfNull]
    public string SystemRef { get; set; }

    /// <summary>Dm only — see DmPairKey.For. Unique partial index (Type == Dm).</summary>
    [BsonIgnoreIfNull]
    public string PairKey { get; set; }

    [BsonRepresentation(BsonType.String)]
    [BsonIgnoreIfNull]
    public DmRequestState? RequestState { get; set; }

    /// <summary>Per-channel monotonic message counter — allocated via findOneAndUpdate $inc.</summary>
    public long LastSeq { get; set; }

    [BsonIgnoreIfNull]
    public DateTime? LastMessageAt { get; set; }

    /// <summary>Absolute expiry instant (TTL index, expireAfterSeconds 0). Absent = permanent.</summary>
    [BsonIgnoreIfNull]
    public DateTime? ExpiresAt { get; set; }
}

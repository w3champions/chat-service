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

    /// <summary>
    /// Dm only — the battleTag of whoever wrote first, stamped once at pending-shell creation via
    /// $setOnInsert (never mutated afterwards). Serialized to BOTH parties (C5 D3): it rides the raw
    /// <see cref="ChatChannel"/> in <c>ChannelDto</c>/<c>ChannelAddedDto</c>, and both parties already
    /// know who opened the conversation, so this is not a leak. Decline state is placement-DISJOINT
    /// from this field — see <see cref="W3ChampionsChatService.Memberships.ChannelMembership.DeclinedUntil"/>.
    /// </summary>
    [BsonIgnoreIfNull]
    public string RequestInitiatedBy { get; set; }

    /// <summary>Per-channel monotonic message counter — allocated via findOneAndUpdate $inc.</summary>
    public long LastSeq { get; set; }

    [BsonIgnoreIfNull]
    public DateTime? LastMessageAt { get; set; }

    /// <summary>Absolute expiry instant (TTL index, expireAfterSeconds 0). Absent = permanent.</summary>
    [BsonIgnoreIfNull]
    public DateTime? ExpiresAt { get; set; }
}

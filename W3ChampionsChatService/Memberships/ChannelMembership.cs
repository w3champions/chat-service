using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Memberships;

/// <summary>
/// One doc per (channel, user) for ALL channel types incl. public. Pure subscription +
/// read-state: for public channels it carries NO ACL meaning and is never enumerated
/// channel→users — all queries go user→channels via the battleTag index.
/// </summary>
public class ChannelMembership
{
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string ChannelId { get; set; }
    public string BattleTag { get; set; }

    [BsonRepresentation(BsonType.String)]
    public MembershipRole Role { get; set; } = MembershipRole.Member;

    [BsonRepresentation(BsonType.String)]
    public NotificationLevel NotificationLevel { get; set; } = NotificationLevel.All;

    /// <summary>Unread math is channel.LastSeq - LastReadSeq (computed by C3, stored here).</summary>
    public long LastReadSeq { get; set; }

    public DateTime JoinedAt { get; set; }
}

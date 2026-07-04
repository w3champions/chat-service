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

    /// <summary>
    /// Dm only, lives on the RECIPIENT's membership row (never the sender's, never the channel doc) —
    /// C5 D3's soft+temporal decline: the recipient's tray suppression window ("declined" is NOT a
    /// DmRequestState value; the channel stays Pending). MUST NEVER be serialized to the client —
    /// <see cref="W3ChampionsChatService.Protocol.MembershipDto"/> is an explicit projection and MUST
    /// NOT gain this field (the sender must never learn they were declined).
    /// </summary>
    [BsonIgnoreIfNull]
    public DateTime? DeclinedUntil { get; set; }
}

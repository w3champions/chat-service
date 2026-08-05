using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Memberships;

/// <summary>
/// PR36 follow-up (task-1-brief.md, D2): one doc per (battleTag, channelId) holding the LAST
/// EXPLICITLY-set <see cref="NotificationLevel"/> for a name-joinable (Public/SemiPublic) room — the
/// durable carrier that survives a leave/rejoin cycle, since <see cref="ChannelMembership"/> itself does
/// not (<c>ChatHub.LeaveChannel</c> hard-deletes the membership row). Written ONLY by
/// <c>ChatHub.SetNotificationLevel</c> for Public/SemiPublic channels; read by <c>ChatHub.JoinChannel</c>
/// (seeds a (re)joined membership with the persisted level instead of the fresh-join
/// <see cref="NotificationLevel.Mentions"/> default) and by <see cref="Mentions.MentionFanOut"/>'s
/// non-member Public branch (keeps a mention silenced for a room the target left after opting out).
/// Independent of <see cref="ChannelMembership"/>'s lifecycle by design — leaving stays a hard delete;
/// this collection is what carries the setting across.
/// </summary>
public class NotificationPreference
{
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string BattleTag { get; set; }
    public string ChannelId { get; set; }

    [BsonRepresentation(BsonType.String)]
    public NotificationLevel NotificationLevel { get; set; }

    public DateTime UpdatedAt { get; set; }
}

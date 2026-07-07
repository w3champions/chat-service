using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace W3ChampionsChatService.Mentions;

/// <summary>
/// Offline mention notification. Excerpt (~120 chars) is denormalization only; entries are
/// deleted when the referenced message is moderation-deleted (C4/C6 hook). 30d TTL — always
/// at or below the message TTL so a notification never outlives its message.
/// </summary>
public class MentionInboxEntry
{
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string BattleTag { get; set; }
    public string ChannelId { get; set; }
    public string MessageId { get; set; }

    /// <summary>The mentioning message's per-channel sequence (C6-plan.md D5) — lets
    /// <c>MentionNotifiedDto</c>/<c>MentionInboxEntryDto</c> consumers jump straight to it via
    /// <c>FocusChannel</c> + <c>GetMessages(aroundSeq)</c> without an extra lookup. Defaults to 0;
    /// pre-existing seeds that predate this field are unaffected (never read as a real sequence).</summary>
    public long Seq { get; set; }

    public string AuthorBattleTag { get; set; }
    public string AuthorName { get; set; }
    public string Excerpt { get; set; }
    public DateTime CreatedAt { get; set; }

    [BsonIgnoreIfNull]
    public DateTime? ReadAt { get; set; }

    [BsonIgnoreIfNull]
    public DateTime? ExpiresAt { get; set; }
}

using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Messages;

/// <summary>Immutable snapshot of the sender at send time (spec §5).</summary>
public class MessageSender
{
    public string BattleTag { get; set; }
    public string Name { get; set; }

    [BsonIgnoreIfNull]
    public ChatProfile Flair { get; set; }
}

/// <summary>Set by moderation soft-delete. Physical removal happens ONLY via TTL.</summary>
public class MessageDeletion
{
    public string By { get; set; }
    public DateTime At { get; set; }
}

public class ChannelMessage
{
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string ChannelId { get; set; }

    /// <summary>Per-channel monotonic sequence — (ChannelId, Seq) unique.</summary>
    public long Seq { get; set; }

    public MessageSender Sender { get; set; }

    /// <summary>Raw content incl. mention markup (validation is C6's).</summary>
    public string Content { get; set; }

    public DateTime SentAt { get; set; }

    /// <summary>Soft-delete marker. Absent = visible. User reads exclude, moderator reads include flagged.</summary>
    [BsonIgnoreIfNull]
    public MessageDeletion Deleted { get; set; }

    /// <summary>Shadow-ban flag — representation only in C1 (author-only visibility semantics are C4's).</summary>
    public bool Shadow { get; set; }

    /// <summary>Absolute expiry instant (30d channel / 90d dm+group — computed via ExpiryCalculator).</summary>
    [BsonIgnoreIfNull]
    public DateTime? ExpiresAt { get; set; }
}

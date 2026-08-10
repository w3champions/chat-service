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

    /// <summary>
    /// Authorship discriminator. <see cref="MessageKind.User"/> ⇒ <see cref="Sender"/> and
    /// <see cref="Content"/> are populated and <see cref="SystemMessage"/> is null;
    /// <see cref="MessageKind.System"/> ⇒ exactly the inverse. Defaulted (and stored as a string) so
    /// every document written before this field existed deserializes as User with no migration.
    /// </summary>
    [BsonRepresentation(BsonType.String)]
    public MessageKind Kind { get; set; } = MessageKind.User;

    /// <summary>User messages only; null for a system message.</summary>
    public MessageSender Sender { get; set; }

    /// <summary>Raw content incl. mention markup (validation is C6's). User messages only; null for a system message.</summary>
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

    /// <summary>Structured system content. Non-null iff <see cref="Kind"/> is <see cref="MessageKind.System"/>.</summary>
    [BsonIgnoreIfNull]
    public SystemMessageBody SystemMessage { get; set; }

    /// <summary>
    /// Per-channel idempotency key for server-authored messages (System only; null for every user
    /// message). Backed by the partial unique index <c>ux_channelId_dedupeKey</c> — matchmaking-service
    /// retries its publish call on timeout, and without this the post-game intro double-posts.
    /// </summary>
    [BsonIgnoreIfNull]
    public string DedupeKey { get; set; }
}

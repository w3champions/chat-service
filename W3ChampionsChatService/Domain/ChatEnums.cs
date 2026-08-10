namespace W3ChampionsChatService.Domain;

// Stored as strings in Mongo ([BsonRepresentation(BsonType.String)] at each usage site)
// so index partial filters and raw documents stay human-readable.
public enum ChannelType { Public, SemiPublic, System, Dm, GroupDm }

public enum SystemChannelKind { Lobby, Match, Clan }

public enum DmRequestState { Pending, Accepted }

public enum MembershipRole { Member, Owner }

public enum NotificationLevel { All, Mentions, None }

public enum DmPrivacy { Everyone, Friends, Nobody }

/// <summary>
/// Message authorship discriminator. <see cref="User"/> is a player-authored message with a
/// <c>MessageSender</c> snapshot and free-form <c>Content</c>; <see cref="System"/> is
/// server-authored, has NO sender and NO content, and carries a structured
/// <c>SystemMessageBody</c> instead. Stored as a string so a future kind is additive, and
/// defaulted to <see cref="User"/> on <c>ChannelMessage</c> so every pre-existing document
/// deserializes correctly with no migration.
/// </summary>
public enum MessageKind
{
    User,
    System,
}

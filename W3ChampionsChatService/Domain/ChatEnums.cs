namespace W3ChampionsChatService.Domain;

// Stored as strings in Mongo ([BsonRepresentation(BsonType.String)] at each usage site)
// so index partial filters and raw documents stay human-readable.
public enum ChannelType { Public, SemiPublic, System, Dm, GroupDm }

public enum SystemChannelKind { Lobby, Match, Clan }

public enum DmRequestState { Pending, Accepted }

public enum MembershipRole { Member, Owner }

public enum NotificationLevel { All, Mentions, None }

public enum DmPrivacy { Everyone, Friends, Nobody }

using System.Text.Json.Serialization;

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
/// <c>SystemMessageBody</c> instead. Defaulted to <see cref="User"/> on <c>ChannelMessage</c> so every
/// pre-existing document deserializes correctly with no migration.
/// <para>
/// TWO INDEPENDENT REPRESENTATIONS, both string, set by two different mechanisms — do not read either
/// as implying the other:
/// </para>
/// <list type="bullet">
/// <item>BSON (at rest): <c>[BsonRepresentation(BsonType.String)]</c> at the <c>ChannelMessage.Kind</c>
/// usage site, matching every other stored enum here, so index partial filters and raw documents stay
/// human-readable and a future kind is purely additive.</item>
/// <item>JSON (on the wire): the <see cref="JsonStringEnumConverter"/> below. There is no global string
/// -enum converter (<c>ChatJsonProtocol.Configure</c> only sets <c>DefaultIgnoreCondition</c>), so
/// without this attribute <c>kind</c> would ride as an undocumented ORDINAL and clients would be
/// writing <c>if (msg.kind === 1)</c>. This mirrors the per-type attribute on
/// <see cref="Protocol.ChatResultCode"/>, for the same reason: wire payloads and logs stay
/// self-describing.</item>
/// </list>
/// <para>
/// <c>kind</c> is ALWAYS EMITTED, including on ordinary user messages — deliberately NOT shrunk with
/// <c>JsonIgnoreCondition.WhenWritingDefault</c>. A discriminator that vanishes on the common case
/// invites <c>msg.kind === undefined</c> bugs in the client, and <see cref="Protocol.ChatResultCode"/>
/// sets the always-emit precedent. The saved bytes are not worth a discriminator you cannot rely on.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageKind
{
    User,
    System,
}

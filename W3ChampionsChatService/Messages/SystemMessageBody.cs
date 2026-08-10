using System.Collections.Generic;
using MongoDB.Bson.Serialization.Attributes;

namespace W3ChampionsChatService.Messages;

/// <summary>
/// Structured content of a server-authored system message. Deliberately NOT a pre-rendered string:
/// the launcher ships 13 locales, so a stored English sentence would be permanently untranslatable.
/// Clients render <see cref="Key"/> against their own catalogue using <see cref="Params"/> /
/// <see cref="ListParams"/>, and fall back to <see cref="FallbackText"/> for any key they do not
/// recognise — which is what lets chat-service add new system messages without breaking older clients.
/// <para>
/// Two dictionaries rather than one <c>object</c> bag: both round-trip through BSON and
/// System.Text.Json with no custom converters, and give TypeScript a clean shape. Scalars go in
/// <see cref="Params"/>, lists in <see cref="ListParams"/>.
/// </para>
/// </summary>
public class SystemMessageBody
{
    /// <summary>Template id, e.g. <c>match_intro</c>. Stable — clients key their catalogue off it.</summary>
    public string Key { get; set; }

    [BsonIgnoreIfNull]
    public Dictionary<string, string> Params { get; set; }

    [BsonIgnoreIfNull]
    public Dictionary<string, List<string>> ListParams { get; set; }

    /// <summary>
    /// Server-rendered English. The ONLY thing a client that does not know <see cref="Key"/> can show,
    /// and what the moderation history endpoint reads. Required — never null.
    /// </summary>
    public string FallbackText { get; set; }
}

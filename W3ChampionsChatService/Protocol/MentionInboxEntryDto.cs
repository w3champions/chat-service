using System;
using System.Text.Json.Serialization;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// Explicit wire projection of <see cref="Mentions.MentionInboxEntry"/> for <c>GetMentionInbox</c>
/// (C6-plan.md D6, boundary-privacy convention): <c>ExpiresAt</c> is server-only TTL bookkeeping
/// and is deliberately NOT exposed here. Returned newest-first, capped at
/// <see cref="Domain.ChatLimits.MentionInboxMaxEntries"/>. <see cref="ReadAt"/> is null until the
/// entry is acknowledged via <c>MarkMentionsRead</c>/<c>MarkAllMentionsRead</c>.
/// </summary>
public record MentionInboxEntryDto(
    string Id,
    string ChannelId,
    string MessageId,
    long Seq,
    string AuthorBattleTag,
    string AuthorName,
    string Excerpt,
    DateTime CreatedAt,
    // ChatJsonProtocol.Configure omits null properties from EVERY hub payload (WhenWritingNull), but
    // a null ReadAt is not an absence here — it IS the "unread" state. Pinned to always serialize
    // (even as null) so the client can keep using a presence/nullness check instead of every reader
    // having to treat "key missing" and "key null" as the same thing.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    DateTime? ReadAt);

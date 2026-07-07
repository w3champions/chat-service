using System;

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
    DateTime? ReadAt);

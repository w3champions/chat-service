using System;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// Payload for <see cref="ChatEvents.MentionNotified"/> (C6-plan.md D4) — pushed to a mention
/// target's live connection immediately after the offline <see cref="Mentions.MentionInboxEntry"/>
/// row (<see cref="EntryId"/>) is inserted. Carries the author and a bounded excerpt so the client
/// can toast/OS-notify without an extra fetch; <see cref="Seq"/> lets the client jump straight to
/// the mentioning message via <c>FocusChannel</c> + <c>GetMessages(aroundSeq)</c>.
/// </summary>
public record MentionNotifiedDto(
    string EntryId,
    string ChannelId,
    string MessageId,
    long Seq,
    string AuthorBattleTag,
    string AuthorName,
    string Excerpt,
    DateTime CreatedAt);

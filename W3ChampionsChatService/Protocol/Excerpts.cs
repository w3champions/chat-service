using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// The ONE surrogate-pair-safe string truncation in this service (C6-plan.md D5). Promoted from the
/// C5 (Task 9) private <c>FanOutEngine.BuildDmPreviewExcerpt</c> so the DM activity preview (C5) and the
/// mention-inbox entry (C6 Task 5) shared one implementation instead of forking a second surrogate-split
/// copy, and GENERALIZED over the limit (post-game chat Plan A final review, finding 10) so every
/// truncation site routes through it rather than growing a fourth copy:
/// <list type="bullet">
/// <item>the two activity previews + the mention-inbox excerpt, at
/// <see cref="ChatLimits.DmPreviewExcerptLength"/> (the spec §5 "~120 chars" precedent);</item>
/// <item>the internal system-message <c>fallbackText</c>, at <see cref="ChatLimits.MaxMessageLength"/>
/// — server-rendered display text of the same shape as a user message body;</item>
/// <item>the internal channel <c>name</c> on both the create and roster-assert routes, at
/// <see cref="ChatLimits.InternalChannelNameMaxLength"/>. Those two were naive
/// <c>name[..limit]</c> slices and could persist a lone surrogate; routing them here fixes that.</item>
/// </list>
/// </summary>
internal static class Excerpts
{
    /// <summary>
    /// The first <paramref name="limit"/> characters of <paramref name="content"/>, or
    /// <paramref name="content"/> itself when it is already at or under the limit (no padding, and the
    /// <c>&lt;=</c> boundary never truncates). A plain bounded substring; NO word-boundary trimming (no
    /// existing excerpt helper does that either).
    /// <para>
    /// SURROGATE-SAFE, which is the whole reason this exists: chat content is emoji-heavy, and
    /// <see cref="string.Length"/>/<see cref="string.Substring(int, int)"/> count UTF-16 code units, so
    /// a naive cut can land inside a supplementary-plane character's surrogate pair and emit a lone high
    /// surrogate. That is not valid UTF-16 text — BSON-encoding it is undefined at best (a replacement
    /// character) and a throw at worst, and it then fans out to every reader. If the boundary would
    /// split a pair, the whole character is dropped instead (the result is <c>limit - 1</c> chars).
    /// </para>
    /// <para>
    /// <paramref name="limit"/> is deliberately REQUIRED rather than defaulted to
    /// <see cref="ChatLimits.DmPreviewExcerptLength"/>: this is now a general helper with three
    /// different caps, and a DM-shaped default would quietly hand 120 chars to a caller that meant
    /// something else.
    /// </para>
    /// </summary>
    internal static string Bounded(string content, int limit)
    {
        if (content.Length <= limit)
        {
            return content;
        }

        if (char.IsHighSurrogate(content[limit - 1]))
        {
            limit--;
        }

        return content.Substring(0, limit);
    }
}

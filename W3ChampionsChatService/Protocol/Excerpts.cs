using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// Shared, surrogate-pair-safe message-content excerpt (C6-plan.md D5). Promoted from the
/// C5 (Task 9) private <c>FanOutEngine.BuildDmPreviewExcerpt</c> so the DM activity preview (C5) and the
/// mention-inbox entry (C6 Task 5) share ONE implementation instead of forking a second surrogate-split
/// copy (DRY — the C5 report already named the DM-preview method "a reference"). Both surfaces cap at
/// <see cref="ChatLimits.DmPreviewExcerptLength"/> — the spec §5 "~120 chars" precedent.
/// </summary>
internal static class Excerpts
{
    /// <summary>
    /// The first <see cref="ChatLimits.DmPreviewExcerptLength"/> characters of <paramref name="content"/>.
    /// A plain bounded substring; NO word-boundary trimming (no existing excerpt helper does that either).
    /// Surrogate-safe: chat content is emoji-heavy, and <see cref="string.Length"/>/
    /// <see cref="string.Substring(int, int)"/> count UTF-16 code units, so a naive cut can land inside a
    /// supplementary-plane character's surrogate pair and emit a lone high surrogate. If the boundary
    /// would split a pair, the whole character is dropped instead.
    /// </summary>
    internal static string Bounded(string content)
    {
        var limit = ChatLimits.DmPreviewExcerptLength;
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace W3ChampionsChatService.Mentions;

/// <summary>
/// Pure mention-markup parser (spec §7; C6-plan.md D1). Markup is <c>&lt;@BattleTag#123&gt;</c>: a
/// name part (1-32 chars, none of <c>&lt;&gt;@#</c>) followed by a literal <c>#</c> and a 1-10 digit
/// numeric suffix, wrapped in angle brackets. No allocation-heavy backtracking is needed — content
/// is already bounded to <see cref="Domain.ChatLimits.MaxMessageLength"/> (512 chars) by the C3 cap
/// that runs before this parser ever sees it.
/// </summary>
public static class MentionMarkup
{
    private static readonly Regex TokenPattern = new(
        @"<@([^<>@#]{1,32}#[0-9]{1,10})>",
        RegexOptions.Compiled);

    /// <summary>
    /// Extracts every well-formed mention tag from <paramref name="content"/>, deduplicated
    /// case-insensitively with first-occurrence order preserved (a duplicate tag counts once — a
    /// user is only fanned out to once regardless of how many times they're tagged). This parser
    /// only EXTRACTS; enforcing <see cref="Domain.ChatLimits.MaxMentionsPerMessage"/> is the
    /// caller's job (C6-plan.md D2).
    /// </summary>
    public static IReadOnlyList<string> ExtractTags(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return Array.Empty<string>();
        }

        return TokenPattern
            .Matches(content)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

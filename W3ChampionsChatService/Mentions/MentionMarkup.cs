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

    /// <summary>
    /// D2 (2026-08-05, server-canonical mention rendering): a single pass over the SAME token grammar
    /// <see cref="ExtractTags"/> uses (reusing <see cref="TokenPattern"/> — this is deliberately NOT a
    /// second parser) that downgrades every mention token whose target is not a legal render target for
    /// the channel to its plain-text display form, and leaves every other token's markup untouched.
    /// <para>
    /// The plain-text form is <c>@{tag}</c> — no angle brackets, tag byte-for-byte as captured (display
    /// casing preserved). This matches the launcher's PRE-EXISTING client-side downgrade path byte-for-
    /// byte (<c>mention-markup.helper.ts</c>'s <c>parseMessageSegments</c>: <c>segment.text = `@${battleTag}`</c>,
    /// returned verbatim by <c>ChatMessage.tsx</c>'s <c>MessageContent</c> for a non-rendering mention),
    /// so a pre-change client and a post-change client show byte-identical text for the same content.
    /// </para>
    /// <paramref name="isRenderable"/> is invoked once per REGEX MATCH, not once per distinct tag — the
    /// same target mentioned twice with different casing in one message is evaluated once per occurrence.
    /// Callers should back it with a case-insensitive lookup (the same dedup convention
    /// <see cref="ExtractTags"/> uses) so every occurrence of the same target gets the same decision.
    /// </summary>
    public static string RewriteUnrenderable(string content, Func<string, bool> isRenderable)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        return TokenPattern.Replace(content, m =>
        {
            var tag = m.Groups[1].Value;
            return isRenderable(tag) ? m.Value : $"@{tag}";
        });
    }
}

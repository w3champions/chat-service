using System.Text.RegularExpressions;

namespace W3ChampionsChatService.Domain;

/// <summary>
/// Pure predicate for "does this message look like a chat command?" (design §4, §5.1). The service
/// supports no chat commands; broadcasting a <c>/w Grubby hi</c> verbatim leaks both the intended
/// recipient and the body the sender believed was private to the whole channel, so
/// <see cref="Chats.ChatHub.SendMessage"/> rejects these at step 4.5.
/// <para>
/// Grammar: a leading <c>/</c>, one or more Unicode letters, then whitespace or end-of-string.
/// <c>\p{L}</c> rather than <c>[a-zA-Z]</c> so a Cyrillic or CJK verb is caught too; <c>\z</c> rather
/// than <c>$</c> so there is no trailing-newline ambiguity. The <c>(?:\s|\z)</c> arm is what lets
/// <c>/usr/local/bin</c>, <c>//note</c>, a bare <c>/</c> and <c>/ 10 gold</c> through as ordinary text.
/// </para>
/// <para>
/// Mirrored byte-for-byte by the launcher at
/// <c>launcher-e/src/helpers/chat-command.helper.ts</c>. That copy DOES trim (it holds raw composer
/// input); this one does NOT, because <c>SendMessage</c> step 2 has already trimmed. Keep the two
/// grammars in sync — the canonical case table lives in the test project, in
/// <c>SlashCommandDetectorTests</c>.
/// </para>
/// <para>
/// No backtracking exposure: content is trimmed and capped at
/// <see cref="ChatLimits.MaxMessageLength"/> (512) by step 2 before this parser ever sees it — the
/// same reasoning <see cref="Mentions.MentionMarkup"/> documents for itself.
/// </para>
/// </summary>
public static class SlashCommandDetector
{
    private static readonly Regex CommandPattern = new(
        @"^/\p{L}+(?:\s|\z)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// True when <paramref name="content"/> is command-shaped. Expects ALREADY-TRIMMED content.
    /// </summary>
    public static bool IsSlashCommand(string content) =>
        !string.IsNullOrEmpty(content) && CommandPattern.IsMatch(content);
}

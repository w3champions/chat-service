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
/// Mirrored by the launcher at <c>launcher-e/src/helpers/chat-command.helper.ts</c>. The two grammars
/// are BEHAVIOURALLY IDENTICAL ON ALL PRINTABLE INPUT — not byte-for-byte, because .NET and JavaScript
/// do not define <c>\s</c> as the same set. Keep them in sync; the canonical case table lives in the
/// test project, in <c>SlashCommandDetectorTests</c>.
/// </para>
/// <para>
/// That copy DOES normalize its own input (it holds raw composer text); this one does NOT, because
/// <c>SendMessage</c> step 2 has already normalized. Do NOT add a trim here — the no-trim contract is
/// pinned by <c>SlashCommandDetectorTests.IsSlashCommand_LeadingWhitespace_False_BecauseCallerTrimsFirst</c>.
/// </para>
/// <para>
/// NO KNOWN DIVERGENCE (verified empirically on .NET 8 / V8, design §4). The client agrees with this
/// parser on every input tested, in all three positions — leading, trailing, and the separator between
/// verb and argument.
/// <para>
/// This rests on one measured fact worth not forgetting: .NET's regex <c>\s</c>, .NET's
/// <see cref="char.IsWhiteSpace(char)"/>, and .NET's <see cref="string.TrimEnd()"/> are all the SAME
/// 25-code-point set, and JavaScript's <c>\p{White_Space}</c> is an EXACT match for it (compared code
/// point by code point across the whole BMP, zero delta in either direction). JavaScript's <c>\s</c> is
/// NOT that set — it omits U+0085 (NEL) and adds U+FEFF (BOM) — which is why the client spells every
/// position with <c>\p{White_Space}</c> and never with <c>\s</c>. Four inputs used to disagree because
/// of that (<c>/w</c>+NEL+<c>hi</c>, <c>/w</c>+BOM+<c>hi</c>, <c>/stats</c>+NEL, <c>/stats</c>+BOM);
/// none do now.
/// </para>
/// <para>
/// The three mirrored positions: LEADING — <see cref="Chats.ChatHub"/>'s <c>NormalizeSendContent</c>
/// against <c>chat-command.helper.ts</c>'s <c>LEADING_IGNORABLE_PATTERN</c> (whitespace and format
/// characters, 68 code points, astral included). TRAILING — <see cref="string.TrimEnd()"/> against
/// <c>TRAILING_WHITESPACE_PATTERN</c> (whitespace ONLY: neither side strips a trailing format
/// character, so a trailing BOM is content on both). SEPARATOR — this parser's <c>(?:\s|\z)</c> against
/// the client's <c>(?:\p{White_Space}|$)</c>.
/// </para>
/// <para>
/// If you change the grammar here, change the client's to match and re-check those four inputs. The
/// server remains the enforcement point regardless: the composer's <c>?? "other"</c> fallback
/// (<c>MentionComposer.tsx</c>, the <c>UnsupportedCommand</c> arm) keeps the error copy sensible for
/// anything this server rejects that the client did not classify.
/// </para>
/// <para>
/// No backtracking exposure: content is normalized and capped at
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
    /// True when <paramref name="content"/> is command-shaped. Expects ALREADY-NORMALIZED content —
    /// <c>SendMessage</c> step 2 has stripped leading whitespace AND leading Unicode format characters
    /// (the latter matters: the pattern is anchored, so a surviving leading U+FEFF would defeat it).
    /// </summary>
    public static bool IsSlashCommand(string content) =>
        !string.IsNullOrEmpty(content) && CommandPattern.IsMatch(content);
}

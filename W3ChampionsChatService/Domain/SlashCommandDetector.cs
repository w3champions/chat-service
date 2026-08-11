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
/// KNOWN <c>\s</c> DIVERGENCE (verified empirically on .NET 8 / V8, design §4). Two code points differ,
/// and only in the SEPARATOR position.
/// <para>
/// The LEADING position is fully converged: both sides strip the identical class before the anchored
/// check — whitespace and Unicode format characters, in any interleaving, astral included. Server side
/// that is <see cref="Chats.ChatHub"/>'s <c>NormalizeSendContent</c>; client side it is
/// <c>chat-command.helper.ts</c>'s <c>LEADING_IGNORABLE_PATTERN</c>
/// (<c>/^[\p{White_Space}\p{Cf}]+/u</c>). The client deliberately uses <c>\p{White_Space}</c> and NOT
/// <c>\s</c>, because JavaScript's <c>\s</c> omits U+0085: the two strip sets were compared code point
/// by code point across the whole BMP and are IDENTICAL (68 code points).
/// </para>
/// What remains divergent:
/// <list type="bullet">
/// <item><c>/w</c> + U+0085 (NEL) + <c>hi</c> — BLOCKED here, allowed by the client: .NET <c>\s</c>
/// matches U+0085, JavaScript's does not.</item>
/// <item><c>/w</c> + U+FEFF (BOM) + <c>hi</c> — allowed here, BLOCKED by the client: JavaScript's
/// <c>\s</c> matches U+FEFF, .NET's does not.</item>
/// </list>
/// Both directions degrade safely. This server is the authority, so the NEL case is still refused on
/// the wire; the composer's <c>?? "other"</c> fallback (<c>MentionComposer.tsx</c>, the
/// <c>UnsupportedCommand</c> arm) keeps the error copy sensible when the client cannot classify what the
/// server rejected. The U+FEFF case is only a client-side false positive on what was a whisper attempt
/// anyway. Chasing exact <c>\s</c> parity across two regex engines costs more than it returns for two
/// code points in one position; this is documented rather than fixed, so a future editor does not
/// assume an equivalence that has no edges.
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

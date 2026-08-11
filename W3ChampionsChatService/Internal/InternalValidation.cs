using System.Linq;

namespace W3ChampionsChatService.Internal;

/// <summary>
/// Validation shared by every <c>internal/*</c> endpoint. This rule must hold IDENTICALLY across all
/// of them, so it lives in one place — it previously existed as two byte-identical private copies,
/// one of which carried a comment promising it mirrored the other exactly.
/// </summary>
public static class InternalValidation
{
    /// <summary>
    /// A usable battleTag: non-blank, and free of control characters. U+2028 and U+2029 are checked
    /// explicitly because <c>char.IsControl</c> classifies them as separators, not controls, yet they
    /// terminate lines in JavaScript sources and log viewers.
    /// </summary>
    public static bool IsValidBattleTag(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029');
}

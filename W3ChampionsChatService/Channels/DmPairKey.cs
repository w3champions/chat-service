using System;

namespace W3ChampionsChatService.Channels;

public static class DmPairKey
{
    /// <summary>
    /// One conversation per pair, ever: normalized battleTags, sorted, pipe-joined.
    /// Unique-indexed on channels (partial, Type == Dm).
    /// </summary>
    public static string For(string battleTagA, string battleTagB)
    {
        var a = battleTagA.Trim().ToLowerInvariant();
        var b = battleTagB.Trim().ToLowerInvariant();
        return string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
    }

    /// <summary>
    /// The OTHER half of a pair-key (lowercased/normalized, matching how <see cref="For"/> built it) —
    /// the counterpart of <paramref name="battleTag"/> in a 1:1 Dm. Mirrors the split logic
    /// ChatHub.ResolveDmCounterpart has always used; hoisted here so the SessionStateAssembler's
    /// blocked-shell keep (follow-up spec §6) and the hub share ONE implementation. The comparison is
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> (the pre-hoist ResolveDmCounterpart behavior) —
    /// not a plain ordinal <c>==</c> — because <see cref="string.ToLowerInvariant"/> is not guaranteed
    /// to fold every code point identically on both sides (e.g. locale-sensitive casing edge cases), so
    /// an ordinal-only match could spuriously fall through to <c>parts[0]</c> and hand back the CALLER's
    /// own tag as its "counterpart".
    /// </summary>
    public static string CounterpartOf(string pairKey, string battleTag)
    {
        var parts = pairKey.Split('|');
        var normalized = battleTag.Trim().ToLowerInvariant();
        return string.Equals(parts[0], normalized, StringComparison.OrdinalIgnoreCase) ? parts[1] : parts[0];
    }
}

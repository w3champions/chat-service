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
}

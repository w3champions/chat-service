namespace W3ChampionsChatService.Channels;

public static class ChannelNames
{
    /// <summary>
    /// Canonical form used for lookups and unique keys — trimmed and lowercased (case-insensitive).
    /// </summary>
    public static string Normalize(string name) => name?.Trim().ToLowerInvariant();
}

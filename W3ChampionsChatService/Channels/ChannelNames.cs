namespace W3ChampionsChatService.Channels;

public static class ChannelNames
{
    /// <summary>
    /// Canonical form used for lookups and unique keys. Matches the case-insensitive
    /// semantics of DefaultChatRooms.IsPublicRoom (OrdinalIgnoreCase).
    /// </summary>
    public static string Normalize(string name) => name?.Trim().ToLowerInvariant();
}

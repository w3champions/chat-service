using System.Collections.Generic;

namespace W3ChampionsChatService.Chats;

public class DefaultChatRooms()
{
    /// <summary>
    /// The hardcoded public catalog — fed into <c>Channels/PublicChannelSeeder.cs</c> at startup.
    /// Catalog changes remain deploy-only by explicit product decision.
    /// </summary>
    public static readonly IReadOnlyList<string> Rooms = [
        "W3C Lounge",
        "1 vs 1",
        "2 vs 2",
        "4 vs 4",
        "FFA",
        "Legion TD",
        "Survival Chaos",
        "Direct Strike",
        "Warhammer",
        "Castle Fight",
        "Risk Europe",
        "Mini Dota",
    ];
}

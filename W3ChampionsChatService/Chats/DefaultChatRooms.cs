using System.Collections.Generic;

namespace W3ChampionsChatService.Chats;

public class DefaultChatRooms()
{
    public static List<string> Rooms { get; } = [
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

    /// <summary>
    /// Returns true if <paramref name="room"/> is a lounge/ladder channel
    /// where chat mutes apply. Clan and custom-game-lobby rooms return false.
    /// </summary>
    public static bool IsBannedRoom(string room) => Rooms.Contains(room);
}

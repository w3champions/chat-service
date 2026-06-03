using System;
using System.Collections.Generic;
using System.Linq;

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
    /// True only for the official lounge/ladder rooms in <see cref="Rooms"/> (case-insensitive).
    /// Any other room — clan rooms, game-lobby rooms, or any dynamic room — returns false.
    /// </summary>
    public static bool IsBannedRoom(string room) => Rooms.Contains(room, StringComparer.OrdinalIgnoreCase);
}

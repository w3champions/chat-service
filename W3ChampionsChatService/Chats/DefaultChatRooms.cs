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
    /// True only for the official public lounge/ladder rooms in <see cref="Rooms"/> (case-insensitive).
    /// These are the only rooms where lounge mutes (full/shadow bans) apply. Any other room —
    /// clan rooms, game-lobby rooms, or any dynamic room — is a private/exempt room and returns false.
    /// </summary>
    public static bool IsPublicRoom(string room) => Rooms.Contains(room, StringComparer.OrdinalIgnoreCase);
}

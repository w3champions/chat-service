using W3ChampionsChatService.Chats;

namespace W3ChampionsChatService.Domain;

/// <summary>
/// Snapshot of a user's rendering profile ("flair"): used as the immutable sender snapshot
/// on messages (flair at send time) and as the cached profile on user_directory entries.
/// Mirrors the wb chat-profile payload (program contract §4). Extend only additively —
/// C6 (directory) and W1 (wb endpoint) enrich this.
/// </summary>
public class ChatProfile
{
    public string ClanId { get; set; }
    public ProfilePicture ProfilePicture { get; set; }
    public ChatColor ChatColor { get; set; }
    public ChatIcon[] ChatIcons { get; set; }
    public int? LeagueId { get; set; }
    public int? RankNumber { get; set; }
    public int? GamesPlayed { get; set; }
}

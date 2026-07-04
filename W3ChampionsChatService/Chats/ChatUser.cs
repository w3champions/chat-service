using MongoDB.Bson.Serialization.Attributes;

namespace W3ChampionsChatService.Chats;

public class ChatUser(string battleTag, bool isAdmin, string clanTag, ProfilePicture profilePicture, ChatColor chatColor, ChatIcon[] chatIcons)
{
    [BsonId]
    public string BattleTag { get; set; } = battleTag;
    public bool IsAdmin { get; set; } = isAdmin;
    public string Name { get; set; } = battleTag.Split("#")[0];
    public string ClanTag { get; set; } = clanTag;
    public ProfilePicture ProfilePicture { get; set; } = profilePicture;
    public ChatColor ChatColor { get; set; } = chatColor;
    public ChatIcon[] ChatIcons { get; set; } = chatIcons;

    // D9: additive rank/league enrichment (W1 amendment) — plain settable properties (NOT primary-ctor
    // params) so every pre-existing 6-arg `new ChatUser(...)` call site across the test suite keeps
    // compiling unchanged. Populated by ChatAuthenticationService.GetUserFromIdentity from the wb
    // ChatDetailsDto.Rank sub-object (or the cached ChatProfile on the directory-cache fallback tier);
    // null until then. Mirrors the same fields on Domain.ChatProfile (see ChatProfileMapper).
    public int? LeagueId { get; set; }
    public string LeagueName { get; set; }
    public int? LeagueOrder { get; set; }
    public int? LeagueDivision { get; set; }
    public int? RankNumber { get; set; }
    public int? GameMode { get; set; }
    public int? GateWay { get; set; }
    public int? GamesPlayed { get; set; }
    public int? Season { get; set; }
}

public class ProfilePicture
{
    public AvatarCategory Race { get; set; }
    public long PictureId { get; set; }
    public bool IsClassic { get; set; }
}

public enum AvatarCategory
{
    RnD = 0,
    HU = 1,
    OC = 2,
    NE = 4,
    UD = 8,
    Total = 16,
    Special = 32
}

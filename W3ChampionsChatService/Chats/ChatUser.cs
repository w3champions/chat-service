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

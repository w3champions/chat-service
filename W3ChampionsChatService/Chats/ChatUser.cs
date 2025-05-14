using MongoDB.Bson.Serialization.Attributes;

namespace W3ChampionsChatService.Chats;

public class ChatUser(string battleTag, bool isAdmin, string clanTag, ProfilePicture profilePicture)
{
    [BsonId]
    public string BattleTag { get; set; } = battleTag;
    public bool IsAdmin { get; set; } = isAdmin;
    public string Name { get; set; } = battleTag.Split("#")[0];
    public string ClanTag { get; set; } = clanTag;
    public ProfilePicture ProfilePicture { get; set; } = profilePicture;

    /// <summary>
    /// Generates a fake user with the name "SYSTEM" that is based on this user.
    /// This approach is necessary in order to ensure that we do not break backwards compatibility
    /// with the old launcher. This way, a user can click on the "SYSTEM" user and won't get a 404
    /// because the Battle Tag has not been found.
    /// </summary>
    /// <returns>A ChatUser object representing the derived fake system user.</returns>
    public ChatUser GenerateFakeSystemUser()
    {
        var systemUser = new ChatUser(this.BattleTag, this.IsAdmin, this.ClanTag, this.ProfilePicture);
        // Manually set the name to "SYSTEM" because the constructor does not set it.
        // This will allow BattleTag to be the same as the original user to allow clicking it while
        // showing [SYSTEM] as the name in the chat.
        systemUser.Name = "[SYSTEM]";
        return systemUser;
    }
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

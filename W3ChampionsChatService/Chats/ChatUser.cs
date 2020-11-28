using MongoDB.Bson.Serialization.Attributes;

namespace W3ChampionsChatService.Chats
{
    public class ChatUser
    {
        public ChatUser(string battleTag, string clanTag, ProfilePicture profilePicture)
        {
            BattleTag = battleTag;
            ClanTag = clanTag;
            ProfilePicture = profilePicture;
            Name = battleTag.Split("#")[0];
        }

        [BsonId]
        public string BattleTag { get; set; }
        public string Name { get; set; }
        public string ClanTag { get; set; }
        public ProfilePicture ProfilePicture { get; set; }
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

    public class ChatDetailsDto
    {
        public string ClanId { get; set; }
        public ProfilePicture ProfilePicture { get; set;}
    }
}
using System.ComponentModel.DataAnnotations;

namespace W3ChampionsChatService.Bans
{
    public class ChatBan : IIdentifiable
    {
        [Required]
        public string BattleTag { get; set; }

        [Required]
        public string EndDate { get; set; }

        public string BanReason { get; set; }
        public string Id => BattleTag;
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace W3ChampionsChatService.Bans
{
    public interface IBanRepository
    {
        Task<BannedPlayer> GetBannedPlayer(string userBattleTag);
    }

    public class BanRepository : IBanRepository
    {
        private static readonly string MatchmakingApiUrl = Environment.GetEnvironmentVariable("MATCHMAKING_API") ?? "https://matchmaking-service.test.w3champions.com";
        private static readonly string MatchmakingAdminSecret = Environment.GetEnvironmentVariable("ADMIN_SECRET") ?? "300C018C-6321-4BAB-B289-9CB3DB760CBB";

        public async Task<BannedPlayer> GetBannedPlayer(string userBattleTag)
        {
            try
            {
                var httpClient = new HttpClient();
                var result = await httpClient.GetAsync($"{MatchmakingApiUrl}/admin/bannedPlayers?secret={MatchmakingAdminSecret}");
                var content = await result.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(content)) return null;
                var deserializeObject = JsonConvert.DeserializeObject<BannedPlayerResponse>(content);
                var bTagLower = userBattleTag.ToLower();

                return deserializeObject.Players.FirstOrDefault(p => p.BattleTag == bTagLower);
            }
            catch (Exception)
            {
                return null;
            }

        }
    }

    public class BannedPlayerResponse
    {
        public int Total { get; set; }
        public List<BannedPlayer> Players { get; set; }
    }

    public class BannedPlayer
    {
        public string BattleTag { get; set; }

        public string EndDate { get; set; }
    }
}
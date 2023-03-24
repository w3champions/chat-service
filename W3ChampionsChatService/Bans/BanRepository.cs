using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace W3ChampionsChatService.Bans
{
    public interface IBanRepository
    {
        Task<LoungeMute> GetBannedPlayer(string userBattleTag);
    }

    public class BanRepository : IBanRepository
    {
        private static readonly string MatchmakingApiUrl = Environment.GetEnvironmentVariable("MATCHMAKING_API") ?? "https://matchmaking-service.test.w3champions.com";
        private static readonly string MatchmakingAdminSecret = Environment.GetEnvironmentVariable("ADMIN_SECRET") ?? "300C018C-6321-4BAB-B289-9CB3DB760CBB";

        public async Task<LoungeMute> GetBannedPlayer(string userBattleTag)
        {
            try
            {
                var httpClient = new HttpClient();
                var result = await httpClient.GetAsync($"{MatchmakingApiUrl}//admin/loungeMutes?secret={MatchmakingAdminSecret}");
                var content = await result.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(content)) return null;
                var deserializeObject = JsonConvert.DeserializeObject<LoungeMute[]>(content);
                var bTagLower = userBattleTag.ToLower();

                return deserializeObject.FirstOrDefault(p => p.battleTag == bTagLower);
            }
            catch (Exception)
            {
                return null;
            }

        }
    }

  public class LoungeMute
    {
        public string battleTag { get; set; }
        public string endDate { get; set; }
    }
}
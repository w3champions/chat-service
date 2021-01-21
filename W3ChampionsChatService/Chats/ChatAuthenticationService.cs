using System;
using System.Net.Http;
using System.Threading.Tasks;
using MongoDB.Driver;
using Newtonsoft.Json;
using W3ChampionsChatService.Authentication;

namespace W3ChampionsChatService.Chats
{
    public interface IChatAuthenticationService
    {
        Task<ChatUser> GetUser(string chatKey);
    }

    public class ChatAuthenticationService : MongoDbRepositoryBase, IChatAuthenticationService
    {
        private readonly IW3CAuthenticationService _authenticationService;

        private static readonly string StatisticServiceApiUrl = Environment.GetEnvironmentVariable("STATISTIC_SERVICE_URI") ?? "https://statistic-service.test.w3champions.com";

        public ChatAuthenticationService(MongoClient mongoClient, IW3CAuthenticationService authenticationService) : base(mongoClient)
        {
            _authenticationService = authenticationService;
        }

        public async Task<ChatUser> GetUser(string chatKey)
        {
            try
            {
                var user = await _authenticationService.GetUserByToken(chatKey);
                if (user == null) return null;
                var userDetails = await GetChatDetails(user.BattleTag);
                return new ChatUser(user.BattleTag, userDetails?.ClanId, userDetails?.ProfilePicture);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static async Task<ChatDetailsDto> GetChatDetails(string battleTag)
        {
            var httpClient = new HttpClient();
            httpClient.BaseAddress = new Uri(StatisticServiceApiUrl);
            var escapeDataString = Uri.EscapeDataString(battleTag);
            var result = await httpClient.GetAsync($"/api/players/{escapeDataString}/clan-and-picture");
            var content = await result.Content.ReadAsStringAsync();
            var userDetails = JsonConvert.DeserializeObject<ChatDetailsDto>(content);
            return userDetails;
        }
    }

    public class ChatDetailsDto
    {
        public string ClanId { get; set; }
        public ProfilePicture ProfilePicture { get; set;}
    }
}
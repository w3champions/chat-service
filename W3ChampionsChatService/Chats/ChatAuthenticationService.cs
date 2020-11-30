using System;
using System.Net.Http;
using System.Threading.Tasks;
using MongoDB.Driver;
using Newtonsoft.Json;

namespace W3ChampionsChatService.Chats
{
    public class ChatAuthenticationService : MongoDbRepositoryBase
    {
        private static readonly string StatisticServiceApiUrl = Environment.GetEnvironmentVariable("STATISTIC_SERVICE_URI") ?? "https://statistic-service-test.w3champions.com";

        public ChatAuthenticationService(MongoClient mongoClient) : base(mongoClient)
        {
        }

        public async Task<ChatUser> GetUser(string battleTag)
        {
            try
            {
                var httpClient = new HttpClient();
                httpClient.BaseAddress = new Uri(StatisticServiceApiUrl);
                var escapeDataString = Uri.EscapeDataString(battleTag);
                var result = await httpClient.GetAsync($"/api/players/{escapeDataString}/clan-and-picture");
                var content = await result.Content.ReadAsStringAsync();
                var userDetails = JsonConvert.DeserializeObject<ChatDetailsDto>(content);
                return new ChatUser(battleTag, userDetails?.ClanId, userDetails?.ProfilePicture);
            }
            catch (Exception)
            {
                return new ChatUser(battleTag, null, new ProfilePicture());
            }
        }
    }

    public class ChatDetailsDto
    {
        public string ClanId { get; set; }
        public ProfilePicture ProfilePicture { get; set;}
    }
}
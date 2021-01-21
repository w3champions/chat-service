using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace W3ChampionsChatService.Chats
{
    public interface IWebsiteBackendRepository
    {
        Task<ChatDetailsDto> GetChatDetails(string battleTag);
    }

    public class WebsiteBackendRepository : IWebsiteBackendRepository
    {
        private static readonly string StatisticServiceApiUrl = Environment.GetEnvironmentVariable("STATISTIC_SERVICE_URI") ?? "https://statistic-service.test.w3champions.com";

        public async Task<ChatDetailsDto> GetChatDetails(string battleTag)
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
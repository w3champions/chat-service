using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace W3ChampionsChatService.Chats;

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

public class ChatDetailsDto(string clanId, ProfilePicture profilePicture, ChatColor chatColor, ChatIcon[] chatIcons)
{
    public string ClanId { get; } = clanId;
    public ProfilePicture ProfilePicture { get; } = profilePicture;

    public ChatColor ChatColor { get; } = chatColor;
    public ChatIcon[] ChatIcons { get; } = chatIcons;
}


public class ChatColor(string colorId)
{
    public string ColorId { get; } = colorId;
}

public class ChatIcon(string iconId)
{
    public string IconId { get; } = iconId;
}
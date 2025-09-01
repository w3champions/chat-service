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



public class ChatColor(string colorId) : IEquatable<ChatColor>
{
    public static readonly ChatColor AdminColor = new("chat_color_admin");
    // We use an ID instead of a hex code because we want to allow users to configure the selected one themselves.
    // The ID allows us to show localized names and descriptions. The value is resolved on the frontend.
    public string ColorId { get; } = colorId;

    public bool Equals(ChatColor other)
    {
        return ColorId == other.ColorId;
    }

    public override bool Equals(object obj)
    {
        return Equals(obj as ChatColor);
    }

    public override int GetHashCode()
    {
        return ColorId.GetHashCode();
    }
}

public class ChatIcon(string iconId) : IEquatable<ChatIcon>
{
    public static readonly ChatIcon AdminIcon = new("chat_icon_admin");

    // We use an ID instead of a hex code because we want to allow users to configure the selected one themselves.
    // The ID allows us to show localized names and descriptions. The value is resolved on the frontend.
    public string IconId { get; } = iconId;

    public bool Equals(ChatIcon other)
    {
        return IconId == other.IconId;
    }

    public override bool Equals(object obj)
    {
        return Equals(obj as ChatIcon);
    }

    public override int GetHashCode()
    {
        return IconId.GetHashCode();
    }
}

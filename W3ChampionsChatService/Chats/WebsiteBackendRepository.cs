using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace W3ChampionsChatService.Chats;

public interface IWebsiteBackendRepository
{
    Task<ChatDetailsDto> GetChatDetails(string battleTag);
}

/// <summary>
/// D9: rebuilt on <see cref="IHttpClientFactory"/> — kills the per-call <c>new HttpClient()</c>
/// socket-exhaustion anti-pattern (a fresh <see cref="HttpClient"/> allocates its own
/// <see cref="HttpMessageHandler"/>/socket pool every call; the factory pools handlers across calls
/// even though it still hands back a lightweight per-call <see cref="HttpClient"/> wrapper — the
/// standard ASP.NET Core guidance for this exact problem). Same route, same interface — a
/// behavior-preserving plumbing swap.
/// </summary>
public class WebsiteBackendRepository(IHttpClientFactory httpClientFactory) : IWebsiteBackendRepository
{
    private static readonly string StatisticServiceApiUrl = Environment.GetEnvironmentVariable("STATISTIC_SERVICE_URI") ?? "https://statistic-service.test.w3champions.com";

    public async Task<ChatDetailsDto> GetChatDetails(string battleTag)
    {
        var httpClient = httpClientFactory.CreateClient();
        httpClient.BaseAddress = new Uri(StatisticServiceApiUrl);
        // F5: a modest timeout keeps a slow (not necessarily throwing) wb from stalling this call for
        // the .NET default of 100s — mirrors the same 2s precedent on
        // Relationships/WebsiteBackendRelationshipSource.cs's shared HttpClient. This call sits directly
        // on ChatHub.OnConnectedAsync's await chain (via ChatAuthenticationService.GetUserFromIdentity),
        // so a slow/unreachable wb now throws TaskCanceledException at 2s instead of hanging ~100s during
        // a reconnect storm; GetUserFromIdentity's existing three-tier fallback (try/catch around this
        // call) treats that exactly like any other wb failure and degrades to the directory-cache-or-plain
        // fallback, so the connect itself never stalls.
        httpClient.Timeout = TimeSpan.FromSeconds(2);
        var escapeDataString = Uri.EscapeDataString(battleTag);
        var result = await httpClient.GetAsync($"/api/players/{escapeDataString}/clan-and-picture");
        var content = await result.Content.ReadAsStringAsync();
        var userDetails = JsonConvert.DeserializeObject<ChatDetailsDto>(content);
        return userDetails;
    }
}

/// <summary>
/// D9: extended ADDITIVELY and tolerantly — <see cref="Rank"/>/<see cref="GamesPlayed"/>/
/// <see cref="Season"/> are the W1-amendment enrichment fields. Today's wb payload lacks them, so
/// they deserialize null (the legacy-payload tolerant-stub pin); once W1 lands they carry the
/// player's best-rank snapshot. Optional constructor parameters (rather than required ones) so a
/// direct 4-arg construction (the pre-existing legacy shape) still compiles.
/// </summary>
public class ChatDetailsDto(
    string clanId,
    ProfilePicture profilePicture,
    ChatColor chatColor,
    ChatIcon[] chatIcons,
    ChatRankDto rank = null,
    int? gamesPlayed = null,
    int? season = null)
{
    public string ClanId { get; } = clanId;
    public ProfilePicture ProfilePicture { get; } = profilePicture;

    public ChatColor ChatColor { get; } = chatColor;
    public ChatIcon[] ChatIcons { get; } = chatIcons;

    public ChatRankDto Rank { get; } = rank;
    public int? GamesPlayed { get; } = gamesPlayed;
    public int? Season { get; } = season;
}

/// <summary>
/// The wb "best rank" sub-object (W1 amendment). Field names are VERBATIM as wb serializes them
/// (camelCase JSON binds to these PascalCase properties via Newtonsoft's default case-insensitive
/// constructor-parameter matching — no <c>[JsonProperty]</c> needed, matching this file's existing
/// convention) — deliberately NOT renamed to match spec §4's naming; that divergence is intentional
/// (W1 amendment), not a bug. Null on <see cref="ChatDetailsDto.Rank"/> when the player is unranked
/// this season.
/// </summary>
public class ChatRankDto(int leagueId, string leagueName, int leagueOrder, int leagueDivision, int rankNumber, int gameMode, int gateWay)
{
    public int LeagueId { get; } = leagueId;
    public string LeagueName { get; } = leagueName;
    public int LeagueOrder { get; } = leagueOrder;
    public int LeagueDivision { get; } = leagueDivision;
    public int RankNumber { get; } = rankNumber;
    public int GameMode { get; } = gameMode;
    public int GateWay { get; } = gateWay;
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

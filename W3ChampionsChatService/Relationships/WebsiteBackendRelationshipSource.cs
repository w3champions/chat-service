using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace W3ChampionsChatService.Relationships;

/// <summary>
/// Concrete <see cref="IRelationshipSource"/> reading a player's friends/blocked lists from the website
/// backend (statistic service), mirroring <see cref="Chats.WebsiteBackendRepository"/>'s base-URL
/// resolution (C5/D2). The wb route below does NOT exist yet — W2 owns it — and it MUST be
/// service-authenticated when it lands (friends/blocked lists are private, unlike the public
/// clan-and-picture read). Until then every relationship-gated path fails closed (see
/// <see cref="RelationshipProvider"/>). NO test exercises this class — all tests mock
/// <see cref="IRelationshipSource"/>.
/// </summary>
public sealed class WebsiteBackendRelationshipSource : IRelationshipSource
{
    // Mirrors WebsiteBackendRepository.cs:15 — same env var, same fallback host.
    private static readonly string StatisticServiceApiUrl =
        Environment.GetEnvironmentVariable("STATISTIC_SERVICE_URI") ?? "https://statistic-service.test.w3champions.com";

    // One shared client. A modest timeout keeps a slow/unreachable wb from stalling a relationship read —
    // the provider treats any failure (timeout included) as "unavailable" and falls back to the last-known
    // snapshot or fails closed.
    private static readonly HttpClient HttpClient = new HttpClient
    {
        BaseAddress = new Uri(StatisticServiceApiUrl),
        Timeout = TimeSpan.FromSeconds(2),
    };

    // Proposed W2 route (single const, negotiable). Returns { friends: string[], blocked: string[] }.
    internal const string RouteTemplate = "/api/players/{0}/chat-relationships";

    public async Task<RelationshipSnapshot> FetchAsync(string battleTag, DateTime now)
    {
        var route = string.Format(RouteTemplate, Uri.EscapeDataString(battleTag));
        var response = await HttpClient.GetAsync(route);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var dto = JsonConvert.DeserializeObject<ChatRelationshipsDto>(content) ?? new ChatRelationshipsDto();

        var friends = new HashSet<string>(dto.Friends ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(dto.Blocked ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return new RelationshipSnapshot(battleTag, friends, blocked, now);
    }

    // The wb response contract (W2). Null-tolerant: absent/null arrays deserialize to null and are coerced
    // to empty above, so a partial or malformed-but-parseable body never NREs.
    private sealed class ChatRelationshipsDto
    {
        [JsonProperty("friends")] public string[] Friends { get; set; }
        [JsonProperty("blocked")] public string[] Blocked { get; set; }
    }
}

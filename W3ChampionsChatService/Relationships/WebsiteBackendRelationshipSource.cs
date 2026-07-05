using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace W3ChampionsChatService.Relationships;

/// <summary>
/// Concrete <see cref="IRelationshipSource"/> reading a player's friends/blocked lists from the website
/// backend (statistic service), mirroring <see cref="Chats.WebsiteBackendRepository"/>'s base-URL
/// resolution (C5/D2). The wb route below (W2, landed) is service-authenticated — friends/blocked lists
/// are private, unlike the public clan-and-picture read — via the shared secret in
/// <see cref="RelationshipsSourceAuthSettings"/>, attached as the <see cref="HeaderName"/> header
/// (mirrors wb's <c>ChatServiceSecretAuthFilter</c>). When that secret is unconfigured the request is
/// sent with NO auth header at all, wb's filter 401s, and every relationship-gated path fails closed
/// (see <see cref="RelationshipProvider"/>) exactly as before this header existed. Exercised by
/// <c>WebsiteBackendRelationshipSourceTests</c> via the <see cref="BuildRequest"/> test seam.
/// </summary>
public sealed class WebsiteBackendRelationshipSource(RelationshipsSourceAuthSettings authSettings) : IRelationshipSource
{
    private readonly RelationshipsSourceAuthSettings _authSettings = authSettings;

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

    // W2 route. Returns { friends: string[], blocked: string[] }.
    internal const string RouteTemplate = "/api/players/{0}/chat-relationships";

    // The shared-secret header wb's ChatServiceSecretAuthFilter requires — a protocol constant (the
    // header NAME), not a secret, so it is safe to hardcode. The secret VALUE itself never is; it comes
    // from RelationshipsSourceAuthSettings (env-sourced by Startup).
    internal const string HeaderName = "x-chat-relationships-secret";

    public async Task<RelationshipSnapshot> FetchAsync(string battleTag, DateTime now)
    {
        var request = BuildRequest(battleTag);
        var response = await HttpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var dto = JsonConvert.DeserializeObject<ChatRelationshipsDto>(content) ?? new ChatRelationshipsDto();

        var friends = new HashSet<string>(dto.Friends ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(dto.Blocked ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return new RelationshipSnapshot(battleTag, friends, blocked, now);
    }

    // Test seam (assembly has InternalsVisibleTo): builds the outbound request, including the
    // conditional auth header, without performing any real HTTP call — lets a unit test assert on the
    // constructed request's Headers directly instead of mocking HttpMessageHandler.
    internal HttpRequestMessage BuildRequest(string battleTag)
    {
        var route = string.Format(RouteTemplate, Uri.EscapeDataString(battleTag));
        var request = new HttpRequestMessage(HttpMethod.Get, route);
        if (_authSettings.Configured)
        {
            request.Headers.Add(HeaderName, _authSettings.Secret);
        }
        return request;
    }

    // The wb response contract (W2). Null-tolerant: absent/null arrays deserialize to null and are coerced
    // to empty above, so a partial or malformed-but-parseable body never NREs.
    private sealed class ChatRelationshipsDto
    {
        [JsonProperty("friends")] public string[] Friends { get; set; }
        [JsonProperty("blocked")] public string[] Blocked { get; set; }
    }
}

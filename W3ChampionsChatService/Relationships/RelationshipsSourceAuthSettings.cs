namespace W3ChampionsChatService.Relationships;

/// <summary>
/// Outbound auth settings for <see cref="WebsiteBackendRelationshipSource"/>'s call to wb's
/// <c>GET /api/players/{tag}/chat-relationships</c> endpoint. wb guards that route with its own
/// fail-closed <c>ChatServiceSecretAuthFilter</c>, keyed off wb's <c>CHAT_RELATIONSHIPS_API_SECRET</c>
/// env var — this class's <see cref="Secret"/> MUST equal that value. They are two ends of ONE shared
/// secret, set independently in each service's own deployment config (chat-service reads it from
/// <c>STATISTIC_SERVICE_RELATIONSHIPS_SECRET</c>; see <c>Startup.ConfigureServices</c>).
/// <para>
/// <c>Startup</c> is the ONLY place that touches <c>Environment.GetEnvironmentVariable</c> for this
/// secret and constructs the single production instance from a plain string — this class never reads
/// env vars itself, the same seam pattern as <see cref="Internal.InternalCallerSecrets"/>, so tests
/// construct it directly with literal strings.
/// </para>
/// <para>
/// Deliberately NO fallback default: when unset/blank, <see cref="Configured"/> is false and
/// <see cref="WebsiteBackendRelationshipSource"/> sends its request with NO auth header at all — wb's
/// filter then 401s and the existing <see cref="IRelationshipProvider"/> fail-closed path takes over
/// exactly as it does today. This degrades safely; it must never crash or crash-loop the service.
/// </para>
/// </summary>
public class RelationshipsSourceAuthSettings(string secret)
{
    public string Secret { get; } = secret;
    public bool Configured => !string.IsNullOrWhiteSpace(Secret);
}

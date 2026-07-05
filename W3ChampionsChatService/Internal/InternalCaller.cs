namespace W3ChampionsChatService.Internal;

/// <summary>
/// Callers permitted to use chat-service's <c>/internal/*</c> HMAC-authenticated REST surface (C7
/// brief Design decisions 2/3): matchmaking-service (<see cref="Mm"/>) and website-backend
/// (<see cref="Wb"/>). There is no caller-id header — the caller is resolved by which registered
/// secret verifies the request signature (see <see cref="InternalCallerSecrets"/>). Extend here —
/// plus wire the matching <c>INTERNAL_SECRET_*</c> env var in <c>Startup</c> — the day a third
/// caller needs this surface; v1 only ever needs these two.
/// </summary>
public enum InternalCaller
{
    Mm,
    Wb
}

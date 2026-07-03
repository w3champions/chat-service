using Microsoft.AspNetCore.SignalR;
using W3ChampionsChatService.Authentication;

namespace W3ChampionsChatService.Sessions;

/// <summary>
/// The authoritative record of ONE authenticated SignalR connection: its connection id, the
/// identity snapshot captured at connect (fixed for the connection's lifetime — never re-resolved),
/// and the SignalR context used only to close a displaced OLD connection.
/// </summary>
public class ChatSession
{
    public string ConnectionId { get; init; }

    /// <summary>The identity snapshot taken at connect; fixed for the connection lifetime.</summary>
    public W3CUserAuthentication Identity { get; init; }

    /// <summary>
    /// The connection's SignalR context. Used only later (by the hub) to close a displaced OLD
    /// connection; null-tolerated in unit tests, which never need to abort a real connection.
    /// </summary>
    public HubCallerContext Context { get; init; }

    /// <summary>
    /// C4 (D2): exactly the authorization conjunct <see cref="Authentication.ChatHubPermissionFilter"/>
    /// enforces on attributed hub methods — <c>IsAdmin AND Permissions.Contains(permission)</c> — lifted
    /// onto the session so moderator code paths outside the hub-filter pipeline (later C4 tasks) can ask
    /// the same question instead of re-deriving it. NEVER serialized.
    /// </summary>
    public bool HasPermission(EPermission permission) => Identity.IsAdmin && Identity.Permissions.Contains(permission);
}

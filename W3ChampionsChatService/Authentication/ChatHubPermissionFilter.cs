using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using W3ChampionsChatService.Sessions;

namespace W3ChampionsChatService.Authentication;

/// <summary>
/// SignalR hub filter that enforces <see cref="UserHasPermissionAttribute"/> generically on hub
/// methods. The required permission is DECLARED on the method as <c>[UserHasPermission(...)]</c> — the
/// same attribute used on the MVC controllers — so the permission stays co-located with the method and
/// is the single source of truth, enforced by the right pipeline per transport.
/// <para>
/// SECURITY: the MVC <c>[UserHasPermission]</c> attribute is an <c>IAsyncActionFilter</c>, which the
/// SignalR hub pipeline never runs — so on a hub method it is inert without this filter. This filter
/// reads the attribute off the invoked method via reflection and resolves the caller's identity from
/// the per-connection <see cref="ISessionRegistry"/> by <c>Context.ConnectionId</c>: the identity
/// snapshot captured at connect (ticket handshake, C2) and fixed for the connection's lifetime. No JWT
/// is decoded per invocation — the query-string <c>access_token</c> no longer reaches hub methods. The
/// registry is fail-closed: an unregistered connection, or a DISPLACED stale connection, resolves to
/// no session and is rejected. Passing then requires <c>IsAdmin</c> AND the declared permission. A
/// method WITHOUT the attribute is unprotected and passes straight through with zero identity work.
/// </para>
/// Rejections throw <see cref="HubException"/> (a graceful, client-visible error) — NEVER
/// <c>Context.Abort()</c>; the connection stays alive.
/// </summary>
public class ChatHubPermissionFilter(ISessionRegistry sessionRegistry) : IHubFilter
{
    private readonly ISessionRegistry _sessionRegistry = sessionRegistry;

    public async ValueTask<object> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        System.Func<HubInvocationContext, ValueTask<object>> next)
    {
        // The required permission is whatever the method declares via [UserHasPermission(...)].
        // No attribute → unprotected method (e.g. SendMessage) → pass straight through, no identity work.
        var permissionAttribute = invocationContext.HubMethod
            .GetCustomAttributes(typeof(UserHasPermissionAttribute), inherit: true)
            .Cast<UserHasPermissionAttribute>()
            .FirstOrDefault();

        if (permissionAttribute == null)
        {
            return await next(invocationContext);
        }

        // C2: identity is the session snapshot registered at connect (ticket handshake) — resolved by
        // ConnectionId from the in-memory registry. No JWT ever reaches hub invocations anymore. The
        // registry is fail-closed: an unregistered or displaced-stale connection resolves to no session.
        if (!_sessionRegistry.TryGetByConnectionId(invocationContext.Context.ConnectionId, out var session)
            || !session.HasPermission(permissionAttribute.Permission))
        {
            // session is null on the no-session branch (TryGetByConnectionId returned false) → "<unregistered>".
            // When a session WAS resolved (registered-but-under-privileged reject), the caller's battleTag is
            // the durable attribution key for an attempted moderator action — far more useful than an
            // ephemeral connectionId for audit (OWASP A09). Never log any token/ticket.
            Log.Warning("Hub method {Method} rejected: connection {ConnectionId} (battleTag {BattleTag}) lacks {Permission}",
                invocationContext.HubMethod.Name, invocationContext.Context.ConnectionId,
                session?.Identity.BattleTag ?? "<unregistered>", permissionAttribute.Permission);
            // Graceful, client-visible rejection — never Context.Abort().
            throw new HubException($"Unauthorized: {permissionAttribute.Permission} permission required");
        }

        return await next(invocationContext);
    }
}

using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Serilog;

namespace W3ChampionsChatService.Authentication;

/// <summary>
/// SignalR hub filter that enforces <see cref="UserHasPermissionAttribute"/> generically on hub
/// methods. The required permission is DECLARED on the method as <c>[UserHasPermission(...)]</c> — the
/// same attribute used on the MVC controllers — so the permission stays co-located with the method and
/// is the single source of truth, enforced by the right pipeline per transport.
/// <para>
/// SECURITY: the MVC <c>[UserHasPermission]</c> attribute is an <c>IAsyncActionFilter</c>, which the
/// SignalR hub pipeline never runs — so on a hub method it is inert without this filter. This filter
/// reads the attribute off the invoked method via reflection and applies the SAME stateless JWT
/// resolution as the MVC filter: decode the <c>access_token</c> via
/// <see cref="IW3CAuthenticationService.GetUserByToken"/> and require <c>IsAdmin</c> + the declared
/// permission. A method WITHOUT the attribute is unprotected and passes straight through (no JWT decode).
/// </para>
/// Rejections throw <see cref="HubException"/> (a graceful, client-visible error) — NEVER
/// <c>Context.Abort()</c>; the connection stays alive.
/// </summary>
public class ChatHubPermissionFilter(IW3CAuthenticationService authService) : IHubFilter
{
    private readonly IW3CAuthenticationService _authService = authService;

    public async ValueTask<object> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        System.Func<HubInvocationContext, ValueTask<object>> next)
    {
        // The required permission is whatever the method declares via [UserHasPermission(...)].
        // No attribute → unprotected method (e.g. SendMessage) → pass straight through, no auth.
        var permissionAttribute = invocationContext.HubMethod
            .GetCustomAttributes(typeof(UserHasPermissionAttribute), inherit: true)
            .Cast<UserHasPermissionAttribute>()
            .FirstOrDefault();

        if (permissionAttribute == null)
        {
            return await next(invocationContext);
        }

        var requiredPermission = permissionAttribute.Permission;

        // Read the access_token from the SignalR connection's HttpContext. IHttpContextAccessor is NOT
        // usable here: it is null for hub-METHOD invocations over WebSockets (only populated during the
        // handshake). HubCallerContext.GetHttpContext() reads the connection's IHttpContextFeature,
        // which is reliably populated at handshake and persists for the connection lifetime.
        var httpContext = invocationContext.Context.GetHttpContext();
        var token = httpContext?.Request.Query["access_token"];
        var auth = _authService.GetUserByToken(token);

        if (auth == null || !auth.IsAdmin || !auth.Permissions.Contains(requiredPermission))
        {
            Log.Warning("Hub method {Method} rejected: caller {BattleTag} lacks {Permission} permission",
                invocationContext.HubMethodName, auth?.BattleTag ?? "<unauthenticated>", requiredPermission);
            // Graceful, client-visible rejection — never Context.Abort().
            throw new HubException($"Unauthorized: {requiredPermission} permission required");
        }

        return await next(invocationContext);
    }
}

using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Serilog;

namespace W3ChampionsChatService.Authentication;

/// <summary>
/// SignalR hub filter that enforces Moderation permission on the moderator-only hub methods.
/// <para>
/// SECURITY: the MVC <c>[UserHasPermission]</c> attribute is INERT on SignalR hub methods (it is an
/// <c>IAsyncActionFilter</c>, which the hub pipeline never runs), so without this filter ANY
/// authenticated connection could invoke <c>BanUser</c>/<c>DeleteMessage</c>/<c>PurgeMessagesFromUser</c>.
/// This closes that privilege-escalation hole using the SAME stateless JWT resolution as the MVC filter:
/// decode the <c>access_token</c> via <see cref="IW3CAuthenticationService.GetUserByToken"/> and require
/// <c>IsAdmin</c> + the Moderation permission.
/// </para>
/// Rejections throw <see cref="HubException"/> (a graceful, client-visible error) — NEVER
/// <c>Context.Abort()</c>; the connection stays alive.
/// </summary>
public class ChatHubPermissionFilter(IW3CAuthenticationService authService) : IHubFilter
{
    private readonly IW3CAuthenticationService _authService = authService;

    // The moderator-only hub methods that require the Moderation permission.
    private static readonly HashSet<string> ProtectedMethods = new()
    {
        nameof(Chats.ChatHub.BanUser),
        nameof(Chats.ChatHub.DeleteMessage),
        nameof(Chats.ChatHub.PurgeMessagesFromUser),
    };

    public async ValueTask<object> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        System.Func<HubInvocationContext, ValueTask<object>> next)
    {
        if (!ProtectedMethods.Contains(invocationContext.HubMethodName))
        {
            // Unprotected method — pass straight through.
            return await next(invocationContext);
        }

        // Read the access_token from the SignalR connection's HttpContext. IHttpContextAccessor is NOT
        // usable here: it is null for hub-METHOD invocations over WebSockets (only populated during the
        // handshake). HubCallerContext.GetHttpContext() reads the connection's IHttpContextFeature,
        // which is reliably populated at handshake and persists for the connection lifetime.
        var httpContext = invocationContext.Context.GetHttpContext();
        var token = httpContext?.Request.Query["access_token"];
        var auth = _authService.GetUserByToken(token);

        if (auth == null || !auth.IsAdmin || !auth.Permissions.Contains(EPermission.Moderation))
        {
            Log.Warning("Hub method {Method} rejected: caller {BattleTag} lacks Moderation permission",
                invocationContext.HubMethodName, auth?.BattleTag ?? "<unauthenticated>");
            // Graceful, client-visible rejection — never Context.Abort().
            throw new HubException("Unauthorized: Moderation permission required");
        }

        return await next(invocationContext);
    }
}

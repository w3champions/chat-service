using System;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using Serilog;

namespace W3ChampionsChatService.Authentication;

public class UserHasPermissionFilter(IW3CAuthenticationService authService) : IAsyncActionFilter
{
    private readonly IW3CAuthenticationService _authService = authService;

    public EPermission Permission { get; set; }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        try
        {
            var token = GetToken(context.HttpContext.Request.Headers[HeaderNames.Authorization]);
            // item 7: enforce JWT lifetime on the moderation REST surface, aligning with wb's
            // BearerHasPermissionFilter (GetUserByToken(token, true)). rethrowExpiry:true makes an EXPIRED
            // token surface as SecurityTokenExpiredException → the AUTH_TOKEN_EXPIRED branch below (now
            // REACHABLE, previously dead because FromJWT swallowed expiry to null). Any OTHER invalid
            // token still swallows to null in FromJWT, so guard it into a generic 401 via the
            // catch(Exception) branch rather than NRE-ing on res.Permissions (also fixes the latent
            // bad-signature NRE on the pre-existing path).
            var res = _authService.GetUserByTokenEnforcingLifetime(token, rethrowExpiry: true);
            if (res == null)
            {
                throw new SecurityTokenValidationException("Invalid token");
            }
            var hasPermission = res.Permissions.Contains(Permission);
            if (!string.IsNullOrEmpty(res.BattleTag) && res.IsAdmin && hasPermission)
            {
                await next.Invoke();
            }
            else
            {
                Log.Warning($"Permission {Permission} missing for {res.BattleTag}.");
                throw new SecurityTokenValidationException("Permission missing.");
            }
        }
        catch (SecurityTokenExpiredException)
        {
            var unauthorizedResult = new UnauthorizedObjectResult(new
            {
                StatusCode = HttpStatusCode.Unauthorized,
                Error = "AUTH_TOKEN_EXPIRED",
                Message = "Token expired."
            });
            context.Result = unauthorizedResult;
        }
        catch (Exception ex)
        {
            Log.Warning($"Permission {Permission} missing.");
            var unauthorizedResult = new UnauthorizedObjectResult(new ErrorResult(ex.Message));
            context.Result = unauthorizedResult;
        }
    }

    public static string GetToken(StringValues authorization)
    {
        if (AuthenticationHeaderValue.TryParse(authorization, out var headerValue))
        {
            if (headerValue.Scheme == "Bearer")
            {
                return headerValue.Parameter;
            }
        }
        throw new SecurityTokenValidationException("Invalid token");
    }
}

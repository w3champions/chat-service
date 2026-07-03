using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Sessions;

[ApiController]
[Route("auth")]
public class AuthSessionController(
    IW3CAuthenticationService authService,
    ITicketStore ticketStore,
    MintRateLimiter rateLimiter) : ControllerBase
{
    // Contract §3 (pinned): Bearer W3C JWT in; signature + exp enforced (tolerant permission
    // parse inside FromJWT); one-time 60s ticket out. Called from launcher Rust only (L1).
    [HttpPost("session")]
    public IActionResult MintTicket()
    {
        var now = DateTime.UtcNow;
        // Per-IP shield FIRST (cheap, pre-validation). UseForwardedHeaders (Startup) already
        // rewrites RemoteIpAddress from X-Forwarded-For, so this keys on the real client IP.
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!rateLimiter.TryAcquire($"ip:{ip}", ChatLimits.TicketMintPerIpLimit, now))
            return StatusCode(StatusCodes.Status429TooManyRequests);

        string token;
        try { token = UserHasPermissionFilter.GetToken(Request.Headers[HeaderNames.Authorization]); }
        catch (SecurityTokenValidationException) { return Unauthorized(); }

        var identity = authService.GetUserByTokenEnforcingLifetime(token);
        if (identity == null) return Unauthorized();

        if (!rateLimiter.TryAcquire($"bt:{identity.BattleTag}", ChatLimits.TicketMintPerBattleTagLimit, now))
            return StatusCode(StatusCodes.Status429TooManyRequests);

        return Ok(new TicketResponse
        {
            Ticket = ticketStore.Mint(identity, now),
            ExpiresInSeconds = (int)ChatLimits.TicketTtl.TotalSeconds,
        });
    }
}

public class TicketResponse
{
    public string Ticket { get; set; }
    public int ExpiresInSeconds { get; set; }
}

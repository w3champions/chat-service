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
        // Per-IP shield FIRST (cheap, pre-validation). UseForwardedHeaders (Startup) rewrites
        // RemoteIpAddress from X-Forwarded-For when the forwarding proxy is trusted (the hardcoded
        // trust boundary — Russia gateway + Docker network, see Startup.Configure), so this
        // keys on the real client IP; behind an UNtrusted proxy every client collapses to the proxy IP.
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ipKey = $"ip:{ip}";

        // F1 reconnect-storm rework: the per-IP budget counts ONLY the two CHEAP rejection paths — a
        // malformed/non-Bearer header and a signature failure (see the Record() calls below). Neither a
        // successful mint NOR a per-battleTag-throttled attempt charges it.
        // WHY successful mints aren't charged: a legitimate mass reconnect of thousands of DISTINCT
        // valid battleTags behind ONE shared proxy IP must not be IP-throttled — each valid user is
        // already bounded by the per-battleTag 10/min cap.
        // WHY throttled attempts aren't either: they required a validly signed token, so they are not
        // the cheap pre-validation attack this shield targets, and charging them let ONE flapping client
        // lock out every other user behind a shared address (see the branch below).
        // The pre-validation DoS shield is preserved: after TicketMintPerIpLimit cheap REJECTIONS per
        // window per IP, further attempts short-circuit here BEFORE the expensive RSA validation.
        // IsAtLimit is a pure read; the charge is deferred to those rejection branches so the whole
        // valid path stays IP-charge-free.
        if (rateLimiter.IsAtLimit(ipKey, ChatLimits.TicketMintPerIpLimit, now))
            return StatusCode(StatusCodes.Status429TooManyRequests);

        string token;
        try { token = UserHasPermissionFilter.GetToken(Request.Headers[HeaderNames.Authorization]); }
        catch (SecurityTokenValidationException) { rateLimiter.Record(ipKey, now); return Unauthorized(); }

        // `exp` enforcement here is governed by ChatLimits.EnforceJwtLifetimeOnTicketMint (see that
        // const for the full rationale). Both overloads verify the SIGNATURE identically — an
        // unverifiable token is rejected either way; the toggle only decides whether a validly-signed
        // but expired token may still mint. Ternary (not `if`) so the compile-time const cannot make
        // either branch unreachable code.
        var identity = ChatLimits.EnforceJwtLifetimeOnTicketMint
            ? authService.GetUserByTokenEnforcingLifetime(token)
            : authService.GetUserByToken(token);
        if (identity == null) { rateLimiter.Record(ipKey, now); return Unauthorized(); }

        // Per-battleTag throttling does NOT charge the per-IP budget. Reaching this line required a
        // VALIDLY SIGNED token, so the attempt already paid full RSA validation — it was never the cheap
        // pre-validation attack the IP shield exists to stop, and the per-battleTag cap is itself the
        // correct bound for an identity we have already proven. Charging the shared IP budget here let a
        // SINGLE flapping client (10 successes + 30 throttled attempts ≈ 40 requests inside one window)
        // exhaust the budget and 429 every OTHER user behind the same address — behind CGNAT or a shared
        // proxy, unrelated users who did nothing wrong. The shield keeps its teeth where it matters: the
        // two genuinely cheap rejection paths above (malformed header, signature failure) still charge.
        if (!rateLimiter.TryAcquire($"bt:{identity.BattleTag}", ChatLimits.TicketMintPerBattleTagLimit, now))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        // Success: mint and return WITHOUT charging the per-IP budget (F1 — see the shield comment above).
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

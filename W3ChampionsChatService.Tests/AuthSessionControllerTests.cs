using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Sessions;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Contract §3's REST face (C2). Constructs <see cref="AuthSessionController"/> directly with a
/// <see cref="DefaultHttpContext"/> — no TestHost, mirroring the <c>MuteReconciliationTests</c>
/// direct-controller precedent — using the REAL <see cref="W3CAuthenticationService"/> via the
/// Task-1 internal ctor keyed to a freshly-generated RSA keypair.
/// </summary>
public class AuthSessionControllerTests
{
    /// <summary>
    /// Builds a JWT signed with a freshly-generated RSA keypair, carrying the same claim shape the
    /// identification-service emits (mirrors <c>AuthenticationTests.CreateSignedJwt</c>). Returns the
    /// token plus the matching public-key PEM that <see cref="W3CAuthenticationService"/> validates against.
    /// </summary>
    private static (string jwt, string publicKeyPem) CreateSignedJwt(
        string battleTag, bool isAdmin, IEnumerable<string> permissions, DateTime? expires = null)
    {
        using var rsa = RSA.Create(2048);
        var publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();
        return (SignJwt(rsa, battleTag, isAdmin, permissions, expires), publicKeyPem);
    }

    /// <summary>
    /// Signs one token with an EXISTING keypair so a test can mint MANY distinct battleTags that all
    /// validate against a single <see cref="W3CAuthenticationService"/> (the per-key-per-token
    /// <see cref="CreateSignedJwt"/> can't do that — each of its calls generates a fresh keypair).
    /// </summary>
    private static string SignJwt(
        RSA rsa, string battleTag, bool isAdmin, IEnumerable<string> permissions, DateTime? expires = null)
    {
        var signingCredentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };

        var token = new JwtSecurityToken(
            claims: new[]
            {
                new Claim("battleTag", battleTag),
                new Claim("isAdmin", isAdmin.ToString()),
                new Claim("name", battleTag.Split('#')[0]),
                new Claim("permissions", JsonSerializer.Serialize(permissions.ToList()), JsonClaimValueTypes.JsonArray),
            },
            signingCredentials: signingCredentials,
            expires: expires ?? DateTime.UtcNow.AddDays(7));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static DefaultHttpContext BuildHttpContext(string authorizationHeader, string remoteIp)
    {
        var context = new DefaultHttpContext();
        if (authorizationHeader != null)
        {
            context.Request.Headers[HeaderNames.Authorization] = authorizationHeader;
        }
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        return context;
    }

    private static AuthSessionController BuildController(
        IW3CAuthenticationService authService, ITicketStore ticketStore, MintRateLimiter limiter,
        string authorizationHeader, string remoteIp = "127.0.0.1") =>
        new(authService, ticketStore, limiter)
        {
            ControllerContext = new ControllerContext { HttpContext = BuildHttpContext(authorizationHeader, remoteIp) }
        };

    [Test]
    public void ValidJwt_Returns200_WithSingleUseTicketAnd60Seconds()
    {
        var (jwt, publicKeyPem) = CreateSignedJwt("peter#123", true, new[] { "Moderation" });
        var authService = new W3CAuthenticationService(publicKeyPem);
        var ticketStore = new TicketStore();
        var limiter = new MintRateLimiter();
        var controller = BuildController(authService, ticketStore, limiter, $"Bearer {jwt}");

        var result = controller.MintTicket();

        var okResult = result as OkObjectResult;
        Assert.IsNotNull(okResult, "A valid, non-expired JWT must mint a ticket (200)");
        Assert.AreEqual(200, okResult.StatusCode);
        var response = okResult.Value as TicketResponse;
        Assert.IsNotNull(response, "Response body must be a TicketResponse");
        Assert.AreEqual((int)ChatLimits.TicketTtl.TotalSeconds, response.ExpiresInSeconds,
            "ExpiresInSeconds must derive from ChatLimits.TicketTtl, not a literal");
        Assert.IsNotNull(response.Ticket);

        // The ticket must be single-use against the SAME store instance the controller minted from.
        var consumed = ticketStore.TryConsume(response.Ticket, DateTime.UtcNow, out var identity);
        Assert.IsTrue(consumed, "The minted ticket must be consumable from the same store");
        Assert.IsNotNull(identity);
        Assert.AreEqual("peter#123", identity.BattleTag);

        var secondConsume = ticketStore.TryConsume(response.Ticket, DateTime.UtcNow, out var secondIdentity);
        Assert.IsFalse(secondConsume, "A second consume of the same ticket must fail (single-use)");
        Assert.IsNull(secondIdentity);
    }

    [Test]
    public void ExpiredJwt_Returns401()
    {
        var (jwt, publicKeyPem) = CreateSignedJwt("peter#123", true, new[] { "Moderation" },
            expires: DateTime.UtcNow.AddMinutes(-10));
        var authService = new W3CAuthenticationService(publicKeyPem);
        var ticketStore = new TicketStore();
        var limiter = new MintRateLimiter();
        var controller = BuildController(authService, ticketStore, limiter, $"Bearer {jwt}");

        var result = controller.MintTicket();

        Assert.IsInstanceOf<UnauthorizedResult>(result, "An expired JWT must be rejected with 401");
        Assert.AreEqual(0, ticketStore.Count, "No ticket must be minted for an expired JWT");
    }

    [Test]
    public void MissingAuthorizationHeader_Returns401()
    {
        var (_, publicKeyPem) = CreateSignedJwt("unused#1", false, Array.Empty<string>());
        var authService = new W3CAuthenticationService(publicKeyPem);
        var ticketStore = new TicketStore();
        var limiter = new MintRateLimiter();
        var controller = BuildController(authService, ticketStore, limiter, authorizationHeader: null);

        var result = controller.MintTicket();

        Assert.IsInstanceOf<UnauthorizedResult>(result, "A missing Authorization header must return 401");
        Assert.AreEqual(0, ticketStore.Count);
    }

    [Test]
    public void NonBearerScheme_Returns401()
    {
        var (_, publicKeyPem) = CreateSignedJwt("unused#1", false, Array.Empty<string>());
        var authService = new W3CAuthenticationService(publicKeyPem);
        var ticketStore = new TicketStore();
        var limiter = new MintRateLimiter();
        var controller = BuildController(authService, ticketStore, limiter, "Basic xyz");

        var result = controller.MintTicket();

        Assert.IsInstanceOf<UnauthorizedResult>(result, "A non-Bearer scheme must return 401");
        Assert.AreEqual(0, ticketStore.Count);
    }

    [Test]
    public void GarbageToken_Returns401()
    {
        var (_, publicKeyPem) = CreateSignedJwt("unused#1", false, Array.Empty<string>());
        var authService = new W3CAuthenticationService(publicKeyPem);
        var ticketStore = new TicketStore();
        var limiter = new MintRateLimiter();
        var controller = BuildController(authService, ticketStore, limiter, "Bearer not.a.jwt");

        var result = controller.MintTicket();

        Assert.IsInstanceOf<UnauthorizedResult>(result, "A garbage (unparseable) JWT must return 401");
        Assert.AreEqual(0, ticketStore.Count);
    }

    [Test]
    public void UnknownPermissionJwt_StillMints()
    {
        // Acceptance 2 at the endpoint: identification-service can grant permissions chat-service's
        // EPermission enum doesn't recognize yet — the tolerant parse in FromJWT must not sink the mint.
        var (jwt, publicKeyPem) = CreateSignedJwt("moderator#123", true, new[] { "Moderation", "Warnings" });
        var authService = new W3CAuthenticationService(publicKeyPem);
        var ticketStore = new TicketStore();
        var limiter = new MintRateLimiter();
        var controller = BuildController(authService, ticketStore, limiter, $"Bearer {jwt}");

        var result = controller.MintTicket();

        Assert.IsInstanceOf<OkObjectResult>(result, "An unrecognized permission must not block minting");
    }

    [Test]
    public void EleventhMint_SameBattleTag_Returns429()
    {
        // Acceptance 7: per-battleTag mint limit is 10/min. The per-IP limit (30) is not hit here,
        // isolating the per-battleTag limiter.
        var (jwt, publicKeyPem) = CreateSignedJwt("peter#123", true, new[] { "Moderation" });
        var authService = new W3CAuthenticationService(publicKeyPem);
        var ticketStore = new TicketStore();
        var limiter = new MintRateLimiter();

        for (var i = 0; i < ChatLimits.TicketMintPerBattleTagLimit; i++)
        {
            var controller = BuildController(authService, ticketStore, limiter, $"Bearer {jwt}");
            var result = controller.MintTicket();
            Assert.IsInstanceOf<OkObjectResult>(result, $"mint {i + 1} of {ChatLimits.TicketMintPerBattleTagLimit} should succeed");
        }

        var eleventh = BuildController(authService, ticketStore, limiter, $"Bearer {jwt}").MintTicket();

        Assert.IsInstanceOf<StatusCodeResult>(eleventh, "The 11th mint within the window must be rate-limited");
        Assert.AreEqual(StatusCodes.Status429TooManyRequests, ((StatusCodeResult)eleventh).StatusCode);
    }

    [Test]
    public void IpLimitExhausted_Returns429_BeforeJwtValidation()
    {
        // Pins BOTH the per-IP limit AND the check ordering: the per-IP shield runs before any JWT
        // work, so a GARBAGE Authorization header must still surface as 429, never 401. F1: the budget
        // is now filled via Record() (how a REJECTION charges it in production), not TryAcquire.
        var (_, publicKeyPem) = CreateSignedJwt("unused#1", false, Array.Empty<string>());
        var authService = new W3CAuthenticationService(publicKeyPem);
        var ticketStore = new TicketStore();
        var limiter = new MintRateLimiter();
        var now = DateTime.UtcNow;

        for (var i = 0; i < ChatLimits.TicketMintPerIpLimit; i++)
        {
            limiter.Record("ip:127.0.0.1", now);
        }
        Assert.IsTrue(limiter.IsAtLimit("ip:127.0.0.1", ChatLimits.TicketMintPerIpLimit, now),
            "the per-IP budget must be at limit after TicketMintPerIpLimit recorded rejections");

        var controller = BuildController(authService, ticketStore, limiter, "Bearer garbage.garbage.garbage");

        var result = controller.MintTicket();

        Assert.IsInstanceOf<StatusCodeResult>(result, "An exhausted per-IP limit must short-circuit before JWT parsing");
        Assert.AreEqual(StatusCodes.Status429TooManyRequests, ((StatusCodeResult)result).StatusCode,
            "Must be 429, not 401 — proves the IP shield runs before JWT validation");
        Assert.AreEqual(0, ticketStore.Count);
    }

    // ── F1 reconnect-storm rework: the per-IP budget counts ONLY REJECTED mint attempts ────────────
    //
    // A SUCCESSFUL mint (valid, non-expired JWT under the per-battleTag cap) must NOT charge the per-IP
    // budget, so a legitimate mass reconnect of many DISTINCT valid battleTags behind one shared proxy
    // IP is never IP-throttled. Only auth failures and per-battleTag-throttled attempts charge it,
    // keeping the pre-validation DoS shield intact.

    [Test]
    public void ManyDistinctValidBattleTags_FromOneIp_AllMint_PastTheOldPerIpCap()
    {
        // The core F1 acceptance: mint from FAR MORE distinct valid battleTags than the old per-IP cap
        // (30), all from ONE IP. Because successful mints never charge the per-IP budget, every one
        // succeeds — each is bounded only by the per-battleTag 10/min cap (one mint per tag here).
        using var rsa = RSA.Create(2048);
        var publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();
        var authService = new W3CAuthenticationService(publicKeyPem);
        var ticketStore = new TicketStore();
        var limiter = new MintRateLimiter();
        const string sharedProxyIp = "10.0.0.9";

        var total = ChatLimits.TicketMintPerIpLimit + 10; // 40 > the old 30 per-IP cap
        for (var i = 0; i < total; i++)
        {
            var jwt = SignJwt(rsa, $"player{i}#{i}", isAdmin: false, new[] { "Moderation" });
            var result = BuildController(authService, ticketStore, limiter, $"Bearer {jwt}", sharedProxyIp).MintTicket();
            Assert.IsInstanceOf<OkObjectResult>(result,
                $"valid mint {i + 1} of {total} from one IP must succeed — successful mints never charge the per-IP budget");
        }

        Assert.AreEqual(total, ticketStore.Count, "every distinct valid battleTag minted a ticket");
        Assert.IsFalse(limiter.IsAtLimit($"ip:{sharedProxyIp}", ChatLimits.TicketMintPerIpLimit, DateTime.UtcNow),
            "the per-IP budget must remain unspent after a storm of SUCCESSFUL mints");
    }

    [Test]
    public void InvalidTokens_FromOneIp_ChargeTheBudget_ThenBlockPreValidation()
    {
        // The shield is preserved: rejected attempts (garbage tokens → 401) DO charge the per-IP budget.
        // After TicketMintPerIpLimit rejections from one IP, the next attempt short-circuits with 429
        // BEFORE any JWT parsing — even though its header is garbage (which in isolation is a 401).
        var (_, publicKeyPem) = CreateSignedJwt("unused#1", false, Array.Empty<string>());
        var authService = new W3CAuthenticationService(publicKeyPem);
        var ticketStore = new TicketStore();
        var limiter = new MintRateLimiter();
        const string attackerIp = "203.0.113.5";

        for (var i = 0; i < ChatLimits.TicketMintPerIpLimit; i++)
        {
            var rejected = BuildController(authService, ticketStore, limiter, "Bearer garbage.garbage.garbage", attackerIp).MintTicket();
            Assert.IsInstanceOf<UnauthorizedResult>(rejected, $"rejection {i + 1} returns 401 and charges the per-IP budget");
        }

        var blocked = BuildController(authService, ticketStore, limiter, "Bearer garbage.garbage.garbage", attackerIp).MintTicket();

        Assert.IsInstanceOf<StatusCodeResult>(blocked, "after the budget is exhausted by rejections, further attempts are blocked pre-validation");
        Assert.AreEqual(StatusCodes.Status429TooManyRequests, ((StatusCodeResult)blocked).StatusCode);
        Assert.AreEqual(0, ticketStore.Count, "no ticket is ever minted for these invalid tokens");
    }

    [Test]
    public void PerBattleTagThrottledMint_ChargesThePerIpBudget()
    {
        // A per-battleTag-throttled attempt is itself a REJECTION, so it charges the per-IP budget too —
        // while the 10 SUCCESSFUL mints that exhausted the per-battleTag cap did not charge it at all.
        var (jwt, publicKeyPem) = CreateSignedJwt("peter#123", true, new[] { "Moderation" });
        var authService = new W3CAuthenticationService(publicKeyPem);
        var ticketStore = new TicketStore();
        var limiter = new MintRateLimiter();
        const string ip = "198.51.100.7";

        for (var i = 0; i < ChatLimits.TicketMintPerBattleTagLimit; i++)
        {
            Assert.IsInstanceOf<OkObjectResult>(
                BuildController(authService, ticketStore, limiter, $"Bearer {jwt}", ip).MintTicket(),
                $"successful mint {i + 1} within the per-battleTag cap");
        }
        Assert.IsFalse(limiter.IsAtLimit($"ip:{ip}", 1, DateTime.UtcNow),
            "10 SUCCESSFUL mints must not have charged the per-IP budget at all (no window for the IP key)");

        var throttled = BuildController(authService, ticketStore, limiter, $"Bearer {jwt}", ip).MintTicket();

        Assert.IsInstanceOf<StatusCodeResult>(throttled, "the 11th mint is per-battleTag-throttled");
        Assert.AreEqual(StatusCodes.Status429TooManyRequests, ((StatusCodeResult)throttled).StatusCode);
        Assert.IsTrue(limiter.IsAtLimit($"ip:{ip}", 1, DateTime.UtcNow),
            "the per-battleTag-throttled rejection must have charged the per-IP budget once");
    }
}

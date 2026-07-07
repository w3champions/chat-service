using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// item 7: the moderation REST filter (<see cref="UserHasPermissionFilter"/>, gating MuteController and
/// the moderation history/channels endpoints) now enforces JWT lifetime — aligning with wb's
/// <c>BearerHasPermissionFilter</c>. Exercised with the repo's no-TestServer filter idiom (see
/// <see cref="InternalHmacAuthFilterTests"/>): a hand-built <see cref="DefaultHttpContext"/> wrapped in
/// an <see cref="ActionExecutingContext"/> with an <see cref="ActionExecutionDelegate"/> whose invocation
/// is probed, driven against the REAL <see cref="W3CAuthenticationService"/> keyed to a freshly-generated
/// RSA keypair. Pins the three behaviors that used to be broken or dead:
/// <list type="bullet">
/// <item>an EXPIRED valid-signature token → 401 with <c>AUTH_TOKEN_EXPIRED</c> (previously accepted,
/// because <c>FromJWT</c> swallowed expiry to null and the whole filter ran on the null user);</item>
/// <item>a bad-signature/garbage token → generic 401 with NO NullReferenceException (the null-guard fix);</item>
/// <item>a valid, non-expired admin token holding the permission → passes to the action.</item>
/// </list>
/// </summary>
[TestFixture]
public class UserHasPermissionFilterTests
{
    private static (string jwt, string publicKeyPem) CreateSignedJwt(
        string battleTag, bool isAdmin, IEnumerable<string> permissions, DateTime? expires = null)
    {
        using var rsa = RSA.Create(2048);
        var publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();

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

        return (new JwtSecurityTokenHandler().WriteToken(token), publicKeyPem);
    }

    private static async Task<(ActionExecutingContext Ctx, bool NextCalled)> RunFilter(
        IW3CAuthenticationService authService, EPermission permission, string authorizationHeader)
    {
        var filter = new UserHasPermissionFilter(authService) { Permission = permission };

        var http = new DefaultHttpContext();
        if (authorizationHeader != null)
        {
            http.Request.Headers[HeaderNames.Authorization] = authorizationHeader;
        }

        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var ctx = new ActionExecutingContext(
            actionContext, new List<IFilterMetadata>(), new Dictionary<string, object>(), controller: null);

        var nextCalled = false;
        ActionExecutionDelegate next = () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), controller: null));
        };

        await filter.OnActionExecutionAsync(ctx, next);
        return (ctx, nextCalled);
    }

    /// <summary>Reads a public property off a result body (an anonymous type on the expiry path,
    /// an <see cref="ErrorResult"/> on the generic path) via reflection.</summary>
    private static string ReadProperty(object value, string name) =>
        value?.GetType().GetProperty(name)?.GetValue(value) as string;

    [Test]
    public async Task ExpiredToken_ShortCircuits401_WithAuthTokenExpired()
    {
        // -10 minutes clears the default 5-minute ClockSkew, so the token is genuinely expired.
        var (jwt, publicKeyPem) = CreateSignedJwt("mod#1", isAdmin: true, new[] { "Moderation" },
            expires: DateTime.UtcNow.AddMinutes(-10));
        var authService = new W3CAuthenticationService(publicKeyPem);

        var (ctx, nextCalled) = await RunFilter(authService, EPermission.Moderation, $"Bearer {jwt}");

        Assert.IsFalse(nextCalled, "an expired token must NOT reach the moderation action");
        var result = ctx.Result as UnauthorizedObjectResult;
        Assert.IsNotNull(result, "an expired token must short-circuit with a 401");
        Assert.AreEqual("AUTH_TOKEN_EXPIRED", ReadProperty(result.Value, "Error"),
            "item 7: an expired token now surfaces the (previously DEAD) AUTH_TOKEN_EXPIRED branch");
    }

    [Test]
    public async Task GarbageToken_ShortCircuits401_Generic_NoNullReferenceException()
    {
        var (_, publicKeyPem) = CreateSignedJwt("unused#1", false, Array.Empty<string>());
        var authService = new W3CAuthenticationService(publicKeyPem);

        // "Bearer garbage.garbage.garbage" parses as a Bearer token, so GetToken does NOT throw; the JWT
        // fails validation → FromJWT swallows to null → the null-guard maps it to a generic 401 rather
        // than NRE-ing on res.Permissions (the latent bug this change fixes).
        var (ctx, nextCalled) = await RunFilter(authService, EPermission.Moderation, "Bearer garbage.garbage.garbage");

        Assert.IsFalse(nextCalled);
        var result = ctx.Result as UnauthorizedObjectResult;
        Assert.IsNotNull(result, "a bad-signature/garbage token must short-circuit with a 401, not throw an NRE");
        Assert.AreNotEqual("AUTH_TOKEN_EXPIRED", ReadProperty(result.Value, "Error"),
            "a non-expiry invalid token must be a GENERIC 401, never AUTH_TOKEN_EXPIRED");
    }

    [Test]
    public async Task ValidAdminToken_WithPermission_InvokesNext()
    {
        var (jwt, publicKeyPem) = CreateSignedJwt("mod#1", isAdmin: true, new[] { "Moderation" });
        var authService = new W3CAuthenticationService(publicKeyPem);

        var (ctx, nextCalled) = await RunFilter(authService, EPermission.Moderation, $"Bearer {jwt}");

        Assert.IsTrue(nextCalled, "a valid, non-expired admin token holding the permission must reach the action");
        Assert.IsNull(ctx.Result, "next() ran — the filter must not set a short-circuit result");
    }

    [Test]
    public async Task ValidToken_MissingPermission_ShortCircuits401_Generic()
    {
        // Valid, non-expired, but not an admin and lacking Moderation → generic 401, next NOT invoked.
        var (jwt, publicKeyPem) = CreateSignedJwt("user#1", isAdmin: false, Array.Empty<string>());
        var authService = new W3CAuthenticationService(publicKeyPem);

        var (ctx, nextCalled) = await RunFilter(authService, EPermission.Moderation, $"Bearer {jwt}");

        Assert.IsFalse(nextCalled);
        var result = ctx.Result as UnauthorizedObjectResult;
        Assert.IsNotNull(result, "a valid token missing the permission must 401");
        Assert.AreNotEqual("AUTH_TOKEN_EXPIRED", ReadProperty(result.Value, "Error"),
            "a permission-miss is a generic 401, not AUTH_TOKEN_EXPIRED");
    }

    [Test]
    public async Task MissingAuthorizationHeader_ShortCircuits401()
    {
        var (_, publicKeyPem) = CreateSignedJwt("unused#1", false, Array.Empty<string>());
        var authService = new W3CAuthenticationService(publicKeyPem);

        var (ctx, nextCalled) = await RunFilter(authService, EPermission.Moderation, authorizationHeader: null);

        Assert.IsFalse(nextCalled);
        Assert.IsInstanceOf<UnauthorizedObjectResult>(ctx.Result,
            "a missing Authorization header must 401 (GetToken throws SecurityTokenValidationException → generic 401)");
    }
}

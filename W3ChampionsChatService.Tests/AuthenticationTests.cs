using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;

namespace W3ChampionsChatService.Tests;

public class AuthenticationTests
{
    [SetUp]
    public void SetUp()
    {
        // Simple setup without complex mocking
    }

    [Test]
    public void W3CAuthenticationService_GetUserByToken_ReturnsValidUser()
    {
        var service = new W3CAuthenticationService();
        var jwt = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJiYXR0bGVUYWciOiJtb2Rtb3RvIzI4MDkiLCJpc0FkbWluIjoiVHJ1ZSIsIm5hbWUiOiJtb2Rtb3RvIn0.0rJooIabRqj_Gt0fuuW5VP6ICdV1FJfwRJYuhesou7rPqE9HWZRewm12bd4iWusa4lcYK6vp5LCr6fBj4XUc2iQ4Bo9q3qtu54Rwc-eH2m-_7VqJE6D3yLm7Gcre0NE2LHZjh7qA5zHQn5kU_ugOmcovaVJN_zVEM1wRrVwR6mkNDwIwv3f_A_3AQOB8s0rin0MS4950DnFkmM0CLQ-MMzwFHg_kKgiStSiAp-2Mlu5SijGUx8keM3ArjOj7Kplk_wxjPCkjplIfAHb5qXBpdcO5exXD7UJwETqUHu4NgH-9-GWzPPNCW5BMfzPV-BMiO1sESEb4JZUZqTSJCnAG2d1mx_yukDHR_8ZSd-rB5en2WzOdN1Fjds_M0u5BvnAaLQOzz69YURL4mnI-jiNpFNokRWYjzG-_qEVJTRtUugiCipT6SMs3SlwWujxXsNSZZU0LguOuAh4EqF9ST7m_ttOcZvg5G1RLOy6A1QzWVG06Byw-7dZvMpoHrMSqjlNcJk7XtDamAVDyUNpjrqlu_I17U5DN6f8evfBtngsSgpjeswy6ccul10HRNO210I7VejGOmEsxnIDWyF-5p-UIuOaTgMiXhElwSpkIaLGQJXHFXc859UjvqC7jSRnPWpRlYRo7UpKmCJ59fgK-SzZlbp27gN_1uhk18eEWrenn6ew";

        var result = service.GetUserByToken(jwt);

        Assert.IsNotNull(result);
        Assert.AreEqual("modmoto#2809", result.BattleTag);
        Assert.AreEqual("modmoto", result.Name);
        Assert.IsTrue(result.IsAdmin);
        Assert.IsNotNull(result.Permissions);
        Assert.IsInstanceOf<HashSet<EPermission>>(result.Permissions);
    }

    [Test]
    public void W3CAuthenticationService_GetUserByToken_InvalidToken_ReturnsNull()
    {
        var service = new W3CAuthenticationService();
        var invalidJwt = "invalid.jwt.token";

        var result = service.GetUserByToken(invalidJwt);

        Assert.IsNull(result);
    }

    [Test]
    public void W3CUserAuthentication_DefaultPermissions_IsEmptyHashSet()
    {
        var user = new W3CUserAuthentication();

        Assert.IsNotNull(user.Permissions);
        Assert.IsInstanceOf<HashSet<EPermission>>(user.Permissions);
        Assert.AreEqual(0, user.Permissions.Count);
    }

    [Test]
    public void UserHasPermissionAttribute_HasCorrectPermission()
    {
        var attribute = new UserHasPermissionAttribute(EPermission.Moderation);

        Assert.AreEqual(EPermission.Moderation, attribute.Permission);
        Assert.IsFalse(attribute.IsReusable);
    }

    [Test]
    public void UserHasPermissionFilter_GetToken_ValidBearerToken_ReturnsToken()
    {
        var authorization = new StringValues("Bearer validToken123");

        var result = UserHasPermissionFilter.GetToken(authorization);

        Assert.AreEqual("validToken123", result);
    }

    [Test]
    public void UserHasPermissionFilter_GetToken_InvalidScheme_ThrowsException()
    {
        var authorization = new StringValues("Basic validToken123");

        Assert.Throws<SecurityTokenValidationException>(() =>
            UserHasPermissionFilter.GetToken(authorization));
    }

    // ── Permission-vocabulary drift between identification-service and chat-service ────────
    //
    // identification-service can grant permissions that chat-service's EPermission enum does not yet
    // contain (e.g. "Warnings", added id-service-side in #76 after chat-service's enum was last
    // touched). Its JWT carries `permissions` as a serialized JSON array — the JWT handler expands it
    // into one claim per element on read. chat-service must tolerate an unrecognized element: the user
    // still authenticates and the permissions it DOES understand are retained. A hard Enum.Parse on an
    // unknown value throws, is swallowed by FromJWT's catch, and the whole login silently fails with
    // only "Receiver {ConnectionId} failed to authenticate" in the logs.

    /// <summary>
    /// Builds a JWT signed with a freshly-generated RSA keypair, carrying the same claim shape the
    /// identification-service emits — crucially the <c>permissions</c> claim as a serialized JSON array
    /// (<see cref="JsonClaimValueTypes.JsonArray"/>), which the handler expands into one claim per
    /// element on read. Returns the token plus the matching public-key PEM that
    /// <see cref="W3CUserAuthentication.FromJWT"/> validates against.
    /// </summary>
    private static (string jwt, string publicKeyPem) CreateSignedJwt(string battleTag, bool isAdmin, IEnumerable<string> permissions)
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
            expires: DateTime.UtcNow.AddDays(7));

        return (new JwtSecurityTokenHandler().WriteToken(token), publicKeyPem);
    }

    [Test]
    public void FromJWT_TokenWithPermissionUnknownToChatService_StillAuthenticates()
    {
        // Reproduces the production incident: a moderator was granted "Warnings" (unknown to chat-service).
        var (jwt, publicKeyPem) = CreateSignedJwt("moderator#123", isAdmin: true,
            new[] { "Moderation", "Warnings" });

        var result = W3CUserAuthentication.FromJWT(jwt, publicKeyPem);

        Assert.IsNotNull(result, "A user holding a permission unknown to chat-service must still authenticate");
        Assert.AreEqual("moderator#123", result.BattleTag);
        Assert.IsTrue(result.IsAdmin);
        Assert.IsTrue(result.Permissions.Contains(EPermission.Moderation), "Known permissions must be retained");
        Assert.AreEqual(1, result.Permissions.Count, "The unknown 'Warnings' permission must be dropped, not crash the parse");
    }

    [Test]
    public void FromJWT_TokenWithOnlyKnownPermissions_ParsesAll()
    {
        // Control: proves the JSON-array claim is expanded into per-element claims and all known
        // permissions parse. Passes both before and after the tolerant-parse fix.
        var (jwt, publicKeyPem) = CreateSignedJwt("admin#1", isAdmin: true,
            new[] { "Moderation", "Queue" });

        var result = W3CUserAuthentication.FromJWT(jwt, publicKeyPem);

        Assert.IsNotNull(result);
        CollectionAssert.AreEquivalent(new[] { EPermission.Moderation, EPermission.Queue }, result.Permissions);
    }
}

using System.Collections.Generic;
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
}

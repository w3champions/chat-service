using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Chats;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// SECURITY tests for <see cref="ChatHubPermissionFilter"/> — the real Moderation gate on the
/// moderator-only hub methods. Verifies the resolution mechanism against the actual
/// <see cref="IW3CAuthenticationService"/> / <see cref="W3CUserAuthentication"/> API
/// (GetUserByToken → IsAdmin + Permissions).
/// <para>
/// These tests drive the REAL token source the filter uses in production:
/// <c>invocationContext.Context.GetHttpContext()</c>, which reads the connection's
/// <see cref="IHttpContextFeature"/>. They do NOT mock <c>IHttpContextAccessor</c> — that accessor
/// is null for hub-method invocations over a live socket, so a test relying on it would be a false
/// green. A revert to IHttpContextAccessor makes these tests fail (the token resolves to null →
/// HubException even for a moderator).
/// </para>
/// </summary>
public class ChatHubPermissionFilterTests
{
    private const string FakeToken = "fake.jwt.token";

    private static W3CUserAuthentication Moderator() => new()
    {
        BattleTag = "admin#1",
        Name = "admin",
        IsAdmin = true,
        Permissions = new HashSet<EPermission> { EPermission.Moderation },
    };

    private static W3CUserAuthentication NonModerator() => new()
    {
        BattleTag = "user#2",
        Name = "user",
        IsAdmin = false,
        Permissions = new HashSet<EPermission>(),
    };

    // Admin but WITHOUT the Moderation permission — must still be rejected.
    private static W3CUserAuthentication AdminWithoutModeration() => new()
    {
        BattleTag = "admin#3",
        Name = "admin3",
        IsAdmin = true,
        Permissions = new HashSet<EPermission> { EPermission.Maps },
    };

    /// <summary>
    /// Builds a HubInvocationContext whose <c>Context.GetHttpContext()</c> returns a DefaultHttpContext
    /// carrying <paramref name="token"/> in <c>Request.Query["access_token"]</c> — exactly how SignalR
    /// exposes the handshake HttpContext during a hub-method invocation. Pass <c>token = null</c> to
    /// simulate a connection with an HttpContext that has no access_token. Pass
    /// <paramref name="withHttpContext"/> = false to simulate no HttpContext at all (GetHttpContext null).
    /// </summary>
    private static HubInvocationContext BuildContext(string hubMethodName, string token = FakeToken, bool withHttpContext = true)
    {
        var methodInfo = typeof(ChatHub).GetMethod(hubMethodName)
            ?? throw new InvalidOperationException($"ChatHub has no method '{hubMethodName}'");
        var hub = new Mock<Hub>().Object;
        var serviceProvider = new Mock<IServiceProvider>().Object;

        // The connection's feature collection is where GetHttpContext() reads the HttpContext from
        // (SignalR uses the IHttpContextFeature from Microsoft.AspNetCore.Http.Connections.Features).
        var features = new FeatureCollection();
        if (withHttpContext)
        {
            var httpContext = new DefaultHttpContext();
            if (token != null)
            {
                httpContext.Request.QueryString = new QueryString($"?access_token={token}");
            }
            var httpContextFeature = new Mock<IHttpContextFeature>();
            httpContextFeature.Setup(f => f.HttpContext).Returns(httpContext);
            features.Set<IHttpContextFeature>(httpContextFeature.Object);
        }

        var callerContext = new Mock<HubCallerContext>();
        callerContext.Setup(c => c.Features).Returns(features);

        return new HubInvocationContext(callerContext.Object, serviceProvider, hub, methodInfo, Array.Empty<object>());
    }

    private static ChatHubPermissionFilter BuildFilter(W3CUserAuthentication resolved)
    {
        var authService = new Mock<IW3CAuthenticationService>();
        authService.Setup(a => a.GetUserByToken(It.IsAny<string>())).Returns(resolved);
        return new ChatHubPermissionFilter(authService.Object);
    }

    private static ValueTask<object> PassThrough(HubInvocationContext _) => new((object)"ok");

    [TestCase("BanUser")]
    [TestCase("DeleteMessage")]
    [TestCase("PurgeMessagesFromUser")]
    public void NonModerator_ProtectedMethod_ThrowsHubException(string method)
    {
        var filter = BuildFilter(NonModerator());
        var ctx = BuildContext(method);

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(ctx, PassThrough),
            $"A non-moderator must be rejected from {method}");
    }

    [TestCase("BanUser")]
    [TestCase("DeleteMessage")]
    [TestCase("PurgeMessagesFromUser")]
    public void AdminWithoutModerationPermission_ProtectedMethod_ThrowsHubException(string method)
    {
        var filter = BuildFilter(AdminWithoutModeration());
        var ctx = BuildContext(method);

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(ctx, PassThrough),
            $"An admin WITHOUT the Moderation permission must be rejected from {method}");
    }

    [TestCase("BanUser")]
    [TestCase("DeleteMessage")]
    [TestCase("PurgeMessagesFromUser")]
    public async Task Moderator_ProtectedMethod_PassesThrough(string method)
    {
        var filter = BuildFilter(Moderator());
        var ctx = BuildContext(method);

        var result = await filter.InvokeMethodAsync(ctx, PassThrough);

        Assert.AreEqual("ok", result, $"A moderator must be allowed to invoke {method}");
    }

    [Test]
    public async Task NonModerator_UnprotectedMethod_PassesThrough()
    {
        // SendMessage is NOT a protected method — a non-moderator must pass straight through.
        var filter = BuildFilter(NonModerator());
        var ctx = BuildContext("SendMessage");

        var result = await filter.InvokeMethodAsync(ctx, PassThrough);

        Assert.AreEqual("ok", result, "An unprotected method must pass through for any authenticated user");
    }

    [Test]
    public void MissingToken_ProtectedMethod_ThrowsHubException()
    {
        // HttpContext present but no access_token in the query → GetUserByToken returns null → rejected.
        var filter = BuildFilter(resolved: null);
        var ctx = BuildContext("BanUser", token: null);

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(ctx, PassThrough),
            "A missing/invalid token must be rejected from a protected method");
    }

    [Test]
    public void NoHttpContextOnConnection_ProtectedMethod_ThrowsHubException()
    {
        // GetHttpContext() returns null (no IHttpContextFeature) → token null → GetUserByToken null →
        // rejected (fail-closed). Guards against an NRE and proves the filter reads from the connection.
        var filter = BuildFilter(resolved: null);
        var ctx = BuildContext("BanUser", withHttpContext: false);

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(ctx, PassThrough),
            "A connection with no HttpContext must be rejected (fail-closed)");
    }

    [Test]
    public async Task Moderator_TokenComesFromConnectionHttpContext_NotAccessor()
    {
        // Regression guard: the moderator is allowed ONLY because the token is read from
        // Context.GetHttpContext() (the connection's IHttpContextFeature). A revert to
        // IHttpContextAccessor would yield a null token here → HubException → this test fails.
        string capturedToken = null;
        var authService = new Mock<IW3CAuthenticationService>();
        authService.Setup(a => a.GetUserByToken(It.IsAny<string>()))
            .Callback<string>(t => capturedToken = t)
            .Returns(Moderator());
        var filter = new ChatHubPermissionFilter(authService.Object);
        var ctx = BuildContext("BanUser", token: "moderator-token-xyz");

        var result = await filter.InvokeMethodAsync(ctx, PassThrough);

        Assert.AreEqual("ok", result, "Moderator must pass when the token is read from the connection HttpContext");
        Assert.AreEqual("moderator-token-xyz", capturedToken,
            "The filter must resolve the token from Context.GetHttpContext().Request.Query[\"access_token\"]");
    }
}

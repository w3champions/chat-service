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
/// SECURITY tests for <see cref="ChatHubPermissionFilter"/> — the generic, ATTRIBUTE-DRIVEN gate on
/// hub methods. The required permission is whatever the invoked method declares via
/// <c>[UserHasPermission(...)]</c>; the filter reads it off <c>invocationContext.HubMethod</c> via
/// reflection (no hardcoded method-name list). The auth resolution is verified against the real
/// <see cref="IW3CAuthenticationService"/> / <see cref="W3CUserAuthentication"/> API
/// (GetUserByToken → IsAdmin + Permissions).
/// <para>
/// The token is driven through the REAL source the filter uses in production:
/// <c>invocationContext.Context.GetHttpContext()</c>, which reads the connection's
/// <see cref="IHttpContextFeature"/>. These tests do NOT mock <c>IHttpContextAccessor</c> — that
/// accessor is null for hub-method invocations over a live socket, so a test relying on it would be a
/// false green. A revert to IHttpContextAccessor makes the token-source regression test fail.
/// </para>
/// </summary>
public class ChatHubPermissionFilterTests
{
    private const string FakeToken = "fake.jwt.token";

    /// <summary>
    /// A purpose-built hub whose methods declare different (or no) <c>[UserHasPermission]</c>
    /// attributes — so the tests can prove the required permission genuinely comes from the attribute,
    /// not a constant baked into the filter. Methods are never invoked (the filter's `next` is mocked).
    /// </summary>
    private class TestHub : Hub
    {
        [UserHasPermission(EPermission.Moderation)]
        public void RequiresModeration() { }

        [UserHasPermission(EPermission.Queue)]
        public void RequiresQueue() { }

        public void Unprotected() { }
    }

    private static W3CUserAuthentication User(bool isAdmin, params EPermission[] permissions) => new()
    {
        BattleTag = "user#1",
        Name = "user",
        IsAdmin = isAdmin,
        Permissions = new HashSet<EPermission>(permissions),
    };

    /// <summary>
    /// Builds a HubInvocationContext for <paramref name="hubMethodName"/> on <typeparamref name="THub"/>
    /// whose <c>Context.GetHttpContext()</c> returns a DefaultHttpContext carrying <paramref name="token"/>
    /// in <c>Request.Query["access_token"]</c> — exactly how SignalR exposes the handshake HttpContext
    /// during a hub-method invocation. Pass <c>token = null</c> to simulate a connection whose
    /// HttpContext has no access_token; <paramref name="withHttpContext"/> = false simulates no
    /// HttpContext at all (GetHttpContext returns null).
    /// </summary>
    private static HubInvocationContext BuildContext<THub>(string hubMethodName, string token = FakeToken, bool withHttpContext = true)
        where THub : Hub
    {
        var methodInfo = typeof(THub).GetMethod(hubMethodName)
            ?? throw new InvalidOperationException($"{typeof(THub).Name} has no method '{hubMethodName}'");
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

    // ── The real ChatHub moderator methods carry [UserHasPermission(Moderation)] ──────────

    [TestCase("BanUser")]
    [TestCase("DeleteMessage")]
    [TestCase("PurgeMessagesFromUser")]
    public void RealChatHub_ModeratorOnlyMethods_DeclareTheAttribute_AndAreEnforced(string method)
    {
        // Drives the ACTUAL ChatHub method metadata: the attribute must be present (the filter reads it),
        // and a non-moderator is rejected.
        var attrs = typeof(ChatHub).GetMethod(method)
            .GetCustomAttributes(typeof(UserHasPermissionAttribute), true);
        Assert.IsNotEmpty(attrs, $"{method} must declare [UserHasPermission] (the filter enforces what it declares)");

        var filter = BuildFilter(User(isAdmin: false));
        var ctx = BuildContext<ChatHub>(method);

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(ctx, PassThrough),
            $"A non-moderator must be rejected from {method}");
    }

    // ── Attribute-driven enforcement (purpose-built TestHub) ──────────────────────────────

    [Test]
    public void AttributedMethod_NonModerator_ThrowsHubException()
    {
        var filter = BuildFilter(User(isAdmin: false));
        var ctx = BuildContext<TestHub>(nameof(TestHub.RequiresModeration));

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(ctx, PassThrough),
            "A non-moderator must be rejected from a [UserHasPermission(Moderation)] method");
    }

    [Test]
    public void AttributedMethod_AdminWithoutThatPermission_ThrowsHubException()
    {
        // Admin, but holds a DIFFERENT permission than the one the method declares → rejected.
        var filter = BuildFilter(User(isAdmin: true, EPermission.Maps));
        var ctx = BuildContext<TestHub>(nameof(TestHub.RequiresModeration));

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(ctx, PassThrough),
            "An admin without the declared permission must be rejected");
    }

    [Test]
    public async Task AttributedMethod_AdminWithDeclaredPermission_PassesThrough()
    {
        var filter = BuildFilter(User(isAdmin: true, EPermission.Moderation));
        var ctx = BuildContext<TestHub>(nameof(TestHub.RequiresModeration));

        var result = await filter.InvokeMethodAsync(ctx, PassThrough);

        Assert.AreEqual("ok", result, "An admin with the declared permission must pass through");
    }

    [Test]
    public void AttributeDrivesTheRequiredPermission_NotAHardcodedModeration()
    {
        // The method declares Queue (not Moderation). A user holding ONLY Moderation must be REJECTED,
        // proving the required permission comes from the attribute, not a Moderation constant in the filter.
        var filter = BuildFilter(User(isAdmin: true, EPermission.Moderation));
        var ctx = BuildContext<TestHub>(nameof(TestHub.RequiresQueue));

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(ctx, PassThrough),
            "Holding Moderation must NOT satisfy a method that declares [UserHasPermission(Queue)]");
    }

    [Test]
    public async Task AttributeDrivesTheRequiredPermission_HolderOfDeclaredPermissionPasses()
    {
        // Same Queue-declaring method: a user holding Queue passes — the attribute's permission is honored.
        var filter = BuildFilter(User(isAdmin: true, EPermission.Queue));
        var ctx = BuildContext<TestHub>(nameof(TestHub.RequiresQueue));

        var result = await filter.InvokeMethodAsync(ctx, PassThrough);

        Assert.AreEqual("ok", result, "Holding the declared Queue permission must pass");
    }

    [Test]
    public async Task UnattributedMethod_NonModerator_PassesThrough_NoAuthRequired()
    {
        // No [UserHasPermission] → unprotected → passes through WITHOUT any JWT decode.
        var authService = new Mock<IW3CAuthenticationService>();
        var filter = new ChatHubPermissionFilter(authService.Object);
        var ctx = BuildContext<TestHub>(nameof(TestHub.Unprotected));

        var result = await filter.InvokeMethodAsync(ctx, PassThrough);

        Assert.AreEqual("ok", result, "An unprotected method must pass through for anyone");
        authService.Verify(a => a.GetUserByToken(It.IsAny<string>()), Times.Never,
            "An unprotected method must NOT trigger a JWT decode");
    }

    [Test]
    public async Task RealChatHub_SendMessage_IsUnprotected_PassesThrough()
    {
        // SendMessage on the real ChatHub carries no [UserHasPermission] → any user passes through.
        var authService = new Mock<IW3CAuthenticationService>();
        var filter = new ChatHubPermissionFilter(authService.Object);
        var ctx = BuildContext<ChatHub>(nameof(ChatHub.SendMessage));

        var result = await filter.InvokeMethodAsync(ctx, PassThrough);

        Assert.AreEqual("ok", result, "SendMessage is unprotected and must pass through");
        authService.Verify(a => a.GetUserByToken(It.IsAny<string>()), Times.Never);
    }

    // ── Token-source / fail-closed behavior (unchanged from the GetHttpContext fix) ───────

    [Test]
    public void MissingToken_AttributedMethod_ThrowsHubException()
    {
        // HttpContext present but no access_token in the query → GetUserByToken returns null → rejected.
        var filter = BuildFilter(resolved: null);
        var ctx = BuildContext<TestHub>(nameof(TestHub.RequiresModeration), token: null);

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(ctx, PassThrough),
            "A missing/invalid token must be rejected from an attributed method");
    }

    [Test]
    public void NoHttpContextOnConnection_AttributedMethod_ThrowsHubException()
    {
        // GetHttpContext() returns null (no IHttpContextFeature) → token null → GetUserByToken null →
        // rejected (fail-closed). Guards against an NRE and proves the filter reads from the connection.
        var filter = BuildFilter(resolved: null);
        var ctx = BuildContext<TestHub>(nameof(TestHub.RequiresModeration), withHttpContext: false);

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(ctx, PassThrough),
            "A connection with no HttpContext must be rejected (fail-closed)");
    }

    [Test]
    public async Task Token_ComesFromConnectionHttpContext_NotAccessor()
    {
        // Regression guard: the moderator is allowed ONLY because the token is read from
        // Context.GetHttpContext() (the connection's IHttpContextFeature). A revert to
        // IHttpContextAccessor would yield a null token here → HubException → this test fails.
        string capturedToken = null;
        var authService = new Mock<IW3CAuthenticationService>();
        authService.Setup(a => a.GetUserByToken(It.IsAny<string>()))
            .Callback<string>(t => capturedToken = t)
            .Returns(User(isAdmin: true, EPermission.Moderation));
        var filter = new ChatHubPermissionFilter(authService.Object);
        var ctx = BuildContext<TestHub>(nameof(TestHub.RequiresModeration), token: "moderator-token-xyz");

        var result = await filter.InvokeMethodAsync(ctx, PassThrough);

        Assert.AreEqual("ok", result, "Moderator must pass when the token is read from the connection HttpContext");
        Assert.AreEqual("moderator-token-xyz", capturedToken,
            "The filter must resolve the token from Context.GetHttpContext().Request.Query[\"access_token\"]");
    }
}

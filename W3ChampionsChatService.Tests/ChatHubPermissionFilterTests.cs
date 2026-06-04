using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
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
/// </summary>
public class ChatHubPermissionFilterTests
{
    private const string FakeToken = "fake.jwt.token";

    private static IHttpContextAccessor ContextAccessorWithToken(string token)
    {
        var httpContext = new DefaultHttpContext();
        if (token != null)
        {
            httpContext.Request.QueryString = new QueryString($"?access_token={token}");
        }
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns(httpContext);
        return accessor.Object;
    }

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

    private static HubInvocationContext BuildContext(string hubMethodName)
    {
        var methodInfo = typeof(ChatHub).GetMethod(hubMethodName)
            ?? throw new InvalidOperationException($"ChatHub has no method '{hubMethodName}'");
        var hub = new Mock<Hub>().Object;
        var callerContext = new Mock<HubCallerContext>().Object;
        var serviceProvider = new Mock<IServiceProvider>().Object;
        return new HubInvocationContext(callerContext, serviceProvider, hub, methodInfo, Array.Empty<object>());
    }

    private static ChatHubPermissionFilter BuildFilter(W3CUserAuthentication resolved, string token = FakeToken)
    {
        var authService = new Mock<IW3CAuthenticationService>();
        authService.Setup(a => a.GetUserByToken(It.IsAny<string>())).Returns(resolved);
        return new ChatHubPermissionFilter(authService.Object, ContextAccessorWithToken(token));
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
        // No token in the query → GetUserByToken returns null (auth failure) → rejected.
        var authService = new Mock<IW3CAuthenticationService>();
        authService.Setup(a => a.GetUserByToken(It.IsAny<string>())).Returns((W3CUserAuthentication)null);
        var filter = new ChatHubPermissionFilter(authService.Object, ContextAccessorWithToken(null));
        var ctx = BuildContext("BanUser");

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(ctx, PassThrough),
            "A missing/invalid token must be rejected from a protected method");
    }
}

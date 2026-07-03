using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Sessions;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// SECURITY tests for <see cref="ChatHubPermissionFilter"/> — the generic, ATTRIBUTE-DRIVEN gate on
/// hub methods. The required permission is whatever the invoked method declares via
/// <c>[UserHasPermission(...)]</c>; the filter reads it off <c>invocationContext.HubMethod</c> via
/// reflection (no hardcoded method-name list). The authorization decision requires the caller's
/// identity to be an admin AND to hold the declared permission.
/// <para>
/// C2: identity is resolved through the REAL production path — a per-connection session snapshot in
/// <see cref="ISessionRegistry"/>, keyed by <c>Context.ConnectionId</c>. These tests seed identity via
/// a real <see cref="SessionRegistry"/> (<c>Register(connectionId, identity, null)</c>) and drive the
/// caller's connectionId through a <see cref="HubCallerContext"/> that stubs ONLY <c>.ConnectionId</c>.
/// No JWT, no HttpContext, and no <c>IHttpContextAccessor</c> are involved: the filter never reads the
/// query string anymore, so a fail-closed connection is one with no (or a displaced) registry session.
/// </para>
/// </summary>
public class ChatHubPermissionFilterTests
{
    /// <summary>
    /// A purpose-built hub whose methods declare different (or no) <c>[UserHasPermission]</c>
    /// attributes — so the tests can prove the required permission genuinely comes from the attribute,
    /// not a constant baked into the filter. Methods are never invoked (the filter's `next` is stubbed).
    /// </summary>
    private class TestHub : Hub
    {
        [UserHasPermission(EPermission.Moderation)]
        public void RequiresModeration() { }

        [UserHasPermission(EPermission.Queue)]
        public void RequiresQueue() { }

        public void Unprotected() { }
    }

    private static W3CUserAuthentication Identity(bool isAdmin, params EPermission[] permissions) =>
        Identity("user#1", isAdmin, permissions);

    private static W3CUserAuthentication Identity(string battleTag, bool isAdmin, params EPermission[] permissions) => new()
    {
        BattleTag = battleTag,
        Name = "user",
        IsAdmin = isAdmin,
        Permissions = new HashSet<EPermission>(permissions),
    };

    /// <summary>
    /// Builds a real <see cref="HubInvocationContext"/> for <paramref name="hubMethodName"/> on
    /// <typeparamref name="THub"/> whose <c>Context.ConnectionId</c> is <paramref name="connectionId"/>.
    /// Nothing else on the context is stubbed — the filter resolves identity from the registry by this
    /// connectionId alone (no FeatureCollection, no HttpContext, no accessor).
    /// </summary>
    private static HubInvocationContext BuildContext<THub>(string hubMethodName, string connectionId)
        where THub : Hub
    {
        var methodInfo = typeof(THub).GetMethod(hubMethodName)
            ?? throw new InvalidOperationException($"{typeof(THub).Name} has no method '{hubMethodName}'");
        var hub = new Mock<Hub>().Object;
        var serviceProvider = new Mock<IServiceProvider>().Object;

        var callerContext = new Mock<HubCallerContext>();
        callerContext.Setup(c => c.ConnectionId).Returns(connectionId);

        return new HubInvocationContext(callerContext.Object, serviceProvider, hub, methodInfo, Array.Empty<object>());
    }

    /// <summary>
    /// A filter backed by a REAL registry in which <paramref name="connectionId"/> maps to
    /// <paramref name="identity"/> — the exact seam the hub uses at connect (<c>Register(..., null)</c>
    /// context is fine here; the filter never touches it).
    /// </summary>
    private static ChatHubPermissionFilter FilterWith(string connectionId, W3CUserAuthentication identity)
    {
        var registry = new SessionRegistry();
        registry.Register(connectionId, identity, null);
        return new ChatHubPermissionFilter(registry);
    }

    private static ValueTask<object> PassThrough(HubInvocationContext _) => new((object)"ok");

    // ── The real ChatHub moderator methods carry [UserHasPermission(Moderation)] ──────────

    [TestCase("BanUser")]
    [TestCase("DeleteMessage")]
    [TestCase("PurgeMessagesFromUser")]
    public void RealChatHub_ModeratorOnlyMethods_DeclareTheAttribute_AndAreEnforced(string method)
    {
        // Drives the ACTUAL ChatHub method metadata: the attribute must be present (the filter reads it),
        // and a REGISTERED non-moderator connection is rejected (acceptance 6).
        var attrs = typeof(ChatHub).GetMethod(method)
            .GetCustomAttributes(typeof(UserHasPermissionAttribute), true);
        Assert.IsNotEmpty(attrs, $"{method} must declare [UserHasPermission] (the filter enforces what it declares)");

        const string connectionId = "conn-nonmod";
        var filter = FilterWith(connectionId, Identity(isAdmin: false));
        var ctx = BuildContext<ChatHub>(method, connectionId);

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(ctx, PassThrough),
            $"A registered non-moderator must be rejected from {method}");
    }

    // ── Attribute-driven enforcement (purpose-built TestHub) ──────────────────────────────

    [Test]
    public void AttributedMethod_NonModerator_ThrowsHubException()
    {
        const string connectionId = "conn-1";
        var filter = FilterWith(connectionId, Identity(isAdmin: false));
        var ctx = BuildContext<TestHub>(nameof(TestHub.RequiresModeration), connectionId);

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(ctx, PassThrough),
            "A non-moderator must be rejected from a [UserHasPermission(Moderation)] method");
    }

    [Test]
    public void AttributedMethod_AdminWithoutThatPermission_ThrowsHubException()
    {
        // Admin, but holds a DIFFERENT permission than the one the method declares → rejected.
        const string connectionId = "conn-1";
        var filter = FilterWith(connectionId, Identity(isAdmin: true, EPermission.Maps));
        var ctx = BuildContext<TestHub>(nameof(TestHub.RequiresModeration), connectionId);

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(ctx, PassThrough),
            "An admin without the declared permission must be rejected");
    }

    [Test]
    public void AttributedMethod_NonAdminHoldingPermission_ThrowsHubException()
    {
        // Independently pins the IsAdmin conjunct: a NON-admin who nonetheless holds the declared
        // permission must STILL be rejected (authorization is IsAdmin AND permission, not permission alone).
        // Mutation-sensitive: removing `!session.Identity.IsAdmin` from the filter must make exactly this
        // test fail — every other non-admin case also lacks the permission, so only this one isolates the
        // IsAdmin half of the conjunct.
        const string connectionId = "conn-1";
        var filter = FilterWith(connectionId, Identity(isAdmin: false, EPermission.Moderation));
        var ctx = BuildContext<TestHub>(nameof(TestHub.RequiresModeration), connectionId);

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(ctx, PassThrough),
            "A non-admin holding the declared permission must STILL be rejected (IsAdmin is required)");
    }

    [Test]
    public async Task AttributedMethod_AdminWithDeclaredPermission_PassesThrough()
    {
        const string connectionId = "conn-1";
        var filter = FilterWith(connectionId, Identity(isAdmin: true, EPermission.Moderation));
        var ctx = BuildContext<TestHub>(nameof(TestHub.RequiresModeration), connectionId);

        var result = await filter.InvokeMethodAsync(ctx, PassThrough);

        Assert.AreEqual("ok", result, "An admin with the declared permission must pass through");
    }

    [Test]
    public void AttributeDrivesTheRequiredPermission_NotAHardcodedModeration()
    {
        // The method declares Queue (not Moderation). A user holding ONLY Moderation must be REJECTED,
        // proving the required permission comes from the attribute, not a Moderation constant in the filter.
        const string connectionId = "conn-1";
        var filter = FilterWith(connectionId, Identity(isAdmin: true, EPermission.Moderation));
        var ctx = BuildContext<TestHub>(nameof(TestHub.RequiresQueue), connectionId);

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(ctx, PassThrough),
            "Holding Moderation must NOT satisfy a method that declares [UserHasPermission(Queue)]");
    }

    [Test]
    public async Task AttributeDrivesTheRequiredPermission_HolderOfDeclaredPermissionPasses()
    {
        // Same Queue-declaring method: a user holding Queue passes — the attribute's permission is honored.
        const string connectionId = "conn-1";
        var filter = FilterWith(connectionId, Identity(isAdmin: true, EPermission.Queue));
        var ctx = BuildContext<TestHub>(nameof(TestHub.RequiresQueue), connectionId);

        var result = await filter.InvokeMethodAsync(ctx, PassThrough);

        Assert.AreEqual("ok", result, "Holding the declared Queue permission must pass");
    }

    // ── Un-attributed methods bypass ALL identity work (acceptance 6, second half) ────────

    [Test]
    public async Task UnattributedMethod_SkipsIdentityResolution_Entirely()
    {
        // MockBehavior.Strict with NO setups: any call into the registry throws. The method has no
        // [UserHasPermission], so passing straight through PROVES the filter never consulted the registry.
        var registry = new Mock<ISessionRegistry>(MockBehavior.Strict);
        var filter = new ChatHubPermissionFilter(registry.Object);
        var ctx = BuildContext<TestHub>(nameof(TestHub.Unprotected), "conn-unprotected");

        var result = await filter.InvokeMethodAsync(ctx, PassThrough);

        Assert.AreEqual("ok", result, "An unprotected method must pass through for anyone");
        registry.VerifyNoOtherCalls();
    }

    [Test]
    public async Task RealChatHub_SendMessage_IsUnprotected_PassesThrough()
    {
        // SendMessage on the real ChatHub carries no [UserHasPermission] → any connection passes through,
        // and the strict mock proves no identity resolution happens.
        var registry = new Mock<ISessionRegistry>(MockBehavior.Strict);
        var filter = new ChatHubPermissionFilter(registry.Object);
        var ctx = BuildContext<ChatHub>(nameof(ChatHub.SendMessage), "conn-send");

        var result = await filter.InvokeMethodAsync(ctx, PassThrough);

        Assert.AreEqual("ok", result, "SendMessage is unprotected and must pass through");
        registry.VerifyNoOtherCalls();
    }

    // ── Fail-closed: no session, or a displaced stale connection, is rejected ─────────────

    [Test]
    public void UnregisteredConnection_AttributedMethod_ThrowsHubException()
    {
        // A real registry with NO Register for this connection → TryGetByConnectionId is false → rejected.
        var filter = new ChatHubPermissionFilter(new SessionRegistry());
        var ctx = BuildContext<TestHub>(nameof(TestHub.RequiresModeration), "conn-unregistered");

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(ctx, PassThrough),
            "A connection with no registry session must be rejected (fail-closed)");
    }

    [Test]
    public void DisplacedStaleConnection_AttributedMethod_ThrowsHubException()
    {
        // Register OLD, then register NEW for the SAME battleTag: NEW displaces OLD. Even though OLD's
        // captured identity WAS a moderator, TryGetByConnectionId(oldConn) now returns false, so invoking
        // an attributed method as the stale OLD connection is rejected — pins the Task-5 fail-closed
        // behavior at the permission filter.
        const string oldConn = "conn-old";
        const string newConn = "conn-new";
        var moderator = Identity("mod#1", isAdmin: true, EPermission.Moderation);
        var registry = new SessionRegistry();
        registry.Register(oldConn, moderator, null);
        registry.Register(newConn, moderator, null); // displaces OLD

        var filter = new ChatHubPermissionFilter(registry);
        var ctx = BuildContext<TestHub>(nameof(TestHub.RequiresModeration), oldConn);

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(ctx, PassThrough),
            "A displaced stale connection must be rejected even though its identity was a moderator");
    }

    // ── Identity is resolved per-connectionId, never from ambient state ───────────────────

    [Test]
    public async Task Identity_ComesFromRegistry_ByConnectionId()
    {
        // Two distinct sessions with different permissions live in the SAME registry; each connectionId
        // gets its own verdict. Distinguished ONLY by connectionId — the identity source is the
        // connection, not any ambient/shared state.
        const string modConn = "conn-mod";
        const string plainConn = "conn-plain";
        var registry = new SessionRegistry();
        registry.Register(modConn, Identity("mod#1", isAdmin: true, EPermission.Moderation), null);
        registry.Register(plainConn, Identity("plain#1", isAdmin: false), null);
        var filter = new ChatHubPermissionFilter(registry);

        var modResult = await filter.InvokeMethodAsync(
            BuildContext<TestHub>(nameof(TestHub.RequiresModeration), modConn), PassThrough);
        Assert.AreEqual("ok", modResult, "The moderator connection resolves to a moderator identity and passes");

        Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(
                BuildContext<TestHub>(nameof(TestHub.RequiresModeration), plainConn), PassThrough),
            "The non-moderator connection, distinguished only by its connectionId, is rejected");
    }
}

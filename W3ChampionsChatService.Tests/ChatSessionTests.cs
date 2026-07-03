using System.Collections.Generic;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Sessions;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C4 Task 1 (D2) — <see cref="ChatSession.HasPermission"/> is EXACTLY the permission-filter
/// conjunct lifted onto the session so non-hub-pipeline callers (moderator REST/hub methods in
/// later C4 tasks) can ask the same question <see cref="Authentication.ChatHubPermissionFilter"/>
/// asks. The truth table below mirrors <c>ChatHubPermissionFilterTests</c>'s IsAdmin/permission
/// pinning at the filter layer.
/// </summary>
public class ChatSessionTests
{
    private static ChatSession SessionWith(bool isAdmin, params EPermission[] permissions) => new()
    {
        ConnectionId = "conn-1",
        Identity = new W3CUserAuthentication
        {
            BattleTag = "mod#1",
            Name = "mod",
            IsAdmin = isAdmin,
            Permissions = new HashSet<EPermission>(permissions),
        },
        Context = null,
    };

    [Test]
    public void AdminWithPermission_ReturnsTrue()
    {
        var session = SessionWith(isAdmin: true, EPermission.Moderation);

        Assert.IsTrue(session.HasPermission(EPermission.Moderation));
    }

    [Test]
    public void AdminWithoutThatPermission_ReturnsFalse()
    {
        var session = SessionWith(isAdmin: true, EPermission.Maps);

        Assert.IsFalse(session.HasPermission(EPermission.Moderation));
    }

    [Test]
    public void NonAdminWithPermission_ReturnsFalse()
    {
        // Mutation-sensitive: removing the IsAdmin conjunct makes exactly this case wrongly pass.
        var session = SessionWith(isAdmin: false, EPermission.Moderation);

        Assert.IsFalse(session.HasPermission(EPermission.Moderation));
    }

    [Test]
    public void NonAdminWithoutPermission_ReturnsFalse()
    {
        var session = SessionWith(isAdmin: false);

        Assert.IsFalse(session.HasPermission(EPermission.Moderation));
    }
}

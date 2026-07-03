using System;
using System.Threading.Tasks;
using MongoDB.Driver;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Chats;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C2 (hard cutover): <see cref="ChatAuthenticationService.GetUserFromIdentity"/> enriches a
/// ticket-proven identity via the website backend — the JWT decode is gone (it happened at mint).
/// Decision 11: because the ticket ALREADY proved identity, a wb enrichment failure must NEVER fail a
/// proven-authenticated connect — it must log a warning and return a PLAIN fallback ChatUser (never
/// null). This pins that resilience contract, plus the admin color/icon forcing on the happy path.
/// <para>
/// The <see cref="MongoClient"/> is a lazily-constructed handle: <c>GetUserFromIdentity</c> performs no
/// Mongo I/O, so these tests need no live database.
/// </para>
/// </summary>
public class ChatAuthenticationServiceTests
{
    private static ChatAuthenticationService BuildService(IWebsiteBackendRepository wb) =>
        new(new MongoClient("mongodb://localhost:27017"), wb);

    [Test]
    public async Task GetUserFromIdentity_WhenEnrichmentThrows_ReturnsPlainFallback_NeverNull()
    {
        var wb = new Mock<IWebsiteBackendRepository>();
        wb.Setup(r => r.GetChatDetails(It.IsAny<string>()))
            .ThrowsAsync(new Exception("wb outage"));
        var service = BuildService(wb.Object);
        var identity = new W3CUserAuthentication { BattleTag = "peter#123", Name = "peter", IsAdmin = false };

        var user = await service.GetUserFromIdentity(identity);

        Assert.IsNotNull(user, "A wb enrichment failure must NOT fail a proven-authenticated connect");
        Assert.AreEqual("peter#123", user.BattleTag, "The fallback carries the identity's battleTag");
        Assert.IsFalse(user.IsAdmin, "The fallback carries the identity's admin flag");
        Assert.IsNotNull(user.ProfilePicture, "The fallback supplies a non-null placeholder ProfilePicture");
    }

    [Test]
    public async Task GetUserFromIdentity_Admin_ForcesAdminColorAndIcon()
    {
        var wb = new Mock<IWebsiteBackendRepository>();
        wb.Setup(r => r.GetChatDetails("admin#1"))
            .ReturnsAsync(new ChatDetailsDto("clan-x", new ProfilePicture(), new ChatColor("custom"),
                new[] { new ChatIcon("icon-a") }));
        var service = BuildService(wb.Object);
        var identity = new W3CUserAuthentication { BattleTag = "admin#1", Name = "admin", IsAdmin = true };

        var user = await service.GetUserFromIdentity(identity);

        Assert.IsNotNull(user);
        Assert.AreEqual("admin#1", user.BattleTag);
        Assert.IsTrue(user.IsAdmin);
        Assert.AreEqual(ChatColor.AdminColor, user.ChatColor, "Admins are forced to the admin chat color");
        Assert.AreEqual(ChatIcon.AdminIcon, user.ChatIcons[0], "Admins get the admin icon prepended");
        Assert.AreEqual("clan-x", user.ClanTag, "The wb clan id flows through to the ChatUser");
    }
}

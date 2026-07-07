using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Sessions;

namespace W3ChampionsChatService.Tests;

public class TicketStoreTests
{
    private static W3CUserAuthentication Identity(string bt = "peter#123") => new W3CUserAuthentication
    {
        BattleTag = bt,
        Name = "peter",
        IsAdmin = true,
        Permissions = new HashSet<EPermission> { EPermission.Moderation, EPermission.Queue }
    };

    [Test]
    public void Mint_ReturnsOpaqueUrlSafeTicket_AtLeast128Bits()
    {
        var store = new TicketStore();
        var now = DateTime.UtcNow;
        var tickets = new HashSet<string>();

        for (var i = 0; i < 100; i++)
        {
            var ticket = store.Mint(Identity(), now);

            Assert.AreEqual(64, ticket.Length);
            Assert.IsTrue(ticket.All(Uri.IsHexDigit), "ticket must be URL-safe hex");
            Assert.IsTrue(tickets.Add(ticket), "each minted ticket must be distinct");
        }
    }

    [Test]
    public void TryConsume_ValidTicket_ReturnsIdentity_ExactlyOnce()
    {
        var store = new TicketStore();
        var mintTime = DateTime.UtcNow;
        var identity = Identity();
        var ticket = store.Mint(identity, mintTime);

        var firstResult = store.TryConsume(ticket, mintTime + TimeSpan.FromSeconds(1), out var firstIdentity);

        Assert.IsTrue(firstResult);
        Assert.IsNotNull(firstIdentity);
        Assert.AreEqual(identity.BattleTag, firstIdentity.BattleTag);
        Assert.AreEqual(identity.Name, firstIdentity.Name);
        Assert.AreEqual(identity.IsAdmin, firstIdentity.IsAdmin);
        Assert.AreEqual(identity.Permissions, firstIdentity.Permissions);

        var secondResult = store.TryConsume(ticket, mintTime + TimeSpan.FromSeconds(1), out var secondIdentity);

        Assert.IsFalse(secondResult);
        Assert.IsNull(secondIdentity);
    }

    [Test]
    public void TryConsume_At59Seconds_Succeeds()
    {
        var store = new TicketStore();
        var mintTime = DateTime.UtcNow;
        var ticket = store.Mint(Identity(), mintTime);

        var result = store.TryConsume(ticket, mintTime + TimeSpan.FromSeconds(59), out var identity);

        Assert.IsTrue(result);
        Assert.IsNotNull(identity);
    }

    [Test]
    public void TryConsume_At61Seconds_Fails()
    {
        var store = new TicketStore();
        var mintTime = DateTime.UtcNow;
        var ticket = store.Mint(Identity(), mintTime);

        var result = store.TryConsume(ticket, mintTime + TimeSpan.FromSeconds(61), out var identity);

        Assert.IsFalse(result);
        Assert.IsNull(identity);
    }

    [Test]
    public void TryConsume_UnknownString_Fails()
    {
        var store = new TicketStore();
        var now = DateTime.UtcNow;

        var randomResult = store.TryConsume("this-is-not-a-known-ticket", now, out var randomIdentity);
        var jwtShapedResult = store.TryConsume("eyJhb.x.y", now, out var jwtIdentity);

        Assert.IsFalse(randomResult);
        Assert.IsNull(randomIdentity);
        Assert.IsFalse(jwtShapedResult);
        Assert.IsNull(jwtIdentity);
    }

    [Test]
    public void Mint_PurgesExpiredEntries()
    {
        var store = new TicketStore();
        var t0 = DateTime.UtcNow;
        var ticketA = store.Mint(Identity(), t0);
        store.Mint(Identity(), t0 + TimeSpan.FromSeconds(61));

        Assert.AreEqual(1, store.Count);

        var result = store.TryConsume(ticketA, t0 + TimeSpan.FromSeconds(61), out var identity);

        Assert.IsFalse(result);
        Assert.IsNull(identity);
    }
}

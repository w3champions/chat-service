using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Domain;
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
    public void TryRedeem_ValidTicket_ReturnsIdentitySnapshot()
    {
        var store = new TicketStore();
        var mintTime = DateTime.UtcNow;
        var identity = Identity();
        var ticket = store.Mint(identity, mintTime);

        var result = store.TryRedeem(ticket, "conn-1", mintTime + TimeSpan.FromSeconds(1), out var redeemed);

        Assert.IsTrue(result);
        Assert.IsNotNull(redeemed);
        Assert.AreEqual(identity.BattleTag, redeemed.BattleTag);
        Assert.AreEqual(identity.Name, redeemed.Name);
        Assert.AreEqual(identity.IsAdmin, redeemed.IsAdmin);
        Assert.AreEqual(identity.Permissions, redeemed.Permissions);
    }

    [Test]
    public void TryRedeem_AfterRelease_SucceedsAgain()
    {
        var store = new TicketStore();
        var mintTime = DateTime.UtcNow;
        var ticket = store.Mint(Identity(), mintTime);
        Assert.IsTrue(store.TryRedeem(ticket, "conn-1", mintTime, out _));

        store.Release("conn-1");

        Assert.IsTrue(store.TryRedeem(ticket, "conn-2", mintTime + TimeSpan.FromSeconds(5), out var identity),
            "A reconnect inside the TTL must be able to reuse the ticket once the first connection closed");
        Assert.AreEqual("peter#123", identity.BattleTag);
    }

    [Test]
    public void TryRedeem_WhileHeld_Fails_AndRevokesTicket()
    {
        var store = new TicketStore();
        var now = DateTime.UtcNow;
        var ticket = store.Mint(Identity(), now);
        Assert.IsTrue(store.TryRedeem(ticket, "conn-1", now, out _));

        var concurrent = store.TryRedeem(ticket, "conn-2", now, out var identity);

        Assert.IsFalse(concurrent, "A second live connection on one ticket is a replay");
        Assert.IsNull(identity);
        Assert.AreEqual(0, store.Count, "A replayed ticket must be revoked");
        store.Release("conn-1");
        Assert.IsFalse(store.TryRedeem(ticket, "conn-3", now, out _), "A revoked ticket stays revoked after the holder leaves");
    }

    [Test]
    public void Release_ByNonHolder_DoesNotFreeTicket()
    {
        var store = new TicketStore();
        var now = DateTime.UtcNow;
        var ticket = store.Mint(Identity(), now);
        Assert.IsTrue(store.TryRedeem(ticket, "conn-1", now, out _));

        store.Release("someone-else");

        Assert.IsFalse(store.TryRedeem(ticket, "conn-2", now, out _), "Only the holding connection can release a ticket");
    }

    [Test]
    public void TryRedeem_IsCappedAtMaxRedemptions()
    {
        var store = new TicketStore();
        var now = DateTime.UtcNow;
        var ticket = store.Mint(Identity(), now);

        for (var i = 0; i < ChatLimits.TicketMaxRedemptions; i++)
        {
            Assert.IsTrue(store.TryRedeem(ticket, $"conn-{i}", now, out _), $"redemption {i + 1} must succeed");
            store.Release($"conn-{i}");
        }

        Assert.IsFalse(store.TryRedeem(ticket, "conn-over", now, out var identity), "The cap bounds the replay window");
        Assert.IsNull(identity);
        Assert.AreEqual(0, store.Count, "An exhausted ticket is burned");
    }

    [Test]
    public void TryRedeem_At59Seconds_Succeeds()
    {
        var store = new TicketStore();
        var mintTime = DateTime.UtcNow;
        var ticket = store.Mint(Identity(), mintTime);

        var result = store.TryRedeem(ticket, "conn-1", mintTime + TimeSpan.FromSeconds(59), out var identity);

        Assert.IsTrue(result);
        Assert.IsNotNull(identity);
    }

    [Test]
    public void TryRedeem_At61Seconds_Fails()
    {
        var store = new TicketStore();
        var mintTime = DateTime.UtcNow;
        var ticket = store.Mint(Identity(), mintTime);

        var result = store.TryRedeem(ticket, "conn-1", mintTime + TimeSpan.FromSeconds(61), out var identity);

        Assert.IsFalse(result);
        Assert.IsNull(identity);
    }

    [Test]
    public void Reuse_DoesNotExtendTtl()
    {
        var store = new TicketStore();
        var mintTime = DateTime.UtcNow;
        var ticket = store.Mint(Identity(), mintTime);
        Assert.IsTrue(store.TryRedeem(ticket, "conn-1", mintTime + TimeSpan.FromSeconds(50), out _));
        store.Release("conn-1");

        Assert.IsFalse(store.TryRedeem(ticket, "conn-2", mintTime + TimeSpan.FromSeconds(61), out _),
            "The TTL runs from mint; a redemption must not extend it");
    }

    [Test]
    public void TryRedeem_UnknownString_Fails()
    {
        var store = new TicketStore();
        var now = DateTime.UtcNow;

        var randomResult = store.TryRedeem("this-is-not-a-known-ticket", "conn-1", now, out var randomIdentity);
        var jwtShapedResult = store.TryRedeem("eyJhb.x.y", "conn-1", now, out var jwtIdentity);

        Assert.IsFalse(randomResult);
        Assert.IsNull(randomIdentity);
        Assert.IsFalse(jwtShapedResult);
        Assert.IsNull(jwtIdentity);
    }

    [Test]
    public void TryRedeem_EmptyConnectionId_Fails_WithoutTakingTicket()
    {
        var store = new TicketStore();
        var now = DateTime.UtcNow;
        var ticket = store.Mint(Identity(), now);

        Assert.IsFalse(store.TryRedeem(ticket, "", now, out _), "A redemption must name its connection");
        Assert.IsTrue(store.TryRedeem(ticket, "conn-1", now, out _), "An empty connection id must not take the ticket");
    }

    [Test]
    public void Mint_PurgesExpiredEntries()
    {
        var store = new TicketStore();
        var t0 = DateTime.UtcNow;
        var ticketA = store.Mint(Identity(), t0);
        store.Mint(Identity(), t0 + TimeSpan.FromSeconds(61));

        Assert.AreEqual(1, store.Count);

        var result = store.TryRedeem(ticketA, "conn-1", t0 + TimeSpan.FromSeconds(61), out var identity);

        Assert.IsFalse(result);
        Assert.IsNull(identity);
    }
}

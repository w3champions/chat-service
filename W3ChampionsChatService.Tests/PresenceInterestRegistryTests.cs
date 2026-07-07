using NUnit.Framework;
using W3ChampionsChatService.FanOut;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Unit tests for <see cref="PresenceInterestRegistry"/> (C6 Task 9, D11) — the derived presence-interest
/// index. These pin the pure in-memory contract that the hub/engine wiring depends on: interest is derived
/// ONLY from focus+membership, every revocation leg drops it, the reverse read path is case-insensitive,
/// a connection never watches its own tag, and the refcount-by-channel semantics keep interest alive while
/// ANY focused channel still reaches a tag. Every assertion is falsifiable against a specific mutation.
/// </summary>
public class PresenceInterestRegistryTests
{
    private PresenceInterestRegistry registry;

    [SetUp]
    public void SetUp()
    {
        registry = new PresenceInterestRegistry();
    }

    // ---- RegisterFocus -----------------------------------------------------------------------------

    [Test]
    public void RegisterFocus_ReverseIndexPopulated_CaseInsensitive()
    {
        // A watches a DM containing X — interest stored under the (lowercased, membership-cased) tag, but
        // the subject transitions under LIVE mixed casing, so the reverse read must be case-insensitive.
        registry.RegisterFocus("connA", "dm-1", "Alice#1", new[] { "alice#1", "xavier#9" });

        Assert.That(registry.GetInterestedConnections("Xavier#9"), Is.EquivalentTo(new[] { "connA" }),
            "the reverse index must resolve a live-cased subject to interest recorded under lowercased membership casing");
        Assert.That(registry.GetInterestedConnections("xavier#9"), Is.EquivalentTo(new[] { "connA" }));
    }

    [Test]
    public void RegisterFocus_ExcludesOwnTag()
    {
        // A must never end up watching its OWN presence, even though its own tag is in the member list.
        registry.RegisterFocus("connA", "dm-1", "alice#1", new[] { "alice#1", "xavier#9" });

        Assert.That(registry.GetInterestedConnections("alice#1"), Is.Empty,
            "a connection never watches its own battleTag");
    }

    [Test]
    public void RegisterFocus_ReplaceSemantics_ReFocusWithSmallerRosterDropsVanishedMember()
    {
        // First focus of group g-1 with [A, X, Y] → A watches X and Y.
        registry.RegisterFocus("connA", "g-1", "alice#1", new[] { "alice#1", "xavier#9", "yuki#2" });
        Assert.That(registry.GetInterestedConnections("yuki#2"), Is.EquivalentTo(new[] { "connA" }));

        // Re-focus the SAME channel with a roster that no longer contains Y (authoritative resync).
        registry.RegisterFocus("connA", "g-1", "alice#1", new[] { "alice#1", "xavier#9" });

        Assert.That(registry.GetInterestedConnections("yuki#2"), Is.Empty,
            "an authoritative re-focus drops interest in a member that vanished from the roster");
        Assert.That(registry.GetInterestedConnections("xavier#9"), Is.EquivalentTo(new[] { "connA" }),
            "a still-present member keeps its interest across a re-focus");
    }

    // ---- RevokeFocus (refcount-by-channel) ---------------------------------------------------------

    [Test]
    public void RevokeFocus_TagWatchedViaTwoChannels_SurvivesOneRevoke()
    {
        // A has TWO DMs open, both containing X. Closing one window must NOT stop A watching X.
        registry.RegisterFocus("connA", "dm-1", "alice#1", new[] { "alice#1", "xavier#9" });
        registry.RegisterFocus("connA", "dm-2", "alice#1", new[] { "alice#1", "xavier#9" });

        registry.RevokeFocus("connA", "dm-1");

        Assert.That(registry.GetInterestedConnections("xavier#9"), Is.EquivalentTo(new[] { "connA" }),
            "interest survives revoking ONE of two channels that both reach the tag (refcount-by-channel)");

        // Revoking the SECOND (last) channel finally drops it.
        registry.RevokeFocus("connA", "dm-2");
        Assert.That(registry.GetInterestedConnections("xavier#9"), Is.Empty,
            "interest is gone once the LAST channel reaching the tag is revoked");
    }

    [Test]
    public void RevokeFocus_UnknownConnectionOrChannel_NoOps()
    {
        registry.RegisterFocus("connA", "dm-1", "alice#1", new[] { "alice#1", "xavier#9" });

        Assert.DoesNotThrow(() => registry.RevokeFocus("connGhost", "dm-1"));
        Assert.DoesNotThrow(() => registry.RevokeFocus("connA", "dm-never"));

        Assert.That(registry.GetInterestedConnections("xavier#9"), Is.EquivalentTo(new[] { "connA" }),
            "a no-op revoke leaves existing interest untouched");
    }

    // ---- OnMemberAdded / OnMemberRemoved -----------------------------------------------------------

    [Test]
    public void OnMemberAdded_ExistingWatchersGainTag()
    {
        // A and B both focus group g-1 (initially just the two of them). A third member Z joins.
        registry.RegisterFocus("connA", "g-1", "alice#1", new[] { "alice#1", "bob#2" });
        registry.RegisterFocus("connB", "g-1", "bob#2", new[] { "alice#1", "bob#2" });

        registry.OnMemberAdded("g-1", "zed#3");

        Assert.That(registry.GetInterestedConnections("zed#3"), Is.EquivalentTo(new[] { "connA", "connB" }),
            "every connection currently watching the channel gains interest in the newly-added member");
    }

    [Test]
    public void OnMemberAdded_NoWatchers_NoOp_PublicChannelHasNoInterest()
    {
        // Nobody registered interest through pub-1 (it is not a private lane) → adding a member is inert.
        registry.OnMemberAdded("pub-1", "zed#3");

        Assert.That(registry.GetInterestedConnections("zed#3"), Is.Empty,
            "a channel nobody watches (e.g. a public channel) accrues no interest on member-add");
    }

    [Test]
    public void OnMemberAdded_SkipsWatchersOwnTag()
    {
        registry.RegisterFocus("connA", "g-1", "alice#1", new[] { "alice#1", "bob#2" });

        // A pathological add of A's OWN tag must never make A watch itself.
        registry.OnMemberAdded("g-1", "alice#1");

        Assert.That(registry.GetInterestedConnections("alice#1"), Is.Empty,
            "OnMemberAdded never makes a watcher watch its own battleTag");
    }

    [Test]
    public void OnMemberRemoved_AllWatchersDropTag()
    {
        registry.RegisterFocus("connA", "g-1", "alice#1", new[] { "alice#1", "bob#2", "zed#3" });
        registry.RegisterFocus("connB", "g-1", "bob#2", new[] { "alice#1", "bob#2", "zed#3" });
        Assert.That(registry.GetInterestedConnections("zed#3"), Is.EquivalentTo(new[] { "connA", "connB" }));

        registry.OnMemberRemoved("g-1", "zed#3");

        Assert.That(registry.GetInterestedConnections("zed#3"), Is.Empty,
            "removing a member drops it from EVERY watcher of the channel");
        // Unrelated interest is unaffected.
        Assert.That(registry.GetInterestedConnections("bob#2"), Is.EquivalentTo(new[] { "connA" }));
    }

    [Test]
    public void OnMemberRemoved_SurvivesViaOtherChannel_Refcount()
    {
        // A reaches X via TWO channels; removing X from ONE keeps interest alive via the other.
        registry.RegisterFocus("connA", "g-1", "alice#1", new[] { "alice#1", "xavier#9" });
        registry.RegisterFocus("connA", "dm-2", "alice#1", new[] { "alice#1", "xavier#9" });

        registry.OnMemberRemoved("g-1", "xavier#9");

        Assert.That(registry.GetInterestedConnections("xavier#9"), Is.EquivalentTo(new[] { "connA" }),
            "a member removed from one channel is still watched via another (refcount-by-channel)");
    }

    // ---- RemoveChannel -----------------------------------------------------------------------------

    [Test]
    public void RemoveChannel_DropsAllInterestThroughChannel()
    {
        registry.RegisterFocus("connA", "g-1", "alice#1", new[] { "alice#1", "bob#2" });
        registry.RegisterFocus("connB", "g-1", "bob#2", new[] { "alice#1", "bob#2" });

        registry.RemoveChannel("g-1");

        Assert.That(registry.GetInterestedConnections("alice#1"), Is.Empty);
        Assert.That(registry.GetInterestedConnections("bob#2"), Is.Empty,
            "deleting the channel drops every interest derived through it");
    }

    [Test]
    public void RemoveChannel_RefcountSurvivesViaOtherChannel()
    {
        registry.RegisterFocus("connA", "g-1", "alice#1", new[] { "alice#1", "xavier#9" });
        registry.RegisterFocus("connA", "dm-2", "alice#1", new[] { "alice#1", "xavier#9" });

        registry.RemoveChannel("g-1");

        Assert.That(registry.GetInterestedConnections("xavier#9"), Is.EquivalentTo(new[] { "connA" }),
            "deleting one channel keeps interest a watcher also reaches via another");
    }

    // ---- RemoveConnection --------------------------------------------------------------------------

    [Test]
    public void RemoveConnection_DropsEverything()
    {
        registry.RegisterFocus("connA", "dm-1", "alice#1", new[] { "alice#1", "xavier#9" });
        registry.RegisterFocus("connA", "g-2", "alice#1", new[] { "alice#1", "bob#2" });

        registry.RemoveConnection("connA");

        Assert.That(registry.GetInterestedConnections("xavier#9"), Is.Empty);
        Assert.That(registry.GetInterestedConnections("bob#2"), Is.Empty,
            "disconnecting a watcher drops ALL of its interest, across every channel");
    }

    [Test]
    public void RemoveConnection_DoesNotAffectOtherWatchers()
    {
        registry.RegisterFocus("connA", "g-1", "alice#1", new[] { "alice#1", "bob#2", "zed#3" });
        registry.RegisterFocus("connB", "g-1", "bob#2", new[] { "alice#1", "bob#2", "zed#3" });

        registry.RemoveConnection("connA");

        Assert.That(registry.GetInterestedConnections("zed#3"), Is.EquivalentTo(new[] { "connB" }),
            "removing one watcher leaves the other watchers' interest intact");
    }

    [Test]
    public void RemoveConnection_Unknown_NoOps()
    {
        Assert.DoesNotThrow(() => registry.RemoveConnection("connGhost"));
    }

    // ---- GetInterestedConnections ------------------------------------------------------------------

    [Test]
    public void GetInterestedConnections_UnknownTag_Empty()
    {
        Assert.That(registry.GetInterestedConnections("nobody#0"), Is.Empty);
    }

    [Test]
    public void GetInterestedConnections_ReturnsSnapshotCopy_NotLiveView()
    {
        registry.RegisterFocus("connA", "dm-1", "alice#1", new[] { "alice#1", "xavier#9" });
        var snapshot = registry.GetInterestedConnections("xavier#9");

        // Mutating the registry after reading must not retroactively change the returned snapshot.
        registry.RemoveConnection("connA");

        Assert.That(snapshot, Is.EquivalentTo(new[] { "connA" }),
            "GetInterestedConnections returns a copy safe to iterate outside the lock");
    }
}

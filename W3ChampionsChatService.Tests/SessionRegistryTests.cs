using System.Linq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Sessions;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Unit tests for <see cref="SessionRegistry"/> — the authoritative battleTag→connection map that
/// enforces exactly ONE active connection per battleTag (C2). The load-bearing invariant is the
/// identity-checked teardown in <see cref="SessionRegistry.Unregister"/>: a displaced OLD socket's
/// late disconnect must NEVER evict the client's own NEW session (the flo "signed in elsewhere"
/// race). <see cref="DisplacementRace_TearingDownOldConnection_DoesNotRemoveNewSession"/> is the
/// mutation-sensitive proof of that guard.
/// </summary>
public class SessionRegistryTests
{
    private SessionRegistry registry;

    [SetUp]
    public void SetUp()
    {
        registry = new SessionRegistry();
    }

    // Snapshot the identity exactly like the hub will: a fixed W3CUserAuthentication per connection.
    private static W3CUserAuthentication Identity(string bt, params EPermission[] perms) =>
        new W3CUserAuthentication
        {
            BattleTag = bt,
            Name = bt,
            IsAdmin = false,
            Permissions = perms.ToHashSet()
        };

    [Test]
    public void Register_ThenTryGetByConnectionId_ReturnsSessionWithIdentitySnapshot()
    {
        var identity = Identity("peter#123", EPermission.Moderation);

        registry.Register("conn-1", identity, null);

        Assert.IsTrue(registry.TryGetByConnectionId("conn-1", out var session));
        Assert.IsNotNull(session);
        Assert.AreEqual("conn-1", session.ConnectionId);
        // The identity is the exact snapshot handed to Register (fixed for the connection lifetime).
        Assert.AreSame(identity, session.Identity);
        Assert.AreEqual("peter#123", session.Identity.BattleTag);
        Assert.IsTrue(session.Identity.Permissions.Contains(EPermission.Moderation));
    }

    [Test]
    public void GetByBattleTag_IsCaseInsensitive()
    {
        // Matches ConnectionMapping semantics: the DB lowercases battleTags, live ones keep casing.
        registry.Register("conn-a", Identity("Peter#123"), null);
        Assert.IsNotNull(registry.GetByBattleTag("peter#123"));
    }

    [Test]
    public void SecondRegister_SameBattleTag_ReturnsDisplacedOldSession_AndNewIsCurrent()
    {
        registry.Register("conn-old", Identity("peter#123"), null);
        var displaced = registry.Register("conn-new", Identity("peter#123"), null);
        Assert.AreEqual("conn-old", displaced.ConnectionId);
        Assert.AreEqual("conn-new", registry.GetByBattleTag("peter#123").ConnectionId);
    }

    [Test]
    public void DisplacementRace_TearingDownOldConnection_DoesNotRemoveNewSession()
    {
        // ACCEPTANCE 5 — the flo "signed in elsewhere" race. MUTATION-TESTABLE: deleting the
        // `current.ConnectionId == connectionId` check in Unregister makes EXACTLY this test fail.
        registry.Register("conn-old", Identity("peter#123"), null);
        registry.Register("conn-new", Identity("peter#123"), null); // displaces conn-old

        registry.Unregister("conn-old"); // the dying OLD socket's teardown fires late

        var session = registry.GetByBattleTag("peter#123");
        Assert.IsNotNull(session, "OLD teardown must not evict the NEW session");
        Assert.AreEqual("conn-new", session.ConnectionId);
        Assert.IsTrue(registry.TryGetByConnectionId("conn-new", out _));
    }

    [Test]
    public void Unregister_CurrentConnection_RemovesSession()
    {
        registry.Register("conn-1", Identity("peter#123"), null);

        registry.Unregister("conn-1");

        Assert.IsFalse(registry.TryGetByConnectionId("conn-1", out _));
        Assert.IsNull(registry.GetByBattleTag("peter#123"));
    }

    [Test]
    public void Unregister_UnknownConnection_NoOps()
    {
        registry.Register("conn-1", Identity("peter#123"), null);

        Assert.DoesNotThrow(() => registry.Unregister("conn-unknown"));

        // The unrelated session is untouched.
        Assert.IsNotNull(registry.GetByBattleTag("peter#123"));
        Assert.IsTrue(registry.TryGetByConnectionId("conn-1", out _));
    }

    // ---------------------------------------------------------------------------------------------
    // C6 (Task 9, D11): Unregister's bool return is the disconnect-side presence-transition signal —
    // true iff THIS call actually removed the battleTag's live mapping (a genuine offline transition).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void Unregister_CurrentConnection_ReturnsTrue_GenuineOfflineTransition()
    {
        registry.Register("conn-1", Identity("peter#123"), null);

        Assert.IsTrue(registry.Unregister("conn-1"),
            "unregistering the CURRENT connection removes the live mapping — a genuine offline transition");
    }

    [Test]
    public void Unregister_DisplacedOldSocket_ReturnsFalse_NotATransition()
    {
        registry.Register("conn-old", Identity("peter#123"), null);
        registry.Register("conn-new", Identity("peter#123"), null); // displaces conn-old

        // The dying OLD socket's late teardown did NOT remove the live mapping (it points at conn-new) —
        // the user is still online, so this is NOT an offline transition and must return false.
        Assert.IsFalse(registry.Unregister("conn-old"),
            "a displaced old socket's disconnect is not an offline transition — the user is still online via conn-new");
        Assert.AreEqual("conn-new", registry.GetByBattleTag("peter#123").ConnectionId);
    }

    [Test]
    public void Unregister_UnknownConnection_ReturnsFalse()
    {
        registry.Register("conn-1", Identity("peter#123"), null);

        Assert.IsFalse(registry.Unregister("conn-unknown"),
            "an unknown/already-torn-down connection removed nothing — not a transition");
    }

    [Test]
    public void Register_DifferentBattleTags_Coexist_NoDisplacement()
    {
        var firstDisplaced = registry.Register("conn-a", Identity("alice#1"), null);
        var secondDisplaced = registry.Register("conn-b", Identity("bob#2"), null);

        Assert.IsNull(firstDisplaced);
        Assert.IsNull(secondDisplaced);
        Assert.AreEqual("conn-a", registry.GetByBattleTag("alice#1").ConnectionId);
        Assert.AreEqual("conn-b", registry.GetByBattleTag("bob#2").ConnectionId);
    }

    [Test]
    public void Register_ReturnsNull_WhenNoPreviousSession()
    {
        var displaced = registry.Register("conn-1", Identity("peter#123"), null);
        Assert.IsNull(displaced);
    }

    // ---------------------------------------------------------------------------------------------
    // C6 (Task 8, D10): GetOnlineBattleTags — Tier 2's snapshot source for SearchMentionCandidates.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void GetOnlineBattleTags_SnapshotUnderLock_ReflectsRegisterUnregister()
    {
        registry.Register("conn-a", Identity("alice#1"), null);
        registry.Register("conn-b", Identity("bob#2"), null);

        var online = registry.GetOnlineBattleTags();
        CollectionAssert.AreEquivalent(new[] { "alice#1", "bob#2" }, online);

        registry.Unregister("conn-a");

        var afterUnregister = registry.GetOnlineBattleTags();
        CollectionAssert.AreEquivalent(new[] { "bob#2" }, afterUnregister);
    }
}

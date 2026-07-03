using System;
using System.Linq;
using NUnit.Framework;
using W3ChampionsChatService.Chats;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Pure <see cref="ConnectionMapping"/> unit coverage — the mute-cache round-trips, expiry semantics,
/// connection→user tracking, and room-roster visibility (shadow/full-banned users are full members;
/// there is no presence-hiding). These tests touch NO hub and NO Mongo; they were split out of the
/// old ChatBanRoomScope suite during the C3 old-protocol cutover (Task 19) and preserved verbatim.
/// </summary>
public class ConnectionMappingMuteCacheTests
{
    private ConnectionMapping _connectionMapping;

    [SetUp]
    public void SetupBeforeEach()
    {
        _connectionMapping = new ConnectionMapping();
    }

    // ── Mute cache round-trips ──────────────────────────────────────────────────

    [Test]
    public void ConnectionMapping_Mute_DefaultIsCacheMiss()
    {
        // A connection that has never had SetMute called must be a MISS (TryGetMute returns false).
        var mapping = new ConnectionMapping();
        mapping.Add("conn1", "W3C Lounge", new ChatUser("p#1", false, null, new ProfilePicture(), null, null));

        var hasCached = mapping.TryGetMute("conn1", out _);

        Assert.IsFalse(hasCached, "A connection with no SetMute call must be a cache MISS");
    }

    [Test]
    public void ConnectionMapping_SetMute_Shadow_Roundtrips()
    {
        var mapping = new ConnectionMapping();
        mapping.Add("conn1", "W3C Lounge", new ChatUser("p#1", false, null, new ProfilePicture(), null, null));
        var endDate = DateTime.UtcNow.AddDays(1);

        mapping.SetMute("conn1", MuteStatus.Shadow, endDate);

        Assert.IsTrue(mapping.TryGetMute("conn1", out var cached), "Cache entry must exist after SetMute");
        Assert.AreEqual(MuteStatus.Shadow, cached.Status);
        Assert.AreEqual(endDate, cached.EndDate);
        Assert.AreEqual(MuteStatus.Shadow, mapping.GetEffectiveMuteStatus("conn1", DateTime.UtcNow));
    }

    [Test]
    public void ConnectionMapping_SetMute_Full_Roundtrips()
    {
        var mapping = new ConnectionMapping();
        mapping.Add("conn1", "W3C Lounge", new ChatUser("p#1", false, null, new ProfilePicture(), null, null));
        var endDate = DateTime.UtcNow.AddDays(1);

        mapping.SetMute("conn1", MuteStatus.Full, endDate);

        Assert.IsTrue(mapping.TryGetMute("conn1", out var cached), "Cache entry must exist after SetMute");
        Assert.AreEqual(MuteStatus.Full, cached.Status);
        Assert.AreEqual(endDate, cached.EndDate);
        Assert.AreEqual(MuteStatus.Full, mapping.GetEffectiveMuteStatus("conn1", DateTime.UtcNow));
    }

    [Test]
    public void ConnectionMapping_GetEffectiveMuteStatus_UnknownConnection_ReturnsNone()
    {
        var mapping = new ConnectionMapping();

        var status = mapping.GetEffectiveMuteStatus("no-such-conn", DateTime.UtcNow);

        Assert.AreEqual(MuteStatus.None, status);
    }

    [Test]
    public void ConnectionMapping_TryGetMute_UnknownConnection_ReturnsFalse()
    {
        var mapping = new ConnectionMapping();

        var found = mapping.TryGetMute("no-such-conn", out _);

        Assert.IsFalse(found, "TryGetMute must return false for an unknown connection (cache MISS)");
    }

    [Test]
    public void ConnectionMapping_GetEffectiveMuteStatus_ExpiredBan_ReturnsNone()
    {
        // A cached ban whose EndDate is in the past must be treated as None.
        var mapping = new ConnectionMapping();
        mapping.Add("conn1", "W3C Lounge", new ChatUser("p#1", false, null, new ProfilePicture(), null, null));
        var expiredEnd = DateTime.UtcNow.AddDays(-1);
        mapping.SetMute("conn1", MuteStatus.Full, expiredEnd);

        var status = mapping.GetEffectiveMuteStatus("conn1", DateTime.UtcNow);

        Assert.AreEqual(MuteStatus.None, status,
            "Cached ban with EndDate in the past must be treated as None (expired)");
    }

    [Test]
    public void ConnectionMapping_SetMute_None_IsAHitWithNoneStatus()
    {
        // An explicitly-resolved unbanned connection (SetMute None) must be a cache HIT
        // that returns None — distinguishes "never resolved" (MISS) from "resolved, no ban".
        var mapping = new ConnectionMapping();
        mapping.Add("conn1", "W3C Lounge", new ChatUser("p#1", false, null, new ProfilePicture(), null, null));

        mapping.SetMute("conn1", MuteStatus.None, DateTime.MinValue);

        Assert.IsTrue(mapping.TryGetMute("conn1", out var cached), "SetMute(None) must produce a cache HIT");
        Assert.AreEqual(MuteStatus.None, cached.Status);
        Assert.AreEqual(MuteStatus.None, mapping.GetEffectiveMuteStatus("conn1", DateTime.UtcNow));
    }

    [Test]
    public void ConnectionMapping_Remove_ClearsMuteEntry()
    {
        var mapping = new ConnectionMapping();
        mapping.Add("conn1", "W3C Lounge", new ChatUser("p#1", false, null, new ProfilePicture(), null, null));
        mapping.SetMute("conn1", MuteStatus.Full, DateTime.UtcNow.AddDays(1));

        mapping.Remove("conn1");

        // Direct assertion: Remove must clear the cached entry immediately,
        // before any re-Add — guards against a Remove that silently does nothing.
        Assert.IsFalse(mapping.TryGetMute("conn1", out _),
            "Remove must clear the mute cache entry (cache MISS after Remove)");
        Assert.AreEqual(MuteStatus.None, mapping.GetEffectiveMuteStatus("conn1", DateTime.UtcNow),
            "GetEffectiveMuteStatus must return None after Remove (MISS → None)");

        // After re-add (e.g. SwitchRoom re-populates) status comes back only after explicit SetMute
        mapping.Add("conn1", "clan AB", new ChatUser("p#1", false, null, new ProfilePicture(), null, null));

        Assert.IsFalse(mapping.TryGetMute("conn1", out _),
            "After Remove+re-Add with no SetMute, cache must still be a MISS");
    }

    // ── Connection-level user tracking ──────────────────────────────────────────

    [Test]
    public void ConnectionMapping_RegisterUser_GetUserNonNull_GetRoomNull()
    {
        var mapping = new ConnectionMapping();
        var user = new ChatUser("p#1", false, null, new ProfilePicture(), null, null);

        mapping.RegisterUser("conn1", user);

        Assert.AreSame(user, mapping.GetUser("conn1"), "GetUser must return the registered user");
        Assert.IsNull(mapping.GetRoom("conn1"), "RegisterUser must NOT seat the connection in any room");
    }

    [Test]
    public void ConnectionMapping_RegisterUser_NoRoom_GetConnectionIdsForUser_FindsIt()
    {
        var mapping = new ConnectionMapping();
        mapping.RegisterUser("conn1", new ChatUser("p#1", false, null, new ProfilePicture(), null, null));

        var ids = mapping.GetConnectionIdsForUser("p#1");

        CollectionAssert.Contains(ids, "conn1",
            "GetConnectionIdsForUser must find a no-room (RegisterUser-only) connection");
    }

    [Test]
    public void ConnectionMapping_RegisterUser_ThenRemove_GetUserNull()
    {
        var mapping = new ConnectionMapping();
        mapping.RegisterUser("conn1", new ChatUser("p#1", false, null, new ProfilePicture(), null, null));

        mapping.Remove("conn1");

        Assert.IsNull(mapping.GetUser("conn1"), "Remove must clear the registered user");
        CollectionAssert.DoesNotContain(mapping.GetConnectionIdsForUser("p#1"), "conn1",
            "Remove must drop the connection from GetConnectionIdsForUser");
    }

    [Test]
    public void ConnectionMapping_Add_ThenGetUser_StillWorks()
    {
        // Regression: Add must also register the connection→user mapping.
        var mapping = new ConnectionMapping();
        var user = new ChatUser("p#1", false, null, new ProfilePicture(), null, null);
        mapping.Add("conn1", "W3C Lounge", user);

        Assert.AreSame(user, mapping.GetUser("conn1"), "Add must register the user for GetUser");
        Assert.AreEqual("W3C Lounge", mapping.GetRoom("conn1"));
    }

    [Test]
    public void ConnectionMapping_GetConnectionIdsForUser_FindsRoomSeatedAndNoRoomConns()
    {
        var mapping = new ConnectionMapping();
        mapping.Add("roomConn", "W3C Lounge", new ChatUser("p#1", false, null, new ProfilePicture(), null, null));
        mapping.RegisterUser("noRoomConn", new ChatUser("P#1", false, null, new ProfilePicture(), null, null));

        var ids = mapping.GetConnectionIdsForUser("p#1");

        CollectionAssert.Contains(ids, "roomConn", "Room-seated connection must be found");
        CollectionAssert.Contains(ids, "noRoomConn", "No-room connection must be found (case-insensitive)");
        Assert.AreEqual(2, ids.Count);
    }

    // ── Room-roster visibility (no presence-hiding for shadow/full-banned users) ─

    [Test]
    public void ConnectionMapping_GetUsersOfRoom_ShadowBanInBannedRoom_VisibleToAll()
    {
        // T3: shadow users are full room members — NO presence-hiding. They appear in usersOfRoom
        // for everyone (the only remaining shadow effect is the SendMessage drop).
        var mapping = new ConnectionMapping();
        var normalUser = new ChatUser("normal#1", false, null, new ProfilePicture(), null, null);
        var shadowUser = new ChatUser("shadow#2", false, null, new ProfilePicture(), null, null);

        mapping.Add("conn-normal", "W3C Lounge", normalUser);
        mapping.Add("conn-shadow", "W3C Lounge", shadowUser);
        mapping.SetMute("conn-shadow", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        var users = mapping.GetUsersOfRoom("W3C Lounge");
        var tags = users.Select(u => u.BattleTag).ToList();
        Assert.AreEqual(2, users.Count, "Shadow user must be a visible member of the room");
        CollectionAssert.Contains(tags, "shadow#2", "Shadow-banned user must be visible to others");
        CollectionAssert.Contains(tags, "normal#1");
    }

    [Test]
    public void ConnectionMapping_GetUsersOfRoom_ShadowBanInExemptRoom_VisibleToAll()
    {
        var mapping = new ConnectionMapping();
        var normalUser = new ChatUser("normal#1", false, null, new ProfilePicture(), null, null);
        var shadowUser = new ChatUser("shadow#2", false, null, new ProfilePicture(), null, null);

        mapping.Add("conn-normal", "clan AB", normalUser);
        mapping.Add("conn-shadow", "clan AB", shadowUser);
        mapping.SetMute("conn-shadow", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        var users = mapping.GetUsersOfRoom("clan AB");
        Assert.AreEqual(2, users.Count, "All users (incl. shadow) are visible in exempt rooms too");
    }

    [Test]
    public void ConnectionMapping_GetUsersOfRoom_FullBan_Visible()
    {
        var mapping = new ConnectionMapping();
        var normalUser = new ChatUser("normal#1", false, null, new ProfilePicture(), null, null);
        var fullBanUser = new ChatUser("banned#3", false, null, new ProfilePicture(), null, null);

        // Full-banned users should never be in a public room (rejected at join),
        // but if they somehow are, they appear as-is — there is no presence-hiding at all.
        mapping.Add("conn-normal", "W3C Lounge", normalUser);
        mapping.Add("conn-banned", "W3C Lounge", fullBanUser);
        mapping.SetMute("conn-banned", MuteStatus.Full, DateTime.UtcNow.AddDays(1));

        var users = mapping.GetUsersOfRoom("W3C Lounge");
        Assert.AreEqual(2, users.Count);
    }

    [Test]
    public void ConnectionMapping_GetUsersOfRoom_NoBan_AllVisible()
    {
        var mapping = new ConnectionMapping();
        var user1 = new ChatUser("user#1", false, null, new ProfilePicture(), null, null);
        var user2 = new ChatUser("user#2", false, null, new ProfilePicture(), null, null);

        mapping.Add("conn-1", "W3C Lounge", user1);
        mapping.Add("conn-2", "W3C Lounge", user2);

        var users = mapping.GetUsersOfRoom("W3C Lounge");
        Assert.AreEqual(2, users.Count,
            "Normal (unbanned) users must never be excluded from the user list");
    }

    [Test]
    public void ShadowBan_UsersOfRoom_AllMembersSeeTheShadowUser()
    {
        // T3: the ConnectionMapping room member list contains the shadow user for EVERYONE — there is
        // no presence-hiding (the only remaining shadow effect is the SendMessage drop).
        var normalUser = new ChatUser("normal#1", false, null, new ProfilePicture(), null, null);
        var shadowUser = new ChatUser("shadow#2", false, null, new ProfilePicture(), null, null);

        _connectionMapping.Add("NormalConn", "W3C Lounge", normalUser);
        _connectionMapping.Add("ShadowConn", "W3C Lounge", shadowUser);
        _connectionMapping.SetMute("ShadowConn", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        var users = _connectionMapping.GetUsersOfRoom("W3C Lounge");
        var tags = users.Select(u => u.BattleTag).ToList();
        Assert.AreEqual(2, users.Count, "Both members are visible (no presence-hiding)");
        CollectionAssert.Contains(tags, "normal#1");
        CollectionAssert.Contains(tags, "shadow#2", "Shadow user is a visible member to everyone");
    }
}

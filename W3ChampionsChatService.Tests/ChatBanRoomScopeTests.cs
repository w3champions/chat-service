// W3ChampionsChatService.Tests/ChatBanRoomScopeTests.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Settings;

namespace W3ChampionsChatService.Tests;

public class ChatBanRoomScopeTests : IntegrationTestBase
{
    private ChatHub _chatHub;
    private MuteRepository _muteRepository;
    private Mock<IHubCallerClients> _clients;
    private Mock<HubCallerContext> _hubCallerContext;
    private ConnectionMapping _connectionMapping;
    private ChatHistory _chatHistory;
    private SettingsRepository _settingsRepository;
    private MuteReconciliationTestHarness _reconcileHarness;
    private Mock<ISingleClientProxy> _callerProxy;
    private Mock<IClientProxy> _groupProxy;

    // Captured signal tracking
    private string _lastCallerMethod;
    private object[] _lastCallerArgs;
    private string _lastGroupMethod;
    private object[] _lastGroupArgs;
    private int _groupSendCount;

    [SetUp]
    public void SetupBeforeEach()
    {
        _muteRepository = new MuteRepository(MongoClient);
        _clients = new Mock<IHubCallerClients>();
        _hubCallerContext = new Mock<HubCallerContext>();
        _connectionMapping = new ConnectionMapping();
        _chatHistory = new ChatHistory();
        _settingsRepository = new SettingsRepository(MongoClient);
        _reconcileHarness = new MuteReconciliationTestHarness(_connectionMapping);

        var chatAuthService = new Mock<IChatAuthenticationService>();
        chatAuthService.Setup(m => m.GetUser(It.IsAny<string>()))
            .ReturnsAsync(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        _chatHub = new ChatHub(
            chatAuthService.Object,
            _muteRepository,
            _settingsRepository,
            _connectionMapping,
            _chatHistory,
            _reconcileHarness.Service,
            null);

        _lastCallerMethod = null;
        _lastCallerArgs = null;
        _lastGroupMethod = null;
        _lastGroupArgs = null;
        _groupSendCount = 0;

        _callerProxy = new Mock<ISingleClientProxy>();
        _callerProxy
            .Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) =>
            {
                _lastCallerMethod = method;
                _lastCallerArgs = args;
            })
            .Returns(Task.CompletedTask);

        _groupProxy = new Mock<IClientProxy>();
        _groupProxy
            .Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) =>
            {
                _lastGroupMethod = method;
                _lastGroupArgs = args;
                _groupSendCount++;
            })
            .Returns(Task.CompletedTask);

        _clients.Setup(c => c.Caller).Returns(_callerProxy.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);
        _chatHub.Clients = _clients.Object;

        _hubCallerContext.Setup(c => c.ConnectionId).Returns("TestId");
        _chatHub.Context = _hubCallerContext.Object;
        _chatHub.Groups = new Mock<IGroupManager>().Object;
    }

    // ── Task 1 test ────────────────────────────────────────────────────────────

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

    // ── T4 connection-level user tracking ─────────────────────────────────────

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

    // ── Task 3 helper methods ─────────────────────────────────────────────────

    private async Task AddFullBan(string battleTag, int daysFromNow = 1)
    {
        await _muteRepository.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = battleTag,
            endDate = DateTime.UtcNow.AddDays(daysFromNow).ToString("O"),
            author = "admin#1",
            reason = "test ban",
            isShadowBan = false
        });
    }

    private async Task AddShadowBan(string battleTag, int daysFromNow = 1)
    {
        await _muteRepository.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = battleTag,
            endDate = DateTime.UtcNow.AddDays(daysFromNow).ToString("O"),
            author = "admin#1",
            reason = "test shadow ban",
            isShadowBan = true
        });
    }

    /// <summary>
    /// Asserts the slimmed PlayerBannedFromChat payload exposes ONLY an <c>endDate</c> property
    /// (a future DateTime) and leaks neither <c>reason</c> nor <c>isShadowBan</c> to the client.
    /// The payload is an anonymous type, so it is inspected via reflection.
    /// </summary>
    private static void AssertPlayerBannedPayloadIsEndDateOnly(object payload)
    {
        Assert.IsNotNull(payload, "PlayerBannedFromChat payload must not be null");
        var type = payload.GetType();
        var props = type.GetProperties().Select(p => p.Name).ToList();

        Assert.Contains("endDate", props,
            "PlayerBannedFromChat payload must carry an endDate (backward-compat with old clients)");
        Assert.IsFalse(props.Contains("reason"),
            "SECURITY: PlayerBannedFromChat payload must NOT leak the moderation reason");
        Assert.IsFalse(props.Contains("isShadowBan"),
            "SECURITY: PlayerBannedFromChat payload must NOT leak the isShadowBan flag");

        var endDate = (DateTime)type.GetProperty("endDate").GetValue(payload);
        Assert.Greater(endDate, DateTime.UtcNow,
            "The endDate in the payload must be the (future) ban expiry");
    }

    // ── Task 3 tests ────────────────────────────────────────────────────────────

    [Test]
    public async Task Login_FullBan_DoesNotAbortConnection()
    {
        await AddFullBan("peter#123");

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        Assert.IsFalse(abortCalled, "Context.Abort() must NOT be called for full-banned users");
    }

    [Test]
    public async Task Login_FullBan_SendsPlayerBannedFromChat()
    {
        await AddFullBan("peter#123");

        string callerMethodReceived = null;
        object payloadReceived = null;
        _callerProxy
            .Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) =>
            {
                if (method == "PlayerBannedFromChat")
                {
                    callerMethodReceived = method;
                    payloadReceived = args[0];
                }
            })
            .Returns(Task.CompletedTask);

        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        Assert.AreEqual("PlayerBannedFromChat", callerMethodReceived);
        Assert.IsNotNull(payloadReceived);
        // SECURITY: the slimmed payload carries ONLY the expiry — not the full LoungeMute.
        Assert.IsNotInstanceOf<LoungeMute>(payloadReceived,
            "PlayerBannedFromChat must NOT send the full LoungeMute (leaks reason/isShadowBan)");
        AssertPlayerBannedPayloadIsEndDateOnly(payloadReceived);
    }

    [Test]
    public async Task Login_FullBan_StartChatExcludesBannedRooms()
    {
        await AddFullBan("peter#123");

        List<string> roomListReceived = null;
        _callerProxy
            .Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) =>
            {
                if (method == "StartChat" && args.Length >= 4)
                {
                    roomListReceived = args[3] as List<string>;
                }
            })
            .Returns(Task.CompletedTask);

        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        Assert.IsNotNull(roomListReceived, "StartChat must still be sent");
        foreach (var bannedRoom in DefaultChatRooms.Rooms)
        {
            Assert.IsFalse(roomListReceived.Contains(bannedRoom),
                $"Banned room '{bannedRoom}' must not appear in full-banned user's channel list");
        }
    }

    [Test]
    public async Task Login_FullBan_WithClanTag_SeatedInClanRoom()
    {
        await AddFullBan("peter#123");

        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, "AB", new ProfilePicture(), null, null));

        // Must be in "clan AB", not in any public room
        var room = _connectionMapping.GetRoom("TestId");
        Assert.AreEqual("clan AB", room);
        Assert.IsFalse(DefaultChatRooms.IsPublicRoom(room));
    }

    [Test]
    public async Task Login_FullBan_NoClan_NotSeatedInAnyRoom()
    {
        await AddFullBan("peter#123");

        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        // No clan → not added to any room
        var room = _connectionMapping.GetRoom("TestId");
        Assert.IsNull(room, "Full-banned user with no clan must not be seated in any room");
    }

    [Test]
    public async Task Login_FullBan_NoClan_StillEmitsStartChat()
    {
        // G3: even a full-banned user with no clan/no room must receive a StartChat so legacy
        // clients can initialize. The payload is an empty-room payload (null room).
        await AddFullBan("peter#123");

        bool startChatSent = false;
        string roomArg = "unset";
        _callerProxy
            .Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) =>
            {
                if (method == "StartChat")
                {
                    startChatSent = true;
                    roomArg = args.Length >= 3 ? args[2] as string : "missing";
                }
            })
            .Returns(Task.CompletedTask);

        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        Assert.IsTrue(startChatSent, "Full-banned user with no clan must still receive a StartChat (G3)");
        Assert.IsNull(roomArg, "Empty-room StartChat payload must use a null room");
    }

    [Test]
    public async Task Login_FullBan_NoBannedUserEntered_Broadcast()
    {
        // A second user (normal) is already in W3C Lounge; full-banned user should NOT trigger UserEntered there
        var normalUser = new ChatUser("normal#1", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("OtherConn", "W3C Lounge", normalUser);

        var groupUserEnteredCount = 0;
        _groupProxy
            .Setup(x => x.SendCoreAsync("UserEntered", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => groupUserEnteredCount++)
            .Returns(Task.CompletedTask);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);

        await AddFullBan("peter#123");
        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        Assert.AreEqual(0, groupUserEnteredCount,
            "UserEntered must not be broadcast for full-banned user with no clan");
    }

    [Test]
    public async Task OnDisconnected_FullBanNoClan_NoThrow()
    {
        // Lifecycle regression: a full-banned, no-clan user is connected but seated in NO room.
        // Disconnecting must not throw (no NRE on a null room) and must not broadcast UserLeft,
        // since there is no room/group to notify.
        await AddFullBan("peter#123");
        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        // Track any group send after login (StartChat goes to caller, not group).
        var groupSendsAfterLogin = 0;
        _groupProxy
            .Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => groupSendsAfterLogin++)
            .Returns(Task.CompletedTask);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);

        Assert.DoesNotThrowAsync(async () => await _chatHub.OnDisconnectedAsync(null),
            "Disconnecting a full-banned no-clan user (no room) must not throw");
        Assert.AreEqual(0, groupSendsAfterLogin,
            "No group broadcast (UserLeft) must fire for a user seated in no room");
    }

    // ── Task 4 tests ────────────────────────────────────────────────────────────

    private void LoginNormal(string battleTag = "peter#123", string clanTag = null)
    {
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser(battleTag, false, clanTag, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.None, DateTime.MinValue);
    }

    [Test]
    public async Task SwitchRoom_FullBan_IntoBannedRoom_IsRejected()
    {
        await AddFullBan("peter#123");
        // Seat user in a non-banned room as if they connected with a clan
        _connectionMapping.Add("TestId", "clan AB", new ChatUser("peter#123", false, "AB", new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Full, DateTime.UtcNow.AddDays(1));

        await _chatHub.SwitchRoom("W3C Lounge");

        // Still in clan AB, not moved to W3C Lounge
        var room = _connectionMapping.GetRoom("TestId");
        Assert.AreEqual("clan AB", room, "Full-banned user must not be moved into a banned room");
    }

    [TestCase("W3C Lounge")]
    [TestCase("1 vs 1")]
    [TestCase("2 vs 2")]
    [TestCase("FFA")]
    [TestCase("Legion TD")]
    public async Task SwitchRoom_FullBan_IntoEachBannedRoom_IsRejected(string bannedRoom)
    {
        await AddFullBan("peter#123");
        _connectionMapping.Add("TestId", "clan AB", new ChatUser("peter#123", false, "AB", new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Full, DateTime.UtcNow.AddDays(1));

        await _chatHub.SwitchRoom(bannedRoom);

        var room = _connectionMapping.GetRoom("TestId");
        Assert.AreEqual("clan AB", room, $"Full-banned user must not join banned room '{bannedRoom}'");
    }

    [Test]
    public async Task SwitchRoom_FullBan_IntoClanRoom_IsAllowed()
    {
        await AddFullBan("peter#123");
        _connectionMapping.Add("TestId", "clan AB", new ChatUser("peter#123", false, "AB", new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Full, DateTime.UtcNow.AddDays(1));

        await _chatHub.SwitchRoom("clan XYZ");

        var room = _connectionMapping.GetRoom("TestId");
        Assert.AreEqual("clan XYZ", room, "Full-banned user must be allowed to join a clan room");
    }

    [Test]
    public async Task SwitchRoom_FullBan_IntoLobbyRoom_IsAllowed()
    {
        await AddFullBan("peter#123");
        _connectionMapping.Add("TestId", "clan AB", new ChatUser("peter#123", false, "AB", new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Full, DateTime.UtcNow.AddDays(1));

        await _chatHub.SwitchRoom("game-lobby-9999");

        var room = _connectionMapping.GetRoom("TestId");
        Assert.AreEqual("game-lobby-9999", room, "Full-banned user must be allowed to join a lobby room");
    }

    [Test]
    public async Task SwitchRoom_FullBan_IntoBannedRoom_NoContextAbort()
    {
        // G1: rejected SwitchRoom must NEVER call Context.Abort()
        await AddFullBan("peter#123");
        _connectionMapping.Add("TestId", "clan AB", new ChatUser("peter#123", false, "AB", new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Full, DateTime.UtcNow.AddDays(1));

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        await _chatHub.SwitchRoom("W3C Lounge");

        Assert.IsFalse(abortCalled, "Context.Abort() must NOT be called on a rejected SwitchRoom (G1)");
    }

    [Test]
    public async Task Login_FullBan_NoClan_SeedsCacheWithFullStatus()
    {
        // §7 rework: the no-clan full-ban LOGIN path now seeds the per-connection mute cache
        // authoritatively (Full) even though the user is seated in no room. This is what lets a
        // later SendMessage enforce the ban from the cache with zero DB reads (see
        // SendMessage_FullBan_NoClanLogin_EnforcesWithZeroMuteRepositoryCalls).
        await AddFullBan("peter#123");

        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        Assert.IsNull(_connectionMapping.GetRoom("TestId"), "No-clan full-ban user must be seated in no room");
        Assert.IsTrue(_connectionMapping.TryGetMute("TestId", out var seeded),
            "Login must seed the cache even when the user is seated in no room");
        Assert.AreEqual(MuteStatus.Full, seeded.Status, "No-clan full-ban login must cache Full status");
        Assert.Greater(seeded.EndDate, DateTime.UtcNow, "Cached endDate must be the (future) ban expiry");
    }

    [Test]
    public async Task Login_FullBan_NoClan_GetUser_NonNull()
    {
        // T4: the no-clan full-ban login path registers the connection→user mapping, so GetUser is
        // authoritative (non-null) even though the user is seated in no room.
        await AddFullBan("peter#123");

        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        var user = _connectionMapping.GetUser("TestId");
        Assert.IsNotNull(user, "No-clan full-ban login must register the user (GetUser non-null)");
        Assert.AreEqual("peter#123", user.BattleTag);
        Assert.IsNull(_connectionMapping.GetRoom("TestId"), "User is still seated in no room");
    }

    [Test]
    public async Task SwitchRoom_FullBan_NoClan_IntoPublicRoom_RejectedByCacheGate()
    {
        // T4 + cache gate: a no-clan full-banned user now has a non-null user (registered at login),
        // so SwitchRoom is NOT rejected by the user==null guard. Switching into a PUBLIC room is
        // instead rejected by the Full→public cache gate; the user stays in no room, gracefully.
        await AddFullBan("peter#123");
        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        Assert.IsNotNull(_connectionMapping.GetUser("TestId"), "Precondition: user is registered (non-null)");
        Assert.IsNull(_connectionMapping.GetRoom("TestId"), "Precondition: no-clan full-ban user is in no room");

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        Assert.DoesNotThrowAsync(async () => await _chatHub.SwitchRoom("1 vs 1"),
            "SwitchRoom into a public room must return gracefully, not throw");

        Assert.IsNull(_connectionMapping.GetRoom("TestId"),
            "Full-banned user must remain in no room — public target rejected by the Full→public cache gate");
        Assert.IsFalse(abortCalled, "Rejected SwitchRoom must never call Context.Abort() (G1)");
    }

    [Test]
    public async Task Login_FullBan_NoClan_SwitchRoomToExempt_IsAllowed()
    {
        // T4: a no-clan full-banned user (registered, no room) CAN switch into an exempt clan/lobby
        // room — the cache gate only blocks public targets. No abort/throw.
        await AddFullBan("peter#123");
        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        Assert.IsNull(_connectionMapping.GetRoom("TestId"), "Precondition: no-clan full-ban user is in no room");

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        Assert.DoesNotThrowAsync(async () => await _chatHub.SwitchRoom("clan XYZ"),
            "SwitchRoom into an exempt room must return gracefully, not throw");

        Assert.AreEqual("clan XYZ", _connectionMapping.GetRoom("TestId"),
            "No-clan full-ban user must be allowed into an exempt (clan/lobby) room");
        Assert.IsFalse(abortCalled, "Allowed SwitchRoom must never call Context.Abort() (G1)");
    }

    [Test]
    public void UpdateUserProfilePicture_NullUser_DoesNotThrow()
    {
        // T4 boyscout: an unregistered connection has no user — UpdateUserProfilePicture must return
        // gracefully instead of NRE-ing on a null user.
        // (No login/registration for "TestId" → GetUser returns null.)
        Assert.DoesNotThrowAsync(() =>
            _chatHub.UpdateUserProfilePicture("W3C Lounge", new ProfilePicture()),
            "UpdateUserProfilePicture with no registered user must not throw");
    }

    [Test]
    public async Task SwitchRoom_FullBan_ExemptThenPublic_StillRejected()
    {
        // SECURITY regression (full-ban bypass): a full-banned connection seeded with Full at login.
        // A FIRST switch into an EXEMPT room ("clan AB") is allowed and the cached Full survives.
        // A SECOND switch into a PUBLIC room must STILL reject from the cache — switching through an
        // exempt room must not downgrade the cached ban.
        await AddFullBan("peter#123");
        // Seat the user via the real login path (clan → seated in clan room, cache seeded Full).
        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, "AB", new ProfilePicture(), null, null));
        Assert.AreEqual("clan AB", _connectionMapping.GetRoom("TestId"), "Full-ban with clan seats in clan room");
        // Prove enforcement is cache-only: remove the DB ban so any DB read would allow the move.
        await _muteRepository.DeleteLoungeMute("peter#123");

        // First switch: into another EXEMPT room — allowed; the cached Full status must survive.
        await _chatHub.SwitchRoom("clan XYZ");
        Assert.AreEqual("clan XYZ", _connectionMapping.GetRoom("TestId"),
            "Switch into an exempt room must be allowed");

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        // Second switch: into a PUBLIC room — must reject from the surviving cached Full.
        await _chatHub.SwitchRoom("W3C Lounge");

        var room = _connectionMapping.GetRoom("TestId");
        Assert.AreEqual("clan XYZ", room,
            "Full-banned user must NOT enter a public room after a prior exempt switch (cached ban must survive)");
        Assert.IsFalse(abortCalled, "Rejected SwitchRoom must not call Context.Abort() (G1)");
    }

    [Test]
    public async Task SwitchRoom_ShadowBan_IntoPublicRoom_BroadcastsUserEntered()
    {
        // T3: shadow users are full members — NO presence-hiding. UserEntered fires unconditionally
        // when joining a public room (the only remaining shadow effect is the SendMessage drop).
        await AddShadowBan("peter#123");
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        int userEnteredCount = 0;
        _groupProxy
            .Setup(x => x.SendCoreAsync("UserEntered", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => userEnteredCount++)
            .Returns(Task.CompletedTask);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);

        await _chatHub.SwitchRoom("1 vs 1");

        Assert.AreEqual(1, userEnteredCount, "Shadow-banned user MUST broadcast UserEntered (no presence-hiding)");
    }

    [Test]
    public async Task SwitchRoom_ShadowBan_IntoPublicRoom_UserIsMovedIntoGroup()
    {
        // Shadow-banned user IS added to the new room (can receive messages) — a normal member move.
        await AddShadowBan("peter#123");
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        await _chatHub.SwitchRoom("1 vs 1");

        // Connection mapping must reflect the new room
        var room = _connectionMapping.GetRoom("TestId");
        Assert.AreEqual("1 vs 1", room, "Shadow-banned user must be physically added to the room in the mapping");
    }

    [Test]
    public async Task SwitchRoom_ShadowBan_IntoPublicRoom_CallerReceivesStartChat_FullMemberList()
    {
        await AddShadowBan("peter#123");

        // Add a normal user to "1 vs 1" first
        var normalUser = new ChatUser("normal#1", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("OtherConn", "1 vs 1", normalUser);

        // Shadow-banned user joins from "W3C Lounge"
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        List<ChatUser> startChatUsers = null;
        _callerProxy
            .Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) =>
            {
                if (method == "StartChat" && args.Length >= 1)
                    startChatUsers = args[0] as List<ChatUser>;
            })
            .Returns(Task.CompletedTask);

        await _chatHub.SwitchRoom("1 vs 1");

        // T3: the caller receives the FULL member list — themselves AND every other member.
        Assert.IsNotNull(startChatUsers, "Shadow-banned user must receive a StartChat on join");
        Assert.IsTrue(startChatUsers.Any(u => u.BattleTag == "peter#123"),
            "Shadow-banned user must see themselves in usersOfRoom");
        Assert.IsTrue(startChatUsers.Any(u => u.BattleTag == "normal#1"),
            "Shadow-banned user must see other members in usersOfRoom (no presence-hiding)");
    }

    [Test]
    public async Task SwitchRoom_ShadowBan_IntoPublicRoom_BroadcastsUserLeftOnOldRoom()
    {
        // T3: shadow users are full members — leaving the old room fires UserLeft unconditionally.
        await AddShadowBan("peter#123");
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        int userLeftCount = 0;
        _groupProxy
            .Setup(x => x.SendCoreAsync("UserLeft", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => userLeftCount++)
            .Returns(Task.CompletedTask);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);

        await _chatHub.SwitchRoom("1 vs 1");

        Assert.AreEqual(1, userLeftCount,
            "Shadow-banned user MUST trigger UserLeft on the old room (no presence-hiding)");
    }

    [Test]
    public async Task SwitchRoom_ShadowBan_IntoExemptRoom_NormalJoin()
    {
        await AddShadowBan("peter#123");
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        int userEnteredCount = 0;
        _groupProxy
            .Setup(x => x.SendCoreAsync("UserEntered", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => userEnteredCount++)
            .Returns(Task.CompletedTask);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);

        await _chatHub.SwitchRoom("clan XYZ");

        // UserEntered should fire normally for exempt rooms
        Assert.AreEqual(1, userEnteredCount,
            "Shadow-banned user entering an exempt room must broadcast UserEntered normally");
        var room = _connectionMapping.GetRoom("TestId");
        Assert.AreEqual("clan XYZ", room);
    }

    [Test]
    public async Task SwitchRoom_ShadowBan_ExemptToPublic_BothLeaveAndEnterFire()
    {
        // T3: no presence-hiding. Both the old-room UserLeft and the target-room UserEntered fire
        // unconditionally, regardless of room category.
        await AddShadowBan("peter#123");
        _connectionMapping.Add("TestId", "clan XYZ", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        int userLeftCount = 0;
        int userEnteredCount = 0;
        _groupProxy
            .Setup(x => x.SendCoreAsync("UserLeft", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => userLeftCount++)
            .Returns(Task.CompletedTask);
        _groupProxy
            .Setup(x => x.SendCoreAsync("UserEntered", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => userEnteredCount++)
            .Returns(Task.CompletedTask);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);

        await _chatHub.SwitchRoom("W3C Lounge");

        Assert.AreEqual(1, userLeftCount, "Leaving the old room must broadcast UserLeft");
        Assert.AreEqual(1, userEnteredCount, "Joining the public target room must broadcast UserEntered");
    }

    [Test]
    public async Task SwitchRoom_ShadowBan_PublicToExempt_BothLeaveAndEnterFire()
    {
        // T3: no presence-hiding. Both UserLeft (old public room) and UserEntered (exempt target) fire.
        await AddShadowBan("peter#123");
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        int userLeftCount = 0;
        int userEnteredCount = 0;
        _groupProxy
            .Setup(x => x.SendCoreAsync("UserLeft", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => userLeftCount++)
            .Returns(Task.CompletedTask);
        _groupProxy
            .Setup(x => x.SendCoreAsync("UserEntered", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => userEnteredCount++)
            .Returns(Task.CompletedTask);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);

        await _chatHub.SwitchRoom("clan XYZ");

        Assert.AreEqual(1, userLeftCount, "Leaving the old public room must broadcast UserLeft");
        Assert.AreEqual(1, userEnteredCount, "Joining the exempt target room must broadcast UserEntered");
    }

    [Test]
    public async Task SwitchRoom_UnbannedUser_NormalSwitch_BroadcastsUserEntered()
    {
        // Normal (unbanned) user switching rooms: standard behavior unchanged
        LoginNormal("peter#123");

        int userEnteredCount = 0;
        _groupProxy
            .Setup(x => x.SendCoreAsync("UserEntered", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => userEnteredCount++)
            .Returns(Task.CompletedTask);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);

        await _chatHub.SwitchRoom("1 vs 1");

        Assert.AreEqual(1, userEnteredCount, "Unbanned user SwitchRoom must broadcast UserEntered");
        var room = _connectionMapping.GetRoom("TestId");
        Assert.AreEqual("1 vs 1", room);
    }

    // ── Task 5 tests ────────────────────────────────────────────────────────────

    [Test]
    public async Task SendMessage_NoRoom_MembershipCheck_Rejected()
    {
        // User is NOT added to any room (not in _connectionMapping)
        // Calling SendMessage without being a member → rejected, no group broadcast

        await _chatHub.SendMessage("Hello world");

        Assert.AreEqual(0, _groupSendCount,
            "Message must not be broadcast when user has no room membership");
        Assert.IsEmpty(_chatHistory.GetMessages("W3C Lounge"));
    }

    [Test]
    public async Task SendMessage_FullBan_InBannedRoom_IsRejectedSilently()
    {
        // Full-banned user somehow ends up in a banned room (defense-in-depth)
        await AddFullBan("peter#123");
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Full, DateTime.UtcNow.AddDays(1));

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        await _chatHub.SendMessage("Hello world");

        Assert.IsFalse(abortCalled, "Context.Abort() must not be called from SendMessage");
        Assert.AreEqual(0, _groupSendCount, "Full-banned message in banned room must not be broadcast");
        Assert.IsEmpty(_chatHistory.GetMessages("W3C Lounge"));
    }

    [Test]
    public async Task SendMessage_FullBan_InExemptRoom_Broadcasts()
    {
        await AddFullBan("peter#123");
        _connectionMapping.Add("TestId", "clan AB", new ChatUser("peter#123", false, "AB", new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Full, DateTime.UtcNow.AddDays(1));

        await _chatHub.SendMessage("Hello clan");

        Assert.AreEqual(1, _groupSendCount, "Full-banned user's message in exempt room must broadcast");
        Assert.AreEqual(1, _chatHistory.GetMessages("clan AB").Count);
        Assert.AreEqual("Hello clan", _chatHistory.GetMessages("clan AB")[0].Message);
    }

    [Test]
    public async Task SendMessage_ShadowBan_InBannedRoom_DroppedEchoToCaller()
    {
        await AddShadowBan("peter#123");
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        ChatMessage callerEcho = null;
        _callerProxy
            .Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) =>
            {
                if (method == "ReceiveMessage" && args.Length > 0)
                    callerEcho = args[0] as ChatMessage;
            })
            .Returns(Task.CompletedTask);

        await _chatHub.SendMessage("Invisible message");

        Assert.AreEqual(0, _groupSendCount, "Shadow-banned message in banned room must NOT be broadcast to group");
        Assert.IsEmpty(_chatHistory.GetMessages("W3C Lounge"), "Dropped message must not enter history");
        Assert.IsNotNull(callerEcho, "Shadow-banned user must receive echo of their own message");
        Assert.AreEqual("Invisible message", callerEcho.Message);
    }

    [Test]
    public async Task SendMessage_ShadowBan_InExemptRoom_Broadcasts()
    {
        await AddShadowBan("peter#123");
        _connectionMapping.Add("TestId", "clan AB", new ChatUser("peter#123", false, "AB", new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        await _chatHub.SendMessage("Clan message");

        Assert.AreEqual(1, _groupSendCount, "Shadow-banned user in exempt room must broadcast normally");
        Assert.AreEqual(1, _chatHistory.GetMessages("clan AB").Count);
        Assert.AreEqual("Clan message", _chatHistory.GetMessages("clan AB")[0].Message);
    }

    [Test]
    public async Task SendMessage_NoBan_NormalUser_Broadcasts()
    {
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.None, DateTime.MinValue);

        await _chatHub.SendMessage("Normal message");

        Assert.AreEqual(1, _groupSendCount);
        Assert.AreEqual(1, _chatHistory.GetMessages("W3C Lounge").Count);
    }

    [Test]
    public async Task SendMessage_ExpiredMute_TreatedAsNoBan_Broadcasts()
    {
        // Expired mute — endDate is in the past
        await _muteRepository.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = "peter#123",
            endDate = DateTime.UtcNow.AddDays(-1).ToString("O"),
            author = "admin#1",
            reason = "expired ban",
            isShadowBan = false
        });
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.None, DateTime.MinValue);

        await _chatHub.SendMessage("Should broadcast");

        Assert.AreEqual(1, _groupSendCount, "Expired ban must not restrict sending");
        Assert.AreEqual(1, _chatHistory.GetMessages("W3C Lounge").Count);
    }

    [Test]
    public async Task SendMessage_UnbannedUser_InAllRoomTypes_Broadcasts()
    {
        _connectionMapping.Add("TestId", "2 vs 2", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.None, DateTime.MinValue);

        await _chatHub.SendMessage("Ladder message");

        Assert.AreEqual(1, _groupSendCount, "Unbanned user must broadcast in any room");
    }

    // ── New tests pinning cache-endDate behavior (spec §7) ────────────────────

    [Test]
    public async Task SendMessage_FullBan_LoginSeedsCache_EnforcedWithoutDbRead()
    {
        // §7 rework: SendMessage consults the per-connection cache ONLY — never the DB.
        // A full-banned user seated in a public room (e.g. a stale membership) is enforced from
        // the cache. Proof of "no DB read": the DB has NO ban for this user (SetUp wipes the DB),
        // yet the cached Full status still rejects the message — a DB read would have allowed it.
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Full, DateTime.UtcNow.AddDays(1));
        // DB has NO ban (SetUp dropped the database) — only the cache knows about the ban.

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        await _chatHub.SendMessage("Should be rejected");

        Assert.IsFalse(abortCalled, "Context.Abort() must not be called");
        Assert.AreEqual(0, _groupSendCount,
            "Cached full-ban in a public room must reject the message (enforced from cache, no DB read)");
        Assert.IsEmpty(_chatHistory.GetMessages("W3C Lounge"),
            "Rejected message must not enter history");
    }

    [Test]
    public async Task SendMessage_CacheMiss_OutOfBandDbBan_InPublicRoom_NotEnforced_AcceptedTradeoff()
    {
        // ACCEPTED TRADE-OFF (§7): the send hot path consults the cache ONLY, never the DB.
        // An out-of-band ban written straight to Mongo (NOT via the BanUser hub) is NOT reflected
        // in the cache, so it does NOT take effect until the user's next reconnect (which re-seeds
        // the cache at login). Here we simulate an unseeded connection (cache MISS) with a DB ban:
        // the message must BROADCAST — proving SendMessage does not re-query the DB per send.
        await AddFullBan("peter#123");
        // Seat the user WITHOUT seeding the cache (cache MISS) — models a never-reconnected connection.
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        await _chatHub.SendMessage("Out-of-band ban not yet effective");

        Assert.AreEqual(1, _groupSendCount,
            "Out-of-band DB ban (cache MISS) must NOT be enforced on the send hot path — accepted trade-off");
        Assert.AreEqual(1, _chatHistory.GetMessages("W3C Lounge").Count);
    }

    /// <summary>
    /// An <see cref="IMuteRepository"/> spy (decorator over a real <see cref="MuteRepository"/>) that
    /// counts <see cref="IMuteRepository.GetMutedPlayer"/> calls so a test can assert the send/switch
    /// hot paths perform ZERO mute-repository reads.
    /// </summary>
    private sealed class CountingMuteRepository(MongoDB.Driver.MongoClient client) : IMuteRepository
    {
        private readonly MuteRepository _inner = new(client);

        public int GetMutedPlayerCallCount { get; private set; }

        public Task<LoungeMute> GetMutedPlayer(string battleTag)
        {
            GetMutedPlayerCallCount++;
            return _inner.GetMutedPlayer(battleTag);
        }

        public Task AddLoungeMute(LoungeMuteRequest loungeMuteRequest) => _inner.AddLoungeMute(loungeMuteRequest);
        public Task<System.Collections.Generic.List<LoungeMute>> GetLoungeMutes() => _inner.GetLoungeMutes();
        public Task<MongoDB.Driver.DeleteResult> DeleteLoungeMute(string battleTag) => _inner.DeleteLoungeMute(battleTag);
    }

    private ChatHub BuildHubWithRepository(IMuteRepository repository)
    {
        var chatAuthService = new Mock<IChatAuthenticationService>();
        chatAuthService.Setup(m => m.GetUser(It.IsAny<string>()))
            .ReturnsAsync(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        var hub = new ChatHub(
            chatAuthService.Object,
            repository,
            _settingsRepository,
            _connectionMapping,
            _chatHistory,
            _reconcileHarness.Service,
            null)
        {
            Clients = _clients.Object,
            Context = _hubCallerContext.Object,
            Groups = new Mock<IGroupManager>().Object,
        };
        return hub;
    }

    [Test]
    public async Task SendMessage_UnmutedUser_InPublicRoom_MakesZeroMuteRepositoryCalls()
    {
        // §7 contract: the SendMessage hot path consults the per-connection cache ONLY.
        // An unmuted user sending in a public room must NOT hit the mute repository at all.
        var countingRepo = new CountingMuteRepository(MongoClient);
        var hub = BuildHubWithRepository(countingRepo);

        // Seat an unmuted user in a public room with an explicit cached None (as login would seed).
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.None, DateTime.MinValue);

        await hub.SendMessage("hello world");

        Assert.AreEqual(0, countingRepo.GetMutedPlayerCallCount,
            "SendMessage must make ZERO mute-repository reads on the hot path (§7 cache-only)");
        Assert.AreEqual(1, _groupSendCount, "Unmuted message must broadcast to the room");
    }

    [Test]
    public async Task SwitchRoom_UnmutedUser_MakesZeroMuteRepositoryCalls()
    {
        // §7 contract: SwitchRoom consults the per-connection cache ONLY — no mute-repository read.
        var countingRepo = new CountingMuteRepository(MongoClient);
        var hub = BuildHubWithRepository(countingRepo);

        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.None, DateTime.MinValue);

        await hub.SwitchRoom("1 vs 1");

        Assert.AreEqual(0, countingRepo.GetMutedPlayerCallCount,
            "SwitchRoom must make ZERO mute-repository reads on the hot path (§7 cache-only)");
        Assert.AreEqual("1 vs 1", _connectionMapping.GetRoom("TestId"),
            "Unmuted user must be moved into the target room");
    }

    [Test]
    public async Task SendMessage_FullBan_NoClanLogin_EnforcesWithZeroMuteRepositoryCalls()
    {
        // §7 + no-clan login seeding: after login (one DB read to resolve the ban), the cache is
        // seeded. A subsequent SendMessage in a public room must enforce the ban with ZERO further
        // mute-repository reads — the only DB read happens at login, never on the send hot path.
        var countingRepo = new CountingMuteRepository(MongoClient);
        // Persist a full ban via the (non-spy) repo so the login resolve finds it.
        await _muteRepository.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = "peter#123",
            endDate = DateTime.UtcNow.AddDays(1).ToString("O"),
            author = "admin#1",
            reason = "test ban",
            isShadowBan = false
        });
        var hub = BuildHubWithRepository(countingRepo);

        // No-clan full-ban login: seats in no room, seeds the cache with Full.
        await hub.LoginAsAuthenticated(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        Assert.AreEqual(1, countingRepo.GetMutedPlayerCallCount,
            "Login resolves the ban with exactly one mute-repository read");

        // Seat the cached full-banned user into a public room to exercise the SendMessage gate.
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        await hub.SendMessage("should be rejected");

        Assert.AreEqual(1, countingRepo.GetMutedPlayerCallCount,
            "SendMessage must NOT add any mute-repository read beyond the single login resolve (§7)");
        Assert.AreEqual(0, _groupSendCount,
            "Cached full-ban must reject the message in a public room");
    }

    [Test]
    public async Task SendMessage_ShadowBan_InPublicRoom_LogsDropWithMessageContent()
    {
        // §3: a shadow-banned user's silently dropped message must be logged INCLUDING the content
        // (BattleTag, room, AND the attempted message) so moderators can audit shadow activity.
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        var capturedLogs = new List<string>();
        var sink = new DelegatingLogSink(evt => capturedLogs.Add(evt.RenderMessage()));
        var originalLogger = Serilog.Log.Logger;
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            const string secretMessage = "shadow-drop-audit-marker-42";
            await _chatHub.SendMessage(secretMessage);

            Assert.AreEqual(0, _groupSendCount, "Shadow-banned message must not broadcast to the room");
            var dropLog = capturedLogs.FirstOrDefault(l => l.Contains("dropped"));
            Assert.IsNotNull(dropLog, "Shadow-banned drop must emit a log line");
            StringAssert.Contains("peter#123", dropLog, "Drop log must include the BattleTag");
            StringAssert.Contains("W3C Lounge", dropLog, "Drop log must include the room");
            StringAssert.Contains(secretMessage, dropLog, "Drop log must include the attempted message content");
        }
        finally
        {
            Serilog.Log.Logger = originalLogger;
            (Serilog.Log.Logger as IDisposable)?.Dispose();
        }
    }

    /// <summary>A Serilog sink that forwards each event to a callback (for asserting log content).</summary>
    private sealed class DelegatingLogSink(Action<Serilog.Events.LogEvent> onEmit) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent) => onEmit(logEvent);
    }

    // ── T1 max message length (1024) ──────────────────────────────────────────

    [Test]
    public async Task SendMessage_ExactlyMaxLength_Broadcasts()
    {
        LoginNormal("peter#123");
        var message = new string('a', 1024);

        await _chatHub.SendMessage(message);

        Assert.AreEqual(1, _groupSendCount, "A message of exactly 1024 chars must broadcast");
        Assert.AreEqual(1, _chatHistory.GetMessages("W3C Lounge").Count);
    }

    [Test]
    public async Task SendMessage_OneOverMaxLength_Rejected()
    {
        LoginNormal("peter#123");
        var message = new string('a', 1025);

        await _chatHub.SendMessage(message);

        Assert.AreEqual(0, _groupSendCount, "A 1025-char message must be rejected (not broadcast)");
        Assert.IsEmpty(_chatHistory.GetMessages("W3C Lounge"), "Over-length message must not enter history");
    }

    [Test]
    public async Task SendMessage_FarOverMaxLength_RejectedWithoutAbort()
    {
        LoginNormal("peter#123");
        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);
        var message = new string('a', 5000);

        await _chatHub.SendMessage(message);

        Assert.AreEqual(0, _groupSendCount, "A 5000-char message must be rejected");
        Assert.IsFalse(abortCalled, "Over-length rejection must never call Context.Abort() (G1)");
    }

    [Test]
    public async Task SendMessage_OverMaxLength_LogsWarningWithBattleTag()
    {
        LoginNormal("peter#123");
        var capturedLogs = new List<string>();
        var sink = new DelegatingLogSink(evt => capturedLogs.Add(evt.RenderMessage()));
        var originalLogger = Serilog.Log.Logger;
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            await _chatHub.SendMessage(new string('a', 2000));

            var rejectLog = capturedLogs.FirstOrDefault(l => l.Contains("exceeds"));
            Assert.IsNotNull(rejectLog, "Over-length send must emit a warning log");
            StringAssert.Contains("peter#123", rejectLog, "Over-length warning must include the battleTag");
        }
        finally
        {
            Serilog.Log.Logger = originalLogger;
            (Serilog.Log.Logger as IDisposable)?.Dispose();
        }
    }

    // ── T2 battletag in rejection logs ────────────────────────────────────────

    [Test]
    public async Task SendMessage_NullUser_LogsConnectionIdOnly_NoBattleTag()
    {
        // Unauthenticated connection (no RegisterUser/Add) → GetUser null → log carries ConnectionId,
        // and has no battletag '#' marker.
        var capturedLogs = new List<string>();
        var sink = new DelegatingLogSink(evt => capturedLogs.Add(evt.RenderMessage()));
        var originalLogger = Serilog.Log.Logger;
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            await _chatHub.SendMessage("hello");

            var rejectLog = capturedLogs.FirstOrDefault(l => l.Contains("rejected"));
            Assert.IsNotNull(rejectLog, "Unauthenticated send must emit a rejection log");
            StringAssert.Contains("TestId", rejectLog, "Null-user rejection log must include the ConnectionId");
            StringAssert.DoesNotContain("#", rejectLog, "Null-user rejection log must NOT carry a battletag");
        }
        finally
        {
            Serilog.Log.Logger = originalLogger;
            (Serilog.Log.Logger as IDisposable)?.Dispose();
        }
    }

    [Test]
    public async Task SendMessage_NoRoom_UserPresent_LogsBattleTag()
    {
        // User registered (T4) but seated in NO room → room-null rejection log carries the battletag.
        _connectionMapping.RegisterUser("TestId", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        var capturedLogs = new List<string>();
        var sink = new DelegatingLogSink(evt => capturedLogs.Add(evt.RenderMessage()));
        var originalLogger = Serilog.Log.Logger;
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            await _chatHub.SendMessage("hello");

            Assert.AreEqual(0, _groupSendCount, "No-room send must be rejected (not broadcast)");
            var rejectLog = capturedLogs.FirstOrDefault(l => l.Contains("no room membership"));
            Assert.IsNotNull(rejectLog, "No-room send must emit a rejection log");
            StringAssert.Contains("peter#123", rejectLog, "No-room rejection log must include the battleTag");
        }
        finally
        {
            Serilog.Log.Logger = originalLogger;
            (Serilog.Log.Logger as IDisposable)?.Dispose();
        }
    }

    [Test]
    public async Task SendMessage_CachedBan_Expired_InBannedRoom_Broadcasts()
    {
        // Cache shows a ban but its EndDate <= now → treated as expired → message broadcasts.
        // Verifies expiry is honored from the cached endDate without any DB read.
        // (No active ban in DB either, so if there WERE a DB read it would also return None —
        //  but the point is the cache alone is sufficient.)
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        // Set a full-ban cache entry with an expiry in the PAST.
        var expiredEndDate = DateTime.UtcNow.AddDays(-1);
        _connectionMapping.SetMute("TestId", MuteStatus.Full, expiredEndDate);

        await _chatHub.SendMessage("Should broadcast after expiry");

        Assert.AreEqual(1, _groupSendCount,
            "Cached ban with expired EndDate must be treated as no ban — message must broadcast");
        Assert.AreEqual(1, _chatHistory.GetMessages("W3C Lounge").Count,
            "Message from expired-ban user must be added to history");
    }

    [Test]
    public async Task SendMessage_CachedNonNone_ActiveBan_InBannedRoom_NoRepositoryCall()
    {
        // Cache HIT with non-None active ban → zero repository calls (spec §7 "no per-message DB read").
        //
        // Proof strategy: the live DB has NO ban for "peter#123" (SetUp drops the DB).
        // If SendMessage consulted the DB it would find no ban and broadcast the message.
        // Because the cache has an active shadow ban, the message is DROPPED instead.
        // A dropped message (group count = 0) proves the decision came from the cache alone,
        // not from a DB read (which would have returned None → broadcast).
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        // Cache a shadow ban with a future EndDate — non-None HIT, no DB read should occur.
        _connectionMapping.SetMute("TestId", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));
        // DB has NO ban (SetUp already wiped the database).

        ChatMessage callerEcho = null;
        _callerProxy
            .Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) =>
            {
                if (method == "ReceiveMessage" && args.Length > 0)
                    callerEcho = args[0] as ChatMessage;
            })
            .Returns(Task.CompletedTask);

        await _chatHub.SendMessage("Hello lounge");

        // If the DB had been consulted it would return None (no ban in DB) → message would broadcast.
        // The message was DROPPED (group=0, echo to caller) → proves the cached shadow ban was the
        // authoritative source, fulfilling spec §7 "no per-message DB read" for cached non-None bans.
        Assert.AreEqual(0, _groupSendCount,
            "Cached non-None active ban must drop the message with ZERO DB reads (spec §7)");
        Assert.IsNotNull(callerEcho,
            "Shadow-banned user must still receive an echo of their own message");
        Assert.IsEmpty(_chatHistory.GetMessages("W3C Lounge"),
            "Dropped message must not enter history");
    }

    // ── Task 6 tests ────────────────────────────────────────────────────────────

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

        // Full-banned users should never be in a public room (rejected at SwitchRoom),
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
    public async Task SwitchRoom_ShadowBan_CallerReceivesStartChat_SeesAllMembers()
    {
        // T3: when a shadow-banned user joins a public room, the StartChat sent to them includes
        // EVERY member — themselves, normal users, AND other shadow users (no presence-hiding).
        await AddShadowBan("peter#123");

        // Add another shadow-banned user already in "1 vs 1"
        var otherShadowUser = new ChatUser("shadow-other#5", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("OtherShadowConn", "1 vs 1", otherShadowUser);
        _connectionMapping.SetMute("OtherShadowConn", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        // A normal user also in "1 vs 1"
        var normalUser = new ChatUser("normal#1", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("NormalConn", "1 vs 1", normalUser);

        // Shadow-banned user starts in W3C Lounge, switches to "1 vs 1"
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        List<ChatUser> startChatUsers = null;
        _callerProxy
            .Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) =>
            {
                if (method == "StartChat" && args.Length >= 1)
                    startChatUsers = args[0] as List<ChatUser>;
            })
            .Returns(Task.CompletedTask);

        await _chatHub.SwitchRoom("1 vs 1");

        Assert.IsNotNull(startChatUsers, "Shadow-banned user must receive StartChat on join");
        Assert.IsTrue(startChatUsers.Any(u => u.BattleTag == "peter#123"),
            "Shadow-banned viewer must see themselves in usersOfRoom");
        Assert.IsTrue(startChatUsers.Any(u => u.BattleTag == "normal#1"),
            "Normal user must be visible to the shadow-banned viewer");
        Assert.IsTrue(startChatUsers.Any(u => u.BattleTag == "shadow-other#5"),
            "Other shadow-banned users must ALSO be visible (no presence-hiding)");
    }

    [Test]
    public async Task Login_ShadowBan_StartChat_ShowsAllMembers()
    {
        // T3: on login, the shadow-banned user's StartChat shows EVERY member of the room — themselves,
        // normal users, AND other shadow users (no presence-hiding).
        await AddShadowBan("peter#123");

        // Another shadow user already in W3C Lounge
        var otherShadow = new ChatUser("other-shadow#9", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("OtherShadowConn", "W3C Lounge", otherShadow);
        _connectionMapping.SetMute("OtherShadowConn", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        // A normal user also in W3C Lounge
        var normalUser = new ChatUser("normal#1", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("NormalConn", "W3C Lounge", normalUser);

        List<ChatUser> startChatUsers = null;
        _callerProxy
            .Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) =>
            {
                if (method == "StartChat" && args.Length >= 1)
                    startChatUsers = args[0] as List<ChatUser>;
            })
            .Returns(Task.CompletedTask);

        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        Assert.IsNotNull(startChatUsers, "Shadow-banned user must receive StartChat on login");
        Assert.IsTrue(startChatUsers.Any(u => u.BattleTag == "peter#123"),
            "Shadow-banned viewer must see themselves");
        Assert.IsTrue(startChatUsers.Any(u => u.BattleTag == "normal#1"),
            "Normal user must be visible to shadow-banned viewer");
        Assert.IsTrue(startChatUsers.Any(u => u.BattleTag == "other-shadow#9"),
            "Other shadow-banned users must ALSO be visible (no presence-hiding)");
    }

    // ── Task 7 tests ────────────────────────────────────────────────────────────

    [Test]
    public async Task BanUser_LiveUser_FullBan_UpdatesCacheToFull()
    {
        // Arrange: a live user in W3C Lounge
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        // Admin performs ban (the reconcile signal goes through the MuteReconciliationService harness).
        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);
        _hubCallerContext.Setup(c => c.ConnectionId).Returns("TestId");

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "bad behavior", false, endDate);

        // Mute cache must be updated on the live connection
        Assert.IsTrue(_connectionMapping.TryGetMute("VictimConn", out var cached),
            "Full ban must produce a cache HIT on the live connection");
        Assert.AreEqual(MuteStatus.Full, cached.Status,
            "Full ban must update the cached MuteStatus to Full for the live connection");
    }

    [Test]
    public async Task BanUser_LiveUser_ShadowBan_UpdatesCacheToShadow()
    {
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "spam", true, endDate);

        Assert.IsTrue(_connectionMapping.TryGetMute("VictimConn", out var cached),
            "Shadow ban must produce a cache HIT on the live connection");
        Assert.AreEqual(MuteStatus.Shadow, cached.Status,
            "Shadow ban must update the cached MuteStatus to Shadow for the live connection");
    }

    [Test]
    public async Task BanUser_LiveUser_FullBan_CacheEndDateMatchesBanEndDate()
    {
        // Verify the cached endDate is populated so expiry enforcement works from the cache
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDateStr = DateTime.UtcNow.AddDays(7).ToString("O");
        await _chatHub.BanUser("victim#123", "reason", false, endDateStr);

        _connectionMapping.TryGetMute("VictimConn", out var cached);
        Assert.AreEqual(MuteStatus.Full, cached.Status);
        // EndDate must be in the future (ban is active) and close to the requested endDate
        Assert.Greater(cached.EndDate, DateTime.UtcNow,
            "Cached EndDate must be in the future for an active full ban");
    }

    [Test]
    public async Task BanUser_LiveUser_FullBan_InBannedRoom_SendsPlayerBannedFromChat()
    {
        // Live user sitting in W3C Lounge (a public room)
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "bad behavior", false, endDate);

        // The reconcile signal flows through MuteReconciliationService (IHubContext), captured by the harness.
        var payloadInSignal = _reconcileHarness.PayloadFor("VictimConn", "PlayerBannedFromChat");
        Assert.AreEqual(1, _reconcileHarness.SignalCount("VictimConn", "PlayerBannedFromChat"),
            "Live fully-banned user in a public room must receive PlayerBannedFromChat");
        // SECURITY: the slimmed payload carries ONLY the expiry — never the LoungeMute.
        Assert.IsNotInstanceOf<LoungeMute>(payloadInSignal,
            "PlayerBannedFromChat must NOT send the full LoungeMute (leaks reason/isShadowBan)");
        AssertPlayerBannedPayloadIsEndDateOnly(payloadInSignal);
    }

    [Test]
    public async Task BanUser_LiveUser_FullBan_InBannedRoom_NoContextAbort()
    {
        // G1: PlayerBannedFromChat is sent with NO Context.Abort() — the connection must stay alive
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "bad behavior", false, endDate);

        Assert.IsFalse(abortCalled,
            "BanUser must NOT call Context.Abort() on the admin or victim (G1 — no abort anywhere)");
    }

    [Test]
    public async Task BanUser_LiveUser_FullBan_DoesNotEvictFromRoom()
    {
        // Spec §12: do NOT forcibly evict the user from their current room.
        // Enforcement happens on their next SendMessage/SwitchRoom which reads the updated cache.
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "bad behavior", false, endDate);

        // The victim must still be in W3C Lounge after the ban — not evicted
        var room = _connectionMapping.GetRoom("VictimConn");
        Assert.AreEqual("W3C Lounge", room,
            "BanUser must NOT evict the user from their current room (spec §12)");
    }

    [Test]
    public async Task BanUser_LiveUser_FullBan_InExemptRoom_AlsoSendsPlayerBannedFromChat()
    {
        // R7/G5: a user full-banned while sitting in an EXEMPT room (clan/lobby) must STILL
        // receive PlayerBannedFromChat — they must clearly and persistently know they're banned,
        // independent of channel. The signal is NOT gated on the room being a banned room.
        var liveUser = new ChatUser("victim#123", false, "AB", new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "clan AB", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "bad behavior", false, endDate);

        var payloadInSignal = _reconcileHarness.PayloadFor("VictimConn", "PlayerBannedFromChat");
        Assert.AreEqual(1, _reconcileHarness.SignalCount("VictimConn", "PlayerBannedFromChat"),
            "Full-banned live user in an EXEMPT room must still receive PlayerBannedFromChat (R7/G5)");
        // SECURITY: the slimmed payload carries ONLY the expiry — never the LoungeMute.
        Assert.IsNotInstanceOf<LoungeMute>(payloadInSignal,
            "PlayerBannedFromChat must NOT send the full LoungeMute (leaks reason/isShadowBan)");
        AssertPlayerBannedPayloadIsEndDateOnly(payloadInSignal);
        Assert.IsFalse(abortCalled,
            "Context.Abort() must NOT be called when signalling a full ban in an exempt room (G1)");
    }

    [Test]
    public async Task BanUser_LiveUser_MultipleConnections_AllReconciled()
    {
        // A user can be connected from multiple clients (multiple connection ids). A full ban
        // must reconcile EVERY live connection: each cache flips to Full AND each receives
        // PlayerBannedFromChat. No Context.Abort() on any of them.
        var conn1User = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        var conn2User = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn1", "W3C Lounge", conn1User);
        _connectionMapping.SetMute("VictimConn1", MuteStatus.None, DateTime.MinValue);
        _connectionMapping.Add("VictimConn2", "1 vs 1", conn2User);
        _connectionMapping.SetMute("VictimConn2", MuteStatus.None, DateTime.MinValue);

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "bad behavior", false, endDate);

        // Both caches updated to Full
        Assert.IsTrue(_connectionMapping.TryGetMute("VictimConn1", out var cached1));
        Assert.AreEqual(MuteStatus.Full, cached1.Status,
            "First connection's cache must be reconciled to Full");
        Assert.IsTrue(_connectionMapping.TryGetMute("VictimConn2", out var cached2));
        Assert.AreEqual(MuteStatus.Full, cached2.Status,
            "Second connection's cache must be reconciled to Full");

        // Both connections received exactly one PlayerBannedFromChat (via the reconcile harness)
        Assert.AreEqual(1, _reconcileHarness.SignalCount("VictimConn1", "PlayerBannedFromChat"),
            "First connection must receive PlayerBannedFromChat");
        Assert.AreEqual(1, _reconcileHarness.SignalCount("VictimConn2", "PlayerBannedFromChat"),
            "Second connection must receive PlayerBannedFromChat");

        Assert.IsFalse(abortCalled, "Context.Abort() must NOT be called on any connection (G1)");
    }

    [Test]
    public async Task BanUser_LiveUser_ShadowBan_SendsNoSignalToTarget()
    {
        // Shadow ban: illusion preserved — no PlayerBannedFromChat sent to target
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "spam", true, endDate);

        Assert.AreEqual(0, _reconcileHarness.SignalsFor("VictimConn").Count,
            "Shadow ban must send NO signal to the target (illusion preserved)");
    }

    [Test]
    public async Task BanUser_LiveUser_FullBan_SubsequentSendMessage_InBannedRoom_IsRejectedFromCache()
    {
        // End-to-end: full ban applied live → subsequent SendMessage in banned room is rejected
        // WITHOUT requiring another DB read (the cache is now Full).
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "bad behavior", false, endDate);

        // Now switch to victim's hub context and attempt to send
        _hubCallerContext.Setup(c => c.ConnectionId).Returns("VictimConn");
        _chatHub.Clients = _clients.Object;
        _chatHub.Context = _hubCallerContext.Object;

        int groupSendCount = 0;
        _groupProxy
            .Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => groupSendCount++)
            .Returns(Task.CompletedTask);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        await _chatHub.SendMessage("Should be rejected");

        Assert.IsFalse(abortCalled, "Context.Abort() must NOT be called from SendMessage after a live ban");
        Assert.AreEqual(0, groupSendCount,
            "SendMessage in a banned room after a live full ban must be rejected without a DB read");
        Assert.IsEmpty(_chatHistory.GetMessages("W3C Lounge"),
            "Rejected message must not enter history");
    }

    [Test]
    public async Task BanUser_LiveUser_ShadowBan_SubsequentSendMessage_InBannedRoom_IsDroppedFromCache()
    {
        // End-to-end: shadow ban applied live → subsequent SendMessage in banned room is echo-only
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "spam", true, endDate);

        // Switch to victim's hub context
        _hubCallerContext.Setup(c => c.ConnectionId).Returns("VictimConn");

        // Setup victim's caller proxy
        var victimCallerProxy = new Mock<ISingleClientProxy>();
        ChatMessage callerEcho = null;
        victimCallerProxy
            .Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) =>
            {
                if (method == "ReceiveMessage" && args.Length > 0)
                    callerEcho = args[0] as ChatMessage;
            })
            .Returns(Task.CompletedTask);
        _clients.Setup(c => c.Caller).Returns(victimCallerProxy.Object);

        int groupSendCount = 0;
        _groupProxy
            .Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => groupSendCount++)
            .Returns(Task.CompletedTask);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);

        await _chatHub.SendMessage("Invisible message");

        Assert.AreEqual(0, groupSendCount,
            "Shadow-banned user's message in banned room must be dropped (not broadcast) after live shadow ban");
        Assert.IsEmpty(_chatHistory.GetMessages("W3C Lounge"),
            "Dropped shadow message must not enter history");
        Assert.IsNotNull(callerEcho, "Shadow-banned user must still receive echo of their own message");
        Assert.AreEqual("Invisible message", callerEcho.Message);
    }

    [Test]
    public void BanUser_UserNotConnected_NoException()
    {
        // Banning a user who is not currently connected — should complete without error
        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");

        Assert.DoesNotThrowAsync(() =>
            _chatHub.BanUser("offline#999", "reason", false, endDate));
    }

    // ── Task 8 tests ────────────────────────────────────────────────────────────

    [Test]
    public async Task OnDisconnectedAsync_ShadowBan_InPublicRoom_BroadcastsUserLeft()
    {
        // T3: shadow users are full members — disconnecting from a public room broadcasts UserLeft
        // unconditionally (no presence-hiding).
        await AddShadowBan("peter#123");
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        int userLeftCount = 0;
        _groupProxy
            .Setup(x => x.SendCoreAsync("UserLeft", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => userLeftCount++)
            .Returns(Task.CompletedTask);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);

        await _chatHub.OnDisconnectedAsync(null);

        Assert.AreEqual(1, userLeftCount,
            "Shadow-banned user leaving a public room MUST broadcast UserLeft (no presence-hiding)");
    }

    [Test]
    public async Task OnDisconnectedAsync_ShadowBan_InExemptRoom_UserLeftBroadcast()
    {
        await AddShadowBan("peter#123");
        _connectionMapping.Add("TestId", "clan AB", new ChatUser("peter#123", false, "AB", new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        int userLeftCount = 0;
        _groupProxy
            .Setup(x => x.SendCoreAsync("UserLeft", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => userLeftCount++)
            .Returns(Task.CompletedTask);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);

        await _chatHub.OnDisconnectedAsync(null);

        Assert.AreEqual(1, userLeftCount,
            "Shadow-banned user leaving an exempt room must broadcast UserLeft normally");
    }

    [Test]
    public async Task OnDisconnectedAsync_NormalUser_BroadcastsUserLeft()
    {
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.None, DateTime.MinValue);

        int userLeftCount = 0;
        _groupProxy
            .Setup(x => x.SendCoreAsync("UserLeft", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => userLeftCount++)
            .Returns(Task.CompletedTask);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);

        await _chatHub.OnDisconnectedAsync(null);

        Assert.AreEqual(1, userLeftCount, "Normal user disconnect must broadcast UserLeft");
    }

    [Test]
    public async Task OnDisconnectedAsync_ShadowBan_Expired_BroadcastsUserLeft()
    {
        // Expiry regression: a cached Shadow ban whose endDate has passed must NOT suppress UserLeft.
        // GetEffectiveMuteStatus resolves an expired shadow ban to None, so the user was visible —
        // their UserLeft must broadcast on disconnect, even in a banned room.
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Shadow, DateTime.UtcNow.AddDays(-1));

        int userLeftCount = 0;
        _groupProxy
            .Setup(x => x.SendCoreAsync("UserLeft", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => userLeftCount++)
            .Returns(Task.CompletedTask);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);

        await _chatHub.OnDisconnectedAsync(null);

        Assert.AreEqual(1, userLeftCount,
            "Expired shadow ban must NOT suppress UserLeft — user was visible, so UserLeft must broadcast");
    }

    // ── Task 9 tests — expiry regression ─────────────────────────────────────────

    [Test]
    public async Task ExpiredFullBan_LoginConnectsNormally()
    {
        // An expired full ban must not restrict connect at all:
        // no abort, no PlayerBannedFromChat, user seated in default room.
        await _muteRepository.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = "peter#123",
            endDate = DateTime.UtcNow.AddDays(-1).ToString("O"),
            author = "admin#1",
            reason = "old ban",
            isShadowBan = false
        });

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        bool bannedSignalSent = false;
        _callerProxy
            .Setup(x => x.SendCoreAsync("PlayerBannedFromChat", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => bannedSignalSent = true)
            .Returns(Task.CompletedTask);

        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        Assert.IsFalse(abortCalled, "Expired ban must not abort connection");
        Assert.IsFalse(bannedSignalSent, "Expired ban must not send PlayerBannedFromChat");

        // Must be seated in the default room (not blocked from entering)
        var room = _connectionMapping.GetRoom("TestId");
        Assert.AreEqual("W3C Lounge", room, "Expired-ban user must be seated in the default room");
    }

    [Test]
    public async Task ExpiredFullBan_SwitchRoomToBannedRoom_IsAllowed()
    {
        // Expired ban → cached as None → SwitchRoom into a banned room must succeed.
        await _muteRepository.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = "peter#123",
            endDate = DateTime.UtcNow.AddDays(-1).ToString("O"),
            author = "admin#1",
            reason = "old ban",
            isShadowBan = false
        });

        _connectionMapping.Add("TestId", "clan AB", new ChatUser("peter#123", false, "AB", new ProfilePicture(), null, null));
        // Expired: correctly cached as None (not Full)
        _connectionMapping.SetMute("TestId", MuteStatus.None, DateTime.MinValue);

        await _chatHub.SwitchRoom("W3C Lounge");

        Assert.AreEqual("W3C Lounge", _connectionMapping.GetRoom("TestId"),
            "Expired ban must not block joining a banned room via SwitchRoom");
    }

    [Test]
    public async Task ExpiredShadowBan_SendMessage_CachedExpiredEndDate_Broadcasts()
    {
        // Cached Shadow ban whose EndDate is in the past → treated as None at the cache level,
        // so SendMessage must broadcast without consulting the DB.
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        // Cache a shadow ban that has already expired
        _connectionMapping.SetMute("TestId", MuteStatus.Shadow, DateTime.UtcNow.AddDays(-1));
        // DB has NO active ban (IntegrationTestBase drops the DB before each test).

        await _chatHub.SendMessage("Should broadcast now");

        Assert.AreEqual(1, _groupSendCount,
            "Cached shadow ban with expired EndDate must not restrict sending — message must broadcast");
        Assert.AreEqual(1, _chatHistory.GetMessages("W3C Lounge").Count,
            "Message from expired shadow-ban must enter history");
    }

    // ── Task 10 tests — §15 integration sweep ────────────────────────────────────

    [Test]
    public async Task UnbannedUser_SwitchRoom_ToAllRoomTypes_Allowed()
    {
        // An unbanned user must be able to switch freely between banned rooms, clan rooms, and lobbies.
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, "AB", new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.None, DateTime.MinValue);

        await _chatHub.SwitchRoom("1 vs 1");
        Assert.AreEqual("1 vs 1", _connectionMapping.GetRoom("TestId"),
            "Unbanned user must be allowed into a banned room");

        await _chatHub.SwitchRoom("clan AB");
        Assert.AreEqual("clan AB", _connectionMapping.GetRoom("TestId"),
            "Unbanned user must be allowed into a clan room");

        await _chatHub.SwitchRoom("game-lobby-1");
        Assert.AreEqual("game-lobby-1", _connectionMapping.GetRoom("TestId"),
            "Unbanned user must be allowed into a lobby room");
    }

    [Test]
    public async Task MembershipInvariant_UserNotInAnyRoom_SendRejected()
    {
        // R6: a user that has NO room membership must not be able to broadcast a message.
        // ConnectionMapping has no entry for TestId at all.
        await _chatHub.SendMessage("Ghost message");

        Assert.AreEqual(0, _groupSendCount,
            "User with no room membership must not be able to broadcast (R6 membership invariant)");
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

    [Test]
    public async Task FullBan_PersistedDefaultChatIsPublicRoom_OverriddenToSafe()
    {
        // Edge case (§15): a full-banned user whose persisted DefaultChat is a public room
        // must NOT be seated in that public room at connect. They must be placed in their clan
        // room instead (default settings have DefaultChat = "W3C Lounge").
        await AddFullBan("peter#123");

        // Default ChatSettings has DefaultChat = "W3C Lounge" (a public room).
        // The user has ClanTag "AB" → must end up in "clan AB", not "W3C Lounge".
        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, "AB", new ProfilePicture(), null, null));

        var room = _connectionMapping.GetRoom("TestId");
        Assert.AreEqual("clan AB", room,
            "Full-banned user's persisted DefaultChat (a public room) must be overridden to their clan room");
        Assert.IsFalse(DefaultChatRooms.IsPublicRoom(room),
            "The seat room after login must not be a public room");
    }

    // ── Task 11 tests — spec §16 backward-compatibility guardrails ────────────────

    [Test]
    public async Task Compat_SwitchRoom_FullBanRejected_DoesNotAbort_KeepsCurrentRoom()
    {
        // G1/G2: rejecting a full-banned join must not call Context.Abort() AND must leave the
        // user in their current valid room (return BEFORE any Remove/Add).
        await AddFullBan("peter#123");
        _connectionMapping.Add("TestId", "clan AB", new ChatUser("peter#123", false, "AB", new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Full, DateTime.UtcNow.AddDays(1));

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        Assert.DoesNotThrowAsync(async () => await _chatHub.SwitchRoom("W3C Lounge"),
            "Rejected SwitchRoom must return gracefully, not throw (G2)");
        Assert.IsFalse(abortCalled, "G1: SwitchRoom rejection must never call Context.Abort()");
        Assert.AreEqual("clan AB", _connectionMapping.GetRoom("TestId"),
            "G2: rejected SwitchRoom must keep the user in their current valid room");
    }

    [Test]
    public async Task Compat_SendMessage_FullBanInBannedRoom_DoesNotAbort_NoBroadcast()
    {
        // G1/G2: rejecting a full-banned send must not call Context.Abort(), not throw, and
        // must leave the user's room membership intact.
        await AddFullBan("peter#123");
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Full, DateTime.UtcNow.AddDays(1));

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        Assert.DoesNotThrowAsync(async () => await _chatHub.SendMessage("blocked"),
            "Rejected SendMessage must return gracefully, not throw (G2)");
        Assert.IsFalse(abortCalled, "G1: SendMessage rejection must never call Context.Abort()");
        Assert.AreEqual(0, _groupSendCount, "Rejected message must not be broadcast");
        Assert.AreEqual("W3C Lounge", _connectionMapping.GetRoom("TestId"),
            "G2: rejected SendMessage must not corrupt the user's room membership");
    }

    [Test]
    public void Compat_SendMessage_NoRoom_MembershipReject_DoesNotAbort()
    {
        // G1/G2: membership-before-send rejection (null room) must not call Context.Abort() or throw.
        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        Assert.DoesNotThrowAsync(async () => await _chatHub.SendMessage("ghost"),
            "Membership-reject SendMessage must return gracefully, not throw (G2)");
        Assert.IsFalse(abortCalled, "G1: membership rejection must never call Context.Abort()");
        Assert.AreEqual(0, _groupSendCount,
            "No broadcast for membership-rejected message");
    }

    [Test]
    public async Task Compat_Login_FullBanNoClan_StillEmitsStartChat_NoAbort()
    {
        // G1 + G3: a full-banned user with no clan must connect without abort AND receive a
        // StartChat so a legacy client can initialize from it.
        await AddFullBan("peter#123");

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        bool startChatSent = false;
        _callerProxy
            .Setup(x => x.SendCoreAsync("StartChat", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => startChatSent = true)
            .Returns(Task.CompletedTask);

        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        Assert.IsFalse(abortCalled, "G1: full-ban connect must never call Context.Abort()");
        Assert.IsTrue(startChatSent,
            "G3: connect must always emit a StartChat, even for full-ban with no clan/no room");
    }

    [Test]
    public async Task Compat_Login_FullBan_StillSendsLegacyPlayerBannedFromChat()
    {
        // G5: legacy clients depend on PlayerBannedFromChat to render their in-channel ban notice.
        // This must still flow at connect regardless of whether the user has a clan.
        await AddFullBan("peter#123");

        bool bannedSignalSent = false;
        _callerProxy
            .Setup(x => x.SendCoreAsync("PlayerBannedFromChat", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => bannedSignalSent = true)
            .Returns(Task.CompletedTask);

        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, "AB", new ProfilePicture(), null, null));

        Assert.IsTrue(bannedSignalSent,
            "G5: full-ban connect must still emit the legacy PlayerBannedFromChat event");
    }

    // ── Task 2 tests ────────────────────────────────────────────────────────────

    [TestCase("W3C Lounge", ExpectedResult = true)]
    [TestCase("1 vs 1", ExpectedResult = true)]
    [TestCase("2 vs 2", ExpectedResult = true)]
    [TestCase("4 vs 4", ExpectedResult = true)]
    [TestCase("FFA", ExpectedResult = true)]
    [TestCase("Legion TD", ExpectedResult = true)]
    [TestCase("Survival Chaos", ExpectedResult = true)]
    [TestCase("Direct Strike", ExpectedResult = true)]
    [TestCase("Warhammer", ExpectedResult = true)]
    [TestCase("Castle Fight", ExpectedResult = true)]
    [TestCase("Risk Europe", ExpectedResult = true)]
    [TestCase("Mini Dota", ExpectedResult = true)]
    // Mixed-case variants of public rooms must still be caught (case-insensitive check)
    [TestCase("w3c lounge", ExpectedResult = true)]
    [TestCase("1 VS 1", ExpectedResult = true)]
    [TestCase("LEGION TD", ExpectedResult = true)]
    [TestCase("clan AB", ExpectedResult = false)]
    [TestCase("clan XYZ", ExpectedResult = false)]
    [TestCase("game-lobby-42", ExpectedResult = false)]
    [TestCase("custom_lobby", ExpectedResult = false)]
    [TestCase("", ExpectedResult = false)]
    [TestCase(null, ExpectedResult = false)]
    public bool IsPublicRoom_ClassifiesCorrectly(string room)
    {
        return DefaultChatRooms.IsPublicRoom(room);
    }
}

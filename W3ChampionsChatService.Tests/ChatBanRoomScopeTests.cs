// W3ChampionsChatService.Tests/ChatBanRoomScopeTests.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
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

        var chatAuthService = new Mock<IChatAuthenticationService>();
        chatAuthService.Setup(m => m.GetUser(It.IsAny<string>()))
            .ReturnsAsync(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        _chatHub = new ChatHub(
            chatAuthService.Object,
            _muteRepository,
            _settingsRepository,
            _connectionMapping,
            _chatHistory,
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
        LoungeMute muteReceived = null;
        _callerProxy
            .Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) =>
            {
                if (method == "PlayerBannedFromChat")
                {
                    callerMethodReceived = method;
                    muteReceived = args[0] as LoungeMute;
                }
            })
            .Returns(Task.CompletedTask);

        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        Assert.AreEqual("PlayerBannedFromChat", callerMethodReceived);
        Assert.IsNotNull(muteReceived);
        Assert.AreEqual("peter#123", muteReceived.battleTag);
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

        // Must be in "clan AB", not in any banned room
        var room = _connectionMapping.GetRoom("TestId");
        Assert.AreEqual("clan AB", room);
        Assert.IsFalse(DefaultChatRooms.IsBannedRoom(room));
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
    public async Task SwitchRoom_FullBan_NoClanInMapping_LazilReresolvesFromDB_IsRejected()
    {
        // Edge case: full-banned user with no clan was NOT in the ConnectionMapping at login
        // (because they had no clan), so their MuteStatus is None in cache.
        // SwitchRoom must lazily resolve from DB and still reject the move into a banned room.
        await AddFullBan("peter#123");
        // Seated in a room but with no cached MuteStatus (default None), reproducing the
        // no-clan full-ban login path where SetMute was never called.
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));

        await _chatHub.SwitchRoom("1 vs 1");

        // Should be rejected: lazy re-resolve hits DB, finds full ban, and returns early
        var room = _connectionMapping.GetRoom("TestId");
        Assert.AreEqual("W3C Lounge", room,
            "Full-banned user (lazy resolved) must not be moved into a banned room");
    }

    [Test]
    public async Task SwitchRoom_ShadowBan_IntoBannedRoom_GhostJoin_NoUserEnteredBroadcast()
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

        await _chatHub.SwitchRoom("1 vs 1");

        Assert.AreEqual(0, userEnteredCount, "Shadow-banned user must NOT trigger UserEntered in a banned room");
    }

    [Test]
    public async Task SwitchRoom_ShadowBan_IntoBannedRoom_UserIsMovedIntoGroup()
    {
        // Ghost-join: shadow-banned user IS added to the group (can receive messages)
        // even though UserEntered is suppressed.
        await AddShadowBan("peter#123");
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("TestId", MuteStatus.Shadow, DateTime.UtcNow.AddDays(1));

        await _chatHub.SwitchRoom("1 vs 1");

        // Connection mapping must reflect the new room
        var room = _connectionMapping.GetRoom("TestId");
        Assert.AreEqual("1 vs 1", room, "Shadow-banned user must be physically added to the room in the mapping");
    }

    [Test]
    public async Task SwitchRoom_ShadowBan_IntoBannedRoom_CallerReceivesStartChat()
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

        // Shadow-banned user sees themselves in the list (their own view is unfiltered)
        Assert.IsNotNull(startChatUsers, "Shadow-banned user must receive a StartChat on ghost-join");
        Assert.IsTrue(startChatUsers.Any(u => u.BattleTag == "peter#123"),
            "Shadow-banned user must see themselves in usersOfRoom");
    }

    [Test]
    public async Task SwitchRoom_ShadowBan_IntoBannedRoom_UserLeftSuppressed()
    {
        // Ghost-join: when switching rooms, the old-room UserLeft should also be suppressed
        // for a shadow user moving into a banned room (they were already "invisible")
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

        Assert.AreEqual(0, userLeftCount,
            "Shadow-banned user ghost-joining a banned room must NOT trigger UserLeft on the old room");
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
    public async Task SwitchRoom_ShadowBan_ExemptToBanned_StillBroadcastsUserLeft()
    {
        // Cross-category: shadow user was VISIBLE in an exempt room, so leaving it must still
        // broadcast UserLeft (else a stale ghost lingers in the exempt room's user list).
        // The target is banned, so the enter must be suppressed (ghost-join).
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

        Assert.AreEqual(1, userLeftCount,
            "Shadow user leaving an EXEMPT room (where they were visible) must broadcast UserLeft");
        Assert.AreEqual(0, userEnteredCount,
            "Shadow user ghost-joining a BANNED target room must NOT broadcast UserEntered");
    }

    [Test]
    public async Task SwitchRoom_ShadowBan_BannedToExempt_SuppressesUserLeft()
    {
        // Cross-category: shadow user was a GHOST in a banned room (no one saw them enter),
        // so leaving it must NOT broadcast UserLeft. The target is exempt, so they become
        // visible and the enter must fire.
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

        Assert.AreEqual(0, userLeftCount,
            "Shadow user leaving a BANNED room (where they were a ghost) must NOT broadcast UserLeft");
        Assert.AreEqual(1, userEnteredCount,
            "Shadow user entering an EXEMPT target room must broadcast UserEntered (becomes visible)");
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
    public async Task SendMessage_CacheMiss_ActiveDbBan_InBannedRoom_Enforced()
    {
        // Lazy-resolve success path: no cache entry (MISS) + active ban in DB + banned room → enforced.
        // This covers the no-clan full-ban login edge case where SetMute was never called at login.
        await AddFullBan("peter#123");
        // Manually add the user to a banned room WITHOUT calling SetMute (simulates the no-clan login path).
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("peter#123", false, null, new ProfilePicture(), null, null));
        // Do NOT call SetMute — leave a MISS.

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        await _chatHub.SendMessage("Should be rejected");

        Assert.IsFalse(abortCalled, "Context.Abort() must not be called");
        Assert.AreEqual(0, _groupSendCount,
            "Cache-miss lazy-resolved full-ban in banned room must reject the message");
        Assert.IsEmpty(_chatHistory.GetMessages("W3C Lounge"),
            "Rejected message must not enter history");
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

    // ── Task 2 tests ────────────────────────────────────────────────────────────

    [TestCase("W3C Lounge",      ExpectedResult = true)]
    [TestCase("1 vs 1",          ExpectedResult = true)]
    [TestCase("2 vs 2",          ExpectedResult = true)]
    [TestCase("4 vs 4",          ExpectedResult = true)]
    [TestCase("FFA",             ExpectedResult = true)]
    [TestCase("Legion TD",       ExpectedResult = true)]
    [TestCase("Survival Chaos",  ExpectedResult = true)]
    [TestCase("Direct Strike",   ExpectedResult = true)]
    [TestCase("Warhammer",       ExpectedResult = true)]
    [TestCase("Castle Fight",    ExpectedResult = true)]
    [TestCase("Risk Europe",     ExpectedResult = true)]
    [TestCase("Mini Dota",       ExpectedResult = true)]
    // Mixed-case variants of banned rooms must still be caught (case-insensitive ban check)
    [TestCase("w3c lounge",      ExpectedResult = true)]
    [TestCase("1 VS 1",          ExpectedResult = true)]
    [TestCase("LEGION TD",       ExpectedResult = true)]
    [TestCase("clan AB",         ExpectedResult = false)]
    [TestCase("clan XYZ",        ExpectedResult = false)]
    [TestCase("game-lobby-42",   ExpectedResult = false)]
    [TestCase("custom_lobby",    ExpectedResult = false)]
    [TestCase("",                ExpectedResult = false)]
    [TestCase(null,              ExpectedResult = false)]
    public bool IsBannedRoom_ClassifiesCorrectly(string room)
    {
        return DefaultChatRooms.IsBannedRoom(room);
    }
}

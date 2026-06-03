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
    public void ConnectionMapping_MuteStatus_DefaultIsNone()
    {
        var mapping = new ConnectionMapping();
        mapping.Add("conn1", "W3C Lounge", new ChatUser("p#1", false, null, new ProfilePicture(), null, null));

        var status = mapping.GetMuteStatus("conn1");

        Assert.AreEqual(MuteStatus.None, status);
    }

    [Test]
    public void ConnectionMapping_SetMuteStatus_Roundtrips()
    {
        var mapping = new ConnectionMapping();
        mapping.Add("conn1", "W3C Lounge", new ChatUser("p#1", false, null, new ProfilePicture(), null, null));

        mapping.SetMuteStatus("conn1", MuteStatus.Shadow);

        Assert.AreEqual(MuteStatus.Shadow, mapping.GetMuteStatus("conn1"));
    }

    [Test]
    public void ConnectionMapping_SetMuteStatus_Full_Roundtrips()
    {
        var mapping = new ConnectionMapping();
        mapping.Add("conn1", "W3C Lounge", new ChatUser("p#1", false, null, new ProfilePicture(), null, null));

        mapping.SetMuteStatus("conn1", MuteStatus.Full);

        Assert.AreEqual(MuteStatus.Full, mapping.GetMuteStatus("conn1"));
    }

    [Test]
    public void ConnectionMapping_GetMuteStatus_UnknownConnection_ReturnsNone()
    {
        var mapping = new ConnectionMapping();

        var status = mapping.GetMuteStatus("no-such-conn");

        Assert.AreEqual(MuteStatus.None, status);
    }

    [Test]
    public void ConnectionMapping_Remove_ClearsMuteStatus()
    {
        var mapping = new ConnectionMapping();
        mapping.Add("conn1", "W3C Lounge", new ChatUser("p#1", false, null, new ProfilePicture(), null, null));
        mapping.SetMuteStatus("conn1", MuteStatus.Full);

        mapping.Remove("conn1");

        // Direct assertion: Remove must clear the cached status immediately,
        // before any re-Add — guards against a Remove that silently does nothing.
        Assert.AreEqual(MuteStatus.None, mapping.GetMuteStatus("conn1"));

        // After re-add (e.g. SwitchRoom) status is still None
        mapping.Add("conn1", "clan AB", new ChatUser("p#1", false, null, new ProfilePicture(), null, null));

        Assert.AreEqual(MuteStatus.None, mapping.GetMuteStatus("conn1"));
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
        _connectionMapping.SetMuteStatus("TestId", MuteStatus.None);
    }

    [Test]
    public async Task SwitchRoom_FullBan_IntoBannedRoom_IsRejected()
    {
        await AddFullBan("peter#123");
        // Seat user in a non-banned room as if they connected with a clan
        _connectionMapping.Add("TestId", "clan AB", new ChatUser("peter#123", false, "AB", new ProfilePicture(), null, null));
        _connectionMapping.SetMuteStatus("TestId", MuteStatus.Full);

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
        _connectionMapping.SetMuteStatus("TestId", MuteStatus.Full);

        await _chatHub.SwitchRoom(bannedRoom);

        var room = _connectionMapping.GetRoom("TestId");
        Assert.AreEqual("clan AB", room, $"Full-banned user must not join banned room '{bannedRoom}'");
    }

    [Test]
    public async Task SwitchRoom_FullBan_IntoClanRoom_IsAllowed()
    {
        await AddFullBan("peter#123");
        _connectionMapping.Add("TestId", "clan AB", new ChatUser("peter#123", false, "AB", new ProfilePicture(), null, null));
        _connectionMapping.SetMuteStatus("TestId", MuteStatus.Full);

        await _chatHub.SwitchRoom("clan XYZ");

        var room = _connectionMapping.GetRoom("TestId");
        Assert.AreEqual("clan XYZ", room, "Full-banned user must be allowed to join a clan room");
    }

    [Test]
    public async Task SwitchRoom_FullBan_IntoLobbyRoom_IsAllowed()
    {
        await AddFullBan("peter#123");
        _connectionMapping.Add("TestId", "clan AB", new ChatUser("peter#123", false, "AB", new ProfilePicture(), null, null));
        _connectionMapping.SetMuteStatus("TestId", MuteStatus.Full);

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
        _connectionMapping.SetMuteStatus("TestId", MuteStatus.Full);

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
        // no-clan full-ban login path where SetMuteStatus was never called.
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
        _connectionMapping.SetMuteStatus("TestId", MuteStatus.Shadow);

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
        _connectionMapping.SetMuteStatus("TestId", MuteStatus.Shadow);

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
        _connectionMapping.SetMuteStatus("TestId", MuteStatus.Shadow);

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
        _connectionMapping.SetMuteStatus("TestId", MuteStatus.Shadow);

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
        _connectionMapping.SetMuteStatus("TestId", MuteStatus.Shadow);

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
        _connectionMapping.SetMuteStatus("TestId", MuteStatus.Shadow);

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
        _connectionMapping.SetMuteStatus("TestId", MuteStatus.Shadow);

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

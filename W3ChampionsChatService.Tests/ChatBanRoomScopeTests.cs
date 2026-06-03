// W3ChampionsChatService.Tests/ChatBanRoomScopeTests.cs
using System;
using System.Collections.Generic;
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

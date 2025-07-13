using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Settings;

namespace W3ChampionsChatService.Tests;

public class ChatTests : IntegrationTestBase
{
    private ChatHub _chatHub;
    private IChatAuthenticationService _chatAuthenticationService;
    private MuteRepository _muteRepository;
    private Mock<IHubCallerClients> _clients;
    private Mock<HubCallerContext> _hubCallerContext;
    private ConnectionMapping _connectionMapping;
    private ChatHistory _chatHistory;
    private SettingsRepository _settingsRepository;
    private string _jwt = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJiYXR0bGVUYWciOiJtb2Rtb3RvIzI4MDkiLCJpc0FkbWluIjoiVHJ1ZSIsIm5hbWUiOiJtb2Rtb3RvIn0.0rJooIabRqj_Gt0fuuW5VP6ICdV1FJfwRJYuhesou7rPqE9HWZRewm12bd4iWusa4lcYK6vp5LCr6fBj4XUc2iQ4Bo9q3qtu54Rwc-eH2m-_7VqJE6D3yLm7Gcre0NE2LHZjh7qA5zHQn5kU_ugOmcovaVJN_zVEM1wRrVwR6mkNDwIwv3f_A_3AQOB8s0rin0MS4950DnFkmM0CLQ-MMzwFHg_kKgiStSiAp-2Mlu5SijGUx8keM3ArjOj7Kplk_wxjPCkjplIfAHb5qXBpdcO5exXD7UJwETqUHu4NgH-9-GWzPPNCW5BMfzPV-BMiO1sESEb4JZUZqTSJCnAG2d1mx_yukDHR_8ZSd-rB5en2WzOdN1Fjds_M0u5BvnAaLQOzz69YURL4mnI-jiNpFNokRWYjzG-_qEVJTRtUugiCipT6SMs3SlwWujxXsNSZZU0LguOuAh4EqF9ST7m_ttOcZvg5G1RLOy6A1QzWVG06Byw-7dZvMpoHrMSqjlNcJk7XtDamAVDyUNpjrqlu_I17U5DN6f8evfBtngsSgpjeswy6ccul10HRNO210I7VejGOmEsxnIDWyF-5p-UIuOaTgMiXhElwSpkIaLGQJXHFXc859UjvqC7jSRnPWpRlYRo7UpKmCJ59fgK-SzZlbp27gN_1uhk18eEWrenn6ew";

    // For capturing messages in tests
    private ChatMessage _capturedCallerMessage;
    private ChatMessage _capturedGroupMessage;
    private bool _groupMessageSent;
    private Mock<ISingleClientProxy> _callerProxy;
    private Mock<IClientProxy> _groupProxy;

    [SetUp]
    public void SetupBeforeEach()
    {
        _muteRepository = new MuteRepository(MongoClient);
        _clients = new Mock<IHubCallerClients>();
        _hubCallerContext = new Mock<HubCallerContext>();
        ResetSetups();

        var chatAuthenticationService = new Mock<IChatAuthenticationService>();
        chatAuthenticationService.Setup(m => m.GetUser(It.IsAny<string>()))
            .ReturnsAsync(new ChatUser("peter#123", false, "AB", new ProfilePicture()));
        _chatAuthenticationService = chatAuthenticationService.Object;
        _connectionMapping = new ConnectionMapping();
        _chatHistory = new ChatHistory();
        _settingsRepository = new SettingsRepository(MongoClient);

        _chatHub = new ChatHub(
            _chatAuthenticationService,
            _muteRepository,
            _settingsRepository,
            _connectionMapping,
            _chatHistory,
            null);

        // Setup message capturing proxies
        _capturedCallerMessage = null;
        _capturedGroupMessage = null;
        _groupMessageSent = false;

        _callerProxy = new Mock<ISingleClientProxy>();
        _callerProxy.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, token) =>
            {
                if (method == "ReceiveMessage" && args.Length > 0 && args[0] is ChatMessage msg)
                {
                    _capturedCallerMessage = msg;
                }
            })
            .Returns(Task.CompletedTask);

        _groupProxy = new Mock<IClientProxy>();
        _groupProxy.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, token) =>
            {
                if (method == "ReceiveMessage")
                {
                    _groupMessageSent = true;
                    if (args.Length > 0 && args[0] is ChatMessage msg)
                    {
                        _capturedGroupMessage = msg;
                    }
                }
            })
            .Returns(Task.CompletedTask);

        _clients.Setup(c => c.Caller).Returns(_callerProxy.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);
        _chatHub.Clients = _clients.Object;

        _hubCallerContext.Setup(c => c.ConnectionId).Returns("TestId");
        _chatHub.Context = _hubCallerContext.Object;
        _chatHub.Groups = new Mock<IGroupManager>().Object;
    }

    // Helper method to authenticate a user and prepare for tests
    private async Task LoginUser()
    {
        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, "[123]", new ProfilePicture()));
    }

    // Helper method to verify message was sent only to caller and not to group
    private void VerifyMessageOnlySentToCaller(string expectedMessage, string expectedSenderName = "[SYSTEM]")
    {
        // Verify message sent to caller
        Assert.IsNotNull(_capturedCallerMessage, "No message was sent to caller");
        Assert.AreEqual(expectedMessage, _capturedCallerMessage.Message);
        Assert.AreEqual(expectedSenderName, _capturedCallerMessage.User.Name);

        // Verify no message broadcast to group
        Assert.IsFalse(_groupMessageSent, "Message was incorrectly sent to group");
        Assert.AreEqual(0, _chatHistory.GetMessages("W3C Lounge").Count, "Message was incorrectly added to history");
    }

    [Test]
    public async Task Login()
    {
        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, "[123]", new ProfilePicture()));

        var usersOfRoom = _connectionMapping.GetUsersOfRoom("W3C Lounge");
        Assert.AreEqual(1, usersOfRoom.Count);
        Assert.AreEqual("peter", usersOfRoom[0].Name);
        Assert.AreEqual("peter#123", usersOfRoom[0].BattleTag);
    }

    [Test]
    public async Task SwitchRoom()
    {
        await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", false, "[123]", new ProfilePicture()));

        await _chatHub.SwitchRoom("w3c");

        var usersOfRoom1 = _connectionMapping.GetUsersOfRoom("W3C Lounge");
        var usersOfRoom2 = _connectionMapping.GetUsersOfRoom("w3c");
        Assert.AreEqual(0, usersOfRoom1.Count);
        Assert.AreEqual(1, usersOfRoom2.Count);
        Assert.AreEqual("peter", usersOfRoom2[0].Name);
        Assert.AreEqual("peter#123", usersOfRoom2[0].BattleTag);

        var setting = await _settingsRepository.Load("peter#123");
        Assert.AreEqual(setting.DefaultChat, "w3c");
    }

    [Test]
    public void GetToken()
    {
        var w3CAuthenticationService = new W3CAuthenticationService();
        var userByToken1 = w3CAuthenticationService.GetUserByToken(_jwt);

        Assert.AreEqual("modmoto#2809", userByToken1.BattleTag);
    }

    [Test]
    public async Task BannedUser_CannotSendMessage()
    {
        // Login before ban
        await LoginUser();

        // Reset the group message flag after login
        _groupMessageSent = false;

        // Setup muted player
        var mute = new LoungeMuteRequest
        {
            battleTag = "peter#123",
            endDate = DateTime.UtcNow.AddDays(1).ToString(),
            author = "modmoto#2809"
        };

        await _muteRepository.AddLoungeMute(mute);

        // Set up tracking for Context.Abort()
        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        // Send message
        await _chatHub.SendMessage("Hello world");

        // Verify connection aborted and message not sent
        Assert.IsTrue(abortCalled);
        Assert.IsFalse(_groupMessageSent, "No message should be sent to the group for banned users");
        Assert.AreEqual(0, _chatHistory.GetMessages("W3C Lounge").Count);
    }

    [Test]
    public async Task RegularMessage_IsBroadcastToAllUsers()
    {
        // Login user and send message
        await LoginUser();
        await _chatHub.SendMessage("Hello world");

        // Verify message is in history
        var messages = _chatHistory.GetMessages("W3C Lounge");
        Assert.AreEqual(1, messages.Count);
        Assert.AreEqual("Hello world", messages[0].Message);

        // Verify message is broadcast to group
        Assert.IsNotNull(_capturedGroupMessage);
        Assert.AreEqual("Hello world", _capturedGroupMessage.Message);
        Assert.AreEqual("peter#123", _capturedGroupMessage.User.BattleTag);
    }

    [Test]
    [TestCase("/w john#456 Hello there", "Private messages to other players are currently not supported!", Description = "Short whisper command")]
    [TestCase("/whisper john#456 Hello there", "Private messages to other players are currently not supported!", Description = "Full whisper command")]
    [TestCase("/r Hello there again", "Private messages to other players are currently not supported!", Description = "Short reply command")]
    [TestCase("/reply Hello there again", "Private messages to other players are currently not supported!", Description = "Full reply command")]
    [TestCase("/help", "Chat commands are currently not supported!", Description = "Other command")]
    public async Task ChatCommands_Not_Supported(string command, string expectedResponse)
    {
        await LoginUser();

        // Test the command
        await _chatHub.SendMessage(command);

        // Verify system message sent only to caller
        VerifyMessageOnlySentToCaller(expectedResponse);
    }

    private void ResetSetups()
    {
        _clients.Reset();
        _hubCallerContext.Reset();
    }
}

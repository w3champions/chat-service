using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Settings;

namespace W3ChampionsChatService.Tests;

public class ChatHubDeletionTests : IntegrationTestBase
{
    private ChatHub _chatHub;
    private IChatAuthenticationService _chatAuthenticationService;
    private MuteRepository _muteRepository;
    private Mock<IHubCallerClients> _clients;
    private Mock<HubCallerContext> _hubCallerContext;
    private ConnectionMapping _connectionMapping;
    private ChatHistory _chatHistory;
    private SettingsRepository _settingsRepository;
    private Mock<IClientProxy> _mockAllProxy;

    [SetUp]
    public void SetupBeforeEach()
    {
        _muteRepository = new MuteRepository(MongoClient);
        _clients = new Mock<IHubCallerClients>();
        _hubCallerContext = new Mock<HubCallerContext>();
        _mockAllProxy = new Mock<IClientProxy>();

        var chatAuthenticationService = new Mock<IChatAuthenticationService>();
        chatAuthenticationService.Setup(m => m.GetUser(It.IsAny<string>()))
            .ReturnsAsync(new ChatUser("admin#123", true, "Admin", new ProfilePicture()));
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

        _clients.Setup(c => c.All).Returns(_mockAllProxy.Object);
        _chatHub.Clients = _clients.Object;

        _hubCallerContext.Setup(c => c.ConnectionId).Returns("AdminConnectionId");
        _chatHub.Context = _hubCallerContext.Object;
        _chatHub.Groups = new Mock<IGroupManager>().Object;

        // Add admin user to connections
        var adminUser = new ChatUser("admin#123", true, "Admin", new ProfilePicture());
        _connectionMapping.Add("AdminConnectionId", "W3C Lounge", adminUser);
    }

    [Test]
    public async Task DeleteMessage_ValidMessage_DeletesAndNotifiesClients()
    {
        // Arrange
        var user = new ChatUser("sender#123", false, "Sender", new ProfilePicture());
        var message = new ChatMessage(user, "Message to delete");
        _chatHistory.AddMessage("W3C Lounge", message);

        // Act
        await _chatHub.DeleteMessage(message.Id);

        // Assert
        var messages = _chatHistory.GetMessages("W3C Lounge");
        Assert.AreEqual(0, messages.Count, "Message should be deleted from history");

        _mockAllProxy.Verify(p => p.SendCoreAsync("MessageDeleted",
            It.Is<object[]>(args => args.Length == 1 && args[0].Equals(message.Id)),
            default), Times.Once);
    }

    [Test]
    public async Task DeleteMessage_NonExistentMessage_DoesNotNotifyClients()
    {
        // Act
        await _chatHub.DeleteMessage("nonexistent-id");

        // Assert
        _mockAllProxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), default),
            Times.Never);
    }

    [Test]
    public async Task PurgeMessagesFromUser_ExistingUser_DeletesAllMessagesAndNotifies()
    {
        // Arrange
        var targetUser = new ChatUser("target#123", false, "Target", new ProfilePicture());
        var otherUser = new ChatUser("other#456", false, "Other", new ProfilePicture());

        var message1 = new ChatMessage(targetUser, "Message 1");
        var message2 = new ChatMessage(otherUser, "Message 2");
        var message3 = new ChatMessage(targetUser, "Message 3");

        _chatHistory.AddMessage("W3C Lounge", message1);
        _chatHistory.AddMessage("W3C Lounge", message2);
        _chatHistory.AddMessage("room2", message3);

        // Act
        await _chatHub.PurgeMessagesFromUser("target#123");

        // Assert
        var loungeMessages = _chatHistory.GetMessages("W3C Lounge");
        var room2Messages = _chatHistory.GetMessages("room2");

        Assert.AreEqual(1, loungeMessages.Count, "Only other user's message should remain in lounge");
        Assert.AreEqual("other#456", loungeMessages[0].User.BattleTag);
        Assert.AreEqual(0, room2Messages.Count, "Target user's message should be deleted from room2");

        _mockAllProxy.Verify(p => p.SendCoreAsync("BulkMessageDeleted",
            It.Is<object[]>(args => args.Length == 1 &&
                args[0] != null &&
                args[0].GetType() == typeof(System.Collections.Generic.List<string>)),
            default), Times.Once);
    }

    [Test]
    public async Task PurgeMessagesFromUser_UserWithNoMessages_DoesNotNotifyClients()
    {
        // Arrange
        var user = new ChatUser("other#456", false, "Other", new ProfilePicture());
        var message = new ChatMessage(user, "Message");
        _chatHistory.AddMessage("W3C Lounge", message);

        // Act
        await _chatHub.PurgeMessagesFromUser("nonexistent#123");

        // Assert
        var messages = _chatHistory.GetMessages("W3C Lounge");
        Assert.AreEqual(1, messages.Count, "Original message should remain");

        _mockAllProxy.Verify(p => p.SendCoreAsync("BulkMessageDeleted", It.IsAny<object[]>(), default),
            Times.Never);
    }

    [Test]
    [TestCase("target#123", "Inappropriate behavior", false, 1, Description = "Regular ban for 1 day")]
    [TestCase("spammer#456", "Spam", true, 0.25, Description = "Shadow ban for 6 hours")]
    [TestCase("toxic#789", "Toxic behavior", false, 7, Description = "Regular ban for 7 days")]
    [TestCase("shadow#321", "Trolling", true, 3, Description = "Shadow ban for 3 days")]
    public async Task BanUser_ValidRequest_AddsLoungeMute(string battleTag, string reason, bool isShadowBan, double daysToAdd)
    {
        // Arrange
        var endDateTime = DateTime.UtcNow.AddDays(daysToAdd);
        var endDate = endDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

        // Act
        await _chatHub.BanUser(battleTag, reason, isShadowBan, endDate);

        // Assert
        var mute = await _muteRepository.GetMutedPlayer(battleTag);
        Assert.IsNotNull(mute);
        Assert.AreEqual(battleTag, mute.battleTag);
        Assert.AreEqual(reason, mute.reason);
        Assert.AreEqual("admin#123", mute.author);
        Assert.AreEqual(isShadowBan, mute.isShadowBan);

        // Allow for small time differences due to test execution time
        var timeDifference = Math.Abs((mute.endDate - endDateTime).TotalSeconds);
        Assert.IsTrue(timeDifference < 10, $"Expected end date to be close to {endDateTime}, but was {mute.endDate}");
    }

    [Test]
    [TestCase("test#123", "Test message", "room1", Description = "Delete message from room1")]
    [TestCase("user#456", "Another message", "room2", Description = "Delete message from room2")]
    [TestCase("admin#789", "Admin message", "admin-room", Description = "Delete admin message")]
    public void ChatHistory_DeleteMessage_ReturnsDeletedMessage(string battleTag, string messageText, string room)
    {
        // Arrange
        var user = new ChatUser(battleTag, false, "Test", new ProfilePicture());
        var message = new ChatMessage(user, messageText);
        _chatHistory.AddMessage(room, message);

        // Act
        var deletedMessage = _chatHistory.DeleteMessage(message.Id);

        // Assert
        Assert.IsNotNull(deletedMessage);
        Assert.AreEqual(message.Id, deletedMessage.Id);
        Assert.AreEqual(messageText, deletedMessage.Message);
        Assert.AreEqual(battleTag, deletedMessage.User.BattleTag);
        Assert.AreEqual(0, _chatHistory.GetMessages(room).Count);
    }

    [Test]
    [TestCase("nonexistent-id")]
    [TestCase("")]
    [TestCase("invalid-guid")]
    public void ChatHistory_DeleteMessage_NonExistentMessage_ReturnsNull(string messageId)
    {
        // Act
        var deletedMessage = _chatHistory.DeleteMessage(messageId);

        // Assert
        Assert.IsNull(deletedMessage);
    }

    [Test]
    public void ChatHistory_DeleteMessagesFromUser_ReturnsDeletedMessagesList()
    {
        // Arrange
        var user1 = new ChatUser("test#123", false, "Test1", new ProfilePicture());
        var user2 = new ChatUser("other#456", false, "Test2", new ProfilePicture());
        var message1 = new ChatMessage(user1, "Message 1");
        var message2 = new ChatMessage(user2, "Message 2");
        var message3 = new ChatMessage(user1, "Message 3");

        _chatHistory.AddMessage("room1", message1);
        _chatHistory.AddMessage("room1", message2);
        _chatHistory.AddMessage("room2", message3);

        // Act
        var deletedMessages = _chatHistory.DeleteMessagesFromUser("test#123");

        // Assert
        Assert.AreEqual(2, deletedMessages.Count);
        Assert.IsTrue(deletedMessages.Any(m => m.Id == message1.Id));
        Assert.IsTrue(deletedMessages.Any(m => m.Id == message3.Id));
        Assert.AreEqual(1, _chatHistory.GetMessages("room1").Count);
        Assert.AreEqual("other#456", _chatHistory.GetMessages("room1")[0].User.BattleTag);
        Assert.AreEqual(0, _chatHistory.GetMessages("room2").Count);
    }
}

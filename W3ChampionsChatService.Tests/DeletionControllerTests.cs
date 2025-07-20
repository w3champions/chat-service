using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Mutes;

namespace W3ChampionsChatService.Tests;

public class DeletionControllerTests
{
    private DeletionController _controller;
    private ChatHistory _chatHistory;
    private Mock<IHubContext<ChatHub>> _mockHubContext;
    private Mock<IHubClients> _mockClients;
    private Mock<IClientProxy> _mockClientProxy;
    private readonly string _validSecret = "300C018C-6321-4BAB-B289-9CB3DB760CBB";

    [SetUp]
    public void SetUp()
    {
        _chatHistory = new ChatHistory();
        _mockHubContext = new Mock<IHubContext<ChatHub>>();
        _mockClients = new Mock<IHubClients>();
        _mockClientProxy = new Mock<IClientProxy>();

        _mockHubContext.Setup(x => x.Clients).Returns(_mockClients.Object);
        _mockClients.Setup(x => x.All).Returns(_mockClientProxy.Object);

        _controller = new DeletionController(_chatHistory, _mockHubContext.Object);
    }

    [Test]
    public async Task DeleteMessage_ValidSecret_DeletesMessage()
    {
        var user = new ChatUser("test#123", false, "Test", new ProfilePicture());
        var message = new ChatMessage(user, "Test message");
        _chatHistory.AddMessage("room1", message);

        var result = await _controller.DeleteMessage(message.Id, _validSecret);

        Assert.IsInstanceOf<OkResult>(result);
        Assert.AreEqual(0, _chatHistory.GetMessages("room1").Count);
        _mockClientProxy.Verify(x => x.SendCoreAsync("MessageDeleted", new object[] { message.Id }, default), Times.Once);
    }

    [Test]
    [TestCase("wrongSecret", 403, Description = "Invalid secret returns 403")]
    [TestCase("", 403, Description = "Empty secret returns 403")]
    [TestCase(null, 403, Description = "Null secret returns 403")]
    public async Task DeleteMessage_InvalidSecret_Returns403(string secret, int expectedStatusCode)
    {
        var result = await _controller.DeleteMessage("anyId", secret);

        Assert.IsInstanceOf<StatusCodeResult>(result);
        Assert.AreEqual(expectedStatusCode, ((StatusCodeResult)result).StatusCode);
    }

    [Test]
    public async Task DeleteMessage_MessageNotFound_Returns404()
    {
        var result = await _controller.DeleteMessage("nonExistentId", _validSecret);

        Assert.IsInstanceOf<NotFoundObjectResult>(result);
    }

    [Test]
    public async Task PurgeMessagesFromUser_ValidSecret_DeletesAllUserMessages()
    {
        var user1 = new ChatUser("test#123", false, "Test1", new ProfilePicture());
        var user2 = new ChatUser("other#456", false, "Test2", new ProfilePicture());
        var message1 = new ChatMessage(user1, "Message 1");
        var message2 = new ChatMessage(user2, "Message 2");
        var message3 = new ChatMessage(user1, "Message 3");

        _chatHistory.AddMessage("room1", message1);
        _chatHistory.AddMessage("room1", message2);
        _chatHistory.AddMessage("room2", message3);

        var result = await _controller.PurgeMessagesFromUser("test#123", _validSecret);

        Assert.IsInstanceOf<OkObjectResult>(result);
        var okResult = (OkObjectResult)result;
        Assert.AreEqual(2, ((dynamic)okResult.Value).DeletedCount);

        Assert.AreEqual(1, _chatHistory.GetMessages("room1").Count);
        Assert.AreEqual("other#456", _chatHistory.GetMessages("room1")[0].User.BattleTag);
        Assert.AreEqual(0, _chatHistory.GetMessages("room2").Count);

        _mockClientProxy.Verify(x => x.SendCoreAsync("MessageDeleted", It.IsAny<object[]>(), default), Times.Exactly(2));
    }

    [Test]
    [TestCase("wrongSecret", 403, Description = "Invalid secret returns 403")]
    [TestCase("", 403, Description = "Empty secret returns 403")]
    [TestCase(null, 403, Description = "Null secret returns 403")]
    public async Task PurgeMessagesFromUser_InvalidSecret_Returns403(string secret, int expectedStatusCode)
    {
        var result = await _controller.PurgeMessagesFromUser("test#123", secret);

        Assert.IsInstanceOf<StatusCodeResult>(result);
        Assert.AreEqual(expectedStatusCode, ((StatusCodeResult)result).StatusCode);
    }
}

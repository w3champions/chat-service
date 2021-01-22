using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Bans;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Settings;

namespace W3ChampionsChatService.Tests
{
    public class ChatTests : IntegrationTestBase
    {
        private ChatHub _chatHub;
        private IChatAuthenticationService _chatAuthenticationService;
        private BanRepository _banRepository;
        private ConnectionMapping _connectionMapping;
        private ChatHistory _chatHistory;
        private SettingsRepository _settingsRepository;

        [SetUp]
        public void SetupHere()
        {
            var chatAuthenticationService = new Mock<IChatAuthenticationService>();
            chatAuthenticationService.Setup(m => m.GetUser(It.IsAny<string>()))
                .ReturnsAsync(new ChatUser("peter#123", "AB", new ProfilePicture()));
            _chatAuthenticationService = chatAuthenticationService.Object;
            _banRepository = new BanRepository(MongoClient);
            _connectionMapping = new ConnectionMapping();
            _chatHistory = new ChatHistory();
            _settingsRepository = new SettingsRepository(MongoClient);
            _chatHub = new ChatHub(_chatAuthenticationService, _banRepository, _settingsRepository,
            _connectionMapping, _chatHistory, null);

            var clients = new Mock<IHubCallerClients>();
            clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
            clients.Setup(c => c.Caller).Returns(new Mock<IClientProxy>().Object);
            _chatHub.Clients = clients.Object;

            var context = new Mock<HubCallerContext>();
            context.Setup(c => c.ConnectionId).Returns("TestId");
            _chatHub.Context = context.Object;
            _chatHub.Groups = new Mock<IGroupManager>().Object;
        }

        [Test]
        public async Task Login()
        {
            await _chatHub.LoginAs(new ChatUser("peter#123", "[123]", new ProfilePicture()));

            var usersOfRoom = _connectionMapping.GetUsersOfRoom("W3C Lounge");
            Assert.AreEqual(1, usersOfRoom.Count);
            Assert.AreEqual("peter", usersOfRoom[0].Name);
            Assert.AreEqual("peter#123", usersOfRoom[0].BattleTag);
        }

        [Test]
        public async Task SwitchRoom()
        {
            await _chatHub.LoginAs(new ChatUser("peter#123", "[123]", new ProfilePicture()));

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
    }
}
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Bans;
using W3ChampionsChatService.Chats;

namespace W3ChampionsChatService.Tests
{
    public class ChatTests : IntegrationTestBase
    {
        private ChatHub _chatHub;
        private ChatAuthenticationService _chatAuthenticationService;
        private BanRepository _banRepository;
        private ConnectionMapping _connectionMapping;
        private ChatHistory _chatHistory;

        [SetUp]
        public void Setup()
        {
            _chatAuthenticationService = new ChatAuthenticationService(MongoClient);
            _banRepository = new BanRepository(MongoClient);
            _connectionMapping = new ConnectionMapping();
            _chatHistory = new ChatHistory();
            _chatHub = new ChatHub(_chatAuthenticationService, _banRepository, _connectionMapping, _chatHistory);

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
            await _chatHub.LoginAs("", "peter#123", "clan rbtv");

            var usersOfRoom = _connectionMapping.GetUsersOfRoom("clan rbtv");
            Assert.AreEqual(1, usersOfRoom.Count);
            Assert.AreEqual("peter", usersOfRoom[0].Name);
            Assert.AreEqual("peter#123", usersOfRoom[0].BattleTag);
        }
    }
}
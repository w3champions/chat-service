using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
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
        private string _jwt = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJCYXR0bGVUYWciOiJtb2Rtb3RvIzI4MDkiLCJJc0FkbWluIjoiVHJ1ZSIsIk5hbWUiOiJtb2Rtb3RvIn0.Y4xe1wqRceSdJW2evar5LFVsWfixZUUQtWWckehnkNwVpGiNIzQb90GP30fzOFt9GKUXO7ADNuy4ss8tTNxlvSiYmkT9Ulx1-ve64WO8SYJUBwFVqPorBrunz628tFyf4t1YMt_q_lfbVuQc1WdJiNVqFy1FNzkWENW-GsZbJB-shrCIVj9qp_MtP7MC0Bata7XCjTszlZnVAJUh7-iBPlUhSg8405U5aHkGpPzjLRgQtlGm6s8F1lYOyIzT-rCCvAI_dVI3F4ee6cjS0MbY9m8KPjloOx2NJGKvbwE0dAKBszKbQ7Ic3zr6yCvj-FBt82VmAaDan7pzXJLyZcSnFbikhsKSjLzcAXw1fP_I-FhEIvS-9vysWmXx9uNF91cDlXvdZZo57gV7o6vS4CgXscvpwiPQ9KnKsQA3Ezn61snZoXjGKspiTI_yblC4zLPHm-s40RmPOI_9TwxaiOurl6GjZk1uNY5dm7cGQjh4QWbha8CkllAmgknKOfQw9Mj7TvEKukkFetKF96jOjnqBFQUVXM8YL8K9rzATEy45vkPbfTs7MP9dHUVyEUYfD-HoYMpexEkPRwpCsLty2VfDmIV9Jkj3yOh3ybeKgv7N3Dh8ROx2lxSnqZhyc5HfE_AsnjaLTq2SvEqJ4ndYtYH9rVIARx0p_gPBZF9kAl-Nb2M";

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

        [Test]
        public void GetToken()
        {
            var w3CAuthenticationService = new W3CAuthenticationService();
            var userByToken1 = w3CAuthenticationService.GetUserByToken(_jwt);

            Assert.AreEqual("modmoto#2809", userByToken1.BattleTag);
        }
    }
}
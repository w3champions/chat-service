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
        private IBanRepository _banRepository;
        private ConnectionMapping _connectionMapping;
        private ChatHistory _chatHistory;
        private SettingsRepository _settingsRepository;
        private string _jwt = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJiYXR0bGVUYWciOiJtb2Rtb3RvIzI4MDkiLCJpc0FkbWluIjoiVHJ1ZSIsIm5hbWUiOiJtb2Rtb3RvIn0.0rJooIabRqj_Gt0fuuW5VP6ICdV1FJfwRJYuhesou7rPqE9HWZRewm12bd4iWusa4lcYK6vp5LCr6fBj4XUc2iQ4Bo9q3qtu54Rwc-eH2m-_7VqJE6D3yLm7Gcre0NE2LHZjh7qA5zHQn5kU_ugOmcovaVJN_zVEM1wRrVwR6mkNDwIwv3f_A_3AQOB8s0rin0MS4950DnFkmM0CLQ-MMzwFHg_kKgiStSiAp-2Mlu5SijGUx8keM3ArjOj7Kplk_wxjPCkjplIfAHb5qXBpdcO5exXD7UJwETqUHu4NgH-9-GWzPPNCW5BMfzPV-BMiO1sESEb4JZUZqTSJCnAG2d1mx_yukDHR_8ZSd-rB5en2WzOdN1Fjds_M0u5BvnAaLQOzz69YURL4mnI-jiNpFNokRWYjzG-_qEVJTRtUugiCipT6SMs3SlwWujxXsNSZZU0LguOuAh4EqF9ST7m_ttOcZvg5G1RLOy6A1QzWVG06Byw-7dZvMpoHrMSqjlNcJk7XtDamAVDyUNpjrqlu_I17U5DN6f8evfBtngsSgpjeswy6ccul10HRNO210I7VejGOmEsxnIDWyF-5p-UIuOaTgMiXhElwSpkIaLGQJXHFXc859UjvqC7jSRnPWpRlYRo7UpKmCJ59fgK-SzZlbp27gN_1uhk18eEWrenn6ew";

        [SetUp]
        public void SetupHere()
        {
            var chatAuthenticationService = new Mock<IChatAuthenticationService>();
            chatAuthenticationService.Setup(m => m.GetUser(It.IsAny<string>()))
                .ReturnsAsync(new ChatUser("peter#123", "AB", new ProfilePicture()));
            _chatAuthenticationService = chatAuthenticationService.Object;
            _banRepository = new Mock<IBanRepository>().Object;
            _connectionMapping = new ConnectionMapping();
            _chatHistory = new ChatHistory();
            _settingsRepository = new SettingsRepository(MongoClient);
            _chatHub = new ChatHub(_chatAuthenticationService, _banRepository, _settingsRepository,
            _connectionMapping, _chatHistory, null, null);

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
            await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", "[123]", new ProfilePicture()));

            var usersOfRoom = _connectionMapping.GetUsersOfRoom("W3C Lounge");
            Assert.AreEqual(1, usersOfRoom.Count);
            Assert.AreEqual("peter", usersOfRoom[0].Name);
            Assert.AreEqual("peter#123", usersOfRoom[0].BattleTag);
        }

        [Test]
        public async Task SwitchRoom()
        {
            await _chatHub.LoginAsAuthenticated(new ChatUser("peter#123", "[123]", new ProfilePicture()));

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
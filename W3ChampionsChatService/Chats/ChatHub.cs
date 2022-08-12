using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using W3ChampionsChatService.Bans;
using W3ChampionsChatService.Settings;

[assembly:InternalsVisibleTo("W3ChampionsChatService.Tests")]
namespace W3ChampionsChatService.Chats
{
    public class ChatHub : Hub
    {
        private readonly IChatAuthenticationService _authenticationService;
        private readonly IBanRepository _banRepository;
        private readonly SettingsRepository _settingsRepository;
        private readonly ConnectionMapping _connections;
        private readonly ChatHistory _chatHistory;
        private readonly IHttpContextAccessor _contextAccessor;
        private readonly IWebsiteBackendRepository _websiteBackendRepository;

        public ChatHub(
            IChatAuthenticationService authenticationService,
            IBanRepository banRepository,
            SettingsRepository settingsRepository,
            ConnectionMapping connections,
            ChatHistory chatHistory,
            IHttpContextAccessor contextAccessor,
            IWebsiteBackendRepository websiteBackendRepository)
        {
            _authenticationService = authenticationService;
            _banRepository = banRepository;
            _settingsRepository = settingsRepository;
            _connections = connections;
            _chatHistory = chatHistory;
            _contextAccessor = contextAccessor;
            _websiteBackendRepository = websiteBackendRepository;
        }

        public async Task SendMessage(string message)
        {
            var trimmedMessage = message.Trim();
            if (!string.IsNullOrEmpty(trimmedMessage))
            {
                var chatRoom = _connections.GetRoom(Context.ConnectionId);
                var user = _connections.GetUser(Context.ConnectionId);

                var chatMessage = new ChatMessage(user, trimmedMessage);
                _chatHistory.AddMessage(chatRoom, chatMessage);
                await Clients.Group(chatRoom).SendAsync("ReceiveMessage", chatMessage);
            }
        }

        public override async Task OnConnectedAsync()
        {
            bool oauth = Environment.GetEnvironmentVariable("BNET_OAUTH") == "true";
            if (oauth)
            {
                var accessToken = _contextAccessor?.HttpContext?.Request.Query["access_token"];
                var user = await _authenticationService.GetUser(accessToken);
                if (user == null)
                {
                    await Clients.Caller.SendAsync("AuthorizationFailed");
                    Context.Abort();
                    return;
                }
                await LoginAsAuthenticated(user);
            }
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            var user = _connections.GetUser(Context.ConnectionId);
            if (user != null)
            {
                var chatRoom = _connections.GetRoom(Context.ConnectionId);
                _connections.Remove(Context.ConnectionId);
                await Clients.Group(chatRoom).SendAsync("UserLeft", user);
            }

            await base.OnDisconnectedAsync(exception);
        }

        public async Task SwitchRoom(string chatRoom)
        {
            var oldRoom = _connections.GetRoom(Context.ConnectionId);
            var user = _connections.GetUser(Context.ConnectionId);

            _connections.Remove(Context.ConnectionId);
            _connections.Add(Context.ConnectionId, chatRoom, user);

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, oldRoom);
            await Groups.AddToGroupAsync(Context.ConnectionId, chatRoom);

            var usersOfRoom = _connections.GetUsersOfRoom(chatRoom);
            await Clients.Group(oldRoom).SendAsync("UserLeft", user);
            await Clients.Group(chatRoom).SendAsync("UserEntered", user);
            await Clients.Caller.SendAsync("StartChat", usersOfRoom, _chatHistory.GetMessages(chatRoom), chatRoom);

            var memberShip = await _settingsRepository.Load(user.BattleTag) ?? new ChatSettings(user.BattleTag);
            memberShip.Update(chatRoom);
            await _settingsRepository.Save(memberShip);
        }

        // used when OAuth is off, invoked from ingame-client
        public async Task LoginAs(string battleTag)
        {
            var userDetails = await _websiteBackendRepository.GetChatDetails(battleTag);
            var chatUser = new ChatUser(battleTag, userDetails?.ClanId, userDetails?.ProfilePicture);
            await LoginAsAuthenticated(chatUser);
        }

        internal async Task LoginAsAuthenticated(ChatUser user)
        {
            var memberShip = await _settingsRepository.Load(user.BattleTag) ?? new ChatSettings(user.BattleTag);

            var ban = await _banRepository.GetBannedPlayer(user.BattleTag);

            var nowDate = DateTime.Now.ToString("yyyy-MM-dd");
            if (ban != null && string.Compare(ban.EndDate, nowDate, StringComparison.Ordinal) > 0)
            {
                await Clients.Caller.SendAsync("PlayerBannedFromChat", ban);
                Context.Abort();
            }
            else
            {
                _connections.Add(Context.ConnectionId, memberShip.DefaultChat, user);
                await Groups.AddToGroupAsync(Context.ConnectionId, memberShip.DefaultChat);
                var usersOfRoom = _connections.GetUsersOfRoom(memberShip.DefaultChat);
                await Clients.Group(memberShip.DefaultChat).SendAsync("UserEntered", user);
                await Clients.Caller.SendAsync("StartChat", usersOfRoom, _chatHistory.GetMessages(memberShip.DefaultChat), memberShip.DefaultChat);
            }
        }
    }
}
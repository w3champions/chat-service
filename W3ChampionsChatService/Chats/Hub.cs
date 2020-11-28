using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using W3ChampionsChatService.Bans;
using W3ChampionsChatService.Settings;

namespace W3ChampionsChatService.Chats
{
    public class ChatHub : Hub
    {
        private readonly ILogger<ChatHub> _logger;
        private readonly ChatAuthenticationService _authenticationService;
        private readonly BanRepository _banRepository;
        private readonly SettingsRepository _settingsRepository;
        private readonly ConnectionMapping _connections;
        private readonly ChatHistory _chatHistory;

        public ChatHub(ChatAuthenticationService authenticationService,
            BanRepository banRepository,
            SettingsRepository settingsRepository,
            ConnectionMapping connections,
            ChatHistory chatHistory,
            ILogger<ChatHub> logger = null)
        {
            _logger = logger ?? new NullLogger<ChatHub>();
            _authenticationService = authenticationService;
            _banRepository = banRepository;
            _settingsRepository = settingsRepository;
            _connections = connections;
            _chatHistory = chatHistory;
        }

        public async Task SendMessage(string chatKey, string battleTag, string message)
        {
            var trimmedMessage = message.Trim();
            var user = await _authenticationService.GetUser(battleTag);
            if (!string.IsNullOrEmpty(trimmedMessage))
            {
                var chatRoom = _connections.GetRoom(Context.ConnectionId);
                var chatMessage = new ChatMessage(user, trimmedMessage);
                _chatHistory.AddMessage(chatRoom, chatMessage);
                await Clients.Group(chatRoom).SendAsync("ReceiveMessage", chatMessage);
            }
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

        public async Task SwitchRoom(string chatKey, string battleTag, string chatRoom)
        {
            var user = await _authenticationService.GetUser(battleTag);

            var oldRoom = _connections.GetRoom(Context.ConnectionId);
            _connections.Remove(Context.ConnectionId);
            _connections.Add(Context.ConnectionId, chatRoom, user);

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, oldRoom);
            await Groups.AddToGroupAsync(Context.ConnectionId, chatRoom);

            var usersOfRoom = _connections.GetUsersOfRoom(chatRoom);
            await Clients.Group(oldRoom).SendAsync("UserLeft", user);
            await Clients.Group(chatRoom).SendAsync("UserEntered", user);
            await Clients.Caller.SendAsync("StartChat", usersOfRoom, _chatHistory.GetMessages(chatRoom), chatRoom);

            var memberShip = await _settingsRepository.Load(battleTag) ?? new ChatSettings(battleTag);
            memberShip.Update(chatRoom);
            await _settingsRepository.Save(memberShip);
        }

        public async Task LoginAs(string chatKey, string battleTag)
        {
            try
            {
                _logger.LogInformation("login started");
                var user = await _authenticationService.GetUser(battleTag);
                _logger.LogInformation("BT" + user.BattleTag);
                _logger.LogInformation("CT" + user.ClanTag);
                var memberShip = await _settingsRepository.Load(battleTag) ?? new ChatSettings(battleTag);
                _logger.LogInformation("MBT" + memberShip.BattleTag);
                _logger.LogInformation("DC" + memberShip.DefaultChat);

                var ban = await _banRepository.Load(battleTag.ToLower());
                _logger.LogInformation("BT" + ban?.BattleTag);


                var nowDate = DateTime.Now.ToString("yyyy-MM-dd");
                if (ban != null && string.Compare(ban.EndDate, nowDate, StringComparison.Ordinal) > 0)
                {
                    await Clients.Caller.SendAsync("PlayerBannedFromChat", ban);
                }
                else
                {
                    _logger.LogInformation("1");
                    _connections.Add(Context.ConnectionId, memberShip.DefaultChat, user);
                    _logger.LogInformation("2");
                    await Groups.AddToGroupAsync(Context.ConnectionId, memberShip.DefaultChat);
                    _logger.LogInformation("3");
                    var usersOfRoom = _connections.GetUsersOfRoom(memberShip.DefaultChat);
                    _logger.LogInformation("4");
                    await Clients.Group(memberShip.DefaultChat).SendAsync("UserEntered", user);
                    _logger.LogInformation("5");
                    await Clients.Caller.SendAsync("StartChat", usersOfRoom, _chatHistory.GetMessages(memberShip.DefaultChat), memberShip.DefaultChat);
                }
            }
            catch (Exception e)
            {

                await Clients.Caller.SendAsync("StartChat", new List<ChatUser>(), new List<ChatMessage>
                {
                    new ChatMessage(new ChatUser("error#123", "clanerror", new ProfilePicture()), $"{e.Message} STACK: {e.StackTrace}")
                }, "error");
            }

        }
    }
}
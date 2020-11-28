using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using W3ChampionsChatService.Bans;
using W3ChampionsChatService.Settings;

namespace W3ChampionsChatService.Chats
{
    public class ChatHub : Hub
    {
        private readonly ChatAuthenticationService _authenticationService;
        private readonly BanRepository _banRepository;
        private readonly SettingsRepository _settingsRepository;
        private readonly ConnectionMapping _connections;
        private readonly ChatHistory _chatHistory;

        public ChatHub(ChatAuthenticationService authenticationService,
            BanRepository banRepository,
            SettingsRepository settingsRepository,
            ConnectionMapping connections,
            ChatHistory chatHistory)
        {
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
            Console.WriteLine("login started");
            var user = await _authenticationService.GetUser(battleTag);
            Console.WriteLine("BT" + user.BattleTag);
            Console.WriteLine("CT" + user.ClanTag);
            var memberShip = await _settingsRepository.Load(battleTag) ?? new ChatSettings(battleTag);
            Console.WriteLine("MBT" + memberShip.BattleTag);
            Console.WriteLine("DC" + memberShip.DefaultChat);

            var ban = await _banRepository.Load(battleTag.ToLower());
            Console.WriteLine("BT" + ban?.BattleTag);


            var nowDate = DateTime.Now.ToString("yyyy-MM-dd");
            if (ban != null && string.Compare(ban.EndDate, nowDate, StringComparison.Ordinal) > 0)
            {
                await Clients.Caller.SendAsync("PlayerBannedFromChat", ban);
            }
            else
            {
                Console.WriteLine("1");
                _connections.Add(Context.ConnectionId, memberShip.DefaultChat, user);
                Console.WriteLine("2");
                await Groups.AddToGroupAsync(Context.ConnectionId, memberShip.DefaultChat);
                Console.WriteLine("3");
                var usersOfRoom = _connections.GetUsersOfRoom(memberShip.DefaultChat);
                Console.WriteLine("4");
                await Clients.Group(memberShip.DefaultChat).SendAsync("UserEntered", user);
                Console.WriteLine("5");
                await Clients.Caller.SendAsync("StartChat", usersOfRoom, _chatHistory.GetMessages(memberShip.DefaultChat), memberShip.DefaultChat);
            }
        }
    }
}
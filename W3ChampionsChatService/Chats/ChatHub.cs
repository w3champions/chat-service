using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Settings;
using Serilog;

[assembly: InternalsVisibleTo("W3ChampionsChatService.Tests")]
namespace W3ChampionsChatService.Chats;

public class ChatHub(
    IChatAuthenticationService authenticationService,
    MuteRepository muteRepository,
    SettingsRepository settingsRepository,
    ConnectionMapping connections,
    ChatHistory chatHistory,
    IHttpContextAccessor contextAccessor) : Hub
{
    private readonly IChatAuthenticationService _authenticationService = authenticationService;
    private readonly MuteRepository _muteRepository = muteRepository;
    private readonly SettingsRepository _settingsRepository = settingsRepository;
    private readonly ConnectionMapping _connections = connections;
    private readonly ChatHistory _chatHistory = chatHistory;
    private readonly IHttpContextAccessor _contextAccessor = contextAccessor;

    public async Task SendMessage(string message)
    {
        var trimmedMessage = message.Trim();
        if (!string.IsNullOrEmpty(trimmedMessage))
        {
            var chatRoom = _connections.GetRoom(Context.ConnectionId);
            var user = _connections.GetUser(Context.ConnectionId);

            // Check if player is on Lounge Mute list. If yes, disconnect. If no, send chat message.
            var mute = await _muteRepository.GetMutedPlayer(user.BattleTag);
            if (mute != null && DateTime.Compare(mute.endDate, DateTime.UtcNow) > 0)
            {
                await Clients.Caller.SendAsync("PlayerBannedFromChat", mute);
                Context.Abort();
            }
            else
            {
                var chatMessage = new ChatMessage(user, trimmedMessage);
                if (!await ProcessChatCommand(chatMessage))
                {
                    _chatHistory.AddMessage(chatRoom, chatMessage);
                    await Clients.Group(chatRoom).SendAsync("ReceiveMessage", chatMessage);
                }
            }
        }
    }

    /// <summary>
    /// Processes chat commands and returns a boolean indicating whether a command was processed 
    /// or the message should be sent as a normal message.
    /// </summary>
    /// <param name="message">The chat message to process.</param>
    /// <returns>True if a command was processed, false otherwise.</returns>
    private async Task<bool> ProcessChatCommand(ChatMessage message)
    {
        if (!message.Message.StartsWith("/"))
        {
            return false;
        }

        var fakeSystemUser = message.User.GenerateFakeSystemUser();
        string messageToSend;

        if (message.Message.StartsWith("/w ") || message.Message.StartsWith("/whisper ") || message.Message.StartsWith("/r ") || message.Message.StartsWith("/reply "))
        {
            messageToSend = "Private messages to other players are currently not supported!";
        }
        else
        {
            messageToSend = "Chat commands are currently not supported!";
        }

        await Clients.Caller.SendAsync("ReceiveMessage", new ChatMessage(fakeSystemUser, messageToSend));
        return true;
    }

    public override async Task OnConnectedAsync()
    {
        var accessToken = _contextAccessor?.HttpContext?.Request.Query["access_token"];
        var user = await _authenticationService.GetUser(accessToken);
        if (user == null)
        {
            Log.Warning("Receiver {ConnectionId} failed to authenticate", Context.ConnectionId);
            await Clients.Caller.SendAsync("AuthorizationFailed");
            Context.Abort();
            return;
        }
        await LoginAsAuthenticated(user);
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

    internal async Task LoginAsAuthenticated(ChatUser user)
    {
        var memberShip = await _settingsRepository.Load(user.BattleTag) ?? new ChatSettings(user.BattleTag);

        var mute = await _muteRepository.GetMutedPlayer(user.BattleTag);

        if (mute != null && DateTime.Compare(mute.endDate, DateTime.UtcNow) > 0)
        {
            Log.Information("Declining connection for {BattleTag} because they are banned until {EndDate}", user.BattleTag, mute.endDate);
            await Clients.Caller.SendAsync("PlayerBannedFromChat", mute);
            Context.Abort();
        }
        else
        {
            Log.Information("Accepting connection for {BattleTag} and adding to room {Room}", user.BattleTag, memberShip.DefaultChat);
            _connections.Add(Context.ConnectionId, memberShip.DefaultChat, user);
            await Groups.AddToGroupAsync(Context.ConnectionId, memberShip.DefaultChat);
            var usersOfRoom = _connections.GetUsersOfRoom(memberShip.DefaultChat);
            await Clients.Group(memberShip.DefaultChat).SendAsync("UserEntered", user);
            await Clients.Caller.SendAsync("StartChat", usersOfRoom, _chatHistory.GetMessages(memberShip.DefaultChat), memberShip.DefaultChat);
        }
    }

    public async Task UpdateUserProfilePicture(string chatRoom, ProfilePicture profilePicture)
    {
        var user = _connections.GetUser(Context.ConnectionId);
        user.ProfilePicture = profilePicture;
        await Clients.Group(chatRoom).SendAsync("UserUpdated", user);
    }
}

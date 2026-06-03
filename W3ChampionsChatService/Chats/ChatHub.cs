using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Settings;
using Serilog;
using W3ChampionsChatService.Authentication;

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

            // R6 (membership-before-send, start): a connected user may be seated in NO room
            // (e.g. full-banned with no clan). Without a room/user there is nothing to send.
            // Guard before dereferencing user/chatRoom to avoid an NRE. Task 5 completes room-scoping.
            if (user == null || chatRoom == null)
            {
                return;
            }

            // Check if player is on Lounge Mute list. If yes, handle accordingly.
            var mute = await _muteRepository.GetMutedPlayer(user.BattleTag);
            if (mute != null && !mute.IsActive(DateTime.UtcNow))
            {
                mute = null;
            }

            // TODO (Task 5): room-scope via DefaultChatRooms.IsBannedRoom + cached GetMuteStatus; currently room-blind.
            if (mute != null && !mute.isShadowBan)
            {
                // G1: Full ban — silently drop the message (no Context.Abort(), no teardown).
                // The PlayerBannedFromChat notice was already sent at connect time (LoginAsAuthenticated).
                Log.Information("Full-banned user {BattleTag} attempted to send message in room {Room} — dropped silently",
                    user.BattleTag, chatRoom);
                return;
            }
            else
            {
                var chatMessage = new ChatMessage(user, trimmedMessage);
                if (!await ProcessChatCommand(chatMessage))
                {
                    if (mute != null && mute.isShadowBan)
                    {
                        // Only send to caller to make them think it was sent
                        Log.Information("Shadow banned user {BattleTag} sent message {Message}", user.BattleTag, trimmedMessage);
                        await Clients.Caller.SendAsync("ReceiveMessage", chatMessage);
                    }
                    else
                    {
                        _chatHistory.AddMessage(chatRoom, chatMessage);
                        await Clients.Group(chatRoom).SendAsync("ReceiveMessage", chatMessage);
                    }
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

        // Resolve the cached mute status for this connection.
        var muteStatus = _connections.GetMuteStatus(Context.ConnectionId);

        // Lazy re-resolve: if cache shows None but target is a banned room, check DB in case
        // this connection was never cached (e.g. full-banned user with no clan at login).
        if (muteStatus == MuteStatus.None && user != null && DefaultChatRooms.IsBannedRoom(chatRoom))
        {
            var mute = await _muteRepository.GetMutedPlayer(user.BattleTag);
            if (mute != null && mute.IsActive(DateTime.UtcNow))
            {
                muteStatus = mute.isShadowBan ? MuteStatus.Shadow : MuteStatus.Full;
                _connections.SetMuteStatus(Context.ConnectionId, muteStatus);
            }
        }

        // G1/G2: Full-ban → graceful reject BEFORE any Remove/Add. User stays in their current room.
        // No Context.Abort(), no throw, no connection teardown.
        if (DefaultChatRooms.IsBannedRoom(chatRoom) && muteStatus == MuteStatus.Full)
        {
            Log.Information("Full-banned user {BattleTag} rejected from joining banned room {Room} — staying in {OldRoom}",
                user?.BattleTag, chatRoom, oldRoom);
            return;
        }

        // Ghost-join flag: shadow-banned user entering a banned room receives messages
        // but their presence is hidden from others (no UserEntered / UserLeft broadcasts).
        var ghostJoin = DefaultChatRooms.IsBannedRoom(chatRoom) && muteStatus == MuteStatus.Shadow;

        _connections.Remove(Context.ConnectionId);
        _connections.Add(Context.ConnectionId, chatRoom, user);
        // Re-cache the mute status after Remove (which clears it) + Add.
        _connections.SetMuteStatus(Context.ConnectionId, muteStatus);

        if (oldRoom != null)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, oldRoom);
            if (!ghostJoin)
            {
                await Clients.Group(oldRoom).SendAsync("UserLeft", user);
            }
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, chatRoom);

        var usersOfRoom = _connections.GetUsersOfRoom(chatRoom);
        if (!ghostJoin)
        {
            await Clients.Group(chatRoom).SendAsync("UserEntered", user);
        }
        // Caller always receives StartChat (their own view — unfiltered for now; Task 6 adds viewer-aware filtering).
        await Clients.Caller.SendAsync("StartChat", usersOfRoom, _chatHistory.GetMessages(chatRoom), chatRoom);

        var memberShip = await _settingsRepository.Load(user.BattleTag) ?? new ChatSettings(user.BattleTag);
        memberShip.Update(chatRoom);
        await _settingsRepository.Save(memberShip);
    }

    [UserHasPermission(EPermission.Moderation)]
    public async Task DeleteMessage(string messageId)
    {
        var deletedMessage = _chatHistory.DeleteMessage(messageId);
        if (deletedMessage != null)
        {
            var adminUser = _connections.GetUser(Context.ConnectionId);
            Log.Information("Deleted message '{MessageContent}' from {MessageSender} by request of {AdminUserName}", deletedMessage.Message, deletedMessage.User.BattleTag, adminUser.BattleTag);

            var authorConnectionIds = _connections.GetConnectionIdsForUser(deletedMessage.User.BattleTag);
            await Clients.AllExcept(authorConnectionIds).SendAsync("MessageDeleted", deletedMessage.Id);
        }
    }

    [UserHasPermission(EPermission.Moderation)]
    public async Task PurgeMessagesFromUser(string battleTag)
    {
        var deletedMessages = _chatHistory.DeleteMessagesFromUser(battleTag);
        if (deletedMessages.Count > 0)
        {
            var adminUser = _connections.GetUser(Context.ConnectionId);
            Log.Information("Purging {Count} messages from user {BattleTag} on request of {AdminUserName}", deletedMessages.Count, battleTag, adminUser.BattleTag);

            var authorConnectionIds = _connections.GetConnectionIdsForUser(battleTag);
            await Clients.AllExcept(authorConnectionIds).SendAsync("BulkMessageDeleted", deletedMessages.Select(m => m.Id).ToList());
        }
        else
        {
            var adminUser = _connections.GetUser(Context.ConnectionId);
            Log.Information("Purging messages from user {BattleTag} by request of {AdminUserName} failed: No messages found", battleTag, adminUser.BattleTag);
        }
    }

    [UserHasPermission(EPermission.Moderation)]
    public async Task BanUser(string battleTag, string reason, bool isShadowBan, string endDate)
    {
        var adminUser = _connections.GetUser(Context.ConnectionId);
        Log.Information("Banning user {BattleTag} until {EndDate} by {AdminUser}. Reason: {Reason}, ShadowBan: {IsShadowBan}",
            battleTag, endDate, adminUser.BattleTag, reason, isShadowBan);

        var loungeMuteRequest = new LoungeMuteRequest
        {
            battleTag = battleTag,
            endDate = endDate,
            isShadowBan = isShadowBan,
            author = adminUser.BattleTag,
            reason = reason
        };

        await _muteRepository.AddLoungeMute(loungeMuteRequest);
    }

    internal async Task LoginAsAuthenticated(ChatUser user)
    {
        var memberShip = await _settingsRepository.Load(user.BattleTag) ?? new ChatSettings(user.BattleTag);
        var mute = await _muteRepository.GetMutedPlayer(user.BattleTag);

        // Treat expired mutes as no mute
        if (mute != null && !mute.IsActive(DateTime.UtcNow))
        {
            mute = null;
        }

        var isFullBan = mute != null && !mute.isShadowBan;
        var isShadowBan = mute != null && mute.isShadowBan;

        if (isFullBan)
        {
            // G1: do NOT abort — connect the user. G5: still send legacy PlayerBannedFromChat.
            Log.Information("Full-banned user {BattleTag} connecting — sending ban notice, filtering rooms", user.BattleTag);

            // G5: legacy ban notice so old clients render their notice.
            await Clients.Caller.SendAsync("PlayerBannedFromChat", mute);

            // Filtered channel list excludes all lounge/ladder banned rooms (spec §5.1).
            var availableRooms = new List<string>();

            // Seat in clan room if available, else no room (spec D2 — empty state).
            var safeRoom = string.IsNullOrWhiteSpace(user.ClanTag) ? null : $"clan {user.ClanTag}";

            if (safeRoom != null)
            {
                _connections.Add(Context.ConnectionId, safeRoom, user);
                _connections.SetMuteStatus(Context.ConnectionId, MuteStatus.Full);
                await Groups.AddToGroupAsync(Context.ConnectionId, safeRoom);
                var usersOfRoom = _connections.GetUsersOfRoom(safeRoom);
                await Clients.Group(safeRoom).SendAsync("UserEntered", user);
                // G3: StartChat with the seated room's payload.
                await Clients.Caller.SendAsync("StartChat", usersOfRoom, _chatHistory.GetMessages(safeRoom), safeRoom, availableRooms);
            }
            else
            {
                // No clan: not seated in any room (spec D2).
                // Note: SetMuteStatus is not called here because the connection is not in the mapping yet.
                // SwitchRoom enforcement (Task 4) lazily re-resolves the mute from MongoDB for this edge case.
                // G3: STILL emit a StartChat — an empty-room payload — so legacy clients can initialize.
                await Clients.Caller.SendAsync("StartChat", new List<ChatUser>(), new List<ChatMessage>(), (string)null, availableRooms);
            }
        }
        else
        {
            // Shadow ban or no ban: connect as normal (G1 — never abort).
            var muteStatus = isShadowBan ? MuteStatus.Shadow : MuteStatus.None;
            Log.Information("Accepting connection for {BattleTag}, mute={MuteStatus}, room={Room}",
                user.BattleTag, muteStatus, memberShip.DefaultChat);

            _connections.Add(Context.ConnectionId, memberShip.DefaultChat, user);
            _connections.SetMuteStatus(Context.ConnectionId, muteStatus);
            await Groups.AddToGroupAsync(Context.ConnectionId, memberShip.DefaultChat);
            var usersOfRoom = _connections.GetUsersOfRoom(memberShip.DefaultChat);
            await Clients.Group(memberShip.DefaultChat).SendAsync("UserEntered", user);
            await Clients.Caller.SendAsync("StartChat", usersOfRoom, _chatHistory.GetMessages(memberShip.DefaultChat), memberShip.DefaultChat, DefaultChatRooms.Rooms);
        }
    }

    public async Task UpdateUserProfilePicture(string chatRoom, ProfilePicture profilePicture)
    {
        var user = _connections.GetUser(Context.ConnectionId);
        user.ProfilePicture = profilePicture;
        await Clients.Group(chatRoom).SendAsync("UserUpdated", user);
    }
}

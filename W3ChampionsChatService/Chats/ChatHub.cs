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
        if (string.IsNullOrEmpty(trimmedMessage))
        {
            return;
        }

        var chatRoom = _connections.GetRoom(Context.ConnectionId);
        var user = _connections.GetUser(Context.ConnectionId);

        // R6: Membership prerequisite — user must be a member of a valid room.
        // G1/G2: graceful reject — return, never Context.Abort(), never throw.
        if (chatRoom == null || user == null)
        {
            Log.Warning("SendMessage rejected: connection {ConnectionId} has no room membership", Context.ConnectionId);
            return;
        }

        var inBannedRoom = DefaultChatRooms.IsBannedRoom(chatRoom);
        var muteStatus = MuteStatus.None;

        if (inBannedRoom)
        {
            var hasCached = _connections.TryGetMute(Context.ConnectionId, out var cached);

            if (hasCached && cached.Status != MuteStatus.None)
            {
                // Cache HIT with an active ban — honor cached status + expiry. ZERO DB reads.
                // This is the primary §7 win: a cached-banned user does NOT hit the DB on every send.
                muteStatus = cached.EndDate > DateTime.UtcNow ? cached.Status : MuteStatus.None;

                if (muteStatus == MuteStatus.None)
                {
                    // Ban expired — update the cache so future sends skip the DB too.
                    _connections.SetMute(Context.ConnectionId, MuteStatus.None, DateTime.MinValue);
                }
            }
            else
            {
                // Cache MISS or cache HIT with None:
                //   - MISS: no-clan full-ban login edge, or any first-send path.
                //   - HIT-None: also re-resolve as a pre-Task 7 live-ban safety net
                //     (a ban applied mid-session won't be reflected in the cached None until
                //     Task 7 adds live cache-invalidation via BanUser; this keeps the net tight).
                var mute = await _muteRepository.GetMutedPlayer(user.BattleTag);
                if (mute != null && mute.IsActive(DateTime.UtcNow))
                {
                    muteStatus = mute.isShadowBan ? MuteStatus.Shadow : MuteStatus.Full;
                    _connections.SetMute(Context.ConnectionId, muteStatus, mute.endDate);
                }
                else if (!hasCached)
                {
                    // MISS path: cache the resolved None so future sends know the entry exists.
                    _connections.SetMute(Context.ConnectionId, MuteStatus.None, DateTime.MinValue);
                }
                // HIT-None path: leave cache as-is (ban is truly not there).
            }
        }

        if (inBannedRoom && muteStatus == MuteStatus.Full)
        {
            // Defense-in-depth: full-banned user should never be in a banned room,
            // but reject silently if they somehow are.
            // G1: the LEGACY SendMessage called Context.Abort() here for full bans — REMOVED.
            // G2: reject without abort, without throwing; connection + membership stay valid.
            Log.Warning("Full-banned user {BattleTag} attempted to send in banned room {Room} — rejected",
                user.BattleTag, chatRoom);
            return;
        }

        var chatMessage = new ChatMessage(user, trimmedMessage);

        if (!await ProcessChatCommand(chatMessage))
        {
            if (inBannedRoom && muteStatus == MuteStatus.Shadow)
            {
                // Drop: echo only to caller (illusion of sending).
                // Boyscout: do NOT log the raw message body — arbitrary user input should not appear in logs.
                Log.Information("Shadow banned user {BattleTag} attempted to send in banned room {Room} — dropped",
                    user.BattleTag, chatRoom);
                await Clients.Caller.SendAsync("ReceiveMessage", chatMessage);
            }
            else
            {
                // Broadcast to room: covers no-ban in any room AND banned users in exempt rooms.
                // Bans only restrict sending in the lounge/ladder banned rooms; clan/lobby rooms
                // are fully exempt — even full- and shadow-banned users can post freely there.
                _chatHistory.AddMessage(chatRoom, chatMessage);
                await Clients.Group(chatRoom).SendAsync("ReceiveMessage", chatMessage);
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
            // Capture room and mute status BEFORE Remove (which clears the cache entry).
            var chatRoom = _connections.GetRoom(Context.ConnectionId);
            var muteStatus = _connections.GetEffectiveMuteStatus(Context.ConnectionId, DateTime.UtcNow);
            _connections.Remove(Context.ConnectionId);

            // Guard against null room (full-ban no-clan user has no room — G3/G2: no crash).
            // Suppress UserLeft for shadow-banned users in banned rooms: they were never announced
            // as present (ghost-join), so broadcasting UserLeft would break the illusion (spec §6).
            // Shadow users in exempt rooms (clan/lobby) were announced normally — broadcast as usual.
            var suppressUserLeft = muteStatus == MuteStatus.Shadow
                && chatRoom != null
                && DefaultChatRooms.IsBannedRoom(chatRoom);

            if (!suppressUserLeft && chatRoom != null)
            {
                await Clients.Group(chatRoom).SendAsync("UserLeft", user);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task SwitchRoom(string chatRoom)
    {
        var oldRoom = _connections.GetRoom(Context.ConnectionId);
        var user = _connections.GetUser(Context.ConnectionId);

        var muteStatus = MuteStatus.None;
        // Capture the full cached entry BEFORE Remove so we can re-populate it afterwards.
        var hasCachedEntry = _connections.TryGetMute(Context.ConnectionId, out var preSwitchCached);
        var cachedEndDate = hasCachedEntry ? preSwitchCached.EndDate : DateTime.MinValue;
        var targetIsBanned = DefaultChatRooms.IsBannedRoom(chatRoom);

        if (user != null)
        {
            if (targetIsBanned)
            {
                // SECURITY: for a BANNED target, only trust the cache on a HIT with a real ban
                // (Status != None). A cache MISS *or* a HIT-None must lazy-resolve from the DB —
                // otherwise a no-clan full-banned user who first switched into an exempt room
                // (which writes a cache-None) would bypass the ban on a later switch into a
                // banned room. This mirrors SendMessage's HIT-None re-resolve path.
                if (hasCachedEntry && preSwitchCached.Status != MuteStatus.None)
                {
                    // Fast path: cached active ban — honor cached status + expiry, no DB read.
                    muteStatus = preSwitchCached.EndDate > DateTime.UtcNow
                        ? preSwitchCached.Status
                        : MuteStatus.None;
                    cachedEndDate = preSwitchCached.EndDate;
                }
                else
                {
                    // Cache MISS or HIT-None — lazy-resolve the mute for the banned target.
                    var mute = await _muteRepository.GetMutedPlayer(user.BattleTag);
                    if (mute != null && mute.IsActive(DateTime.UtcNow))
                    {
                        muteStatus = mute.isShadowBan ? MuteStatus.Shadow : MuteStatus.Full;
                        cachedEndDate = mute.endDate;
                    }
                    else
                    {
                        muteStatus = MuteStatus.None;
                        cachedEndDate = DateTime.MinValue;
                    }
                }
            }
            else if (hasCachedEntry)
            {
                // Exempt target — cache HIT drives the shadow presence gates below; no DB read.
                muteStatus = (preSwitchCached.Status == MuteStatus.None || preSwitchCached.EndDate > DateTime.UtcNow)
                    ? preSwitchCached.Status
                    : MuteStatus.None;
            }
            // Exempt target + cache MISS: muteStatus stays None; no DB read needed.
        }

        // G1/G2: Full-ban → graceful reject BEFORE any Remove/Add. User stays in their current room.
        // No Context.Abort(), no throw, no connection teardown.
        if (targetIsBanned && muteStatus == MuteStatus.Full)
        {
            // S3: cache the resolved full ban before returning so subsequent SwitchRoom/SendMessage
            // enforce from the cache instead of re-reading the DB.
            _connections.SetMute(Context.ConnectionId, MuteStatus.Full, cachedEndDate);
            Log.Information("Full-banned user {BattleTag} rejected from joining banned room {Room} — staying in {OldRoom}",
                user.BattleTag, chatRoom, oldRoom);
            return;
        }

        // Two independent presence gates, each derived from the relevant room:
        // - suppressLeave: the user was a GHOST in the OLD room (shadow + old room is banned),
        //   so no one ever saw them there — suppress the UserLeft on the room they're leaving.
        // - suppressEnter: the user ghost-joins the TARGET room (shadow + target is banned),
        //   so suppress the UserEntered on the room they're entering.
        // Using a single target-based flag for both would leak/strand presence on cross-category
        // shadow transitions (exempt→banned must still broadcast UserLeft; banned→exempt must not).
        var suppressLeave = oldRoom != null && DefaultChatRooms.IsBannedRoom(oldRoom) && muteStatus == MuteStatus.Shadow;
        var suppressEnter = targetIsBanned && muteStatus == MuteStatus.Shadow;

        _connections.Remove(Context.ConnectionId);
        _connections.Add(Context.ConnectionId, chatRoom, user);
        // Re-cache the mute status + endDate after Remove (which clears it) + Add.
        // S3: this also caches a freshly lazy-resolved shadow ban on the ghost-join path.
        _connections.SetMute(Context.ConnectionId, muteStatus, cachedEndDate);

        if (oldRoom != null)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, oldRoom);
            if (!suppressLeave)
            {
                await Clients.Group(oldRoom).SendAsync("UserLeft", user);
            }
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, chatRoom);

        var usersOfRoom = _connections.GetUsersOfRoomForViewer(chatRoom, Context.ConnectionId);
        if (!suppressEnter)
        {
            await Clients.Group(chatRoom).SendAsync("UserEntered", user);
        }
        // Caller receives their viewer-filtered user list: they always see themselves;
        // other shadow-banned users are hidden from them in banned rooms (spec §6).
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

        // Spec §12: Reconcile any live connections for this user so enforcement is instant.
        // Parse the endDate once — used for both the cache and the PlayerBannedFromChat payload.
        var parsedEndDate = DateTime.Parse(endDate, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        var newStatus = isShadowBan ? MuteStatus.Shadow : MuteStatus.Full;

        var liveConnectionIds = _connections.GetConnectionIdsForUser(battleTag);
        foreach (var connId in liveConnectionIds)
        {
            // Update the cache so the next SendMessage/SwitchRoom enforces from the cache (no DB read).
            _connections.SetMute(connId, newStatus, parsedEndDate);

            if (!isShadowBan)
            {
                // Full ban — R7/G5: notify the target REGARDLESS of their current room so they
                // clearly and persistently know they're banned, independent of channel. A user
                // full-banned while sitting in a clan/lobby room must still receive the notice
                // (not just users in a lounge/ladder room).
                // G1: SendAsync only — do NOT call Context.Abort(); the connection must stay alive.
                // §12: no forced eviction — the user keeps their current room membership.
                var mute = new LoungeMute
                {
                    battleTag = battleTag,
                    endDate = parsedEndDate,
                    insertDate = DateTime.UtcNow,
                    author = adminUser.BattleTag,
                    reason = reason,
                    isShadowBan = false
                };
                await Clients.Client(connId).SendAsync("PlayerBannedFromChat", mute);
            }
            // Shadow ban: no signal to the target whatsoever — preserve the illusion (spec §12).
        }
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
                // Cache full-ban status + endDate so SwitchRoom/SendMessage never need a DB read.
                _connections.SetMute(Context.ConnectionId, MuteStatus.Full, mute.endDate);
                await Groups.AddToGroupAsync(Context.ConnectionId, safeRoom);
                var usersOfRoom = _connections.GetUsersOfRoomForViewer(safeRoom, Context.ConnectionId);
                await Clients.Group(safeRoom).SendAsync("UserEntered", user);
                // G3: StartChat with the seated room's payload.
                await Clients.Caller.SendAsync("StartChat", usersOfRoom, _chatHistory.GetMessages(safeRoom), safeRoom, availableRooms);
            }
            else
            {
                // No clan: not seated in any room (spec D2).
                // SetMute is NOT called here — the connection is not in the mapping yet.
                // SwitchRoom/SendMessage enforcement lazily re-resolves from MongoDB for this edge case
                // (cache MISS path). This is acceptable: full-banned users with no clan have no room,
                // so any SwitchRoom into a banned room still triggers the lazy resolve and is rejected.
                // G3: STILL emit a StartChat — an empty-room payload — so legacy clients can initialize.
                await Clients.Caller.SendAsync("StartChat", new List<ChatUser>(), new List<ChatMessage>(), (string)null, availableRooms);
            }
        }
        else
        {
            // Shadow ban or no ban: connect as normal (G1 — never abort).
            var muteStatus = isShadowBan ? MuteStatus.Shadow : MuteStatus.None;
            var muteEndDate = mute?.endDate ?? DateTime.MinValue;
            Log.Information("Accepting connection for {BattleTag}, mute={MuteStatus}, room={Room}",
                user.BattleTag, muteStatus, memberShip.DefaultChat);

            _connections.Add(Context.ConnectionId, memberShip.DefaultChat, user);
            // Cache status + endDate at login so every subsequent send/join works from the cache.
            _connections.SetMute(Context.ConnectionId, muteStatus, muteEndDate);
            await Groups.AddToGroupAsync(Context.ConnectionId, memberShip.DefaultChat);
            var usersOfRoom = _connections.GetUsersOfRoomForViewer(memberShip.DefaultChat, Context.ConnectionId);
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

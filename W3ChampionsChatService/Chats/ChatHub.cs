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
    IMuteRepository muteRepository,
    SettingsRepository settingsRepository,
    ConnectionMapping connections,
    ChatHistory chatHistory,
    MuteReconciliationService muteReconciliation,
    IHttpContextAccessor contextAccessor) : Hub
{
    private readonly IChatAuthenticationService _authenticationService = authenticationService;
    private readonly IMuteRepository _muteRepository = muteRepository;
    private readonly SettingsRepository _settingsRepository = settingsRepository;
    private readonly ConnectionMapping _connections = connections;
    private readonly ChatHistory _chatHistory = chatHistory;
    private readonly MuteReconciliationService _muteReconciliation = muteReconciliation;
    private readonly IHttpContextAccessor _contextAccessor = contextAccessor;

    // Cap how much of an arbitrary user message is written to logs (the shadow-drop audit line) so a
    // single huge message can't bloat the logs.
    private const int MaxLoggedMessageLength = 500;

    private static string TruncateForLog(string message) =>
        message.Length <= MaxLoggedMessageLength
            ? message
            : message[..MaxLoggedMessageLength] + "…[truncated]";

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

        // Mutes only apply in public lounge/ladder rooms; clan/lobby rooms are fully exempt.
        // Read the per-user cached status once + classify the room — ZERO DB reads on the hot path.
        // The cache is seeded authoritatively at login (every path) and reconciled live by every ban
        // WRITE path (the hub's BanUser AND the REST MuteController, via MuteReconciliationService),
        // so consulting GetEffectiveMuteStatus alone is sufficient and expiry-aware. RESIDUAL TRADE-OFF:
        // a ban written DIRECTLY to the Mongo collection — bypassing BOTH the hub and the REST controller
        // (e.g. a manual DB edit or migration) — only takes effect on the user's next (re)connect; we
        // deliberately do NOT re-query the DB per send.
        var inPublicRoom = DefaultChatRooms.IsPublicRoom(chatRoom);
        var muteStatus = inPublicRoom
            ? _connections.GetEffectiveMuteStatus(Context.ConnectionId, DateTime.UtcNow)
            : MuteStatus.None;

        if (inPublicRoom && muteStatus == MuteStatus.Full)
        {
            // Defense-in-depth: full-banned user should never be in a public room,
            // but reject silently if they somehow are.
            // G1: the LEGACY SendMessage called Context.Abort() here for full bans — REMOVED.
            // G2: reject without abort, without throwing; connection + membership stay valid.
            Log.Warning("Full-banned user {BattleTag} attempted to send in public room {Room} — rejected",
                user.BattleTag, chatRoom);
            return;
        }

        var chatMessage = new ChatMessage(user, trimmedMessage);

        if (!await ProcessChatCommand(chatMessage))
        {
            if (inPublicRoom && muteStatus == MuteStatus.Shadow)
            {
                // Drop: echo only to caller (illusion of sending). The message reaches no one else.
                // Log the dropped attempt (incl. content, bounded) so moderators can audit shadow activity.
                Log.Information("Shadow banned user {BattleTag} attempted to send in public room {Room} — dropped: {Message}",
                    user.BattleTag, chatRoom, TruncateForLog(trimmedMessage));
                await Clients.Caller.SendAsync("ReceiveMessage", chatMessage);
            }
            else
            {
                // Broadcast to room: covers no-ban in any room AND banned users in exempt rooms.
                // Bans only restrict sending in the public lounge/ladder rooms; clan/lobby rooms
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
            // Suppress UserLeft for shadow-banned users in public rooms: they were never announced
            // as present (ghost-join), so broadcasting UserLeft would break the illusion (spec §6).
            // Shadow users in exempt rooms (clan/lobby) were announced normally — broadcast as usual.
            var suppressUserLeft = muteStatus == MuteStatus.Shadow
                && chatRoom != null
                && DefaultChatRooms.IsPublicRoom(chatRoom);

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

        // R6/G1/G2: a connection with no user (not logged in, or a no-clan full-ban with no seat)
        // cannot switch rooms. Reject gracefully — return, never Context.Abort(), never throw.
        if (user == null)
        {
            Log.Warning("SwitchRoom rejected: connection {ConnectionId} has no user", Context.ConnectionId);
            return;
        }

        // Resolve the per-user cached status once. The cache is seeded at login (every path) and
        // reconciled live by every ban write path (hub + REST controller, via MuteReconciliationService),
        // so it is authoritative — consult it ONLY, never the DB. EffectiveStatus is the single expiry
        // rule (expired ban → None). Capture the cached endDate so we can re-populate the cache after
        // Remove (which clears it) + Add.
        var hasCachedEntry = _connections.TryGetMute(Context.ConnectionId, out var preSwitchCached);
        var cachedEndDate = hasCachedEntry ? preSwitchCached.EndDate : DateTime.MinValue;
        var targetIsPublic = DefaultChatRooms.IsPublicRoom(chatRoom);
        var muteStatus = hasCachedEntry ? preSwitchCached.EffectiveStatus(DateTime.UtcNow) : MuteStatus.None;

        // G1/G2: Full-ban → graceful reject BEFORE any Remove/Add. User stays in their current room.
        // No Context.Abort(), no throw, no connection teardown.
        if (targetIsPublic && muteStatus == MuteStatus.Full)
        {
            Log.Information("Full-banned user {BattleTag} rejected from joining public room {Room} — staying in {OldRoom}",
                user.BattleTag, chatRoom, oldRoom);
            return;
        }

        // Two independent presence gates, each derived from the relevant room:
        // - suppressLeave: the user was a GHOST in the OLD room (shadow + old room is public),
        //   so no one ever saw them there — suppress the UserLeft on the room they're leaving.
        // - suppressEnter: the user ghost-joins the TARGET room (shadow + target is public),
        //   so suppress the UserEntered on the room they're entering.
        // Using a single target-based flag for both would leak/strand presence on cross-category
        // shadow transitions (exempt→public must still broadcast UserLeft; public→exempt must not).
        var suppressLeave = oldRoom != null && DefaultChatRooms.IsPublicRoom(oldRoom) && muteStatus == MuteStatus.Shadow;
        var suppressEnter = targetIsPublic && muteStatus == MuteStatus.Shadow;

        _connections.Remove(Context.ConnectionId);
        _connections.Add(Context.ConnectionId, chatRoom, user);
        // Re-cache the mute status + endDate after Remove (which clears it) + Add — the cache survives
        // the switch with the same authoritative status it had before.
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
        // other shadow-banned users are hidden from them in public rooms (spec §6).
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

        // Spec §12: BanUser is one of the two canonical IN-BAND ban paths (the other is the REST
        // MuteController). Both persist the ban AND reconcile every live connection's mute cache via
        // MuteReconciliationService, so enforcement is instant without a per-send DB read. Only a ban
        // written DIRECTLY to the Mongo collection (bypassing both the hub and the REST controller —
        // e.g. a manual DB edit) takes effect on the target's next reconnect.
        // Parse the endDate once. Use the SAME DateTimeStyles the repository uses (AdjustToUniversal)
        // so the CACHED expiry can never disagree with the PERSISTED expiry for an offset-less endDate.
        // Guard a malformed/empty endDate: the ban is already persisted, so on a parse failure skip the
        // live reconcile gracefully (next reconnect re-seeds the cache) rather than throwing after the write.
        if (!DateTime.TryParse(endDate, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsedEndDate))
        {
            Log.Warning("BanUser: could not parse endDate '{EndDate}' for {BattleTag} — ban persisted, skipping live cache reconcile (next reconnect will re-seed the cache)",
                endDate, battleTag);
            return;
        }

        var newStatus = isShadowBan ? MuteStatus.Shadow : MuteStatus.Full;
        await _muteReconciliation.ApplyMuteToLiveConnections(battleTag, newStatus, parsedEndDate);
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
            // SECURITY: send only the expiry — never leak the moderation reason or the shadow flag.
            // The event name + camelCase `endDate` field stay unchanged for backward-compat.
            await Clients.Caller.SendAsync("PlayerBannedFromChat", new { endDate = mute.endDate });

            // Filtered channel list excludes all public lounge/ladder rooms (spec §5.1).
            var availableRooms = new List<string>();

            // Seed the per-connection mute cache authoritatively for BOTH branches so the send/switch
            // hot paths enforce from the cache alone (zero DB reads), even on the no-clan/no-room edge.
            _connections.SetMute(Context.ConnectionId, MuteStatus.Full, mute.endDate);

            // Seat in clan room if available, else no room (spec D2 — empty state).
            var safeRoom = string.IsNullOrWhiteSpace(user.ClanTag) ? null : $"clan {user.ClanTag}";

            if (safeRoom != null)
            {
                _connections.Add(Context.ConnectionId, safeRoom, user);
                await Groups.AddToGroupAsync(Context.ConnectionId, safeRoom);
                var usersOfRoom = _connections.GetUsersOfRoomForViewer(safeRoom, Context.ConnectionId);
                await Clients.Group(safeRoom).SendAsync("UserEntered", user);
                // G3: StartChat with the seated room's payload.
                await Clients.Caller.SendAsync("StartChat", usersOfRoom, _chatHistory.GetMessages(safeRoom), safeRoom, availableRooms);
            }
            else
            {
                // No clan: not seated in any room (spec D2). A SwitchRoom attempt by this connection
                // is rejected by SwitchRoom's `user == null` early-return (GetUser finds no mapping
                // entry), NOT by the cache gate. The cache is STILL seeded above as defense-in-depth:
                // if this connection ever became seated in a public room, SendMessage would enforce
                // the full ban from the cache (zero DB read). SetMute keys by connectionId only, so
                // seeding works even with no room membership.
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

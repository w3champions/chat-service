using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;
using Serilog;

[assembly: InternalsVisibleTo("W3ChampionsChatService.Tests")]
namespace W3ChampionsChatService.Chats;

public partial class ChatHub(
    ConnectionMapping connections,
    ChatHistory chatHistory,
    MuteReconciliationService muteReconciliation,
    ITicketStore ticketStore,
    ISessionRegistry sessionRegistry,
    UserDirectoryRepository userDirectory,
    SessionStateAssembler assembler,
    FocusRegistry focusRegistry,
    OnlineMemberRegistry onlineMemberRegistry,
    MessageRateLimiter messageRateLimiter,
    TimeProvider timeProvider,
    // C3 (Task 9): resolves a channel for the FocusChannel NotFound-vs-NotMember split (cold path,
    // only reached when the caller is NOT already a member per OnlineMemberRegistry).
    ChannelRepository channelRepository,
    // C3 (Task 10): JoinChannel/LeaveChannel/SetNotificationLevel — membership self-service reads and
    // writes ChannelMembership rows directly (Load/Insert/Delete/SetNotificationLevel/
    // CountNameJoinableMembershipsForUser), and the creation throttle gates implicit semiPublic
    // creation (JoinChannel resolution step 2, "not found" branch) — a SEPARATE singleton from
    // MintRateLimiter (see FanOut/ChannelCreationRateLimiter.cs doc comment for why).
    MembershipRepository membershipRepository,
    ChannelCreationRateLimiter channelCreationRateLimiter,
    // C3 (Task 11): the durable send pipeline — SendMessage(channelId, content) in
    // ChatHub.Messaging.cs. MessageRepository inserts the ChannelMessage; FanOutEngine is the
    // post-persist fan-out seam — focused MessageReceived delivery + shadow-author-only routing
    // (Task 12), with fault-isolated per-recipient sends.
    MessageRepository messageRepository,
    FanOutEngine fanOutEngine,
    // C3 (Task 14): the batched ViewersChanged sink. The hub ROUTES viewer-roster changes into it
    // (FocusChannel/UnfocusChannel in ChatHub.Channels.cs, and the disconnect teardown below) BEFORE
    // the corresponding FocusRegistry mutation, so the accumulator captures each battleTag's
    // pre-window viewing baseline as it was BEFORE the change. Singleton (Startup) — it holds the
    // per-channel accumulation window every route writes and the flush hosted service (Task 15) drains.
    ViewersAccumulator viewersAccumulator) : Hub
{
    private readonly ConnectionMapping _connections = connections;
    private readonly ChatHistory _chatHistory = chatHistory;
    private readonly MuteReconciliationService _muteReconciliation = muteReconciliation;
    private readonly ITicketStore _ticketStore = ticketStore;
    private readonly ISessionRegistry _sessionRegistry = sessionRegistry;
    private readonly UserDirectoryRepository _userDirectory = userDirectory;
    // C3 (Task 8): the SessionState snapshot assembler + the in-memory fan-out registries this hub
    // seeds on connect and tears down on disconnect. TimeProvider supplies the trusted server clock
    // for the connect-path `now` handed to the assembler (mute-expiry resolution).
    private readonly SessionStateAssembler _assembler = assembler;
    private readonly FocusRegistry _focusRegistry = focusRegistry;
    private readonly OnlineMemberRegistry _onlineMemberRegistry = onlineMemberRegistry;
    private readonly MessageRateLimiter _messageRateLimiter = messageRateLimiter;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ChannelRepository _channelRepository = channelRepository;
    // C3 (Task 10): membership self-service deps — see the constructor param doc comment above.
    private readonly MembershipRepository _membershipRepository = membershipRepository;
    private readonly ChannelCreationRateLimiter _channelCreationRateLimiter = channelCreationRateLimiter;
    // C3 (Task 11): the durable send pipeline's message store + post-persist fan-out seam.
    private readonly MessageRepository _messageRepository = messageRepository;
    private readonly FanOutEngine _fanOutEngine = fanOutEngine;
    // C3 (Task 14): the batched ViewersChanged sink — see the constructor param doc comment above.
    private readonly ViewersAccumulator _viewersAccumulator = viewersAccumulator;

    public override async Task OnConnectedAsync()
    {
        // HARD CUTOVER (C2): access_token carries a one-time TICKET minted by POST /auth/session.
        // A raw JWT lands here too — it is simply not a valid ticket and is rejected.
        var ticket = Context.GetHttpContext()?.Request.Query["access_token"].ToString();
        if (string.IsNullOrEmpty(ticket) || !_ticketStore.TryConsume(ticket, DateTime.UtcNow, out var identity))
        {
            Log.Warning("Receiver {ConnectionId} failed to authenticate", Context.ConnectionId);
            await Clients.Caller.SendAsync("AuthorizationFailed");
            Context.Abort(); // the ONLY rejection-style abort (unchanged policy); ban paths never abort
            return;
        }

        // Single connection per battleTag (ChatLimits.MaxConnectionsPerBattleTag == 1): new wins.
        var displaced = _sessionRegistry.Register(Context.ConnectionId, identity, Context);
        if (displaced != null)
        {
            // Contract (acceptance 4): notify the OLD connection, THEN close it — event BEFORE close.
            // This displacement close is contract-mandated; it is NOT a ban path (bans never abort).
            await Clients.Client(displaced.ConnectionId).SendAsync("ConnectionDisplaced", "Connected elsewhere");
            displaced.Context?.Abort();
        }

        await UpsertDirectoryStub(identity);

        // C3 (Task 8): assemble the SessionState snapshot and seed this connection's fan-out state (the
        // OnlineMemberRegistry + the legacy mute cache, both done inside AssembleAndSeed), then push the
        // snapshot to the CALLER only — it is that connection's private state rebuild (spec acceptance
        // 8), never a group broadcast. FATAL by design: if AssembleAndSeed throws (e.g. a Mongo hiccup)
        // the connect fails — an authenticated connection with no snapshot is useless — so we do NOT
        // swallow it (unlike the non-fatal directory stub above).
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var (dto, muteStatus) = await _assembler.AssembleAndSeed(identity, Context.ConnectionId, now);
        await Clients.Caller.SendAsync(ChatEvents.SessionState, dto);

        if (muteStatus == MuteStatus.Full)
        {
            // Full ban → also push the legacy PlayerBannedFromChat notice so old clients render it.
            // SECURITY: expiry ONLY — never the reason or the shadow flag. The endDate comes straight
            // off the DTO's own MuteState (present iff Full), mirroring MuteReconciliationService's
            // PlayerBannedFromChat payload shape ({ endDate }).
            await Clients.Caller.SendAsync(ChatEvents.PlayerBannedFromChat, new { endDate = dto.MuteState.EndDate });
        }
    }

    // Stub directory upsert (full enrichment is C6). Read-modify-write via Load → set → Upsert so a
    // future cached Profile is preserved. Non-fatal: a directory write must never fail a connect.
    private async Task UpsertDirectoryStub(W3CUserAuthentication identity)
    {
        try
        {
            var entry = await _userDirectory.Load(identity.BattleTag)
                ?? new UserDirectoryEntry { BattleTag = identity.BattleTag };
            entry.NormalizedName = identity.Name?.Trim().ToLowerInvariant();
            entry.LastSeenAt = DateTime.UtcNow;
            await _userDirectory.Upsert(entry);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to upsert user_directory stub for {BattleTag}", identity.BattleTag);
        }
    }

    public override async Task OnDisconnectedAsync(Exception exception)
    {
        // C3 (Task 8): tear down this connection's in-memory fan-out state UNCONDITIONALLY so it can
        // never leak past the socket's lifetime. INVARIANT, enforced structurally (not by statement
        // order): the removals below live in a `finally`, so they ALWAYS run — even if the teardown in
        // the `try` throws — and a future edit inserting code above/around them cannot silently skip
        // them. All are synchronous, self-contained, no-op-if-absent removals.
        // Task 14 (C2 amendment): the focus teardown now routes THROUGH the ViewersAccumulator FIRST
        // (in the finally, BEFORE FocusRegistry.RemoveConnection) so a leaving viewer's disappearance
        // is reconciled against a same-window reconnect rather than always read as a leave.
        try
        {
            // Identity-checked teardown lives INSIDE the registry — the hub stays dumb. Safe against the
            // displaced-old-socket race: a dying OLD socket will NOT evict the NEW session.
            _sessionRegistry.Unregister(Context.ConnectionId);

            // Drop the connection→user mapping unconditionally. REQUIRED for mute-cache cleanup (a
            // ConnectionMapping.Remove also clears this connection's cached mute entry); no-op if absent.
            _connections.Remove(Context.ConnectionId);
        }
        finally
        {
            // Task 14 (C2 amendment) — the displacement-reconciliation hook. BEFORE FocusRegistry drops
            // this connection's focus entries, route each of them through the ViewersAccumulator so the
            // accumulator captures the battleTag's pre-window baseline (= VIEWING right now, since the
            // removal hasn't happened yet). A NEW connection re-focusing the SAME channel with the SAME
            // battleTag within the 5s window then re-establishes current==baseline, so the flush nets to
            // NO delta (the displaced socket's leave and the reconnect's join cancel). The battleTag is
            // the one FocusRegistry stored for this connection; GetFocusedChannels/RemoveConnection are
            // read/cleared here while FocusRegistry still holds the entries. Kept in the always-run
            // finally so teardown is unconditional even if the try above throws.
            if (_focusRegistry.TryGetBattleTag(Context.ConnectionId, out var focusedBattleTag))
            {
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                foreach (var channelId in _focusRegistry.GetFocusedChannels(Context.ConnectionId))
                {
                    _viewersAccumulator.RecordChange(channelId, focusedBattleTag, now);
                }
            }

            _focusRegistry.RemoveConnection(Context.ConnectionId);
            _onlineMemberRegistry.RemoveConnection(Context.ConnectionId);
            _messageRateLimiter.RemoveConnection(Context.ConnectionId);
            // Task 13: also drop the connection's ChannelActivity coalescing state (routed through the
            // fan-out engine, which owns the coalescer) so the singleton can't leak past the socket.
            _fanOutEngine.OnConnectionClosed(Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
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

        // Spec §12: BanUser is one of the two canonical IN-BAND ban paths (the other is the REST
        // MuteController). Both delegate to MuteReconciliationService.ApplyBanAsync, which persists the
        // ban AND reconciles every live connection's mute cache, so enforcement is instant without a
        // per-send DB read. Only a ban written DIRECTLY to the Mongo collection (bypassing both paths —
        // e.g. a manual DB edit) takes effect on the target's next reconnect.
        await _muteReconciliation.ApplyBanAsync(loungeMuteRequest);
    }
}

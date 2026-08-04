using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Relationships;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;
using Serilog;

[assembly: InternalsVisibleTo("W3ChampionsChatService.Tests")]
namespace W3ChampionsChatService.Chats;

public partial class ChatHub(
    ConnectionMapping connections,
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
    ViewersAccumulator viewersAccumulator,
    // C4 (Task 3, D10): the mention-inbox cleanup hook — DeleteMessage is its FIRST caller. This is the
    // ONLY constructor change this task: the durable soft-delete purges any mention-inbox entries that
    // reference a deleted message, without C4 reaching into C6's inbox internals. Resolves to the real
    // MentionInboxCleaner since C6 Task 7 swapped the DI registration (see IMentionInboxCleaner).
    IMentionInboxCleaner mentionInboxCleaner,
    // C5 (Task 1, D1): the relationship (friends/blocked) provider. Added to the hub ctor HERE — one task
    // ahead of D19's planned T3 growth — because the connect-time warm prefetch below needs it, and
    // ctor injection is the only way OnConnectedAsync obtains a collaborator (there is no service locator
    // in this hub). T3 adds the remaining two DM deps (UserSettingsRepository, DmInitiationTracker) in a
    // single sweep. Singleton (Startup); the prefetch below is the FIRST and ONLY T1 consumer — no gating
    // reads it yet (later tasks gate block/friend/consent on it).
    IRelationshipProvider relationshipProvider,
    // C5 (Task 3, D19): the remaining TWO DM front-door deps, added in a single ctor growth (T1 already
    // took IRelationshipProvider above one task early for the connect-time prefetch). UserSettingsRepository
    // backs the dmPrivacy gate + SetDmPrivacy (a thin per-user settings read/write); DmInitiationTracker is
    // the in-memory 8h stranger-initiation cap (singleton — Startup). Both are consumed by the OpenDm/
    // SetDmPrivacy partial in ChatHub.Dm.cs and reused by later C5 tasks (T4/T6 accept transitions).
    UserSettingsRepository userSettings,
    DmInitiationTracker dmInitiationTracker,
    // D9 (C6 Task 3): the chat-flair resolution service. Previously only SessionStateAssembler held
    // this dependency; it is now HOISTED to the hub so OnConnectedAsync can resolve ONCE (getting both
    // the ChatUser AND whether the enrichment was FreshFromWb) and thread the SAME resolved ChatUser
    // into both AssembleAndSeed (SessionState flair) and the connect-time directory upsert (which needs
    // FreshFromWb to decide whether it may replace the cached Profile) — one wb round-trip serves both,
    // where the pre-D9 path resolved it twice.
    IChatAuthenticationService chatAuthenticationService,
    // C6 (Task 5, D3/D4): the mention fan-out. SendMessage's step 7.75 (ChatHub.Messaging.cs) hands it
    // the validated mention-tag list for each persisted, NON-shadow message; per eligible member it
    // writes a mention-inbox entry + a targeted MentionNotified push. Singleton (Startup).
    MentionFanOut mentionFanOut,
    // C6 (Task 5, D15): the presence-interest index, injected NOW purely so this ctor grows EXACTLY
    // ONCE — a single sweep of every test construction site (the C5 D19 single-ctor-growth discipline)
    // instead of two. There is NO T5 consumer: Task 9 is the first to derive/emit presence interest
    // from it. Singleton (Startup).
    PresenceInterestRegistry presenceInterestRegistry,
    // C6 (Task 6): the mention-inbox store backing the read/ack surface (GetMentionInbox /
    // MarkMentionsRead / MentionUnreadCount), injected now in the SAME single ctor growth. There is NO
    // T5 consumer — the write path uses MentionFanOut's OWN MentionInboxRepository, not this one; Task 6
    // is the first reader.
    MentionInboxRepository mentionInboxRepository) : Hub
{
    private readonly ConnectionMapping _connections = connections;
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
    // C4 (Task 3): the mention-inbox cleanup hook called by the durable DeleteMessage pipeline.
    private readonly IMentionInboxCleaner _mentionInboxCleaner = mentionInboxCleaner;
    // C5 (Task 1): the relationship provider — connect-time warm prefetch below; gating consumers land in
    // later C5 tasks.
    private readonly IRelationshipProvider _relationshipProvider = relationshipProvider;
    // C5 (Task 3): the DM front-door deps — the dmPrivacy settings store and the stranger-initiation cap.
    private readonly UserSettingsRepository _userSettings = userSettings;
    private readonly DmInitiationTracker _dmInitiationTracker = dmInitiationTracker;
    // D9 (C6 Task 3): the hoisted chat-flair resolution — see the constructor param doc comment above.
    private readonly IChatAuthenticationService _chatAuthenticationService = chatAuthenticationService;
    // C6 (Task 5): the mention fan-out seam consumed by SendMessage's step 7.75 (ChatHub.Messaging.cs).
    private readonly MentionFanOut _mentionFanOut = mentionFanOut;
    // C6 (Task 5, D15): injected now, first CONSUMED in Task 9 (presence-interest derivation). No T5
    // reader — see the ctor param doc comment for why it lands in this single ctor growth.
    private readonly PresenceInterestRegistry _presenceInterestRegistry = presenceInterestRegistry;
    // C6 (Task 6): the mention-inbox read/ack store — first CONSUMED in Task 6. No T5 reader — see the
    // ctor param doc comment (the write path uses MentionFanOut's own repository, not this field).
    private readonly MentionInboxRepository _mentionInboxRepository = mentionInboxRepository;

    public override async Task OnConnectedAsync()
    {
        // HARD CUTOVER (C2): access_token carries a one-time TICKET minted by POST /auth/session.
        // A raw JWT lands here too — it is simply not a valid ticket and is rejected.
        var ticket = Context.GetHttpContext()?.Request.Query["access_token"].ToString();
        // Deliberate wall-clock seam, NOT routed through _timeProvider: the ticket is minted with
        // DateTime.UtcNow by the REST AuthSessionController (a separate process boundary that has no
        // access to this hub's injected clock), so the consume-side check MUST compare against the
        // same wall clock the mint side used. This is intentionally decoupled from the injectable
        // fan-out clock below — TicketStore TTL correctness does not depend on TimeProvider.
        if (string.IsNullOrEmpty(ticket) || !_ticketStore.TryConsume(ticket, DateTime.UtcNow, out var identity))
        {
            Log.Warning("Receiver {ConnectionId} failed to authenticate", Context.ConnectionId);
            await Clients.Caller.SendAsync("AuthorizationFailed");
            Context.Abort(); // the ONLY rejection-style abort (unchanged policy); ban paths never abort
            return;
        }

        // Single connection per battleTag (ChatLimits.MaxConnectionsPerBattleTag == 1): new wins.
        var displaced = _sessionRegistry.Register(Context.ConnectionId, identity, Context);

        // C6 (Task 9, D11): a GENUINE offline→online transition is one where there was NO prior session
        // for this battleTag. A displacement (displaced != null) is a reconnect of an ALREADY-online user
        // (same battleTag, new socket) — online before AND after — so it is NOT a transition and must fire
        // no PresenceChanged (the false-transition guard). The actual emit runs at the end of connect,
        // after the session is fully seeded.
        var wentOnline = displaced == null;

        if (displaced != null)
        {
            // Contract (acceptance 4): notify the OLD connection, THEN close it — event BEFORE close.
            // This displacement close is contract-mandated; it is NOT a ban path (bans never abort).
            await Clients.Client(displaced.ConnectionId).SendAsync("ConnectionDisplaced", "Connected elsewhere");
            displaced.Context?.Abort();
        }

        // Single clock read for the whole connect path — reused for the directory upsert's LastSeenAt
        // and the assembler's mute-expiry resolution below, so every "now" on this path comes from the
        // SAME injected TimeProvider read instead of each step taking its own independent wall-clock
        // snapshot (identical in production; makes the path deterministically testable under a
        // FakeTimeProvider).
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // D9 (C6 Task 3): resolve the chat flair ONCE — hoisted out of the assembler so the SAME wb
        // round-trip serves BOTH the SessionState flair (passed straight through to AssembleAndSeed
        // below) AND the connect-time directory upsert's FreshFromWb decision (UpsertDirectory).
        var resolution = await _chatAuthenticationService.GetUserFromIdentity(identity);
        await UpsertDirectory(identity, resolution, now);

        // Follow-up spec §6: resolve the caller's OWN relationship snapshot BEFORE assembly — the
        // bounded DM snapshot needs the block list to keep every blocked 1:1 shell regardless of
        // recency. Bounded wait: the wb source self-caps at its 2s HttpClient timeout and the provider
        // serves fresh-cache/stale tiers first (the connect path already awaits one wb round-trip —
        // GetUserFromIdentity above). Fail-soft: with nothing cached at all, assemble WITHOUT a
        // snapshot — blocked shells outside the recency window may be omitted from THIS SessionState
        // only (self-heals on the next connect); a wb outage must never fail the connect.
        RelationshipSnapshot relationshipSnapshot = null;
        try
        {
            relationshipSnapshot = await _relationshipProvider.GetSnapshotAsync(identity.BattleTag);
        }
        catch (RelationshipUnavailableException ex)
        {
            Log.Warning(ex, "No relationship snapshot for {BattleTag} at connect — blocked 1:1 shells may be omitted from this SessionState", identity.BattleTag);
        }

        // C5 (Task 1) / C6 (Task 11, D13): unchanged role (cache warm + friend-presence push), now
        // dispatched AFTER the awaited fetch above so its own GetSnapshotAsync is a warm-cache hit
        // instead of a duplicate wb round-trip. Still fire-and-forget and non-fatal.
        _ = PrefetchRelationshipSnapshot(identity.BattleTag, wentOnline, Context.ConnectionId);

        // C3 (Task 8): assemble the SessionState snapshot and seed this connection's fan-out state (the
        // OnlineMemberRegistry + the legacy mute cache, both done inside AssembleAndSeed), then push the
        // snapshot to the CALLER only — it is that connection's private state rebuild (spec acceptance
        // 8), never a group broadcast. FATAL by design: if AssembleAndSeed throws (e.g. a Mongo hiccup)
        // the connect fails — an authenticated connection with no snapshot is useless — so we do NOT
        // swallow it (unlike the non-fatal directory upsert above). The already-resolved chatUser is
        // threaded straight through — AssembleAndSeed no longer re-resolves it itself (D9).
        var (dto, muteStatus) = await _assembler.AssembleAndSeed(identity, Context.ConnectionId, now, resolution.User, relationshipSnapshot);
        await Clients.Caller.SendAsync(ChatEvents.SessionState, dto);

        if (muteStatus == MuteStatus.Full)
        {
            // Full ban → also push the legacy PlayerBannedFromChat notice so old clients render it.
            // SECURITY: expiry ONLY — never the reason or the shadow flag. The endDate comes straight
            // off the DTO's own MuteState (present iff Full), mirroring MuteReconciliationService's
            // PlayerBannedFromChat payload shape ({ endDate }).
            await Clients.Caller.SendAsync(ChatEvents.PlayerBannedFromChat, new { endDate = dto.MuteState.EndDate });
        }

        // C6 (Task 9, D11): on a GENUINE offline→online transition, tell every connection with DERIVED
        // interest in this user (someone with a focused Dm/GroupDm containing them) that they are now
        // online — MINUS this user's own connection. Emitted through the fan-out engine's per-recipient
        // fault-isolated path, so a dead watcher's socket can never fail the connect. A displacement
        // (wentOnline == false) emits nothing: the user was already online. The recipient set is derived
        // ENTIRELY from focus+membership — a connection watching nothing, or focused elsewhere, gets zero.
        if (wentOnline)
        {
            await _fanOutEngine.PushPresenceChanged(identity.BattleTag, online: true, Context.ConnectionId);
        }
    }

    // D9 (C6 Task 3): the FULL connect-time directory upsert — replaces the C3 name-only stub.
    // Read-modify-write via Load → set → Upsert (the Task 2 full-replace Upsert) so a Profile this call
    // does NOT own to overwrite survives untouched. LastSeenAt, DisplayBattleTag, and NormalizedName
    // (the lowercased FULL battleTag — D8/T2 convention, e.g. "peter#123", not just "peter") are ALWAYS
    // refreshed: the user IS connecting, that much is true even on a wb outage. Profile is replaced
    // ONLY when <paramref name="resolution"/>.FreshFromWb — the NEVER-CLOBBER invariant: a wb outage
    // must NEVER overwrite a previously-cached, good Profile with the near-null plain/cached-fallback
    // ChatUser's flair. Non-fatal: a directory write must never fail a connect. `now` is the caller's
    // single injected-clock read (OnConnectedAsync) — routed through, not read again here, so this
    // stays on the SAME TimeProvider clock as the rest of the connect path.
    private async Task UpsertDirectory(W3CUserAuthentication identity, ChatUserResolution resolution, DateTime now)
    {
        try
        {
            var entry = await _userDirectory.Load(identity.BattleTag)
                ?? new UserDirectoryEntry { BattleTag = identity.BattleTag };
            entry.DisplayBattleTag = identity.BattleTag;
            entry.NormalizedName = identity.BattleTag?.Trim().ToLowerInvariant();
            entry.LastSeenAt = now;
            if (resolution.FreshFromWb)
            {
                entry.Profile = ChatProfileMapper.FromChatUser(resolution.User);
            }
            await _userDirectory.Upsert(entry);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to upsert user_directory entry for {BattleTag}", identity.BattleTag);
        }
    }

    // C5 (Task 1): best-effort connect-time relationship prefetch. Deliberately fire-and-forget — the
    // caller does NOT await it, so a slow/unreachable wb read never adds latency to (or fails) a connect.
    // Any failure is swallowed and logged exactly like UpsertDirectoryStub; on success the provider caches
    // the snapshot. References only the singleton provider + the captured battleTag/connectionId, so it is
    // safe to run to completion after this hub instance is disposed.
    // C6 (Task 11, D13): EXTENDED (not a second background task) so that, after a successful fetch, on a
    // GENUINE offline→online transition (<paramref name="wentOnline"/> — the SAME flag OnConnectedAsync
    // already computed; never re-derived here) it also pushes FriendPresenceChanged{subject, online:true}
    // to every one of the connecting user's OWN friends (from THIS snapshot) that currently has a live
    // connection. Fault-isolated per-recipient via FanOutEngine.PushFriendPresenceChanged. On ANY failure
    // fetching the snapshot — including RelationshipUnavailableException (no cache at all) — the friend
    // push is silently skipped (logged, never thrown): honest degradation, matching the same
    // stale-usable/fail-closed policy Task 10's GetPresenceDetails already established for this provider.
    // A displaced reconnect (wentOnline == false) pushes nothing, mirroring Task 9's PresenceChanged
    // transition guard exactly.
    private async Task PrefetchRelationshipSnapshot(string battleTag, bool wentOnline, string connectionId)
    {
        RelationshipSnapshot snapshot;
        try
        {
            snapshot = await _relationshipProvider.GetSnapshotAsync(battleTag);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to prefetch relationship snapshot for {BattleTag}", battleTag);
            return;
        }

        if (wentOnline)
        {
            await _fanOutEngine.PushFriendPresenceChanged(battleTag, online: true, snapshot.Friends, connectionId);
        }
    }

    public override async Task OnDisconnectedAsync(Exception exception)
    {
        // D9 (C6 Task 3): capture the disconnecting session's identity BEFORE Unregister —
        // TryGetByConnectionId is fail-closed for a DISPLACED old socket: once the NEW connection's
        // Register() call overwrote SessionRegistry's battleTag→session pointer, this OLD connectionId
        // no longer resolves here, so `hasSession` is false on exactly that race and the disconnect-time
        // directory upsert below is skipped — the user IS still online via their new connection, so
        // their LastSeenAt must not be rewound by the dying old socket. Calling this AFTER Unregister
        // would always return false (Unregister also drops this connectionId's reverse-map entry), so
        // the ordering here is load-bearing, not cosmetic.
        var hasDisconnectingSession = _sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var disconnectingSession);

        // C6 (Task 9, D11): capture the disconnecting battleTag NOW, before Unregister, for the
        // presence-offline emit below (NPE-safe: null for a displaced old socket, which also short-circuits
        // the wentOffline gate).
        var disconnectingBattleTag = hasDisconnectingSession ? disconnectingSession.Identity.BattleTag : null;

        // C6 (Task 9, D11): whether THIS disconnect is a genuine online→offline transition. Set from
        // Unregister's return inside the try below; a displaced old socket returns false (the user is still
        // online via a newer connection), suppressing any PresenceChanged(offline).
        var wentOffline = false;

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
            // displaced-old-socket race: a dying OLD socket will NOT evict the NEW session. Its return is
            // the D11 transition signal: true iff this call actually removed the battleTag's live mapping.
            wentOffline = _sessionRegistry.Unregister(Context.ConnectionId);

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
                    // C5 (Task 5, D11): skip Dm/GroupDm — a forced teardown of a private-lane focus must
                    // never enter the ViewersAccumulator (zero-DB lookup via OnlineMemberRegistry, BEFORE
                    // RemoveConnection below clears the entry). Public/SemiPublic/System are unaffected.
                    if (IsPrivateLaneChannel(channelId, Context.ConnectionId))
                    {
                        continue;
                    }

                    _viewersAccumulator.RecordChange(channelId, focusedBattleTag, now);
                }
            }

            _focusRegistry.RemoveConnection(Context.ConnectionId);
            _onlineMemberRegistry.RemoveConnection(Context.ConnectionId);
            // MessageRateLimiter is deliberately NOT torn down here (2026-08-04 follow-up spec §1): its
            // state is battleTag-keyed and must SURVIVE disconnect/reconnect (violations, the tier
            // ladder, and an active hard throttle all persist across a relaunch) — the limiter prunes
            // its own quiescent entries opportunistically instead. See
            // MessageRateLimiterTests.HardThrottle_SurvivesReconnect_BecauseStateIsKeyedByBattleTag and
            // ChatHubSendMessageTests.Send_AutoThrottle_SurvivesReconnect_SameBattleTag.
            // Task 13: also drop the connection's ChannelActivity coalescing state (routed through the
            // fan-out engine, which owns the coalescer) so the singleton can't leak past the socket.
            _fanOutEngine.OnConnectionClosed(Context.ConnectionId);
            // C6 (Task 9, D11): drop this connection's OWN derived presence interest (it was watching
            // others' presence via its focused DMs/groups). UNCONDITIONAL + no-op-safe, alongside the
            // sibling registries — this removes it as a WATCHER; OTHER connections' interest in THIS
            // user's tag is untouched (that is what the PresenceChanged(offline) emit below conveys).
            _presenceInterestRegistry.RemoveConnection(Context.ConnectionId);
        }

        // C6 (Task 9, D11): a GENUINE online→offline transition (wentOffline) fires PresenceChanged(offline)
        // to every connection with derived interest in this user — MINUS the (now-closing) own connection.
        // A displaced old socket (wentOffline == false) fires nothing: the user is still online via the
        // newer connection. Emitted AFTER the finally so RemoveConnection has cleared this connection's own
        // watching first — harmless either way, since GetInterestedConnections keys on the SUBJECT's tag,
        // which RemoveConnection(self) never touches.
        if (wentOffline && disconnectingBattleTag != null)
        {
            await _fanOutEngine.PushPresenceChanged(disconnectingBattleTag, online: false, Context.ConnectionId);

            // C6 (Task 11, D13): the symmetric friend-presence push — a NEW fire-and-forget task (the
            // disconnect path has no pre-existing background task to ride, unlike connect's prefetch).
            // Fetches the disconnecting user's OWN relationship snapshot and, on success, pushes
            // FriendPresenceChanged{subject, online:false} to every currently-online friend. Deliberately
            // NOT awaited and non-fatal: this runs AFTER the teardown `finally` block above has already
            // completed, so a slow/unreachable snapshot fetch can never delay OR fail this method's return.
            _ = PushFriendPresenceOnDisconnect(disconnectingBattleTag, Context.ConnectionId);
        }

        // D9 (C6 Task 3, acceptance 6): the DISCONNECT-time directory write — true last-seen. Skipped
        // entirely for a displaced old socket (hasDisconnectingSession false — see the capture above).
        if (hasDisconnectingSession)
        {
            await UpsertLastSeenOnDisconnect(disconnectingSession.Identity);
        }

        await base.OnDisconnectedAsync(exception);
    }

    // D9 (C6 Task 3): uses the partial SetLastSeen (Task 2) — NEVER the full-replace Upsert — so a
    // previously-cached Profile is untouched by construction (SetLastSeen's own contract; see
    // Users/UserDirectoryRepository.cs). Non-fatal: a directory write must never fail disconnect
    // teardown.
    private async Task UpsertLastSeenOnDisconnect(W3CUserAuthentication identity)
    {
        try
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var normalizedName = identity.BattleTag?.Trim().ToLowerInvariant();
            await _userDirectory.SetLastSeen(identity.BattleTag, identity.BattleTag, normalizedName, now);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to update user_directory LastSeenAt on disconnect for {BattleTag}", identity.BattleTag);
        }
    }

    // C6 (Task 11, D13): the disconnect-side counterpart to PrefetchRelationshipSnapshot's connect-time
    // push. A NEW fire-and-forget task (the disconnect path had no pre-existing background task to ride).
    // Fetches the disconnecting user's OWN relationship snapshot and, on success, pushes
    // FriendPresenceChanged{subject, online:false} to every currently-online friend via
    // FanOutEngine.PushFriendPresenceChanged (per-recipient fault-isolated). On ANY failure — including
    // RelationshipUnavailableException when nothing is cached — the push is silently skipped (logged,
    // never thrown): this runs AFTER OnDisconnectedAsync's own teardown has already completed, so it can
    // never affect that method's outcome or add latency to it.
    private async Task PushFriendPresenceOnDisconnect(string battleTag, string connectionId)
    {
        RelationshipSnapshot snapshot;
        try
        {
            snapshot = await _relationshipProvider.GetSnapshotAsync(battleTag);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch relationship snapshot for {BattleTag} for disconnect friend-presence push", battleTag);
            return;
        }

        await _fanOutEngine.PushFriendPresenceChanged(battleTag, online: false, snapshot.Friends, connectionId);
    }

    /// <summary>
    /// C4 (Task 3, D5): durable moderation soft-delete of a single message. The
    /// <see cref="UserHasPermission"/> attribute + <see cref="ChatHubPermissionFilter"/> already gate
    /// this on <see cref="EPermission.Moderation"/>; the pipeline below is honored in EXACTLY this
    /// order, every rejection a typed <see cref="ChannelOperationResult"/> (never a silent drop):
    /// <list type="number">
    /// <item>Fail-closed moderator resolution via <see cref="ISessionRegistry.TryGetByConnectionId"/> —
    /// no live session → <see cref="ChatResultCode.PermissionDenied"/> (there is no identity to attribute
    /// the delete to).</item>
    /// <item><see cref="Messages.MessageRepository.Load"/>; missing → <see cref="ChatResultCode.NotFound"/>.</item>
    /// <item>Resolve the message's channel and enforce the SHARED moderation scope wall (spec §10 + plan
    /// D5): single-delete uses the EXACT SAME <see cref="ChannelModeration.IsModeratable"/> include-list as
    /// the cross-channel <see cref="PurgeMessagesFromUser"/> sweep (C4 Task 7: also shared with the REST
    /// moderation-history endpoint) — <see cref="ChannelType.Public"/>,
    /// <see cref="ChannelType.SemiPublic"/>, and <see cref="ChannelType.System"/> with
    /// <see cref="SystemChannelKind.Match"/>. Everything else — <see cref="ChannelType.Dm"/>/
    /// <see cref="ChannelType.GroupDm"/>, System+<see cref="SystemChannelKind.Clan"/>/
    /// <see cref="SystemChannelKind.Lobby"/>, System with no kind, or a vanished/unresolvable channel —
    /// → <see cref="ChatResultCode.PermissionDenied"/>, nothing deleted. Moderators never touch
    /// private/clan/lobby content; the TTL cleans those. Single-delete and purge honor ONE wall so the
    /// two moderation paths can never drift out of scope-parity.</item>
    /// <item>Soft-delete only, CONDITIONAL (C4 Task 4 directive (a)):
    /// <see cref="Messages.MessageRepository.MarkDeleted"/> flips <c>deleted{by,at}</c> only while
    /// <c>Deleted == null</c>; the row (and its <c>ExpiresAt</c>/TTL) survives, physical removal stays
    /// TTL-only (NEVER a hard delete). The whole side-effect tail (audit + cleanup + event) is GATED on
    /// the write having actually modified the row: an already-deleted message, or one another moderator
    /// deleted in the load→write window, returns <see cref="ChatResultCode.Ok"/> with NO
    /// audit/cleanup/event and the ORIGINAL attribution untouched (idempotent — closes the TOCTOU).</item>
    /// <item>Audit-before-side-effects (C4 Task 4 directive (b)): the moderation audit
    /// <see cref="Log.Information"/> (moderator battleTag + message id + channel id) fires IMMEDIATELY
    /// after the durable soft-delete commits — BEFORE the cleaner and fan-out — so a committed moderation
    /// action is always logged even if a later (C6) throwing cleaner aborts the tail.</item>
    /// <item>Mention-inbox cleanup (D10) <see cref="IMentionInboxCleaner.RemoveForMessages"/> — AFTER the
    /// audit and still BEFORE the event so the inbox is cleaned even if fan-out hiccups. DeleteMessage is
    /// this hook's first caller.</item>
    /// <item>Deliver the FINAL channel-scoped <see cref="MessageDeletedDto"/> (D4) to the channel's
    /// FOCUSED viewers, EXCLUDING the moderated author's own connections (legacy <c>AllExcept(author)</c>
    /// semantics) — a focused moderator receives the SAME event and flags client-side. Return
    /// <see cref="ChatResultCode.Ok"/>.</item>
    /// </list>
    /// </summary>
    [UserHasPermission(EPermission.Moderation)]
    public async Task<ChannelOperationResult> DeleteMessage(string messageId)
    {
        // 1. Fail-closed: no live session → no moderator identity to attribute the delete to.
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }
        var moderatorBattleTag = session.Identity.BattleTag;

        // 2. Load the target message; missing → NotFound.
        var message = await _messageRepository.Load(messageId);
        if (message == null)
        {
            return new ChannelOperationResult(ChatResultCode.NotFound);
        }

        // 3. Moderation scope wall (spec §10 + plan D5): single-delete shares the SAME include-list as the
        // cross-channel purge — Public / SemiPublic / System+Match ONLY, via the one
        // ChannelModeration.IsModeratable predicate (C4 Task 7: also shared with the REST moderation
        // history endpoint, Messages/ModerationHistoryController.cs). So a moderator never touches
        // DM/GroupDm, clan, or lobby content, and any unresolvable channel is rejected fail-closed (we
        // cannot prove it is in scope). Nothing is deleted on rejection.
        var channel = await _channelRepository.Load(message.ChannelId);
        if (channel == null || !ChannelModeration.IsModeratable(channel))
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        // 4. Soft-delete only (NEVER a hard delete), CONDITIONAL on Deleted == null (directive (a)): sets
        // deleted{by,at}; the row and its ExpiresAt/TTL survive. `now` comes from the SAME injected
        // TimeProvider the rest of the hub uses. The whole side-effect tail is GATED on this write having
        // modified the row — an already-deleted message, or one another moderator deleted in the
        // load->write window, returns Ok with NO audit/cleanup/event, preserving the original attribution
        // (idempotent; closes the double-delete TOCTOU).
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var modified = await _messageRepository.MarkDeleted(messageId, moderatorBattleTag, now);
        if (!modified)
        {
            return new ChannelOperationResult(ChatResultCode.Ok);
        }

        // 5. Moderation audit (directive (b)): logged IMMEDIATELY after the durable soft-delete commits,
        // BEFORE the cleaner and the fan-out — a committed moderation action must always be audited even
        // if a later (C6) throwing cleaner aborts the tail. Ports the legacy audit line.
        Log.Information(
            "Moderator {ModeratorBattleTag} soft-deleted message {MessageId} in channel {ChannelId}",
            moderatorBattleTag, messageId, channel.Id);

        // 6. Mention-inbox cleanup (D10) — after the audit, still BEFORE the event so the inbox is purged
        // even if fan-out hiccups. DeleteMessage is the first caller of this C4/C6 coordination surface.
        await _mentionInboxCleaner.RemoveForMessages(new[] { messageId });

        // 7. Deliver the channel-scoped removal (D4) to the channel's focused viewers, EXCLUDING the
        // moderated author's own connections (preserving legacy AllExcept(author) semantics — the
        // moderated user is not tipped off live; their copy vanishes on next reload since UserVisible
        // excludes deleted rows). A focused moderator is NOT excluded and receives the same event.
        var authorConnectionIds = _connections.GetConnectionIdsForUser(message.Sender.BattleTag);
        await _fanOutEngine.PushMessageDeleted(channel.Id, messageId, authorConnectionIds);

        return new ChannelOperationResult(ChatResultCode.Ok);
    }

    /// <summary>
    /// C4 (Task 4, D6): durable CROSS-CHANNEL moderation purge of every eligible message a user sent.
    /// <see cref="UserHasPermission"/> + <see cref="ChatHubPermissionFilter"/> already gate this on
    /// <see cref="EPermission.Moderation"/>; the pipeline below is honored in EXACTLY this order:
    /// <list type="number">
    /// <item>Fail-closed moderator resolution via <see cref="ISessionRegistry.TryGetByConnectionId"/> —
    /// no live session → <see cref="ChatResultCode.PermissionDenied"/> (no identity to attribute to).</item>
    /// <item><see cref="Messages.MessageRepository.LoadPurgeableBySender"/> — the target's NON-deleted
    /// rows as (Id, ChannelId), CASE-INSENSITIVE via the sender collation (so a mixed-case argument still
    /// matches the stored casing). No date filter: "within retention" is already bounded by the TTL.</item>
    /// <item>PRIVACY + SCOPE WALL (the pinned wall): resolve the distinct channels via
    /// <see cref="ChannelRepository.LoadByIds"/> and keep ONLY <see cref="ChannelType.Public"/>,
    /// <see cref="ChannelType.SemiPublic"/>, and <see cref="ChannelType.System"/> with
    /// <see cref="SystemChannelKind.Match"/>. <see cref="ChannelType.Dm"/>/<see cref="ChannelType.GroupDm"/>
    /// and System+<see cref="SystemChannelKind.Clan"/>/<see cref="SystemChannelKind.Lobby"/> are NEVER
    /// purged; a row whose channel is unresolvable is dropped fail-closed (we cannot prove it is not
    /// private).</item>
    /// <item>CONDITIONAL bulk soft-delete (directive (a)) of the surviving eligible ids via
    /// <see cref="Messages.MessageRepository.MarkDeletedMany"/> (filters <c>Deleted == null</c>); the
    /// returned <c>ModifiedCount</c> is the COUNT the result + audit are based on. Zero eligible (or zero
    /// actually modified on a re-purge) → <see cref="ChatResultCode.Ok"/> + 0 with NO side-effects
    /// (idempotency is structural — a re-purge's load returns empty because the rows are now deleted).</item>
    /// <item>Audit-before-side-effects (directive (b)): the audit <see cref="Log.Information"/> (moderator
    /// battleTag + target + COUNT) fires IMMEDIATELY after the durable commit — BEFORE cleaner + fan-out.</item>
    /// <item><see cref="IMentionInboxCleaner.RemoveForMessages"/> over the soft-deleted ids ONLY (never the
    /// dm/clan/lobby/unresolvable ids that were never touched), then per affected channel emit
    /// <see cref="FanOutEngine.PushBulkMessagesDeleted"/> to that channel's FOCUSED viewers MINUS the
    /// target's own connections (resolved case-insensitively via
    /// <see cref="ConnectionMapping.GetConnectionIdsForUser"/>).</item>
    /// <item>Return <see cref="PurgeMessagesResult"/>(<see cref="ChatResultCode.Ok"/>, ModifiedCount).</item>
    /// </list>
    /// The target's own SHADOW rows in eligible channels are purged like any other row. NEVER a hard
    /// delete — docs survive with <c>ExpiresAt</c>/TTL untouched.
    /// </summary>
    [UserHasPermission(EPermission.Moderation)]
    public async Task<PurgeMessagesResult> PurgeMessagesFromUser(string battleTag)
    {
        // 1. Fail-closed: no live session → no moderator identity to attribute the purge to.
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new PurgeMessagesResult(ChatResultCode.PermissionDenied, 0);
        }
        var moderatorBattleTag = session.Identity.BattleTag;

        // 2. Load the target's non-deleted rows (case-insensitive). Already-deleted rows are excluded, so
        // a re-purge naturally returns empty — structural idempotency, no separate guard needed.
        var targets = await _messageRepository.LoadPurgeableBySender(battleTag);
        if (targets.Count == 0)
        {
            return new PurgeMessagesResult(ChatResultCode.Ok, 0);
        }

        // 3. Privacy + scope wall: resolve the distinct channels and keep ONLY eligible types. A row whose
        // channel is unresolvable (no doc) is absent from eligibleChannelIds → dropped fail-closed.
        var distinctChannelIds = targets.Select(t => t.ChannelId).Distinct().ToList();
        var channels = await _channelRepository.LoadByIds(distinctChannelIds);
        var eligibleChannelIds = channels.Where(ChannelModeration.IsModeratable).Select(c => c.Id).ToHashSet();

        var eligibleTargets = targets.Where(t => eligibleChannelIds.Contains(t.ChannelId)).ToList();
        if (eligibleTargets.Count == 0)
        {
            return new PurgeMessagesResult(ChatResultCode.Ok, 0);
        }
        var eligibleIds = eligibleTargets.Select(t => t.Id).ToList();

        // 4. Conditional bulk soft-delete (directive (a)); the actual ModifiedCount drives count + audit.
        // Zero modified (e.g. a racing concurrent purge already flipped them) → Ok + 0, no side-effects.
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var modifiedCount = await _messageRepository.MarkDeletedMany(eligibleIds, moderatorBattleTag, now);
        if (modifiedCount == 0)
        {
            return new PurgeMessagesResult(ChatResultCode.Ok, 0);
        }

        // 5. Audit-before-side-effects (directive (b)): logged immediately after the durable commit,
        // BEFORE cleaner + fan-out, so a committed purge is always audited even if a later throwing
        // cleaner (C6) aborts the tail.
        Log.Information(
            "Moderator {ModeratorBattleTag} purged {Count} messages from user {BattleTag}",
            moderatorBattleTag, modifiedCount, battleTag);

        // 6. Mention-inbox cleanup over the soft-deleted ids only (the eligible-channel ids — never the
        // dm/clan/lobby/unresolvable ids that were never touched), BEFORE the fan-out.
        await _mentionInboxCleaner.RemoveForMessages(eligibleIds);

        // 7. Per affected channel, emit the channel-scoped BulkMessagesDeletedDto to that channel's
        // FOCUSED viewers MINUS the target's own connections (not tipped off live). A channel with no
        // focused viewers emits nothing (handled inside the engine). Emitting the loaded eligible ids per
        // channel is idempotent client-side even if a concurrent purge flipped a subset first.
        var targetConnectionIds = _connections.GetConnectionIdsForUser(battleTag);
        foreach (var group in eligibleTargets.GroupBy(t => t.ChannelId))
        {
            var messageIds = group.Select(t => t.Id).ToList();
            await _fanOutEngine.PushBulkMessagesDeleted(group.Key, messageIds, targetConnectionIds);
        }

        return new PurgeMessagesResult(ChatResultCode.Ok, (int)modifiedCount);
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

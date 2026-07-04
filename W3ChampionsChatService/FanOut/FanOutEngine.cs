using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;

namespace W3ChampionsChatService.FanOut;

/// <summary>
/// The single seam the send pipeline calls once a message is durably persisted (seam created in Task
/// 11; body filled in Task 12). <see cref="OnMessagePersisted"/> delivers the full
/// <c>MessageReceived</c> payload to the channel's online+focused viewers ONLY, with
/// shadow-author-plus-moderator routing (C4 D8): a shadow message reaches only its own author — who
/// gets the unflagged illusion echo — and any focused connection holding <see
/// cref="EPermission.Moderation"/>, who gets a real-flagged <c>ForModerator</c> copy; every other
/// focused connection gets nothing. It delivers through <see cref="IHubContext{ChatHub}"/> targeting
/// the focused connections from <see cref="FocusRegistry"/>.
/// <para>
/// This engine does NOT itself SEND the unfocused members' coalesced <c>ChannelActivity</c> — that is
/// the <see cref="ActivityCoalescer"/>, driven by the flush hosted service (Task 15). What
/// <see cref="OnMessagePersisted"/> DOES do (Task 13, per fan-out decision 3) is ROUTE: for every online
/// member that is a channel member but NOT focused, at notification level <c>All</c>, it hands the
/// message's seq to <see cref="ActivityCoalescer.Offer"/>. The coalescer owns the ≥10s coalescing and
/// the &gt;100-unread suppression; this engine never emits a full payload to an unfocused connection
/// (the "no full payloads to unfocused" guardrail).
/// </para>
/// Singleton (registered in <see cref="Startup"/>): it holds no per-call state and is shared by every
/// hub invocation, mirroring the other C3 fan-out registries. Task 15 owns the flush hosted service +
/// the ViewersAccumulator; the <see cref="ActivityCoalescer"/> this engine depends on is registered
/// alongside it in <see cref="Startup"/> (a constructor dependency forces it to be DI-resolvable now).
/// <para>
/// Fault isolation (review fix): each per-recipient <c>SendAsync</c> is wrapped so one recipient's
/// failed send (e.g. a connection torn down mid-fan-out) cannot abort delivery to the remaining
/// focused viewers, and can never propagate out of <see cref="OnMessagePersisted"/> — the caller has
/// already durably persisted the message and must still return its typed <c>Ok</c> ack. Live delivery
/// is best-effort; a missed recipient refetches via <c>GetMessages</c> on open.
/// </para>
/// <para>
/// Task 18 adds the <see cref="ISessionRegistry"/> dependency plus <see cref="PushChannelAdded"/> /
/// <see cref="PushChannelRemoved"/> — the <c>ChannelAdded</c>/<c>ChannelRemoved</c> emit helpers.
/// CONTRACT COMPLETENESS ONLY: C3 defines the shapes and provides these helpers, but does not call
/// them — C5/C7 wire the actual channel-add/remove triggers (channel creation, invite-join,
/// moderator removal, etc.) in later chunks. Both are single-connection pushes (mirroring the
/// existing <c>Clients.Client(connectionId).SendAsync(...)</c> pattern above), resolved via
/// <see cref="ISessionRegistry.GetByBattleTag"/> rather than the focused/online-member indexes, since
/// a channel add/remove targets a SPECIFIC user's live connection regardless of what they currently
/// have focused.
/// </para>
/// </summary>
public class FanOutEngine(
    IHubContext<ChatHub> hubContext,
    FocusRegistry focusRegistry,
    OnlineMemberRegistry onlineMemberRegistry,
    ActivityCoalescer activityCoalescer,
    ISessionRegistry sessionRegistry)
{
    // The SignalR delivery channel — pushes the full MessageReceived payload to targeted connections.
    private readonly IHubContext<ChatHub> _hubContext = hubContext;

    // The focused-channel index (Tasks 8/9). OnMessagePersisted reads GetFocusedConnections to target
    // full MessageReceived at focused viewers only — the "never full payloads to unfocused" guardrail.
    // PushChannelRemoved (Task 18) also uses this to clear a removed channel's focus entry.
    private readonly FocusRegistry _focusRegistry = focusRegistry;

    // The online-member subscription index (Task 5). OnMessagePersisted enumerates a channel's online
    // members (with connectionIds) to route the unfocused level-All ones to the ActivityCoalescer.
    // PushChannelAdded/PushChannelRemoved (Task 18) seed/clear a single (channel, connection) entry.
    private readonly OnlineMemberRegistry _onlineMemberRegistry = onlineMemberRegistry;

    // The coalescing/suppressing sink for unfocused level-All ChannelActivity (Task 13). This engine
    // only OFFERS (routes); the coalescer owns the ≥10s window + >100-unread suppression + emit.
    private readonly ActivityCoalescer _activityCoalescer = activityCoalescer;

    // The battleTag->live-connection resolver (Task 18). PushChannelAdded/PushChannelRemoved target a
    // SPECIFIC user's connection by battleTag rather than a channel's focused/online set, so they
    // resolve through this instead of FocusRegistry/OnlineMemberRegistry.
    private readonly ISessionRegistry _sessionRegistry = sessionRegistry;

    /// <summary>
    /// Called by the send pipeline AFTER the message is durably persisted (seq allocated + inserted).
    /// Delivers the full <c>MessageReceived</c> payload to the channel's FOCUSED connections only, with
    /// shadow-author-plus-moderator routing (C4 D8).
    /// <list type="bullet">
    /// <item>Non-shadow: every focused connection receives it — INCLUDING the sender's own focused
    /// connection (the echo; the client dedups against its ack <c>{messageId, seq}</c>).</item>
    /// <item>Shadow (<paramref name="isShadow"/> true, C4 D8): the author's own focused connection
    /// (<paramref name="senderConnectionId"/>) receives the unflagged illusion echo (<c>Shadow</c> forced
    /// false), AND any OTHER focused connection whose session holds <see cref="EPermission.Moderation"/>
    /// receives a real-flagged (<c>Shadow == true</c>) <c>ForModerator</c> copy. Every other focused
    /// member, and every unfocused connection, receives nothing (shadow-ban integrity holds for
    /// non-moderators). Shadow messages still generate zero activity/unread for anyone — moderators
    /// included — per the activity routing below.</item>
    /// </list>
    /// GUARDRAIL: UNFOCUSED connections never receive <c>MessageReceived</c> — full payloads go to
    /// focused connections only. Unfocused members' notification is the coalesced <c>ChannelActivity</c>,
    /// which this method ROUTES (per member, via <see cref="ActivityCoalescer.Offer"/>) but does not send.
    /// <para>
    /// Activity routing (Task 13, fan-out decision 3): for every online member of the channel that is
    /// NOT focused on it and whose notification level is <see cref="NotificationLevel.All"/>, offer the
    /// message's seq to the coalescer. Focused members already got the full <c>MessageReceived</c> above,
    /// so they get NO activity; <see cref="NotificationLevel.Mentions"/>/<see cref="NotificationLevel.None"/>
    /// get nothing here (mentions are C6's job). SHADOW hard constraint: a shadow message is NEVER routed
    /// to activity — it must not surface as an activity/unread ping to any non-author; the author's own
    /// visible copy is the focused echo delivered above, and no unfocused member may learn a shadow post
    /// exists. <paramref name="now"/> is the trusted server clock the hub already read once for this send
    /// (threaded in rather than re-read here, so the whole send decides against a single instant).
    /// </para>
    /// </summary>
    public async Task OnMessagePersisted(ChatChannel channel, ChannelMessage message, string senderConnectionId, bool isShadow, DateTime now)
    {
        // The user-facing projection — shared with the pull path (ChatHub.GetMessages, Task 16) via
        // MessageDto.ForUserDelivery, so the deleted/shadow illusion (C3-plan.md decision 7) can never
        // drift between push and pull. See that factory's doc comment for why deleted/shadow are
        // ALWAYS forced false, even on a shadow author's OWN echo.
        var dto = MessageDto.ForUserDelivery(channel.Id, message);

        // The ONLY delivery targets: connections currently focused on this channel. Iterating this set
        // (never the membership roster) is what enforces the "no full payloads to unfocused" guardrail.
        foreach (var connectionId in _focusRegistry.GetFocusedConnections(channel.Id))
        {
            // Default payload: the user-facing projection. It is what EVERY focused connection receives
            // on a non-shadow message, and what the shadow AUTHOR's own echo receives (illusion forced
            // false). Only the shadow branch below may swap it for the moderator projection.
            var payload = dto;

            // Shadow routing (C4 D8). The author-echo case is excluded here by the connectionId guard so
            // it is decided BEFORE the moderator branch — a shadow author who is ALSO a moderator gets the
            // unflagged echo above (the illusion outranks the flag), never the real-flagged copy.
            if (isShadow && connectionId != senderConnectionId)
            {
                // A shadow post reaches no OTHER member EXCEPT a focused moderator, who receives it with
                // the REAL shadow flag (ForModerator). Permission is resolved in-memory (zero DB) from the
                // connection's live session — exactly ChatSession.HasPermission's IsAdmin∧Moderation conjunct.
                if (_sessionRegistry.TryGetByConnectionId(connectionId, out var moderatorSession)
                    && moderatorSession.HasPermission(EPermission.Moderation))
                {
                    payload = MessageDto.ForModerator(channel.Id, message);
                }
                else
                {
                    // Any other focused member: shadow-ban integrity — a shadow post reaches them not at all.
                    continue;
                }
            }

            // Fault isolation (review fix): live delivery is best-effort — a missed recipient refetches
            // via GetMessages on open. A single recipient's torn-down connection must not abort the
            // remaining focused viewers' delivery, and must NEVER propagate out of OnMessagePersisted —
            // the caller (SendMessage) has already durably persisted the message and returns its typed
            // Ok ack regardless of fan-out hiccups.
            try
            {
                await _hubContext.Clients.Client(connectionId).SendAsync(ChatEvents.MessageReceived, payload);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Fan-out send of MessageReceived failed for connection {ConnectionId} on channel {ChannelId} — skipping, other recipients unaffected", connectionId, channel.Id);
            }
        }

        // SHADOW hard constraint: a shadow message must never become a ChannelActivity/unread ping to a
        // non-author. Skip activity routing ENTIRELY — the author's own visible copy is the focused echo
        // delivered above (Task 12); no unfocused member may learn a shadow post exists.
        if (isShadow)
        {
            return;
        }

        // Activity routing (fan-out decision 3): unfocused level-All members are offered the seq; the
        // coalescer owns coalescing + suppression + emit. Snapshot the focused connections once into a
        // set for O(1) "is this connection focused?" tests as we scan the member roster.
        var focusedConnections = new HashSet<string>(_focusRegistry.GetFocusedConnections(channel.Id));
        foreach (var (connectionId, state) in _onlineMemberRegistry.GetMembersWithConnections(channel.Id))
        {
            // C5 (Task 4, D4): pending-Dm activity suppression. While a 1:1 request is unresolved (Pending)
            // the RECIPIENT — any member whose battleTag is NOT the request initiator — receives ZERO
            // ChannelActivity; their only signals are the targeted RequestReceived + the tray (SessionState),
            // so a declined/ignored request never pings them. The FOCUSED live MessageReceived above is NOT
            // suppressed (a recipient who deliberately opened the pending window still sees messages), and an
            // ACCEPTED Dm resumes activity normally. The initiator (== RequestInitiatedBy) is never suppressed
            // here (they are the sender anyway, skipped just below).
            if (channel.Type == ChannelType.Dm
                && channel.RequestState == DmRequestState.Pending
                && !state.BattleTag.Equals(channel.RequestInitiatedBy, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Never ping the sender about their OWN message: they already hold the ack {messageId, seq},
            // and SendMessage does not require the sender to be focused on the channel, so a level-All
            // sender posting to an unfocused channel would otherwise self-notify (and their LastReadSeq
            // isn't advanced on send, so the self-unread would only grow).
            if (connectionId == senderConnectionId)
            {
                continue;
            }

            // Focused connections already received the full MessageReceived above — no activity ping.
            if (focusedConnections.Contains(connectionId))
            {
                continue;
            }

            // Only level All wants an unfocused activity ping. Mentions/None get nothing on this path.
            if (state.NotificationLevel != NotificationLevel.All)
            {
                continue;
            }

            await _activityCoalescer.Offer(connectionId, channel.Id, message.Seq, now);
        }
    }

    /// <summary>
    /// Pushes a newly-added channel to <paramref name="membership"/>'s owning user's LIVE connection
    /// (spec §11 <c>ChannelAdded</c>). CONTRACT COMPLETENESS ONLY (Task 18): C5/C7 trigger this — e.g.
    /// an auto-join on lobby/ladder load, or an invite acceptance — C3 only defines the shape and
    /// provides this emit helper; there are no production callers yet, only tests.
    /// <list type="bullet">
    /// <item>Resolves the target connection via <see cref="ISessionRegistry.GetByBattleTag"/> — NOT
    /// via <see cref="FocusRegistry"/>/<see cref="OnlineMemberRegistry"/>, since the user may not have
    /// this (or any) channel focused/seeded yet.</item>
    /// <item>NO-OP if the user is currently offline (<c>GetByBattleTag</c> returns null): the channel
    /// and membership are already durably persisted by the caller BEFORE this is invoked, so an
    /// offline user simply picks the channel up via the next <c>SessionState</c> on connect —
    /// mirroring <see cref="OnMessagePersisted"/>'s live-delivery-is-best-effort posture.</item>
    /// <item>SEEDS the <see cref="OnlineMemberRegistry"/> for (channel, connection) BEFORE emitting, so
    /// this channel's activity fan-out (<see cref="OnMessagePersisted"/>) can route to this connection
    /// starting with the very next message — without this seed the newly-added channel would stay
    /// invisible to activity routing until the user's next reconnect re-seeds it via
    /// <see cref="SessionStateAssembler"/>.</item>
    /// </list>
    /// </summary>
    public async Task PushChannelAdded(ChatChannel channel, ChannelMembership membership, bool focus)
    {
        var battleTag = membership.BattleTag;
        var session = _sessionRegistry.GetByBattleTag(battleTag);
        if (session == null)
        {
            // Offline — nothing to push to, and nothing to seed (there is no live connection to seed
            // the OnlineMemberRegistry entry against).
            return;
        }

        _onlineMemberRegistry.Join(
            channel.Id,
            session.ConnectionId,
            new MemberState(battleTag, membership.NotificationLevel, membership.LastReadSeq));

        var dto = new ChannelAddedDto(channel, MembershipDto.From(membership), focus);
        await _hubContext.Clients.Client(session.ConnectionId).SendAsync(ChatEvents.ChannelAdded, dto);
    }

    /// <summary>
    /// Tells <paramref name="battleTag"/>'s LIVE connection to drop <paramref name="channelId"/> from
    /// its channel list (spec §11 <c>ChannelRemoved</c>). CONTRACT COMPLETENESS ONLY (Task 18): C5/C7
    /// trigger this — C3 only defines the shape and provides this emit helper; there are no production
    /// callers yet, only tests.
    /// <list type="bullet">
    /// <item>NO-OP if the user is currently offline — the membership row is already durably removed by
    /// the caller BEFORE this is invoked, so an offline user's next <c>SessionState</c> on connect
    /// simply omits the channel.</item>
    /// <item>Cleans the <see cref="OnlineMemberRegistry"/> and <see cref="FocusRegistry"/> entries for
    /// (channelId, connection) so this connection is never fanned out to (activity or focus) after the
    /// removal.</item>
    /// </list>
    /// <para>
    /// C5/C7 WIRING NOTE — DELIBERATE SCOPE BOUNDARY (Task 18): this cleans the
    /// <see cref="OnlineMemberRegistry"/>/<see cref="FocusRegistry"/> ONLY. Unlike
    /// <c>ChatHub.LeaveChannel</c> (Task 14's fix), it does NOT route the removal through
    /// <see cref="ViewersAccumulator"/> — so a forced removal of a CURRENTLY-FOCUSED viewer will NOT
    /// emit a <c>ViewersChanged{left}</c> to the channel's remaining viewers. This is the SAME class of
    /// gap Task 14 fixed for the user-initiated <c>LeaveChannel</c>, deliberately left open here
    /// because there is no caller yet to exercise it and no clock/accumulator dependency to justify
    /// wiring speculatively (YAGNI). WHEN C5/C7 wire the trigger, they (or a future revision of this
    /// helper) MUST decide whether a forced removal should emit <c>ViewersChanged{left}</c> to
    /// remaining viewers — if so, route the removed battleTag through
    /// <see cref="ViewersAccumulator.RecordChange"/> BEFORE calling <see cref="FocusRegistry.Unfocus"/>
    /// below, mirroring <c>ChatHub.LeaveChannel</c>'s ordering exactly.
    /// </para>
    /// </summary>
    public async Task PushChannelRemoved(string channelId, string battleTag)
    {
        var session = _sessionRegistry.GetByBattleTag(battleTag);
        if (session == null)
        {
            return;
        }

        // C5/C7 WIRING NOTE: see the doc comment above — a currently-focused viewer's removal does not
        // route through ViewersAccumulator.RecordChange here, so remaining viewers get no ViewersChanged
        // {left} for this forced removal (Task 14 parity gap, deliberately deferred to the eventual caller).
        _onlineMemberRegistry.Leave(channelId, session.ConnectionId);
        _focusRegistry.Unfocus(session.ConnectionId, channelId);

        var dto = new ChannelRemovedDto(channelId);
        await _hubContext.Clients.Client(session.ConnectionId).SendAsync(ChatEvents.ChannelRemoved, dto);
    }

    /// <summary>
    /// C4 (Task 3, D4): the moderation removal emit helper. Delivers the FINAL channel-scoped
    /// <see cref="MessageDeletedDto"/> to the channel's FOCUSED connections, EXCLUDING
    /// <paramref name="excludedConnectionIds"/> — the hub passes the moderated author's own connection
    /// ids there, preserving the legacy <c>AllExcept(author)</c> semantics (the moderated user is not
    /// tipped off live; their own copy vanishes on next reload since <c>UserVisible</c> excludes
    /// deleted rows). A focused MODERATOR receives the SAME event (it is not in the excluded set) and
    /// branches client-side on its own permission to render a flag rather than remove.
    /// <list type="bullet">
    /// <item>Targets the FOCUSED connections only (via <see cref="FocusRegistry.GetFocusedConnections"/>):
    /// unfocused members never received the message (pull-only history already excludes a deleted row),
    /// so they get no removal push.</item>
    /// <item>Per-recipient fault isolation mirrors <see cref="OnMessagePersisted"/>: a single recipient's
    /// torn-down connection cannot abort delivery to the remaining focused viewers and must NEVER
    /// propagate out — the hub has already durably soft-deleted the message and logged its audit line;
    /// live removal delivery is best-effort.</item>
    /// </list>
    /// </summary>
    public async Task PushMessageDeleted(string channelId, string messageId, IReadOnlyCollection<string> excludedConnectionIds)
    {
        var excluded = new HashSet<string>(excludedConnectionIds);
        var dto = new MessageDeletedDto(channelId, messageId);

        foreach (var connectionId in _focusRegistry.GetFocusedConnections(channelId))
        {
            // Legacy AllExcept(author) semantics: the moderated author's own connections are skipped.
            if (excluded.Contains(connectionId))
            {
                continue;
            }

            // Fault isolation (mirrors OnMessagePersisted): live delivery is best-effort — a missed
            // recipient's copy vanishes on its next GetMessages/reconnect anyway. One recipient's
            // torn-down connection must not abort the rest, and must never propagate out of here.
            try
            {
                await _hubContext.Clients.Client(connectionId).SendAsync(ChatEvents.MessageDeleted, dto);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Fan-out send of MessageDeleted failed for connection {ConnectionId} on channel {ChannelId} — skipping, other recipients unaffected", connectionId, channelId);
            }
        }
    }

    /// <summary>
    /// C4 (Task 4, D6): the moderator-purge removal emit helper. Mirrors <see cref="PushMessageDeleted"/>
    /// exactly, but carries a BATCH of message ids as a channel-scoped <see cref="BulkMessagesDeletedDto"/>
    /// to the channel's FOCUSED connections, EXCLUDING <paramref name="excludedConnectionIds"/> — the hub
    /// passes the purge target's own connection ids there (legacy <c>AllExcept(target)</c> semantics: the
    /// purged user is not tipped off live; their own copies vanish on next reload since <c>UserVisible</c>
    /// excludes deleted rows). A focused MODERATOR receives the SAME event and branches client-side.
    /// <list type="bullet">
    /// <item>Targets the FOCUSED connections only (via <see cref="FocusRegistry.GetFocusedConnections"/>):
    /// a channel with NO focused viewers emits NOTHING (the loop simply never runs).</item>
    /// <item>Per-recipient fault isolation mirrors <see cref="OnMessagePersisted"/>: a single recipient's
    /// torn-down connection cannot abort delivery to the remaining focused viewers and must NEVER
    /// propagate out — the hub has already durably soft-deleted the batch and logged its audit line;
    /// live removal delivery is best-effort. A missed recipient's copies vanish on its next reload.</item>
    /// </list>
    /// The hub invokes this ONCE PER affected channel (one dto per channel), so each dto's
    /// <see cref="BulkMessagesDeletedDto.MessageIds"/> are the purged ids for THAT channel only.
    /// </summary>
    public async Task PushBulkMessagesDeleted(string channelId, IReadOnlyList<string> messageIds, IReadOnlyCollection<string> excludedConnectionIds)
    {
        var excluded = new HashSet<string>(excludedConnectionIds);
        var dto = new BulkMessagesDeletedDto(channelId, messageIds);

        foreach (var connectionId in _focusRegistry.GetFocusedConnections(channelId))
        {
            // Legacy AllExcept(target) semantics: the purge target's own connections are skipped.
            if (excluded.Contains(connectionId))
            {
                continue;
            }

            // Fault isolation (mirrors OnMessagePersisted): live delivery is best-effort — a missed
            // recipient's copies vanish on its next GetMessages/reconnect anyway. One recipient's
            // torn-down connection must not abort the rest, and must never propagate out of here.
            try
            {
                await _hubContext.Clients.Client(connectionId).SendAsync(ChatEvents.BulkMessagesDeleted, dto);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Fan-out send of BulkMessagesDeleted failed for connection {ConnectionId} on channel {ChannelId} — skipping, other recipients unaffected", connectionId, channelId);
            }
        }
    }

    /// <summary>
    /// Disconnect hook: drops the closing connection's coalescing window state from the
    /// <see cref="ActivityCoalescer"/>. The hub already delegates all fan-out to this engine and holds
    /// no reference to the coalescer directly, so it routes the coalescer's per-connection teardown
    /// through here — alongside the sibling registries' own <c>RemoveConnection</c> calls in the hub's
    /// disconnect <c>finally</c> — so the singleton's state can never leak past the socket's lifetime.
    /// </summary>
    public void OnConnectionClosed(string connectionId)
    {
        _activityCoalescer.RemoveConnection(connectionId);
    }
}

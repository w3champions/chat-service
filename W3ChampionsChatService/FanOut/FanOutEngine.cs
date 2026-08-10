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
    ISessionRegistry sessionRegistry,
    // C6 (Task 9, D11): the presence-interest index. Two roles here: (1) the channel-membership hooks at
    // the TOP of PushChannelAdded/PushChannelRemoved keep it current when a Dm/GroupDm's roster changes,
    // BEFORE their offline early-returns (an interest update must land even when the changed user is
    // offline); (2) PushPresenceChanged reads it to target the derived-interest set on an online/offline
    // transition. Shared singleton (Startup) — the SAME instance the hub mutates via focus/leave.
    PresenceInterestRegistry presenceInterestRegistry,
    // C7 (Task 5): the batched ViewersChanged sink + the trusted server clock. Added so a FORCED channel
    // removal (PushChannelRemoved) of a currently-focused viewer of a ROSTER-PARTICIPATING channel
    // (Public/SemiPublic/System) can route the removed battleTag through ViewersAccumulator.RecordChange
    // BEFORE FocusRegistry.Unfocus — mirroring ChatHub.LeaveChannel's ordering so remaining viewers get a
    // ViewersChanged{left} rather than a ghost in the roster. Both are the SAME singletons the hub holds
    // (Startup): the accumulator the flush hosted service drains, and the injectable clock every other
    // fan-out path reads. Private lanes (Dm/GroupDm) are excluded from the viewer-roster system, so this
    // pair is left untouched for them (the D11 guard in PushChannelRemoved below).
    ViewersAccumulator viewersAccumulator,
    TimeProvider timeProvider)
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

    // C6 (Task 9, D11): the presence-interest index — see the ctor param doc comment.
    private readonly PresenceInterestRegistry _presenceInterestRegistry = presenceInterestRegistry;

    // C7 (Task 5): the batched ViewersChanged sink + the trusted server clock — see the ctor param doc
    // comment. Read ONLY by PushChannelRemoved's forced-removal roster-delta routing (non-private lanes).
    private readonly ViewersAccumulator _viewersAccumulator = viewersAccumulator;
    private readonly TimeProvider _timeProvider = timeProvider;

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

        // C5 (Task 9, D15) + post-game chat Plan A Task 6: the activity previews. Built ONCE per
        // persisted message (identical for every Offer call in the loop below). TWO slots, because the
        // deployed launcher routes its DM-grade toast/sound/OS-notification on the mere PRESENCE of
        // `preview` and never reads the channel's type — see ChannelActivityDto's doc:
        //   - `dmPreview` → ChannelActivityDto.Preview, the FROZEN LEGACY slot. Dm and ONLY Dm, forever.
        //     Widening it would make every post-game message a DM-grade notification on clients already
        //     in the wild — the exact flooding this feature exists to avoid. A pending Dm never reaches
        //     the Offer call below at all (the suppression `continue` skips every recipient, and the
        //     initiator/sender is skipped just after it), so this is only ever OFFERED for an accepted
        //     Dm — building it unconditionally for any Dm channel is harmless.
        //   - `activityPreview` → ChannelActivityDto.ActivityPreview, the superseding slot. EVERY
        //     preview-eligible class, Dm included, each preview naming its own ChannelType/SystemKind so
        //     a new client routes on the room's class instead of on a field's presence.
        // Today's eligible set is Dm (C5/OQ-7) + System&Match (post-game chat: the client's ONE-TIME
        // nudge toast after the score screen closes needs a sender and an excerpt; without a preview it
        // has nothing to render, which is precisely why post-game messages were previously silent).
        // GroupDm / Public / SemiPublic / System+Clan deliberately stay preview-free in BOTH slots — a
        // busy lounge or clan room keeps its badge-only treatment. Sender fields are REUSED from
        // `dto.Sender` (the MessageDto already built above) rather than a fresh lookup — no extra Mongo
        // read. The User conjunct is LOAD-BEARING for both slots, not defensive: SystemMessagePublisher
        // calls this method on exactly a System+Match channel, and a system message's Sender is null —
        // without it the dto.Sender dereference below is a guaranteed NullReferenceException on every
        // published intro.
        var isDm = channel.Type == ChannelType.Dm;
        var isMatch = channel.Type == ChannelType.System && channel.SystemKind == SystemChannelKind.Match;
        var wantsPreview = message.Kind == MessageKind.User && (isDm || isMatch);
        object dmPreview = wantsPreview && isDm
            ? new DmActivityPreviewDto(dto.Sender.BattleTag, dto.Sender.Name, Excerpts.Bounded(message.Content, ChatLimits.DmPreviewExcerptLength))
            : null;
        object activityPreview = wantsPreview
            ? new ActivityPreviewDto(
                dto.Sender.BattleTag, dto.Sender.Name, Excerpts.Bounded(message.Content, ChatLimits.DmPreviewExcerptLength),
                channel.Type, channel.SystemKind)
            : null;

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

            await _activityCoalescer.Offer(connectionId, channel.Id, message.Seq, now, dmPreview, activityPreview);
        }
    }

    /// <summary>
    /// Pushes a newly-added channel to <paramref name="membership"/>'s owning user's LIVE connection
    /// (spec §11 <c>ChannelAdded</c>). Defined in Task 18 (C3) as CONTRACT COMPLETENESS; C5/C7 now wire the
    /// real triggers. Live production callers: C5's <c>ChatHub.OpenDm</c> (DM first-message materialization),
    /// <c>ChatHub.CreateGroup</c> and <c>ChatHub.AddGroupMember</c> (GroupDm), plus C7's
    /// <c>MatchChannelService.AddMemberWithInvariant</c> (System-match auto-join / one-match-channel swap).
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

        // C6 (Task 9, D11): update the presence-interest index FIRST — BEFORE the offline early-return
        // below. A member added to a Dm/GroupDm makes every connection ALREADY watching that channel
        // interested in the added tag, and that is true REGARDLESS of whether the added user is currently
        // online: their being offline does not mean nobody is watching them (some other member may have
        // the same channel focused right now and must gain interest so they learn when this user first
        // comes online). Safe for any channel type — the index only ever holds private-lane watchers, so
        // this is a no-op for a Public/SemiPublic/System channel nobody registered interest through.
        _presenceInterestRegistry.OnMemberAdded(channel.Id, battleTag);

        var session = _sessionRegistry.GetByBattleTag(battleTag);
        if (session == null)
        {
            // Offline — nothing to push to, and nothing to seed (there is no live connection to seed
            // the OnlineMemberRegistry entry against). The interest-index update above already ran.
            return;
        }

        // AUTHORITATIVE — must always run (it precedes the best-effort send): seeding the registry is what
        // makes this channel's activity fan-out reach the connection from the very next message.
        _onlineMemberRegistry.Join(
            channel.Id,
            session.ConnectionId,
            new MemberState(battleTag, membership.NotificationLevel, membership.LastReadSeq, channel.Type));

        var dto = new ChannelAddedDto(channel, MembershipDto.From(membership), focus);
        // Fault isolation (review fix, SEC-Low-3): the ChannelAdded push is a BEST-EFFORT live
        // notification — the channel + membership are already durably persisted and the registry seeded
        // above, and a reconnecting client re-derives its channel list from SessionState, so a torn-down
        // target connection throwing from SendAsync must NEVER propagate out of the caller
        // (AddGroupMember/OpenDm/CreateGroup) after its durable mutation already succeeded.
        try
        {
            await _hubContext.Clients.Client(session.ConnectionId).SendAsync(ChatEvents.ChannelAdded, dto);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Push of ChannelAdded failed for {BattleTag} connection {ConnectionId} on channel {ChannelId} — dropped; the client re-derives it from SessionState on reconnect", battleTag, session.ConnectionId, channel.Id);
        }
    }

    /// <summary>
    /// Tells <paramref name="battleTag"/>'s LIVE connection to drop <paramref name="channelId"/> from
    /// its channel list (spec §11 <c>ChannelRemoved</c>). The forced-removal counterpart of the
    /// user-initiated <c>ChatHub.LeaveChannel</c>: the live production caller is
    /// <c>ChatHub.RemoveGroupMember</c> (a GroupDm owner kicking a member — a PRIVATE lane), and C7's
    /// one-match-channel swap + DELETE teardown drive it for System-match channels.
    /// <list type="bullet">
    /// <item>NO-OP if the user is currently offline — the membership row is already durably removed by
    /// the caller BEFORE this is invoked, so an offline user's next <c>SessionState</c> on connect
    /// simply omits the channel.</item>
    /// <item>Cleans the <see cref="OnlineMemberRegistry"/> and <see cref="FocusRegistry"/> entries for
    /// (channelId, connection) so this connection is never fanned out to (activity or focus) after the
    /// removal.</item>
    /// <item>For a ROSTER-PARTICIPATING channel (Public/SemiPublic/System), routes the removed viewer's
    /// roster change through <see cref="ViewersAccumulator.RecordChange"/> BEFORE
    /// <see cref="FocusRegistry.Unfocus"/> so the channel's remaining viewers get a
    /// <c>ViewersChanged{left}</c> — see the WIRING NOTE below.</item>
    /// </list>
    /// <para>
    /// C5/C7 WIRING NOTE — OBLIGATION DISCHARGED (C7 Task 5): the Task-18 scope boundary (this helper
    /// once cleaned the <see cref="OnlineMemberRegistry"/>/<see cref="FocusRegistry"/> ONLY, deferring
    /// the roster delta to the eventual caller) is now resolved. Per the C3 amendment (spec §9 — the
    /// roster IS the set of active viewers), a FORCED removal of a CURRENTLY-FOCUSED viewer of a
    /// roster-participating channel MUST emit a <c>ViewersChanged{left}</c> to the remaining viewers,
    /// otherwise they keep a ghost in the roster. So — mirroring <c>ChatHub.LeaveChannel</c>'s ordering
    /// EXACTLY — this captures the member's <see cref="MemberState.ChannelType"/> via
    /// <see cref="OnlineMemberRegistry.TryGetMember"/> BEFORE <see cref="OnlineMemberRegistry.Leave"/>
    /// destroys the entry, then routes the removed battleTag through
    /// <see cref="ViewersAccumulator.RecordChange"/> (pre-window baseline = VIEWING, still focused)
    /// BEFORE <see cref="FocusRegistry.Unfocus"/>. D11 PRIVATE-LANE GUARD: Dm/GroupDm are excluded from
    /// the viewer-roster system entirely, so this SKIPS <c>RecordChange</c> for them — which is exactly
    /// what keeps the live <c>RemoveGroupMember</c> (GroupDm) caller byte-identical: a forced
    /// private-lane removal emits no <c>ViewersChanged</c> (structurally satisfying the C3 amendment for
    /// private lanes, OQ-1), its presence being member-presence via the C6 interest index.
    /// </para>
    /// </summary>
    public async Task PushChannelRemoved(string channelId, string battleTag)
    {
        // C6 (Task 9, D11): update the presence-interest index FIRST — BEFORE the offline early-return
        // below. A member removed from a Dm/GroupDm makes every connection watching that channel drop
        // interest in the removed tag, REGARDLESS of whether the removed user is currently online (an
        // offline removed member still needs to be dropped from the watchers who had them). Safe for any
        // channel type — a no-op for a channel nobody registered private-lane interest through.
        _presenceInterestRegistry.OnMemberRemoved(channelId, battleTag);

        var session = _sessionRegistry.GetByBattleTag(battleTag);
        if (session == null)
        {
            return;
        }

        // C7 (Task 5): capture the member's ChannelType BEFORE Leave destroys the entry — the D11
        // private-lane guard below needs it to decide whether this forced removal emits a roster delta.
        var hadMember = _onlineMemberRegistry.TryGetMember(channelId, session.ConnectionId, out var memberState);

        // AUTHORITATIVE — must always run (it precedes the best-effort send): clearing the registry is
        // what guarantees this connection is never fanned out to (activity) after the removal.
        _onlineMemberRegistry.Leave(channelId, session.ConnectionId);

        // C7 (Task 5) — discharges the C5/C7 WIRING NOTE / C3 amendment (see the doc comment above).
        // Mirroring ChatHub.LeaveChannel EXACTLY: route the removed viewer's roster change through the
        // accumulator BEFORE the FocusRegistry.Unfocus below, so the captured pre-window baseline is
        // VIEWING (the viewer is still focused at this point) and the flush emits a ViewersChanged{left}
        // to the channel's remaining viewers. D11 private-lane guard: SKIP for Dm/GroupDm — private lanes
        // never enter the viewer-roster system, which is what keeps the RemoveGroupMember (GroupDm) caller
        // byte-identical (no RecordChange, hence no ViewersChanged). A member with no live registry entry
        // (hadMember false) has no ChannelType to consult and was not an active roster member, so skip it.
        if (hadMember && memberState.ChannelType is not (ChannelType.Dm or ChannelType.GroupDm))
        {
            _viewersAccumulator.RecordChange(channelId, battleTag, _timeProvider.GetUtcNow().UtcDateTime);
        }

        _focusRegistry.Unfocus(session.ConnectionId, channelId);
        // C6 (Task 9, D11): the removed user is no longer a member, so revoke THEIR OWN interest that was
        // derived from focusing this channel too — a kicked user must not keep learning the presence of
        // the channel's remaining members. (The OnMemberRemoved above only stops OTHERS watching THEM;
        // this stops THEM watching others.) Refcount-safe: a tag they also reach via another focused
        // channel survives.
        _presenceInterestRegistry.RevokeFocus(session.ConnectionId, channelId);

        var dto = new ChannelRemovedDto(channelId);
        // Fault isolation (review fix, SEC-Low-3): the ChannelRemoved push is a BEST-EFFORT live
        // notification — the membership row is already durably removed and the registry/focus cleared
        // above, and a reconnecting client re-derives its channel list from SessionState, so a torn-down
        // target connection throwing from SendAsync must NEVER propagate out of the caller
        // (RemoveGroupMember) after its durable mutation already succeeded.
        try
        {
            await _hubContext.Clients.Client(session.ConnectionId).SendAsync(ChatEvents.ChannelRemoved, dto);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Push of ChannelRemoved failed for {BattleTag} connection {ConnectionId} on channel {ChannelId} — dropped; the client re-derives it from SessionState on reconnect", battleTag, session.ConnectionId, channelId);
        }
    }

    /// <summary>
    /// C6 (Task 9, D11): the presence-change emit helper. On a GENUINE online/offline transition of
    /// <paramref name="battleTag"/>, delivers <see cref="ChatEvents.PresenceChanged"/> (carrying a
    /// <see cref="PresenceChangedDto"/>) to exactly the connections with DERIVED interest — those returned
    /// by <see cref="PresenceInterestRegistry.GetInterestedConnections"/> — MINUS
    /// <paramref name="excludedConnectionId"/> (the subject's own connection; a user is never told about
    /// their own presence). There is no subscribe API: the recipient set is derived purely from who has a
    /// Dm/GroupDm containing <paramref name="battleTag"/> focused right now, so a connection focused
    /// elsewhere (or watching nothing) receives NOTHING.
    /// <para>
    /// Per-recipient fault isolation mirrors <see cref="OnMessagePersisted"/>: a single torn-down
    /// recipient cannot abort delivery to the rest and must NEVER propagate out — the caller is a
    /// connect/disconnect lifecycle path that must complete regardless of a dead watcher's socket. Live
    /// presence is best-effort; a missed recipient reconciles on its next reconnect (fresh SessionState /
    /// GetPresence).
    /// </para>
    /// </summary>
    public async Task PushPresenceChanged(string battleTag, bool online, string excludedConnectionId)
    {
        var dto = new PresenceChangedDto(battleTag, online);
        foreach (var connectionId in _presenceInterestRegistry.GetInterestedConnections(battleTag))
        {
            // A user is never notified about their OWN presence transition. (Interest never records a
            // connection against its own tag, so this is belt-and-suspenders — but it also excludes the
            // exact connection that just connected/disconnected, whatever casing it registered under.)
            if (connectionId == excludedConnectionId)
            {
                continue;
            }

            try
            {
                await _hubContext.Clients.Client(connectionId).SendAsync(ChatEvents.PresenceChanged, dto);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Fan-out send of PresenceChanged failed for connection {ConnectionId} (subject {BattleTag}) — skipping, other recipients unaffected", connectionId, battleTag);
            }
        }
    }

    /// <summary>
    /// C6 (Task 11, D13): the friend-presence push — the mechanism a later cross-repo (W3) item retires
    /// wb's <c>FriendOnlineStatus</c> broadcast against (mirrors wb's <c>NotifyFriendsWithIsOnline</c>).
    /// On a GENUINE online/offline transition of <paramref name="subjectBattleTag"/>, delivers
    /// <see cref="ChatEvents.FriendPresenceChanged"/> (carrying a <see cref="FriendPresenceChangedDto"/>)
    /// to every one of <paramref name="friends"/> that CURRENTLY has a live connection — resolved
    /// per-friend via <see cref="ISessionRegistry.GetByBattleTag"/>. This is a DIFFERENT targeting
    /// mechanism than <see cref="PushPresenceChanged"/>: that method targets the DERIVED
    /// focus/membership interest index, this one targets the subject's actual FRIENDS list (from C5's
    /// <see cref="Relationships.IRelationshipProvider"/>, resolved by the caller). An offline friend is
    /// silently skipped — nothing to push to, not an error.
    /// <para>
    /// <paramref name="excludedConnectionId"/> guards against ever self-notifying the subject's own
    /// connection — defensive, mirroring <see cref="PushPresenceChanged"/>'s own self-notify guard, in
    /// case a malformed friends list ever contained the subject itself.
    /// </para>
    /// <para>
    /// Per-recipient fault isolation mirrors every other push above: one dead friend's socket must never
    /// prevent the rest from being notified, and must NEVER propagate out — the caller is the connect/
    /// disconnect lifecycle's fire-and-forget background task, which has ALREADY completed its own
    /// (connect/disconnect) work by the time this runs. Live delivery is best-effort; a missed friend
    /// reconciles via its own next <c>GetPresence</c>/<c>GetPresenceDetails</c> read.
    /// </para>
    /// </summary>
    public async Task PushFriendPresenceChanged(string subjectBattleTag, bool online, IReadOnlySet<string> friends, string excludedConnectionId)
    {
        var dto = new FriendPresenceChangedDto(subjectBattleTag, online);
        foreach (var friendBattleTag in friends)
        {
            var session = _sessionRegistry.GetByBattleTag(friendBattleTag);
            if (session == null)
            {
                // Offline friend — nothing to push to; not an error.
                continue;
            }

            if (session.ConnectionId == excludedConnectionId)
            {
                // Defensive: never self-notify the subject's own connection (a well-formed friends list
                // never actually contains the subject itself).
                continue;
            }

            try
            {
                await _hubContext.Clients.Client(session.ConnectionId).SendAsync(ChatEvents.FriendPresenceChanged, dto);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Fan-out send of FriendPresenceChanged failed for connection {ConnectionId} (subject {BattleTag}) — skipping, other friends unaffected", session.ConnectionId, subjectBattleTag);
            }
        }
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

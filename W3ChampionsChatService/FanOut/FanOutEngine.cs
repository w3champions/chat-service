using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.FanOut;

/// <summary>
/// The single seam the send pipeline calls once a message is durably persisted (seam created in Task
/// 11; body filled in Task 12). <see cref="OnMessagePersisted"/> delivers the full
/// <c>MessageReceived</c> payload to the channel's online+focused viewers ONLY, with
/// shadow-author-only routing (a shadow message reaches nobody but its own author, preserving the
/// illusion). It delivers through <see cref="IHubContext{ChatHub}"/> targeting the focused connections
/// from <see cref="FocusRegistry"/>.
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
/// </summary>
public class FanOutEngine(
    IHubContext<ChatHub> hubContext,
    FocusRegistry focusRegistry,
    OnlineMemberRegistry onlineMemberRegistry,
    ActivityCoalescer activityCoalescer)
{
    // The SignalR delivery channel — pushes the full MessageReceived payload to targeted connections.
    private readonly IHubContext<ChatHub> _hubContext = hubContext;

    // The focused-channel index (Tasks 8/9). OnMessagePersisted reads GetFocusedConnections to target
    // full MessageReceived at focused viewers only — the "never full payloads to unfocused" guardrail.
    private readonly FocusRegistry _focusRegistry = focusRegistry;

    // The online-member subscription index (Task 5). OnMessagePersisted enumerates a channel's online
    // members (with connectionIds) to route the unfocused level-All ones to the ActivityCoalescer.
    private readonly OnlineMemberRegistry _onlineMemberRegistry = onlineMemberRegistry;

    // The coalescing/suppressing sink for unfocused level-All ChannelActivity (Task 13). This engine
    // only OFFERS (routes); the coalescer owns the ≥10s window + >100-unread suppression + emit.
    private readonly ActivityCoalescer _activityCoalescer = activityCoalescer;

    /// <summary>
    /// Called by the send pipeline AFTER the message is durably persisted (seq allocated + inserted).
    /// Delivers the full <c>MessageReceived</c> payload to the channel's FOCUSED connections only, with
    /// shadow-author-only routing.
    /// <list type="bullet">
    /// <item>Non-shadow: every focused connection receives it — INCLUDING the sender's own focused
    /// connection (the echo; the client dedups against its ack <c>{messageId, seq}</c>).</item>
    /// <item>Shadow (<paramref name="isShadow"/> true): ONLY the author's own focused connection
    /// (<paramref name="senderConnectionId"/>) receives it — the intersection of the focused set and the
    /// author's connection. No other connection may see a shadow post (shadow-ban integrity). If the
    /// author is not focused on the channel, the intersection is empty and the message reaches no one.</item>
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
            // Shadow-ban integrity: a shadow post reaches nobody but its own author's connection.
            if (isShadow && connectionId != senderConnectionId)
            {
                continue;
            }

            // Fault isolation (review fix): live delivery is best-effort — a missed recipient refetches
            // via GetMessages on open. A single recipient's torn-down connection must not abort the
            // remaining focused viewers' delivery, and must NEVER propagate out of OnMessagePersisted —
            // the caller (SendMessage) has already durably persisted the message and returns its typed
            // Ok ack regardless of fan-out hiccups.
            try
            {
                await _hubContext.Clients.Client(connectionId).SendAsync(ChatEvents.MessageReceived, dto);
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

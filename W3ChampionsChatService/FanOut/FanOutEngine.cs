using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
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
/// This engine does NOT emit the unfocused members' coalesced <c>ChannelActivity</c> — that is Task
/// 13's ActivityCoalescer, driven by the flush hosted service, and MUST NOT be sent from here (the
/// "no full payloads to unfocused" guardrail).
/// </para>
/// Singleton (registered in <see cref="Startup"/>): it holds no per-call state and is shared by every
/// hub invocation, mirroring the other C3 fan-out registries. Task 15 owns the REMAINING fan-out DI
/// (ActivityCoalescer / ViewersAccumulator / the flush hosted service) — not registered here.
/// <para>
/// Fault isolation (review fix): each per-recipient <c>SendAsync</c> is wrapped so one recipient's
/// failed send (e.g. a connection torn down mid-fan-out) cannot abort delivery to the remaining
/// focused viewers, and can never propagate out of <see cref="OnMessagePersisted"/> — the caller has
/// already durably persisted the message and must still return its typed <c>Ok</c> ack. Live delivery
/// is best-effort; a missed recipient refetches via <c>GetMessages</c> on open.
/// </para>
/// </summary>
public class FanOutEngine(IHubContext<ChatHub> hubContext, FocusRegistry focusRegistry)
{
    // The SignalR delivery channel — pushes the full MessageReceived payload to targeted connections.
    private readonly IHubContext<ChatHub> _hubContext = hubContext;

    // The focused-channel index (Tasks 8/9). OnMessagePersisted reads GetFocusedConnections to target
    // full MessageReceived at focused viewers only — the "never full payloads to unfocused" guardrail.
    private readonly FocusRegistry _focusRegistry = focusRegistry;

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
    /// focused connections only. Unfocused members' notification is the coalesced <c>ChannelActivity</c>
    /// (Task 13), NOT emitted here.
    /// </summary>
    public async Task OnMessagePersisted(ChatChannel channel, ChannelMessage message, string senderConnectionId, bool isShadow)
    {
        // Build the user-facing projection. deleted/shadow are ALWAYS false for user-facing delivery:
        // they are C4 moderator-rendering slots, and forcing false — even on a shadow author's OWN echo
        // — is the load-bearing illusion that keeps a shadow-banned author from learning they are muted
        // (C3-plan.md decision 7).
        var dto = new MessageDto(
            Id: message.Id,
            ChannelId: channel.Id,
            Seq: message.Seq,
            Sender: message.Sender,
            Content: message.Content,
            SentAt: message.SentAt,
            Deleted: false,
            Shadow: false);

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
    }
}

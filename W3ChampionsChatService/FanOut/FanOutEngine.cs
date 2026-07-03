using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Messages;

namespace W3ChampionsChatService.FanOut;

/// <summary>
/// C3 (Task 11): the single seam the send pipeline calls once a message is durably persisted. This
/// task creates the seam ONLY — <see cref="OnMessagePersisted"/> is a deliberate no-op for now.
/// <para>
/// Task 12 fills the body: focused <c>MessageReceived</c> delivery to the channel's online+focused
/// viewers, coalesced <c>ChannelActivity</c> to the rest, and shadow-author-only routing (a shadow
/// message reaches nobody but its own author, preserving the illusion). It delivers through
/// <see cref="IHubContext{ChatHub}"/> — captured here as the seam dependency so both the DI wiring and
/// the send path (which already calls this method) are proven before Task 12 lands.
/// </para>
/// Singleton (registered in <see cref="Startup"/>): it holds no per-call state and is shared by every
/// hub invocation, mirroring the other C3 fan-out registries. Task 15 owns the REMAINING fan-out DI
/// (ActivityCoalescer / ViewersAccumulator / the flush hosted service) — not registered here.
/// </summary>
public class FanOutEngine(IHubContext<ChatHub> hubContext)
{
    // Retained as the seam dependency (Task 12 delivers focused/coalesced fan-out through it). Not yet
    // read — mirrors ChatHub's retained-but-unread constructor deps until the task that consumes it.
    private readonly IHubContext<ChatHub> _hubContext = hubContext;

    /// <summary>
    /// Called by the send pipeline AFTER the message is durably persisted (seq allocated + inserted).
    /// No-op until Task 12, which implements focused delivery + shadow-author-only routing.
    /// </summary>
    public Task OnMessagePersisted(ChatChannel channel, ChannelMessage message, string senderConnectionId, bool isShadow)
    {
        // The parameters are the pinned seam signature the send path (Task 11) already calls;
        // discarded here so the unused-parameter analyzer (IDE0060, error-level) stays green until
        // Task 12 consumes them to deliver focused/coalesced fan-out.
        _ = (channel, message, senderConnectionId, isShadow);
        return Task.CompletedTask;
    }
}

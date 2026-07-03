using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.FanOut;

/// <summary>
/// The coalescing + suppressing sink for the <c>ChannelActivity</c> push an UNFOCUSED member with
/// notification level <see cref="NotificationLevel.All"/> receives instead of the full
/// <c>MessageReceived</c> payload (C3 acceptance 1 — the "no full payloads to unfocused" guardrail).
/// <see cref="FanOutEngine"/> ROUTES to it; this component owns the timing and the suppression:
/// <list type="bullet">
/// <item>COALESCE: at most one activity per (connection, channel) per
/// <see cref="ChatLimits.ChannelActivityCoalesce"/> (10s). The first offer for a pair fires immediately
/// (opening the window); offers within the window are collapsed into a single PENDING that keeps ONLY
/// the latest seq — lossless, because the payload is just "the newest seq", so a newer push supersedes
/// any it replaced. <see cref="FlushDue"/> (driven by Task 15's 1s-granularity flush service) drains a
/// pending once its window has elapsed. The spacing invariant follows for free: emission only happens
/// when ≥10s has elapsed since the last emit, so per-(conn,channel) inter-emit gaps are ≥10s.</item>
/// <item>SUPPRESS: checked AT EMIT time (both the immediate path and the flush path), never at offer.
/// The member's unread is recomputed as <c>offeredLastSeq − OnlineMemberRegistry.LastReadSeq</c>; if it
/// exceeds <see cref="ChatLimits.ChannelActivitySuppressUnreadThreshold"/> (100) the push is dropped.
/// It is re-checked at emit precisely because a MarkRead can land between the offer and the flush —
/// once unread falls back to ≤100, the very next due offer/flush resumes emission. Suppression still
/// ADVANCES the window (LastSentAt) and clears the pending, so a resumed emission respects the 10s
/// spacing rather than firing a backlog.</item>
/// </list>
/// PURE, deterministic-time: NO timers, NO wall-clock reads — every decision takes an explicit
/// <c>now</c>, so the whole thing is testable without sleeping. Concurrency idiom mirrors the sibling
/// registries / <see cref="MessageRateLimiter"/> (single lock, plain-mutable per-key state mutated only
/// under it); the SignalR sends are done OUTSIDE the lock (never <c>await</c> while holding it) and are
/// fault-isolated per send, exactly like <see cref="FanOutEngine"/>.
/// <para>
/// Singleton (registered in <see cref="Startup"/> in Task 13 — <see cref="FanOutEngine"/> depends on
/// it): it holds the per-(connection, channel) window state that every send writes and the flush
/// service drains. Task 15 owns wiring <see cref="FlushDue"/> into the hosted flush service.
/// </para>
/// </summary>
public class ActivityCoalescer(IHubContext<ChatHub> hubContext, OnlineMemberRegistry onlineMemberRegistry)
{
    // The SignalR delivery channel — pushes the coalesced ChannelActivity to a single connection.
    private readonly IHubContext<ChatHub> _hubContext = hubContext;

    // The online-member index — read at EMIT time for the member's CURRENT LastReadSeq (suppression).
    private readonly OnlineMemberRegistry _onlineMemberRegistry = onlineMemberRegistry;

    // connectionId -> (channelId -> coalescing window state). Nested by connection (mirroring
    // MessageRateLimiter's per-connection map) so RemoveConnection on disconnect is O(1) — dropping a
    // connection never scans every channel. Mutated only under _lock.
    private readonly Dictionary<string, Dictionary<string, Entry>> _entriesByConnection =
        new Dictionary<string, Dictionary<string, Entry>>();

    private readonly object _lock = new object();

    /// <summary>
    /// Offers <paramref name="lastSeq"/> as the latest activity for (<paramref name="connectionId"/>,
    /// <paramref name="channelId"/>) as of <paramref name="now"/>. If the 10s window since the last emit
    /// has elapsed (true for the very first offer, whose <c>LastSentAt</c> defaults to the distant past)
    /// the activity is emitted immediately and the window reopens; otherwise it is COALESCED into the
    /// pending, keeping only the latest seq, with no emit. Emission is still subject to the emit-time
    /// unread suppression.
    /// </summary>
    public async Task Offer(string connectionId, string channelId, long lastSeq, DateTime now)
    {
        bool emit;

        lock (_lock)
        {
            if (!_entriesByConnection.TryGetValue(connectionId, out var channels))
            {
                channels = new Dictionary<string, Entry>();
                _entriesByConnection[connectionId] = channels;
            }
            if (!channels.TryGetValue(channelId, out var entry))
            {
                entry = new Entry();
                channels[channelId] = entry;
            }

            if (now - entry.LastSentAt >= ChatLimits.ChannelActivityCoalesce)
            {
                // Window elapsed → emit this seq now and reopen the window. Advancing LastSentAt here
                // (even if the emit is later suppressed) is what keeps resumed emissions 10s-spaced.
                entry.LastSentAt = now;
                entry.HasPending = false;
                entry.PendingLastSeq = 0;
                emit = true;
            }
            else
            {
                // Within the window → collapse into the single pending, keeping ONLY the latest seq.
                entry.HasPending = true;
                entry.PendingLastSeq = lastSeq;
                emit = false;
            }
        }

        if (emit)
        {
            await EmitIfNotSuppressed(connectionId, channelId, lastSeq);
        }
    }

    /// <summary>
    /// Drains every (connection, channel) whose PENDING activity's window has elapsed as of
    /// <paramref name="now"/>: emits the pending's latest seq (subject to emit-time suppression), resets
    /// the window, and clears the pending. Driven by Task 15's 1s-granularity flush service; because it
    /// only emits when ≥10s has elapsed since the last emit, the per-(conn,channel) spacing floor holds.
    /// </summary>
    public async Task FlushDue(DateTime now)
    {
        List<(string ConnectionId, string ChannelId, long LastSeq)> toEmit = null;

        lock (_lock)
        {
            foreach (var (connectionId, channels) in _entriesByConnection)
            {
                foreach (var (channelId, entry) in channels)
                {
                    if (entry.HasPending && now - entry.LastSentAt >= ChatLimits.ChannelActivityCoalesce)
                    {
                        entry.LastSentAt = now;
                        entry.HasPending = false;
                        var seq = entry.PendingLastSeq;
                        entry.PendingLastSeq = 0;

                        (toEmit ??= new List<(string, string, long)>())
                            .Add((connectionId, channelId, seq));
                    }
                }
            }
        }

        if (toEmit == null)
        {
            return;
        }

        foreach (var (connectionId, channelId, lastSeq) in toEmit)
        {
            await EmitIfNotSuppressed(connectionId, channelId, lastSeq);
        }
    }

    /// <summary>
    /// Drops all coalescing window state for <paramref name="connectionId"/> across every channel, in a
    /// single O(1) map removal. Called from the hub's disconnect teardown (via
    /// <see cref="FanOutEngine.OnConnectionClosed"/>) so this singleton's per-connection state can never
    /// leak past the socket's lifetime — SignalR never reuses a connectionId, so an un-evicted entry
    /// would live for the whole process. Mirrors the <c>RemoveConnection</c> the sibling registries /
    /// <see cref="MessageRateLimiter"/> expose for the same reason. No-op for an unknown connection.
    /// </summary>
    public void RemoveConnection(string connectionId)
    {
        lock (_lock)
        {
            _entriesByConnection.Remove(connectionId);
        }
    }

    // Test seam (assembly has InternalsVisibleTo): number of channels a connection is tracking
    // coalescing state for. Mirrors MessageRateLimiter.TrackedChannelCount — used to assert
    // RemoveConnection actually drops the connection's state on disconnect.
    internal int TrackedChannelCount(string connectionId)
    {
        lock (_lock)
        {
            return _entriesByConnection.TryGetValue(connectionId, out var channels) ? channels.Count : 0;
        }
    }

    /// <summary>
    /// Emits one <c>ChannelActivity</c> unless the member's CURRENT unread exceeds the suppression
    /// threshold. Suppression is purely seq-based (no <c>now</c> needed here — the time-based window
    /// decision was already made by the caller under the lock). Runs OUTSIDE <see cref="_lock"/> and
    /// never lets a single failed send escape — mirroring <see cref="FanOutEngine"/>'s best-effort,
    /// fault-isolated delivery.
    /// </summary>
    private async Task EmitIfNotSuppressed(string connectionId, string channelId, long lastSeq)
    {
        // A connection with no live membership entry for the channel (left/disconnected between offer
        // and emit) is not a valid recipient — skip. The window was already advanced by the caller.
        if (!_onlineMemberRegistry.TryGetMember(channelId, connectionId, out var member))
        {
            return;
        }

        // Suppression re-checked HERE (not at offer): unread can change via a MarkRead in the interim.
        var unread = lastSeq - member.LastReadSeq;
        if (unread > ChatLimits.ChannelActivitySuppressUnreadThreshold)
        {
            return;
        }

        // preview is the C5 DM-preview slot — always null in C3 (see ChannelActivityDto).
        var payload = new ChannelActivityDto(channelId, lastSeq, Preview: null);

        try
        {
            await _hubContext.Clients.Client(connectionId).SendAsync(ChatEvents.ChannelActivity, payload);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Fan-out send of ChannelActivity failed for connection {ConnectionId} on channel {ChannelId} — skipping, coalescing state already advanced", connectionId, channelId);
        }
    }

    /// <summary>
    /// The coalescing window state for one (connection, channel). Plain mutable fields, mutated only
    /// under the coalescer's lock (mirrors <see cref="MessageRateLimiter"/>'s per-connection state).
    /// <see cref="LastSentAt"/> defaults to <see cref="DateTime.MinValue"/> so the first offer is always
    /// "window elapsed" and fires immediately.
    /// </summary>
    private sealed class Entry
    {
        internal DateTime LastSentAt;
        internal bool HasPending;
        internal long PendingLastSeq;
    }
}

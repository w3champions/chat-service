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

    // connectionId -> (channelId -> coalescing window state). Nested by connection — this coalescer's
    // own state is (and stays) connection-scoped for the connection's full lifetime, so RemoveConnection
    // on disconnect is O(1) and dropping a connection never scans every channel. Mutated only under _lock.
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
    /// <para>
    /// MONOTONIC: <c>seq</c> is allocated at persist time, but the <c>OnMessagePersisted</c> → <c>Offer</c>
    /// fan-out calls for the same channel are only serialized by this coalescer's lock — under concurrent
    /// same-channel sends they can reach the lock out of seq order. Both the pending store and the
    /// immediate emit below track the MAX of whatever was already pending/emitted and the newly offered
    /// seq, so a lower out-of-order offer coalesces harmlessly instead of ever regressing the seq a client
    /// observes.
    /// </para>
    /// <para>
    /// C5 (Task 9, D15) + post-game chat Plan A Task 6: TWO preview slots ride through unchanged —
    /// <paramref name="preview"/> is the FROZEN LEGACY Dm-only slot (a <c>DmActivityPreviewDto</c> for an
    /// accepted Dm message, else null) and <paramref name="activityPreview"/> is the superseding
    /// <c>ActivityPreviewDto</c> carried for every preview-eligible class. <see cref="FanOutEngine"/>
    /// decides both; this coalescer only stores and forwards them. The entry keeps the LATEST NON-NULL
    /// offer per slot (see the assignment below for why null must not overwrite) so a coalesced burst
    /// emits the most recent message's preview, mirroring the latest-seq-only coalescing.
    /// </para>
    /// </summary>
    public async Task Offer(string connectionId, string channelId, long lastSeq, DateTime now, object preview = null, object activityPreview = null)
    {
        bool emit;
        long emitSeq = 0;
        object emitPreview = null;
        object emitActivityPreview = null;

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

            // Latest NON-NULL offered preview, per slot, regardless of which branch fires below — a
            // coalesced burst's pending reflects the MOST RECENT message that HAD a preview.
            // The null guard is load-bearing, not tidiness: since post-game chat Plan A Task 6 a
            // preview-eligible channel also carries preview-FREE traffic (a server-authored system
            // message has no sender, so FanOutEngine offers null for both slots). Unconditional
            // latest-wins would let such a message, landing inside the 10s window AFTER a real user
            // message, blank that message's pending preview — the client would then get a bare badge
            // and the post-game message would go unnoticed, the exact failure this feature exists to
            // prevent. Non-behaviour-changing for every pre-existing path: a Dm always offers non-null,
            // and a preview-free channel only ever offers null onto an already-null entry.
            if (preview != null)
            {
                entry.Preview = preview;
            }
            if (activityPreview != null)
            {
                entry.ActivityPreview = activityPreview;
            }

            if (now - entry.LastSentAt >= ChatLimits.ChannelActivityCoalesce)
            {
                // Window elapsed → emit now and reopen the window. The emitted seq is the MAX of the
                // offered seq and anything already tracked (a stale pending or the last emit) so a
                // lower out-of-order offer never regresses what the client observes. Advancing
                // LastSentAt here (even if the emit is later suppressed) is what keeps resumed
                // emissions 10s-spaced.
                emitSeq = Math.Max(Math.Max(entry.PendingLastSeq, entry.LastEmittedSeq), lastSeq);
                entry.LastSentAt = now;
                entry.HasPending = false;
                entry.PendingLastSeq = 0;
                entry.LastEmittedSeq = emitSeq;
                emitPreview = entry.Preview;
                emitActivityPreview = entry.ActivityPreview;
                emit = true;
            }
            else
            {
                // Within the window → collapse into the single pending, keeping ONLY the latest
                // (highest) seq — a lower out-of-order offer must never overwrite a higher pending, or
                // regress below what has already been emitted.
                entry.HasPending = true;
                entry.PendingLastSeq = Math.Max(Math.Max(entry.PendingLastSeq, entry.LastEmittedSeq), lastSeq);
                emit = false;
            }
        }

        if (emit)
        {
            await EmitIfNotSuppressed(connectionId, channelId, emitSeq, emitPreview, emitActivityPreview);
        }
    }

    /// <summary>
    /// Drains every (connection, channel) whose PENDING activity's window has elapsed as of
    /// <paramref name="now"/>: emits the pending's latest seq (subject to emit-time suppression), resets
    /// the window, and clears the pending. Driven by Task 15's 1s-granularity flush service; because it
    /// only emits when ≥10s has elapsed since the last emit, the per-(conn,channel) spacing floor holds.
    /// C5 (Task 9, D15) + Plan A Task 6: the flush carries whatever BOTH preview slots
    /// (<see cref="Entry.Preview"/>, <see cref="Entry.ActivityPreview"/>) hold at drain time — the latest
    /// NON-NULL one <see cref="Offer"/> recorded per slot for the burst, mirroring the latest-seq-only
    /// coalescing.
    /// </summary>
    public async Task FlushDue(DateTime now)
    {
        List<(string ConnectionId, string ChannelId, long LastSeq, object Preview, object ActivityPreview)> toEmit = null;

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
                        // PendingLastSeq is already the running MAX (kept monotonic by Offer), but guard
                        // against LastEmittedSeq too so a flush can never regress what was last emitted.
                        var seq = Math.Max(entry.PendingLastSeq, entry.LastEmittedSeq);
                        entry.PendingLastSeq = 0;
                        entry.LastEmittedSeq = seq;

                        (toEmit ??= new List<(string, string, long, object, object)>())
                            .Add((connectionId, channelId, seq, entry.Preview, entry.ActivityPreview));
                    }
                }
            }
        }

        if (toEmit == null)
        {
            return;
        }

        foreach (var (connectionId, channelId, lastSeq, preview, activityPreview) in toEmit)
        {
            await EmitIfNotSuppressed(connectionId, channelId, lastSeq, preview, activityPreview);
        }
    }

    /// <summary>
    /// Drops all coalescing window state for <paramref name="connectionId"/> across every channel, in a
    /// single O(1) map removal. Called from the hub's disconnect teardown (via
    /// <see cref="FanOutEngine.OnConnectionClosed"/>) so this singleton's per-connection state can never
    /// leak past the socket's lifetime — SignalR never reuses a connectionId, so an un-evicted entry
    /// would live for the whole process. Mirrors the <c>RemoveConnection</c> the sibling registries
    /// (<see cref="FocusRegistry"/>, <see cref="OnlineMemberRegistry"/>) expose for the same reason —
    /// unlike those (and unlike this coalescer), <see cref="MessageRateLimiter"/> is battleTag-keyed and
    /// deliberately SURVIVES disconnect, so it exposes no such method. No-op for an unknown connection.
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
    /// fault-isolated delivery. Both preview slots ride straight onto the payload:
    /// <paramref name="preview"/> (C5, Task 9/D15 — the frozen legacy Dm-only slot) is null for every
    /// non-Dm channel and for C3-era callers that never pass one, and
    /// <paramref name="activityPreview"/> (Plan A Task 6) is null for every channel class that is not
    /// preview-eligible.
    /// </summary>
    private async Task EmitIfNotSuppressed(string connectionId, string channelId, long lastSeq, object preview = null, object activityPreview = null)
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

        var payload = new ChannelActivityDto(channelId, lastSeq, preview, activityPreview);

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
    /// under the coalescer's lock; the entry lives for exactly as long as its owning connection and is
    /// dropped in one shot by <see cref="RemoveConnection"/> on disconnect.
    /// <see cref="LastSentAt"/> defaults to <see cref="DateTime.MinValue"/> so the first offer is always
    /// "window elapsed" and fires immediately. <see cref="LastEmittedSeq"/> is the running high-water
    /// mark of every seq this (connection, channel) has emitted or pended — both <see cref="Offer"/> and
    /// <see cref="FlushDue"/> take the MAX against it so an out-of-order lower offer can never regress
    /// the seq a client observes. <see cref="Preview"/> (C5, Task 9/D15 — the frozen legacy Dm-only slot)
    /// and <see cref="ActivityPreview"/> (Plan A Task 6 — the superseding slot) mirror that latest-wins
    /// discipline, except that <see cref="Offer"/> only overwrites a slot with a NON-NULL offer, so a
    /// preview-free message in a preview-eligible channel cannot blank a pending preview.
    /// </summary>
    private sealed class Entry
    {
        internal DateTime LastSentAt;
        internal bool HasPending;
        internal long PendingLastSeq;
        internal long LastEmittedSeq;
        internal object Preview;
        internal object ActivityPreview;
    }
}

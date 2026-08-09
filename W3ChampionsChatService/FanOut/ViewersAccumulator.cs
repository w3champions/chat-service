using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.FanOut;

/// <summary>
/// The batching + idempotent sink for the <c>ViewersChanged</c> roster-delta push (C3 Task 14 —
/// acceptance 4, and the C2 displacement amendment). The hub ROUTES viewer-roster changes into it
/// (<c>FocusChannel</c>/<c>UnfocusChannel</c> and the disconnect teardown, all in <c>Chats/</c>); this
/// component owns the timing (≤ every <see cref="ChatLimits.ViewersChangedFlush"/> = 5s per channel) and
/// the delta computation (current <see cref="FocusRegistry"/> roster vs a per-window baseline).
///
/// <para><b>The pre-window baseline is the crux.</b> Per channel it holds a set of changed battleTags,
/// each mapped to its viewing state <em>as it was at the START of the window</em> — captured on the FIRST
/// touch of that battleTag in the window and NEVER updated by later touches. Callers MUST invoke
/// <see cref="RecordChange"/> BEFORE the corresponding <see cref="FocusRegistry"/> mutation, so the
/// captured baseline reflects the state as it was BEFORE the change. At flush, each changed battleTag's
/// CURRENT viewing state is compared to its baseline:</para>
/// <list type="bullet">
/// <item>now-viewing AND baseline-not-viewing → <c>joined</c>.</item>
/// <item>baseline-viewing AND now-not-viewing → <c>left</c>.</item>
/// <item>current == baseline → NO delta. This is what makes the batch idempotent: a join+leave or a
/// leave+rejoin flap within one window cancels, and — the C2 amendment — a displaced socket whose leave
/// is routed here (baseline = viewing) followed by a same-window reconnect that re-focuses the same
/// channel (current = viewing again) nets to nothing, rather than a spurious leave.</item>
/// </list>
///
/// <para>The SAME <see cref="ViewersChangedDto"/> object is emitted to EVERY current focused connection of
/// the channel (<see cref="FocusRegistry.GetFocusedConnections"/>) — there are no per-connection deltas
/// (decision 5). If both lists are empty, nothing is emitted for that channel (but the window still
/// resets). PURE, deterministic-time: NO timers, NO wall-clock reads — every decision takes an explicit
/// <c>now</c>, so it is testable without sleeping. Concurrency idiom mirrors the sibling registries /
/// <see cref="ActivityCoalescer"/>: a single lock, plain-mutable per-channel state mutated only under it;
/// the SignalR sends run OUTSIDE the lock and are fault-isolated per send.</para>
///
/// <para>Singleton (registered in <see cref="Startup"/>). Task 15 drives <see cref="FlushDue"/> from the
/// 1s-granularity hosted flush service.</para>
/// </summary>
public class ViewersAccumulator(
    IHubContext<ChatHub> hubContext,
    FocusRegistry focusRegistry,
    Chats.ViewerResolver viewerResolver)
{
    // The SignalR delivery channel — pushes the shared ViewersChanged batch to each focused connection.
    private readonly IHubContext<ChatHub> _hubContext = hubContext;

    // The focus index — read for the CURRENT roster both when capturing a baseline (RecordChange) and
    // when computing the delta (FlushDue). Reads happen under _lock; FocusRegistry never calls back into
    // this accumulator, and every path here acquires this lock BEFORE FocusRegistry's, so there is no
    // lock-ordering cycle.
    private readonly FocusRegistry _focusRegistry = focusRegistry;

    // Resolves a joined battleTag into a full roster entry (display name + flair). Shared with
    // ChatHub.FocusChannel so a join delta and an initial roster can never render differently. FlushDue
    // collects the joined battleTags under _lock but calls Resolve AFTER releasing it (consistent with
    // this component's existing send discipline — sends already run outside _lock) — nesting
    // SessionRegistry's/ConnectionMapping's locks inside the single process-wide accumulator lock would be
    // safe (neither calls back in), but is unnecessary hold-time on a lock every hub thread's RecordChange
    // also contends for, so it is avoided rather than merely tolerated.
    private readonly Chats.ViewerResolver _viewerResolver = viewerResolver;

    // channelId -> the channel's accumulation window. Mutated only under _lock.
    private readonly Dictionary<string, ChannelWindow> _windows = new Dictionary<string, ChannelWindow>();

    private readonly object _lock = new object();

    /// <summary>
    /// Records that <paramref name="battleTag"/>'s viewing state on <paramref name="channelId"/> is about
    /// to change, as of <paramref name="now"/>. On the FIRST touch of this (channel, battleTag) in the
    /// current window, captures the battleTag's CURRENT viewing state (is it in the channel's live roster
    /// right now) as the pre-window baseline; subsequent touches in the same window do NOT update it.
    /// Emits nothing — the change only accumulates until a due <see cref="FlushDue"/>.
    /// <para>
    /// MUST be called BEFORE the corresponding <see cref="FocusRegistry"/> mutation so the baseline
    /// reflects the state BEFORE the change (a genuine first focus → baseline not-viewing; an unfocus or a
    /// disconnect while still focused → baseline viewing).
    /// </para>
    /// </summary>
    public void RecordChange(string channelId, string battleTag, DateTime now)
    {
        lock (_lock)
        {
            if (!_windows.TryGetValue(channelId, out var window))
            {
                // A brand-new window opens at `now`: its first flush is due one full interval later, so
                // the FIRST change accumulates rather than emitting immediately (the "accumulate until
                // flush" contract). An idle channel whose LastFlushedAt is already older than the interval
                // instead flushes on the very next tick — responsive when idle, batched under load.
                window = new ChannelWindow(now);
                _windows[channelId] = window;
            }

            // First touch in this window → capture the pre-window baseline. Later touches keep it.
            if (!window.Baseline.ContainsKey(battleTag))
            {
                window.Baseline[battleTag] = IsCurrentlyViewingNoLock(channelId, battleTag);
            }
        }
    }

    /// <summary>
    /// For each channel whose window is due (<c>now - LastFlushedAt &gt;= ChatLimits.ViewersChangedFlush</c>)
    /// and has accumulated changes, computes each changed battleTag's CURRENT viewing state versus its
    /// pre-window baseline, emits ONE <see cref="ViewersChangedDto"/> (the SAME object) to every current
    /// focused connection of the channel, and resets the window. A channel whose net delta is empty resets
    /// its window but emits nothing. Driven by Task 15's 1s-granularity flush service.
    /// </summary>
    public async Task FlushDue(DateTime now)
    {
        // Per due channel: its target connections, its joined battleTags (NOT YET resolved — Resolve
        // happens after _lock is released, below) and its left battleTags. Resolution is deferred so the
        // single process-wide accumulator lock isn't held across the SessionRegistry/ConnectionMapping
        // lookups ViewerResolver.Resolve makes — matching how the SignalR sends already run outside _lock.
        List<(IReadOnlyCollection<string> Connections, string ChannelId, List<string> JoinedBattleTags, List<string> Left)> toEmit = null;
        List<string> toEvict = null;

        lock (_lock)
        {
            foreach (var (channelId, window) in _windows)
            {
                // Skip channels with nothing accumulated (a persisted, already-drained window) — do NOT
                // touch their LastFlushedAt, so an idle-but-actively-viewed channel's next change flushes
                // promptly.
                if (window.Baseline.Count == 0)
                {
                    continue;
                }

                if (now - window.LastFlushedAt < ChatLimits.ViewersChangedFlush)
                {
                    continue;
                }

                var joinedBattleTags = new List<string>();
                var left = new List<string>();
                foreach (var (battleTag, wasViewing) in window.Baseline)
                {
                    var isViewing = IsCurrentlyViewingNoLock(channelId, battleTag);
                    if (isViewing && !wasViewing)
                    {
                        joinedBattleTags.Add(battleTag);
                    }
                    else if (!isViewing && wasViewing)
                    {
                        left.Add(battleTag);
                    }
                    // isViewing == wasViewing → idempotent, no delta (a flap or a displaced-reconnect cancels).
                }

                // Reset the window regardless of whether the net delta is empty — a due window is drained
                // even if every change cancelled out.
                window.Baseline.Clear();
                window.LastFlushedAt = now;

                var connections = _focusRegistry.GetFocusedConnections(channelId);

                // A drained channel with NO current focused connections is dormant — there is nobody to
                // notify and nothing to accumulate a baseline against, so drop its window. This bounds
                // _windows to the set of actively-viewed channels (plus in-flight ones) instead of leaking
                // one entry per channel ever focused across the process lifetime; a later focus re-creates
                // a fresh window. Collected here and removed after the loop so we never mutate _windows
                // while enumerating it.
                if (connections.Count == 0)
                {
                    (toEvict ??= new List<string>()).Add(channelId);
                    continue;
                }

                if (joinedBattleTags.Count == 0 && left.Count == 0)
                {
                    continue;
                }

                (toEmit ??= new List<(IReadOnlyCollection<string>, string, List<string>, List<string>)>())
                    .Add((connections, channelId, joinedBattleTags, left));
            }

            if (toEvict != null)
            {
                foreach (var channelId in toEvict)
                {
                    _windows.Remove(channelId);
                }
            }
        }

        if (toEmit == null)
        {
            return;
        }

        foreach (var (connections, channelId, joinedBattleTags, left) in toEmit)
        {
            // Resolved OUTSIDE _lock (see the comment on toEmit's declaration above). Order is preserved —
            // joinedBattleTags was built by the same in-order Baseline walk the old under-lock code used,
            // only the Resolve() call itself moved.
            var joined = joinedBattleTags.Select(_viewerResolver.Resolve).ToList();

            // ONE payload object, sent to every current focused connection — no per-connection deltas.
            var payload = new ViewersChangedDto(channelId, joined, left);
            foreach (var connectionId in connections)
            {
                try
                {
                    await _hubContext.Clients.Client(connectionId).SendAsync(ChatEvents.ViewersChanged, payload);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Fan-out send of ViewersChanged failed for connection {ConnectionId} on channel {ChannelId} — skipping, window already reset", connectionId, payload.ChannelId);
                }
            }
        }
    }

    // True iff battleTag currently appears in the channel's live roster (case-insensitive, matching
    // FocusRegistry.GetRoster). Called under _lock.
    private bool IsCurrentlyViewingNoLock(string channelId, string battleTag)
    {
        foreach (var rosterTag in _focusRegistry.GetRoster(channelId))
        {
            if (string.Equals(rosterTag, battleTag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // Test seam (assembly has InternalsVisibleTo): number of changed battleTags accumulated for a channel
    // in the current (undrained) window.
    internal int PendingChangeCount(string channelId)
    {
        lock (_lock)
        {
            return _windows.TryGetValue(channelId, out var window) ? window.Baseline.Count : 0;
        }
    }

    // Test seam (assembly has InternalsVisibleTo): number of channels currently holding a window — asserts
    // that a drained, no-longer-viewed channel's window is evicted rather than leaked. Mirrors
    // ActivityCoalescer.TrackedChannelCount.
    internal int TrackedChannelCount()
    {
        lock (_lock)
        {
            return _windows.Count;
        }
    }

    /// <summary>
    /// One channel's accumulation window. Plain mutable state, mutated only under the accumulator's lock.
    /// <see cref="Baseline"/> maps each battleTag CHANGED in the window to its viewing state at the START
    /// of the window (captured on first touch); its keys are the changed set. Case-insensitive keys match
    /// the casing convention across Sessions/FanOut (a live battleTag keeps its casing; DB-derived ones are
    /// lowercased), so one player never splits into two entries. <see cref="LastFlushedAt"/> starts at the
    /// window's open time so the first change accumulates for a full interval before flushing.
    /// </summary>
    private sealed class ChannelWindow(DateTime openedAt)
    {
        internal readonly Dictionary<string, bool> Baseline =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        internal DateTime LastFlushedAt = openedAt;
    }
}

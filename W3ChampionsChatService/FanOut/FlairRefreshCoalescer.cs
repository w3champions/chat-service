using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.FanOut;

/// <summary>
/// Collapses a burst of flair-change notifications for the same player into one refresh per flush
/// tick. website-backend deliberately over-notifies (it pings on any settings save, not only
/// flair-relevant ones), and a single user action can cross the persistence boundary several times —
/// this is where that volume is absorbed.
/// <para>
/// Mirrors the discipline of <see cref="ActivityCoalescer"/> and <see cref="ViewersAccumulator"/>:
/// mutate state under one lock, do the work outside it, fault-isolate per item.
/// </para>
/// </summary>
public class FlairRefreshCoalescer(IFlairRefresher refresher)
{
    private readonly IFlairRefresher _refresher = refresher;
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Test seam.</summary>
    internal int PendingCount
    {
        get { lock (_lock) { return _pending.Count; } }
    }

    public void RecordChange(string battleTag)
    {
        if (string.IsNullOrWhiteSpace(battleTag)) return;

        lock (_lock)
        {
            // At the cap, drop rather than grow. Dropping degrades to the reconnect backstop; growing
            // without bound would let an upstream write storm become a memory problem here.
            if (_pending.Count >= ChatLimits.FlairRefreshPendingCap && !_pending.Contains(battleTag))
            {
                return;
            }

            _pending.Add(battleTag);
        }
    }

    /// <summary>
    /// Drains at most <see cref="ChatLimits.FlairRefreshPerTickBudget"/> pending battleTags, leaving any
    /// remainder pending for a later call. Each refresh is a website-backend HTTP round trip plus Mongo
    /// I/O plus per-connection sends — a bounded slice keeps one large burst (e.g. a bulk clan delete)
    /// from monopolizing the shared flush tick this coalescer shares with the other, purely in-memory,
    /// <see cref="FanOutFlushService"/> participants.
    /// </summary>
    public async Task Flush()
    {
        List<string> due;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            due = _pending.Take(ChatLimits.FlairRefreshPerTickBudget).ToList();
            foreach (var battleTag in due) _pending.Remove(battleTag);
        }

        foreach (var battleTag in due)
        {
            try
            {
                await _refresher.Refresh(battleTag);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Flair refresh failed for {BattleTag} — skipping, the next connect re-enriches", battleTag);
            }
        }
    }
}

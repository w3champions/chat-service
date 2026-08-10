using System;
using System.Collections.Generic;
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

    public async Task Flush()
    {
        List<string> due;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            due = new List<string>(_pending);
            _pending.Clear();
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

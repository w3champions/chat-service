using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using W3ChampionsChatService.Chats;

namespace W3ChampionsChatService.Mutes;

/// <summary>
/// Reconciles the in-memory per-connection mute cache for EVERY ban write path — the SignalR hub
/// (<see cref="ChatHub.BanUser"/>) AND the REST controller (<see cref="MuteController"/>) — so a
/// mute takes effect on a target's live connections immediately, without a per-send DB read and
/// without waiting for a reconnect.
/// <para>
/// The ONLY residual case that still requires a reconnect is a ban written DIRECTLY to the Mongo
/// collection, bypassing both the hub and the REST controller (e.g. a manual DB edit or a data
/// migration). Those writes never invoke this service, so the cache only catches up at the
/// target's next login.
/// </para>
/// Shares the singleton <see cref="ConnectionMapping"/> with the hub and uses
/// <see cref="IHubContext{ChatHub}"/> to push client signals from outside the hub call context.
/// <para>
/// Lifetime: registered as a singleton. <see cref="IMuteRepository"/> resolves to
/// <see cref="MuteRepository"/>, which wraps a thread-safe singleton <c>MongoClient</c>, so injecting
/// it directly into a singleton is safe (no captured scoped/transient state).
/// </para>
/// </summary>
public class MuteReconciliationService(
    ConnectionMapping connections,
    IHubContext<ChatHub> hubContext,
    IMuteRepository muteRepository)
{
    private readonly ConnectionMapping _connections = connections;
    private readonly IHubContext<ChatHub> _hubContext = hubContext;
    private readonly IMuteRepository _muteRepository = muteRepository;

    /// <summary>
    /// Single home for the IN-BAND ban orchestration shared by the hub (<see cref="ChatHub.BanUser"/>)
    /// and the REST controller (<see cref="MuteController"/>): persist the mute, then reconcile every
    /// live connection of the target. Returns <c>(success, parsedEndDate)</c> — <c>success == false</c>
    /// when the endDate could not be parsed (the mute is still persisted; the live reconcile is skipped
    /// and the target's next reconnect re-seeds the cache).
    /// </summary>
    public async Task<(bool success, DateTime parsedEndDate)> ApplyBanAsync(LoungeMuteRequest request)
    {
        await _muteRepository.AddLoungeMute(request);

        // Parse the endDate with the SAME DateTimeStyles the repository uses (AdjustToUniversal) so the
        // CACHED expiry can never disagree with the PERSISTED expiry for an offset-less endDate.
        if (!DateTime.TryParse(request.endDate, null, DateTimeStyles.AdjustToUniversal, out var parsedEndDate))
        {
            Log.Warning("ApplyBanAsync: could not parse endDate '{EndDate}' for {BattleTag} — ban persisted, skipping live cache reconcile (next reconnect will re-seed the cache)",
                request.endDate, request.battleTag);
            return (false, DateTime.MinValue);
        }

        var status = request.isShadowBan ? MuteStatus.Shadow : MuteStatus.Full;
        await ApplyMuteToLiveConnections(request.battleTag, status, parsedEndDate);
        return (true, parsedEndDate);
    }

    /// <summary>
    /// Applies a mute to every live connection of <paramref name="battleTag"/>: updates the cached
    /// status/expiry so the next SendMessage/SwitchRoom enforces from the cache (zero DB read), and
    /// for a FULL ban pushes <c>PlayerBannedFromChat</c> (expiry only) to each connection. A SHADOW
    /// ban stays completely silent to the target (preserve the illusion). Never aborts a connection.
    /// </summary>
    public async Task ApplyMuteToLiveConnections(string battleTag, MuteStatus status, DateTime endDate)
    {
        // Match the DB convention (GetMutedPlayer/AddLoungeMute lowercase the battleTag) by lowercasing
        // the lookup arg too. GetConnectionIdsForUser also compares case-insensitively, so the reconcile
        // works even on a casing mismatch.
        var liveConnectionIds = _connections.GetConnectionIdsForUser(battleTag.ToLower());
        foreach (var connId in liveConnectionIds)
        {
            // Update the cache so the next SendMessage/SwitchRoom enforces from the cache (no DB read).
            _connections.SetMute(connId, status, endDate);

            if (status == MuteStatus.Full)
            {
                // Full ban — R7/G5: notify the target REGARDLESS of their current room so they
                // clearly and persistently know they're banned, independent of channel. A user
                // full-banned while sitting in a clan/lobby room must still receive the notice
                // (not just users in a public lounge/ladder room).
                // G1: SendAsync only — never abort; the connection must stay alive.
                // §12: no forced eviction — the user keeps their current room membership.
                // SECURITY: send only the expiry — never leak the moderation reason or the shadow flag
                // to the client. The event name + the camelCase `endDate` field stay unchanged so old
                // clients keep reading `.endDate`; the missing reason/isShadowBan deserialize to
                // null/false on legacy clients (harmless).
                await _hubContext.Clients.Client(connId).SendAsync("PlayerBannedFromChat", new { endDate });
            }
            // Shadow ban: no signal to the target whatsoever — preserve the illusion (spec §12).
        }
    }

    /// <summary>
    /// Clears the mute on every live connection of <paramref name="battleTag"/> (sets cached status to
    /// <see cref="MuteStatus.None"/>) so an unbanned user can send again server-side WITHOUT reconnecting.
    /// <para>
    /// Intentionally does NOT restore the client's hidden public rooms or clear its ban banner — those
    /// only refresh on reconnect, matching the product decision (no auto-reconnect on ban lift). No
    /// "ban lifted" client event is emitted.
    /// </para>
    /// </summary>
    public Task ClearMuteOnLiveConnections(string battleTag)
    {
        var liveConnectionIds = _connections.GetConnectionIdsForUser(battleTag.ToLower());
        foreach (var connId in liveConnectionIds)
        {
            _connections.SetMute(connId, MuteStatus.None, DateTime.MinValue);
        }
        return Task.CompletedTask;
    }
}

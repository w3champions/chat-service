using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Chats;
using Serilog;
using MongoDB.Driver;

namespace W3ChampionsChatService.Mutes;

[ApiController]
[Route("api/loungeMute")]
public class MuteController(IMuteRepository muteRepository, MuteReconciliationService muteReconciliation) : ControllerBase
{
    private readonly IMuteRepository _muteRepository = muteRepository;
    private readonly MuteReconciliationService _muteReconciliation = muteReconciliation;

    [HttpGet("")]
    [UserHasPermission(EPermission.Moderation)]
    public async Task<IActionResult> GetLoungeMutes()
    {
        var loungeMutes = await _muteRepository.GetLoungeMutes();
        return Ok(loungeMutes);
    }

    [HttpPost("")]
    [UserHasPermission(EPermission.Moderation)]
    public async Task<IActionResult> AddLoungeMute([FromBody] LoungeMuteRequest loungeMuteRequest)
    {
        if (loungeMuteRequest.battleTag == "")
        {
            return BadRequest("BattleTag cannot be empty.");
        }
        if (loungeMuteRequest.endDate == "")
        {
            return BadRequest("Ban End Date must be set.");
        }

        Log.Information("Adding lounge mute shadowBan={IsShadowBan} for {BattleTag} until {EndDate} by {Author}. Reason: {Reason}", loungeMuteRequest.isShadowBan, loungeMuteRequest.battleTag, loungeMuteRequest.endDate, loungeMuteRequest.author, loungeMuteRequest.reason);
        await _muteRepository.AddLoungeMute(loungeMuteRequest);

        // The REST POST is an IN-BAND ban path: reconcile the target's live connections so the mute
        // takes effect immediately (zero per-send DB read), not only on their next reconnect.
        // Parse the endDate with the SAME DateTimeStyles the repository uses (AdjustToUniversal) so the
        // cached expiry matches the persisted expiry. On a parse failure the ban is already persisted —
        // skip the live reconcile gracefully (the target's next reconnect re-seeds the cache).
        if (DateTime.TryParse(loungeMuteRequest.endDate, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsedEndDate))
        {
            var status = loungeMuteRequest.isShadowBan ? MuteStatus.Shadow : MuteStatus.Full;
            await _muteReconciliation.ApplyMuteToLiveConnections(loungeMuteRequest.battleTag, status, parsedEndDate);
        }
        else
        {
            Log.Warning("AddLoungeMute: could not parse endDate '{EndDate}' for {BattleTag} — ban persisted, skipping live cache reconcile (next reconnect will re-seed the cache)",
                loungeMuteRequest.endDate, loungeMuteRequest.battleTag);
        }

        return Ok($"Lounge mute for {loungeMuteRequest.battleTag} inserted successfully.");
    }

    [HttpDelete("{bTag}")]
    [UserHasPermission(EPermission.Moderation)]
    public async Task<IActionResult> DeleteLoungeMute([FromRoute] string bTag)
    {
        Log.Information("Deleting lounge mute for {BattleTag}", bTag);
        DeleteResult result = await _muteRepository.DeleteLoungeMute(bTag);
        if (result.DeletedCount == 0)
        {
            NotFound($"Unable to delete. Lounge mute for {bTag} not found.");
        }

        // Clear the cached mute on the target's live connections so an unbanned user can send again
        // server-side without reconnecting. Intentionally does not restore hidden rooms or clear the
        // client banner — that refreshes on reconnect (no "ban lifted" event; product decision).
        await _muteReconciliation.ClearMuteOnLiveConnections(bTag);

        return Ok($"Lounge mute for {bTag} deleted.");
    }
}

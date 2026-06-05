using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using W3ChampionsChatService.Authentication;
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

        // The REST POST is an IN-BAND ban path: ApplyBanAsync persists the mute AND reconciles the
        // target's live connections so the mute takes effect immediately (zero per-send DB read), not
        // only on their next reconnect. A malformed endDate still persists; the live reconcile is
        // skipped and the target's next reconnect re-seeds the cache.
        await _muteReconciliation.ApplyBanAsync(loungeMuteRequest);

        return Ok($"Lounge mute for {loungeMuteRequest.battleTag} inserted successfully.");
    }

    [HttpDelete("{bTag}")]
    [UserHasPermission(EPermission.Moderation)]
    public async Task<IActionResult> DeleteLoungeMute([FromRoute] string bTag)
    {
        Log.Information("Deleting lounge mute for {BattleTag}", bTag);
        DeleteResult result = await _muteRepository.DeleteLoungeMute(bTag);

        // Clear the cached mute on the target's live connections FIRST, regardless of whether a DB row
        // was removed. An explicit moderator unban should always free live connections — even if the DB
        // row was already gone or expired — and ClearMuteOnLiveConnections is a safe no-op when there is
        // nothing cached. (Does not restore hidden rooms or clear the client banner — that refreshes on
        // reconnect; no "ban lifted" event, per the product decision.)
        await _muteReconciliation.ClearMuteOnLiveConnections(bTag);

        // Report an accurate status: 404 when nothing was deleted, 200 otherwise.
        return result.DeletedCount == 0
            ? NotFound($"Unable to delete. Lounge mute for {bTag} not found.")
            : Ok($"Lounge mute for {bTag} deleted.");
    }
}

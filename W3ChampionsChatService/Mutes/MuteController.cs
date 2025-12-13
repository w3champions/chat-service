using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using W3ChampionsChatService.Authentication;
using Serilog;
using MongoDB.Driver;

namespace W3ChampionsChatService.Mutes;

[ApiController]
[Route("api/loungeMute")]
public class MuteController(MuteRepository muteRepository) : ControllerBase
{
    private readonly MuteRepository _muteRepository = muteRepository;

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
        return Ok($"Lounge mute for {bTag} deleted.");
    }
}

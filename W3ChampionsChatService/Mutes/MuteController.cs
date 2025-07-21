using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using W3ChampionsChatService.Authentication;
using Serilog;

namespace W3ChampionsChatService.Mutes;

[ApiController]
[Route("api/loungeMute")]
public class MuteController(MuteRepository muteRepository) : ControllerBase
{
    private static readonly string AdminSecret = Environment.GetEnvironmentVariable("ADMIN_SECRET") ?? "300C018C-6321-4BAB-B289-9CB3DB760CBB";
    private readonly MuteRepository _muteRepository = muteRepository;

    [HttpGet("")]
    [CheckIfBattleTagIsAdmin]
    public async Task<IActionResult> GetLoungeMutes(string secret)
    {
        if (secret != AdminSecret)
        {
            return StatusCode(403);
        }
        var loungeMutes = await _muteRepository.GetLoungeMutes();
        return Ok(loungeMutes);
    }

    [HttpPost("")]
    [CheckIfBattleTagIsAdmin]
    public async Task<IActionResult> AddLoungeMute([FromBody] LoungeMuteRequest loungeMuteRequest, string secret)
    {
        if (secret != AdminSecret)
        {
            return StatusCode(403);
        }
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
        return Ok();
    }

    [HttpDelete("{bTag}")]
    [CheckIfBattleTagIsAdmin]
    public async Task<IActionResult> DeleteLoungeMute([FromRoute] string bTag, string secret)
    {
        if (secret != AdminSecret)
        {
            return StatusCode(403);
        }
        Log.Information("Deleting lounge mute for {BattleTag}", bTag);
        await _muteRepository.DeleteLoungeMute(bTag);
        return Ok();
    }
}

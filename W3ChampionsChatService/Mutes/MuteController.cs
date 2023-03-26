using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using W3ChampionsChatService.Authentication;

namespace W3ChampionsChatService.Mutes
{
    [ApiController]
    [Route("api/loungeMute")]
    public class MuteController : ControllerBase
    {
        private readonly MuteRepository _muteRepository;
        public MuteController(MuteRepository muteRepository){
            _muteRepository = muteRepository;
        }

        [HttpGet("")]
        // [CheckIfBattleTagIsAdmin]
        public async Task<IActionResult> GetLoungeMutes()
        {
            var loungeMutes = await _muteRepository.GetLoungeMutes();
            return Ok(loungeMutes);
        }

        [HttpPost("")]
        // [CheckIfBattleTagIsAdmin]
        public async Task<IActionResult> AddLoungeMute([FromBody] LoungeMuteRequest loungeMuteRequest)
        {
            if (loungeMuteRequest.battleTag == "") {
                return BadRequest("BattleTag cannot be empty.");
            }

            if (loungeMuteRequest.endDate == "") {
                return BadRequest("Ban End Date must be set.");
            }

            await _muteRepository.AddLoungeMute(loungeMuteRequest);
            return Ok();
        }

        [HttpDelete("{bTag}")]
        // [CheckIfBattleTagIsAdmin]
        public async Task<IActionResult> DeleteLoungeMute([FromRoute] string bTag)
        {
            if (bTag == "") {
                return BadRequest("BattleTag not specified.");
            }
            await _muteRepository.DeleteLoungeMute(bTag);
            return Ok();
        }
    }
}

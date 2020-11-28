using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using W3ChampionsChatService.Authentication;

namespace W3ChampionsChatService.Bans
{
    [ApiController]
    [Route("api/bans")]
    public class BanController : ControllerBase
    {
        private readonly BanRepository _banRepository;

        public BanController(
            BanRepository banRepository)
        {
            _banRepository = banRepository;
        }

        [HttpGet]
        public async Task<IActionResult> GetBans()
        {
            var ban = await _banRepository.LoadAll();
            return Ok(ban);
        }

        [HttpPut]
        [CheckIfBattleTagIsAdmin]
        public async Task<IActionResult> SetBan([FromBody] ChatBan ban)
        {
            await _banRepository.Upsert(ban);
            return Ok();
        }

        [HttpDelete("{battleTag}")]
        [CheckIfBattleTagIsAdmin]
        public async Task<IActionResult> UpdateSettings(string battleTag)
        {
            await _banRepository.Delete(battleTag);
            return Ok();
        }
    }
}
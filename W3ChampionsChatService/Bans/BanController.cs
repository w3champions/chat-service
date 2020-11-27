using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

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

        [HttpGet("{battleTag}")]
        public async Task<IActionResult> GetBan(string battleTag)
        {
            var ban = await _banRepository.Load(battleTag);
            return Ok(ban);
        }

        [HttpPut]
        public async Task<IActionResult> SetBan([FromBody] ChatBan ban)
        {
            await _banRepository.Upsert(ban);
            return Ok();
        }

        [HttpDelete("{battleTag}")]
        public async Task<IActionResult> UpdateSettings(string battleTag)
        {
            await _banRepository.Delete(battleTag);
            return Ok();
        }
    }
}
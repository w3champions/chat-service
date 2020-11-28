using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace W3ChampionsChatService.Settings
{
    [ApiController]
    [Route("api/chat-settings")]
    public class ChatSettingsController : ControllerBase
    {
        private readonly SettingsRepository _settingsRepository;

        public ChatSettingsController(
            SettingsRepository settingsRepository)
        {
            _settingsRepository = settingsRepository;
        }

        [HttpGet("{battleTag}")]
        public async Task<IActionResult> GetMembership(string battleTag)
        {
            var memberShip = await _settingsRepository.Load(battleTag) ?? new ChatSettings(battleTag)
            {
                DefaultChat = "W3C Lounge"
            };
            return Ok(memberShip);
        }
    }
}
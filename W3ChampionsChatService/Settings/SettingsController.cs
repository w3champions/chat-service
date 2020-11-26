using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace W3ChampionsChatService.Settings
{
    [ApiController]
    [Route("api/chat-settings")]
    public class ChatSettingsController : ControllerBase
    {
        private readonly ChatSettingsRepository _chatSettingsRepository;

        public ChatSettingsController(
            ChatSettingsRepository chatSettingsRepository)
        {
            _chatSettingsRepository = chatSettingsRepository;
        }

        [HttpGet("{battleTag}")]
        public async Task<IActionResult> GetMembership(string battleTag)
        {
            var memberShip = await _chatSettingsRepository.Load(battleTag) ?? new ChatSettings(battleTag)
            {
                DefaultChat = "W3C Lounge",
                HideChat = false
            };
            return Ok(memberShip);
        }

        [HttpPut("{battleTag}")]
        // Todo Authorize with chat key
        public async Task<IActionResult> UpdateSettings(string battleTag, [FromBody] ChatSettingsDto settings)
        {
            var memberShip = await _chatSettingsRepository.Load(battleTag) ?? new ChatSettings(battleTag);
            memberShip.Update(settings.DefaultChat, settings.HideChat);
            await _chatSettingsRepository.Save(memberShip);
            return Ok(memberShip);
        }
    }

    public class ChatSettingsDto
    {
        public string DefaultChat { get; set; }
        public bool? HideChat { get; set; }
    }
}
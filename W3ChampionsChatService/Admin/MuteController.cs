using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Mutes;
using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace W3ChampionsChatService.Admin
{
    // DTO for creating/updating a mute
    public class AdminMuteDto
    {
        public string BattleTag { get; set; }
        public string EndDate { get; set; } // ISO 8601 format string
        
        // Accept string in request, validate and convert to enum in controller
        public string MuteType { get; set; } 
    }

    [ApiController]
    [Route("api/v1/admin/mutes")]
    public class MuteController : ControllerBase
    {
        private readonly MuteRepository _muteRepository;
        private readonly IW3CAuthenticationService _authService; // Used by the filter

        public MuteController(
            MuteRepository muteRepository,
            IW3CAuthenticationService authService) // Inject auth service for filter
        {
            _muteRepository = muteRepository;
            _authService = authService;
        }

        [HttpPut] // Use PUT for idempotency (create or update)
        [CheckIfBattleTagIsAdminFilter]
        public async Task<IActionResult> ApplyMute([FromBody] AdminMuteDto muteDto)
        {
            // The filter provides the admin's battletag if needed, implicitly via context
            var adminBattleTag = HttpContext.Items["battleTag"] as string;

            if (string.IsNullOrEmpty(muteDto.BattleTag) || string.IsNullOrEmpty(muteDto.EndDate) || string.IsNullOrEmpty(muteDto.MuteType))
            {
                return BadRequest("BattleTag, EndDate, and MuteType are required.");
            }

            // Validate and parse MuteType string to Enum
            if (!Enum.TryParse<MuteTypeEnum>(muteDto.MuteType, true, out var muteTypeEnum))
            {
                return BadRequest($"Invalid MuteType. Must be one of: {string.Join(", ", Enum.GetNames(typeof(MuteTypeEnum)))}");
            }

            if (!DateTime.TryParse(muteDto.EndDate, null, System.Globalization.DateTimeStyles.RoundtripKind, out var endDateParsed))
            {
                 return BadRequest("EndDate must be in ISO 8601 format (e.g., YYYY-MM-DDTHH:mm:ssZ).");
            }

            var request = new LoungeMuteRequest
            {
                battleTag = muteDto.BattleTag,
                endDate = muteDto.EndDate, // Keep as string for repo method
                MuteType = muteTypeEnum,   // Use parsed enum
                author = adminBattleTag // Set author as the admin who performed the action
            };

            await _muteRepository.AddLoungeMute(request);

            // TODO: Consider broadcasting a SignalR event to notify relevant clients/admins about the mute

            return Ok();
        }

        [HttpDelete("{battleTag}")]
        [CheckIfBattleTagIsAdminFilter]
        public async Task<IActionResult> RemoveMute(string battleTag)
        {
            if (string.IsNullOrEmpty(battleTag))
            {
                return BadRequest("BattleTag parameter is required.");
            }

            await _muteRepository.DeleteLoungeMute(battleTag.ToLower());

            // TODO: Consider broadcasting a SignalR event to notify relevant clients/admins about the unmute

            return Ok();
        }

        // Optional: GET endpoint to retrieve current mutes
        [HttpGet]
        [CheckIfBattleTagIsAdminFilter]
        public async Task<IActionResult> GetMutes()
        {
            var mutes = await _muteRepository.GetLoungeMutes();
            // Note: This returns the LoungeMute objects which have the Enum type
            // The BsonRepresentation attribute ensures it serializes to string in JSON response
            return Ok(mutes);
        }
    }
} 
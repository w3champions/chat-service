using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;

namespace W3ChampionsChatService.Internal;

/// <summary>
/// Receives flair-change notifications from website-backend. Enqueues each battleTag for a coalesced
/// refresh and returns immediately — the sender is fire-and-forget with a 3 s per-attempt budget, so
/// this must never do the refresh inline.
/// </summary>
[ApiController]
[Route("internal/profile-changes")]
[InternalHmacAuth(InternalCaller.Wb)]
public class InternalProfileChangesController(FlairRefreshCoalescer coalescer) : ControllerBase
{
    private const string GenericValidationError = "Invalid request.";

    [HttpPost]
    public IActionResult Post([FromBody] InternalProfileChangeRequest request)
    {
        // Validate the WHOLE batch before enqueuing any of it — no partial processing, so a malformed
        // request can never leave the coalescer holding half a batch.
        if (request?.BattleTags == null
            || request.BattleTags.Count == 0
            || request.BattleTags.Count > ChatLimits.InternalMaxMembersPerCall
            || request.BattleTags.Any(tag => !InternalValidation.IsValidBattleTag(tag)))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        foreach (var battleTag in request.BattleTags)
        {
            coalescer.RecordChange(battleTag);
        }

        Log.Information("Internal profile change {Caller} count={Count}",
            InternalHmacAuthFilter.ResolveCaller(HttpContext), request.BattleTags.Count);

        return Ok();
    }
}

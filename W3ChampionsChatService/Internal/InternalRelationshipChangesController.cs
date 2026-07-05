using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Relationships;

namespace W3ChampionsChatService.Internal;

/// <summary>
/// C7 Task 10 — <c>POST /internal/relationship-changes</c>, the website-backend change-ping and the
/// FIRST production caller of C5's <see cref="IRelationshipProvider.Invalidate"/> seam. wb pings this
/// endpoint after a block/unblock/friend edit lands so the chat side drops its cached relationship
/// snapshot for the affected pair; C5/C6 re-derive the real state on the next read. There is deliberately
/// NO push and NO re-evaluation call — a cache-drop IS the whole contract, with the 5-minute snapshot TTL
/// (<see cref="Domain.ChatLimits.RelationshipCacheTtl"/>) as the backstop if a ping is ever missed.
/// <para>
/// Both <c>actor</c> and <c>target</c> are ALWAYS invalidated, even when they are equal: the calls are
/// idempotent and the provider's global <c>_version</c> stamp (<c>RelationshipProvider.cs</c>) makes the
/// double drop race-safe against an in-flight snapshot load, so the controller never special-cases a
/// self-ping. Invalidation is type-agnostic — <c>type</c> is validated + logged but never forwarded.
/// </para>
/// <para>
/// SECURITY (H1): gated by <see cref="InternalHmacAuthAttribute"/> at CLASS level with a Wb-ONLY
/// allow-list — the disjoint HMAC auth realm, never <see cref="UserHasPermissionAttribute"/>. The dynamic
/// reflection sweep in <c>InternalChannelsControllerTests</c> now also covers this controller.
/// VALIDATION: <c>type</c> must be one of the EXACT case-sensitive wire literals (a plain <c>switch</c>,
/// not an enum converter — the literals ARE the contract); <c>actor</c>/<c>target</c> must be non-blank
/// AND control-char-free (the log-injection defense a Task 9 review flagged on <c>ref</c>, held here too
/// because both flow into the <see cref="Log.Information(string, object[])"/> line below). Any failure is
/// a plain 400 with a GENERIC <see cref="ErrorResult"/> message and NO <c>Invalidate</c> calls.
/// </para>
/// </summary>
[ApiController]
[Route("internal/relationship-changes")]
[InternalHmacAuth(InternalCaller.Wb)]
public class InternalRelationshipChangesController(IRelationshipProvider relationshipProvider) : ControllerBase
{
    private const string GenericValidationError = "Invalid request.";

    [HttpPost]
    public IActionResult Post([FromBody] InternalRelationshipChangeRequest request)
    {
        if (request == null
            || !IsValidType(request.Type)
            || !IsValidParticipant(request.Actor)
            || !IsValidParticipant(request.Target))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        // Both ALWAYS invalidated, even when actor == target — idempotent; the provider's global
        // _version stamp makes the double drop race-safe against an in-flight snapshot load.
        relationshipProvider.Invalidate(request.Actor);
        relationshipProvider.Invalidate(request.Target);

        Log.Information(
            "Internal relationship change {Caller} type={Type} actor={Actor} target={Target}",
            InternalHmacAuthFilter.ResolveCaller(HttpContext), request.Type, request.Actor, request.Target);

        return Ok();
    }

    // EXACT case-sensitive wire literals — a plain switch, deliberately NOT an enum + JsonStringEnumConverter.
    // The four strings ARE the cross-repo contract; "Block", "friendadd", etc. must be rejected.
    private static bool IsValidType(string type) => type switch
    {
        "block" or "unblock" or "friendAdd" or "friendRemove" => true,
        _ => false,
    };

    // Non-blank AND control-char-free. IsNullOrWhiteSpace rejects null/empty/all-whitespace; char.IsControl
    // catches an EMBEDDED '\n'/'\r'/'\t'/NUL that a partly-printable value would otherwise smuggle into the
    // structured {Actor}/{Target} log sink (log-injection guard — same class the Task 9 ref review caught).
    // char.IsControl does NOT cover U+2028 LINE SEPARATOR / U+2029 PARAGRAPH SEPARATOR (category Zl/Zp, not
    // Cc) — a downstream JS/JSON log viewer can render either as a line break, spoofing a log line. Rejected
    // explicitly here.
    private static bool IsValidParticipant(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029');
}

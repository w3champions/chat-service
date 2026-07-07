using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Internal;

/// <summary>
/// C7 Task 9 — the HTTP surface for the match-channel lifecycle mm drives: <c>POST /internal/channels</c>
/// (idempotent create-or-get, 200 for BOTH a fresh channel and a duplicate call — the pinned idempotency
/// contract), <c>PUT /internal/channels/{ref}/members</c> (membership delta), and
/// <c>DELETE /internal/channels/{ref}</c> (hard teardown, 200 even for an unknown ref — a 404 would only
/// trigger a pointless mm retry). All three delegate to <see cref="MatchChannelService"/> (Tasks 6-8);
/// this controller owns ONLY input validation, the HTTP shape, and logging.
/// <para>
/// SECURITY (H1): gated by <see cref="InternalHmacAuthAttribute"/> at CLASS level with an Mm-only
/// allow-list — the disjoint HMAC auth realm, never <see cref="UserHasPermissionAttribute"/>. See
/// <c>InternalChannelsControllerTests</c>'s dynamic reflection sweep, which fails CI the day any future
/// <c>internal/*</c> controller (e.g. Task 10's relationship-changes surface) lands without this attribute.
/// </para>
/// <para>
/// VALIDATION: every action re-validates <c>ref</c> against the M1 dot-segment defense
/// (<c>\A[A-Za-z0-9_-]{1,64}\z</c>, compiled — <c>\A</c>/<c>\z</c> rather than <c>^</c>/<c>$</c> so a
/// trailing newline cannot slip through) independent of what the HMAC filter already checked —
/// defense-in-depth against a signed-but-malformed ref reaching Mongo as a lookup key. All validation
/// failures return <see cref="ErrorResult"/> with a GENERIC message (never echoing back which rule
/// failed) via a plain 400. Unexpected exceptions from the domain layer are logged once (caller, verb,
/// ref) and rethrown, surfacing as the framework's body-free 500 — this controller never swallows them.
/// </para>
/// </summary>
[ApiController]
[Route("internal/channels")]
[InternalHmacAuth(InternalCaller.Mm)]
public class InternalChannelsController(MatchChannelService matchChannelService) : ControllerBase
{
    private const string MatchKind = "match";
    private const string GenericValidationError = "Invalid request.";

    // \A/\z (absolute start/end), NOT ^/$ — .NET's `$` also matches immediately before a single
    // trailing '\n' when RegexOptions.Multiline is NOT set, so "abc123\n" would otherwise pass this
    // character-class check despite containing a newline (log-injection + distinct Mongo key risk).
    private static readonly Regex RefPattern =
        new($@"\A[A-Za-z0-9_-]{{1,{ChatLimits.InternalRefMaxLength}}}\z", RegexOptions.Compiled);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] InternalChannelCreateRequest request)
    {
        if (request == null || request.Kind != MatchKind)
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        if (!IsValidRef(request.Ref))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > ChatLimits.InternalChannelNameMaxLength)
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        if (!IsValidMembers(request.Members))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        try
        {
            var channel = await matchChannelService.CreateOrGet(request.Ref, name, request.Members, request.Focus ?? false);

            Log.Information(
                "Internal channel create succeeded {Caller} {Verb} {Ref} memberCount={MemberCount}",
                InternalHmacAuthFilter.ResolveCaller(HttpContext), "POST", request.Ref, request.Members.Count);

            return Ok(InternalChannelDto.FromChannel(channel));
        }
        catch (Exception ex)
        {
            LogUnexpected(ex, "POST", request.Ref);
            throw;
        }
    }

    [HttpPut("{ref}/members")]
    public async Task<IActionResult> UpdateMembers(string @ref, [FromBody] InternalMembersDeltaRequest request)
    {
        if (!IsValidRef(@ref))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        // Null add/remove arrays are tolerated on the wire — coerced to empty here since
        // MatchChannelService.ApplyMembersDelta does NOT null-guard its own list parameters.
        var add = request?.Add ?? new List<string>();
        var remove = request?.Remove ?? new List<string>();

        if (!IsValidMembers(add) || !IsValidMembers(remove))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        try
        {
            await matchChannelService.ApplyMembersDelta(@ref, add, remove, request?.Focus ?? false);

            Log.Information(
                "Internal channel members-delta succeeded {Caller} {Verb} {Ref} addCount={AddCount} removeCount={RemoveCount}",
                InternalHmacAuthFilter.ResolveCaller(HttpContext), "PUT", @ref, add.Count, remove.Count);

            return Ok();
        }
        catch (Exception ex)
        {
            LogUnexpected(ex, "PUT", @ref);
            throw;
        }
    }

    [HttpDelete("{ref}")]
    public async Task<IActionResult> Delete(string @ref)
    {
        if (!IsValidRef(@ref))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        try
        {
            await matchChannelService.DeleteChannel(@ref);

            Log.Information(
                "Internal channel delete succeeded {Caller} {Verb} {Ref}", InternalHmacAuthFilter.ResolveCaller(HttpContext), "DELETE", @ref);

            return Ok();
        }
        catch (Exception ex)
        {
            LogUnexpected(ex, "DELETE", @ref);
            throw;
        }
    }

    private static bool IsValidRef(string @ref) => @ref != null && RefPattern.IsMatch(@ref);

    private static bool IsValidMembers(List<string> members) =>
        members != null
        && members.Count <= ChatLimits.InternalMaxMembersPerCall
        && members.All(m => !string.IsNullOrWhiteSpace(m));

    private void LogUnexpected(Exception ex, string verb, string @ref) =>
        Log.Error(ex, "Internal channels endpoint failed {Caller} {Verb} {Ref}", InternalHmacAuthFilter.ResolveCaller(HttpContext), verb, @ref);
}

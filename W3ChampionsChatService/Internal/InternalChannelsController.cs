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
/// contract), <c>PUT /internal/channels/{ref}/roster</c> (the authoritative full-set membership assertion,
/// 2026-08-05 reconciliation spec, plan D1-D4/D7), <c>POST /internal/channels/epoch-sync</c> (mm's
/// boot-time convergence sweep, plan D8), and <c>DELETE /internal/channels/{ref}</c> (hard teardown, 200
/// even for an unknown ref — a 404 would only trigger a pointless mm retry). All four delegate to
/// <see cref="MatchChannelService"/>; this controller owns ONLY input validation, the HTTP shape, and
/// logging.
/// <para>
/// The roster route is named <c>.../roster</c> rather than <c>.../membership</c> deliberately — a
/// one-character-away name from <c>.../members</c> would have collided with the now-removed legacy delta
/// route this endpoint replaced.
/// </para>
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

        // 2026-08-05 fix wave (final review C1): `name` is cosmetic — members is the authoritative
        // payload — so a name chat cannot store must NEVER reject the whole create. mm applies no
        // length/trim/charset validation of its own to a custom-game lobby name before sending it (any
        // authenticated player can trigger a whitespace-only or >100-char name), and CreateOrGet already
        // knows how to fall back to the ref placeholder for a null name. Empty-after-trim normalizes to
        // null; overlong is truncated. Neither is ever a 400.
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            name = null;
        }
        else if (name.Length > ChatLimits.InternalChannelNameMaxLength)
        {
            name = name[..ChatLimits.InternalChannelNameMaxLength];
        }

        if (!IsValidMembers(request.Members))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        // D10 pair rule (2026-08-05 reconciliation spec): epoch/seq must come TOGETHER — a lone one is
        // ambiguous (an unstamped seq, or a seq with no epoch to compare it against), so exactly one
        // present is a 400.
        if ((request.Epoch != null) != request.Seq.HasValue)
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        if (request.Epoch != null && !IsValidEpoch(request.Epoch))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        if (request.Seq.HasValue && request.Seq.Value < 1)
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        try
        {
            var channel = await matchChannelService.CreateOrGet(
                request.Ref, name, request.Members, request.Focus ?? false,
                request.Epoch, request.Seq, request.Detached ?? false, request.Ladder ?? false);

            // `ladder` is logged alongside `detached` because the two are easy to confuse and only one of
            // them decides whether a muted player can talk in this room — an operator diagnosing "why
            // could a banned user chat in that ladder game" needs to see which flag mm actually sent.
            Log.Information(
                "Internal channel create succeeded {Caller} {Verb} {Ref} memberCount={MemberCount} detached={Detached} ladder={Ladder}",
                InternalHmacAuthFilter.ResolveCaller(HttpContext), "POST", request.Ref, request.Members.Count,
                request.Detached ?? false, request.Ladder ?? false);

            return Ok(InternalChannelDto.FromChannel(channel));
        }
        catch (Exception ex)
        {
            LogUnexpected(ex, "POST", request.Ref);
            throw;
        }
    }

    [HttpPut("{ref}/roster")]
    public async Task<IActionResult> AssertRoster(string @ref, [FromBody] InternalRosterAssertRequest request)
    {
        if (request == null)
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        if (!IsValidRef(@ref))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        if (!IsValidEpoch(request.Epoch))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        if (request.Seq < 1)
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        // Deliberately NOT coerced to empty: for a full-set assertion, null and [] are the difference
        // between "no-op" and "tear the whole lobby's membership down" (plan D7) — the caller must state
        // which it means, so a missing array is a 400.
        if (request.Members == null)
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        if (!IsValidMembers(request.Members))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        // The assertion's authoritative payload is `members`. `name` is cosmetic (create-on-demand only,
        // ignored on an existing channel), so a name chat cannot store must NEVER reject the roster — mm
        // has no per-status retry policy and would re-send the same rejected name forever (2026-08-05 fix
        // wave, final review C1).
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            name = null; // ⇒ ApplyRosterAssertion falls back to the ref placeholder
        }
        else if (name.Length > ChatLimits.InternalChannelNameMaxLength)
        {
            name = name[..ChatLimits.InternalChannelNameMaxLength];
        }

        try
        {
            var outcome = await matchChannelService.ApplyRosterAssertion(
                @ref, request.Epoch, request.Seq, request.Members,
                name, request.Detached ?? false, request.Ladder ?? false);

            // 2026-08-05 fix wave (final review M2): the outcome REPLACES the old unconditional
            // "succeeded" wording — a discarded assertion (stale/duplicate or against a frozen channel)
            // used to log a contradictory "succeeded" line here ALONGSIDE the domain layer's own discard
            // line, exactly on the storm paths (an mm retry storm, or mm asserting a frozen lobby) the
            // staleness/detach gates exist to absorb. One line, the real outcome.
            Log.Information(
                "Internal channel roster-assert {Outcome} {Caller} {Verb} {Ref} epoch={Epoch} seq={Seq} memberCount={MemberCount} detached={Detached} ladder={Ladder}",
                outcome, InternalHmacAuthFilter.ResolveCaller(HttpContext), "PUT", @ref,
                request.Epoch, request.Seq, request.Members.Count, request.Detached ?? false, request.Ladder ?? false);

            // A DISCARDED (stale/detached) assertion is still a 200 — it is a successful no-op, not a
            // failure. mm must not retry a correctly-rejected stale assertion; the domain layer already
            // logged the discard.
            return Ok();
        }
        catch (Exception ex)
        {
            LogUnexpected(ex, "PUT", @ref);
            throw;
        }
    }

    [HttpPost("epoch-sync")]
    public async Task<IActionResult> EpochSync([FromBody] InternalEpochSyncRequest request)
    {
        if (request == null)
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        if (!IsValidEpoch(request.Epoch))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        if (request.LiveLobbyRefs == null)
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        if (request.LiveLobbyRefs.Count > ChatLimits.InternalMaxLiveRefsPerSync)
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        if (request.LiveLobbyRefs.Any(r => !IsValidRef(r)))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        try
        {
            // 2026-08-05 fix wave (final review H2): thread the client's own abort signal through the
            // sweep loop. mm's client timeout is far shorter than a large sweep can take; without this,
            // an aborted mm attempt leaves the sweep running headless while mm's retry launches ANOTHER
            // overlapping one. A cancelled sweep is safe — see ApplyEpochSync's own doc.
            await matchChannelService.ApplyEpochSync(request.Epoch, request.LiveLobbyRefs, HttpContext.RequestAborted);

            Log.Information(
                "Internal channel epoch-sync succeeded {Caller} {Verb} epoch={Epoch} liveRefCount={LiveRefCount}",
                InternalHmacAuthFilter.ResolveCaller(HttpContext), "POST", request.Epoch, request.LiveLobbyRefs.Count);

            return Ok();
        }
        catch (Exception ex)
        {
            LogUnexpected(ex, "POST", "epoch-sync");
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

    // The epoch is an OPAQUE token, never parsed — the SAME character class and length cap that
    // defends `ref` (log injection into the Serilog {Epoch} sink; a polluted Mongo key) is exactly the
    // defense it needs, so it reuses RefPattern deliberately rather than inventing a second regex.
    private static bool IsValidEpoch(string epoch) => epoch != null && RefPattern.IsMatch(epoch);

    private static bool IsValidMembers(List<string> members) =>
        members != null
        && members.Count <= ChatLimits.InternalMaxMembersPerCall
        && members.All(IsValidMemberEntry);

    // 2026-08-05 fix wave (final review M5): mirrors InternalRelationshipChangesController's
    // IsValidParticipant EXACTLY — non-blank AND control-char-free. Before this, a member entry was
    // bounded only by the 64 KB body cap and landed as a lowercased Mongo BattleTag key with no
    // per-entry length or control-char guard, asymmetric with the relationship-changes surface's
    // identical-shaped field. char.IsControl catches an embedded '\n'/'\r'/'\t'/NUL (log-injection);
    // U+2028/U+2029 are checked explicitly because they are category Zl/Zp, not Cc, so char.IsControl
    // alone misses them.
    private static bool IsValidMemberEntry(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029');

    private void LogUnexpected(Exception ex, string verb, string @ref) =>
        Log.Error(ex, "Internal channels endpoint failed {Caller} {Verb} {Ref}", InternalHmacAuthFilter.ResolveCaller(HttpContext), verb, @ref);
}

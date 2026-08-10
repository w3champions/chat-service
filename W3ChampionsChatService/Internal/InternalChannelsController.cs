using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.Internal;

/// <summary>
/// C7 Task 9 — the HTTP surface for the match-channel lifecycle mm drives: <c>POST /internal/channels</c>
/// (idempotent create-or-get, 200 for BOTH a fresh channel and a duplicate call — the pinned idempotency
/// contract), <c>PUT /internal/channels/{ref}/roster</c> (the authoritative full-set membership assertion,
/// 2026-08-05 reconciliation spec, plan D1-D4/D7), <c>POST /internal/channels/epoch-sync</c> (mm's
/// boot-time convergence sweep, plan D8), <c>DELETE /internal/channels/{ref}</c> (hard teardown, 200
/// even for an unknown ref — a 404 would only trigger a pointless mm retry), and
/// <c>POST /internal/channels/{ref}/system-message</c> (Task 4 — a server-authored message published into
/// an EXISTING channel; LOOKUP-ONLY, so an unknown ref is a 404 rather than an implicit create). The
/// first four delegate to <see cref="MatchChannelService"/>; the system-message route delegates to
/// <see cref="ChannelRepository.LoadBySystemRef"/> and <see cref="SystemMessagePublisher"/> instead. This
/// controller owns ONLY input validation, the HTTP shape, and logging.
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
public class InternalChannelsController(
    MatchChannelService matchChannelService,
    ChannelRepository channelRepository,
    SystemMessagePublisher systemMessagePublisher) : ControllerBase
{
    private const string MatchKind = "match";
    private const string GenericValidationError = "Invalid request.";

    // Same text as GenericValidationError, aliased under a 404-shaped name so a reader is not left
    // wondering why a "validation error" backs a lookup miss.
    private const string GenericNotFoundError = GenericValidationError;

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
        else
        {
            // Excerpts.Bounded, not a naive name[..limit] slice: a lobby name is emoji-capable and a raw
            // cut can land mid-surrogate-pair, persisting a lone code unit (final review, finding 10).
            name = Excerpts.Bounded(name, ChatLimits.InternalChannelNameMaxLength);
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
        else
        {
            // Surrogate-safe truncation, same helper and same reason as the create route above.
            name = Excerpts.Bounded(name, ChatLimits.InternalChannelNameMaxLength);
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

    /// <summary>
    /// Publishes a server-authored system message into the match channel identified by
    /// <paramref name="systemRef"/>. LOOKUP-ONLY — deliberately unlike <c>POST /internal/channels</c>:
    /// a system message is meaningless without the room it narrates, so an unknown ref is a 404 rather
    /// than an implicit create (which would leave a memberless channel nobody can ever see).
    /// Idempotent when the caller supplies a dedupeKey — a retry returns 200 and re-publishes nothing.
    /// </summary>
    [HttpPost("{systemRef}/system-message")]
    public async Task<IActionResult> PublishSystemMessage(string systemRef, [FromBody] InternalSystemMessageRequest request)
    {
        if (request == null || !IsValidRef(systemRef))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        var key = request.Key?.Trim();
        var fallbackText = request.FallbackText?.Trim();
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(fallbackText))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        // Same character class as `ref` — the key is logged and becomes a client catalogue lookup, so it
        // gets the same log-injection / control-char defense.
        if (!IsValidRef(key))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        // Defense-in-depth against a signed-but-malformed payload: fallbackText is bounded only by the
        // 64 KB HMAC body cap otherwise, and it persists + fans out to every channel member. TRUNCATED,
        // never rejected — same convention (and, since final-review finding 10, the same surrogate-safe
        // helper) as `name` above: a signed field the caller cannot usefully retry its way out of a 400
        // on. Capped to ChatLimits.MaxMessageLength: this is server-rendered display text a client shows
        // directly, the same shape as a user message body, so it reuses that cap rather than inventing a
        // new number.
        fallbackText = Excerpts.Bounded(fallbackText, ChatLimits.MaxMessageLength);

        // `Params`/`ListParams` validation, applied per-element. Split by what each half actually
        // becomes — and, since final review M3, validated by TWO DIFFERENT rules, not one shared one:
        //   - KEYS become BSON element names on the persisted SystemMessageBody, and are catalogue
        //     placeholder identifiers on the client. They get IsValidRef — the SAME identifier class
        //     `key`/`dedupeKey` get two blocks up, which is also the only guard that keeps a
        //     `$`-prefixed or dotted element name (awkward-to-impossible to query) out of Mongo.
        //   - VALUES (and every list item) are free DISPLAY TEXT that persists and fans out to every
        //     channel member as rendered text — that is the justification, not log injection: no
        //     `Log.*` call in this file or in SystemMessagePublisher ever writes a param value. They get
        //     IsValidDisplayText — control-char- and U+2028/U+2029-free, but (final review M3)
        //     deliberately NOT non-blank: unlike a `members` entry, which is an identity mm cannot
        //     normalize its way out of, a param value is display text `fallbackText` already covers for
        //     rendering, so blank or absent is accepted and stored as-is rather than a permanent 400.
        // NOT length-capped, deliberately: the 64 KB HMAC body cap (InternalHmacAuthFilter) already
        // bounds the worst case, so there is no DoS to defend and no existing per-entry length cap to
        // mirror. A null dictionary means "no params" and is always legal.
        if (!IsValidParams(request.Params) || !IsValidListParams(request.ListParams))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        // DedupeKey is OPTIONAL — absent, empty, or whitespace-only ALL mean "no dedupe" and are never a
        // 400 (mirrors SystemMessagePublisher.Publish's own dedupeKey normalization): mm has no
        // per-status retry policy, so an optional field must never reject the whole call and get retried
        // forever with the same rejected body. Only a NON-empty key that fails the ref character class
        // is rejected — it would otherwise become a Mongo dedupe-index key.
        var dedupeKey = request.DedupeKey?.Trim();
        if (string.IsNullOrEmpty(dedupeKey))
        {
            dedupeKey = null; // ⇒ Publish's "no dedupe" path
        }
        else if (!IsValidRef(dedupeKey))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        try
        {
            var channel = await channelRepository.LoadBySystemRef(SystemChannelKind.Match, systemRef);
            if (channel == null)
            {
                return NotFound(new ErrorResult(GenericNotFoundError));
            }

            var body = new SystemMessageBody
            {
                Key = key,
                Params = request.Params,
                ListParams = request.ListParams,
                FallbackText = fallbackText,
            };

            var result = await systemMessagePublisher.Publish(channel, body, dedupeKey);
            if (result.Code != ChatResultCode.Ok)
            {
                // GENUINELY REACHABLE — this is no longer defense-in-depth. Publish maps a channel that
                // VANISHES between the lookup above and its own AllocateSeq to NotFound (match channels
                // are TTL-backed shells and mm can tear one down via DELETE /internal/channels/{ref}),
                // so a real race lands here. A 404 is the right answer for it: the room this message
                // narrates is gone, and mm must stop retrying rather than hammer a call that can never
                // succeed. Publish's OTHER non-Ok code (TooLong, for a null/blank body) stays unreachable
                // from here, since `key`/`fallbackText` are already validated non-blank above.
                return NotFound(new ErrorResult(GenericNotFoundError));
            }

            Log.Information(
                "Internal system message succeeded {Caller} {Verb} {Ref} key={Key} seq={Seq}",
                InternalHmacAuthFilter.ResolveCaller(HttpContext), "POST", systemRef, key, result.Seq);

            return Ok();
        }
        catch (Exception ex)
        {
            LogUnexpected(ex, "POST", systemRef);
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

    // A member entry is an IDENTITY: non-blank (a blank battleTag is a missing identity with nothing to
    // normalize it into) and control-char-free -- the rule InternalValidation owns for every internal/*
    // surface, deduplicated there by the live-flair change. Post-game chat final review M3: it is
    // deliberately NOT shared with the system-message param/list-item guard below (IsValidDisplayText).
    // That guard had to become MORE permissive (blank/null accepted) once a param value turned out to be
    // display text `fallbackText` already covers, not an identity like this one. Split rather than
    // weakened, so `members` keeps its non-blank guarantee unchanged.
    private static bool IsValidMemberEntry(string value) => InternalValidation.IsValidBattleTag(value);

    // System-message params. A null dictionary means "no params" and is always legal. Keys get the
    // identifier class (they become BSON element names on the persisted body AND client catalogue
    // placeholders); values get IsValidDisplayText. Full rationale at the call site.
    private static bool IsValidParams(Dictionary<string, string> parameters) =>
        parameters == null
        || parameters.All(p => IsValidRef(p.Key) && IsValidDisplayText(p.Value));

    private static bool IsValidListParams(Dictionary<string, List<string>> parameters) =>
        parameters == null
        || parameters.All(p => IsValidRef(p.Key) && p.Value != null && p.Value.All(IsValidDisplayText));

    // Final review M3 (human-ruled): a param value / list item is free DISPLAY TEXT that persists and
    // fans out to every channel member as rendered text — it is NOT an identity like a `members` entry,
    // and `fallbackText` already renders for a client that does not recognise `key`. So unlike
    // IsValidIdentityText above, blank or null is ACCEPTED and stored as-is rather than a permanent 400
    // mm cannot usefully retry its way out of (the same reasoning the endpoint already applies to
    // `fallbackText` itself and to a blank `dedupeKey`). Still control-char- and U+2028/U+2029-free:
    // persistence plus fan-out to every member is reason enough to keep that half of the guard, even
    // though it is never logged. `value == null` is valid here (a JSON `null` param value / list item),
    // unlike IsValidRef/IsValidIdentityText, which treat null as invalid.
    private static bool IsValidDisplayText(string value) =>
        value == null || !value.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029');

    private void LogUnexpected(Exception ex, string verb, string @ref) =>
        Log.Error(ex, "Internal channels endpoint failed {Caller} {Verb} {Ref}", InternalHmacAuthFilter.ResolveCaller(HttpContext), verb, @ref);
}

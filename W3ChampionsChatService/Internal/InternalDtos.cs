using System;
using System.Collections.Generic;
using W3ChampionsChatService.Channels;

namespace W3ChampionsChatService.Internal;

/// <summary>
/// <c>POST /internal/channels</c> request body (C7 Task 9) — mm's match-channel create-or-get call.
/// <see cref="Kind"/> is the extensibility discriminator: only <c>"match"</c> is accepted today, but
/// the field stays so a future system-channel kind can be added without a breaking wire change; unknown
/// kinds are rejected 400. <see cref="Ref"/> is the caller's systemRef (a URL-encoded <c>nanoid(10)</c>),
/// re-validated server-side against the M1 dot-segment defense regardless of what the caller sent.
/// <see cref="Members"/> is the initial battleTag roster; <see cref="Focus"/> hints whether newly-added
/// members should have the channel auto-focused client-side (defaults to <c>false</c> when omitted).
/// <para>
/// <see cref="Epoch"/>/<see cref="Seq"/>/<see cref="Detached"/> are OPTIONAL additions (2026-08-05
/// reconciliation spec, plan D10) — absent ⇒ today's create behavior, byte-for-byte (the transition
/// guarantee for today's not-yet-deployed mm). They MUST come together: exactly one of
/// <see cref="Epoch"/>/<see cref="Seq"/> present is a 400. When present, they stamp
/// <c>(epoch, seq)</c> so a late-landing create retry cannot resurrect a member a newer roster
/// assertion already removed (<see cref="MatchChannelService.CreateOrGet"/>'s member handling is
/// additive). <see cref="Detached"/> marks a channel born already frozen — the LADDER-MATCH case:
/// chat-service uses one <c>SystemKind=Match</c> for both custom-lobby and ladder-match channels, and
/// ladder refs are never in mm's <c>liveLobbyRefs</c> (that registry only holds custom lobbies), so
/// without birth-detach the FIRST epoch sync after any mm restart would tear down every in-progress
/// ladder game's chat.
/// </para>
/// </summary>
public class InternalChannelCreateRequest
{
    public string Kind { get; set; }
    public string Ref { get; set; }

    /// <summary>
    /// Cosmetic display name — NORMALIZED, NEVER REJECTED (2026-08-05 fix wave, final review C1):
    /// trimmed and clamped to <see cref="Domain.ChatLimits.InternalChannelNameMaxLength"/> (100 chars);
    /// empty-after-trim falls back to <see cref="Ref"/> as a placeholder. mm applies no length/trim/
    /// charset validation of its own before sending this, so a name chat cannot store must never be able
    /// to reject an otherwise-valid create.
    /// </summary>
    public string Name { get; set; }
    public List<string> Members { get; set; }
    public bool? Focus { get; set; }
    public string Epoch { get; set; }
    public long? Seq { get; set; }
    public bool? Detached { get; set; }

    /// <summary>
    /// Declares this ref a LADDER match rather than a custom-game lobby. Absent/false ⇒ custom lobby
    /// (today's behavior, byte-for-byte). The ONLY consumer is the send-path mute scope
    /// (<see cref="Channels.ChannelModeration.IsMuteEnforced"/>): a lounge-muted or shadow-banned
    /// player must not be able to talk in a ladder game's in-game/post-game room, while a custom
    /// lobby stays exempt.
    /// <para>
    /// Deliberately SEPARATE from <see cref="Detached"/> even though mm happens to send both together
    /// on the ladder create path today. Detach means "membership is frozen, sweeps skip this"; it is
    /// also set on every custom lobby at GAME_STARTED, so it does not — and must never be made to —
    /// answer "is this ladder". Inferring one from the other would silently un-moderate ladder rooms
    /// the day mm's detach timing changes.
    /// </para>
    /// STICKY-TRUE server-side: see <see cref="Channels.ChatChannel.Ladder"/>.
    /// </summary>
    public bool? Ladder { get; set; }
}

/// <summary>
/// <c>PUT /internal/channels/{ref}/roster</c> request body — mm's AUTHORITATIVE full-set membership
/// assertion (2026-08-05 reconciliation spec §1), the sole membership-mutation protocol mm drives.
/// <see cref="Epoch"/> is an OPAQUE token (mm's authority generation, fresh per mm boot) — compared
/// for equality only, never parsed or ordered, and re-validated against the same character class and
/// length cap as <c>ref</c>. <see cref="Seq"/> is mm's per-(lobby, epoch) monotonic counter and MUST
/// be >= 1 (0 is chat-side's "nothing applied yet under this epoch" sentinel).
/// <see cref="Members"/> is the COMPLETE member set and is NOT null-tolerant: null and [] differ by
/// "no-op" vs "tear the whole lobby's membership down", so the caller must state which it means.
/// <see cref="Name"/> is the display name used ONLY when the
/// assertion must create the channel on demand (mm's boot-race healing — a recreated room must not
/// display its nanoid ref); ignored for an existing channel; optional (null ⇒ ref placeholder).
/// NORMALIZED, NEVER REJECTED (2026-08-05 fix wave, final review C1): trimmed and clamped to
/// <see cref="Domain.ChatLimits.InternalChannelNameMaxLength"/>; empty-after-trim also falls back to the
/// ref placeholder — a cosmetic field must never block an authoritative roster.
/// <see cref="Detached"/> marks mm's GAME_STARTED final assertion: the set is applied, then the
/// room freezes. There is deliberately NO Focus field — mm has never sent one on any internal call,
/// and a new contract carries no dead parameters (plan §2.3).
/// </summary>
public class InternalRosterAssertRequest
{
    public string Epoch { get; set; }
    public long Seq { get; set; }
    public List<string> Members { get; set; }
    public string Name { get; set; }
    public bool? Detached { get; set; }

    /// <summary>
    /// Same meaning as <see cref="InternalChannelCreateRequest.Ladder"/>. Carried on this route too
    /// because the roster endpoint is ALSO a channel-creating path: mm's ladder create has a
    /// retry-on-failure fallback that converges through <c>PUT .../roster</c> instead, and that
    /// assertion may well be the call that creates the channel on demand. Without the flag here, a
    /// ladder room born on the fallback path would be indistinguishable from a custom lobby and would
    /// silently escape the mute gate.
    /// <para>
    /// Applied BEFORE the detach-freeze and staleness gates, and independently of them: it is a
    /// property assertion about the ref, not a membership mutation, so a stale/duplicate/frozen
    /// assertion that legitimately discards its ROSTER must still be able to correct the room's
    /// classification.
    /// </para>
    /// </summary>
    public bool? Ladder { get; set; }
}

/// <summary>
/// <c>POST /internal/channels/epoch-sync</c> request body — mm's boot-time authoritative world
/// (2026-08-05 reconciliation spec §3). <see cref="LiveLobbyRefs"/> is the COMPLETE set of lobby
/// refs mm still knows about (the EMPTY set after a crash, since lobbies are ephemeral in mm) and is
/// NOT null-tolerant for the same reason <see cref="InternalRosterAssertRequest.Members"/> isn't.
/// Every entry is re-validated against the same ref character class, and the array is capped at
/// <see cref="Domain.ChatLimits.InternalMaxLiveRefsPerSync"/>.
/// </summary>
public class InternalEpochSyncRequest
{
    public string Epoch { get; set; }
    public List<string> LiveLobbyRefs { get; set; }
}

/// <summary>
/// <c>POST /internal/relationship-changes</c> request body (C7 Task 10) — website-backend's change-ping
/// that a block/unblock/friend edit landed for a pair of battleTags. <see cref="Type"/> is one of the
/// EXACT wire literals <c>"block"</c>/<c>"unblock"</c>/<c>"friendAdd"</c>/<c>"friendRemove"</c>, matched
/// case-sensitively via a plain <c>switch</c> (deliberately NOT an enum + <c>JsonStringEnumConverter</c> —
/// the literal strings ARE the cross-repo contract). The type is logged but never forwarded: invalidation
/// is type-agnostic, so the controller simply drops the cache entry for BOTH <see cref="Actor"/> and
/// <see cref="Target"/> and lets C5/C6 re-derive the relationship state on the next read (the 5-min
/// snapshot TTL is the backstop if a ping is ever missed). <see cref="Actor"/>/<see cref="Target"/> are
/// re-validated non-blank AND control-char-free server-side — the log-injection defense a Task 9 review
/// flagged on <c>ref</c>, held here too even for this trusted-but-signed caller.
/// </summary>
public class InternalRelationshipChangeRequest
{
    public string Type { get; set; }
    public string Actor { get; set; }
    public string Target { get; set; }
}

/// <summary>
/// <c>POST /internal/channels/{ref}/system-message</c> request body — a server-authored message
/// published into an EXISTING channel. Lookup-only: unlike the create/roster routes this one never
/// creates a channel, so an unknown ref is a 404 rather than an implicit create.
/// <para>
/// <see cref="Key"/> and <see cref="FallbackText"/> are both REQUIRED: the key is what a client
/// renders through its own locale catalogue, and the fallback is the only thing a client that does not
/// know the key (or the moderation history endpoint, which has no catalogue at all) can display.
/// <see cref="Key"/> is re-validated server-side against the same character class as <c>ref</c> —
/// <c>\A[A-Za-z0-9_-]{1,64}\z</c>, where 64 is <see cref="Domain.ChatLimits.InternalRefMaxLength"/> —
/// so a dotted or namespaced catalogue key (e.g. <c>match.intro</c> or <c>chat:match_intro</c>) is
/// rejected 400; catalogue keys for this endpoint must stick to alphanumerics, <c>_</c>, and <c>-</c>.
/// <see cref="DedupeKey"/> is optional but strongly recommended — mm retries on timeout, and without a
/// key a retried publish posts twice. An ABSENT, EMPTY, or WHITESPACE-ONLY <see cref="DedupeKey"/> all
/// mean the same thing — "no dedupe" — and the endpoint deliberately never rejects the call over it;
/// only a non-empty key that fails the <see cref="Key"/> character class above is a 400.
/// </para>
/// </summary>
public class InternalSystemMessageRequest
{
    public string Key { get; set; }
    public Dictionary<string, string> Params { get; set; }
    public Dictionary<string, List<string>> ListParams { get; set; }
    public string FallbackText { get; set; }
    public string DedupeKey { get; set; }
}

/// <summary>
/// Body of <c>POST /internal/profile-changes</c>: the players whose flair website-backend believes
/// may have changed. Capped at <see cref="Domain.ChatLimits.InternalMaxMembersPerCall"/>; the sender
/// chunks larger sets into separate requests.
/// </summary>
public class InternalProfileChangeRequest
{
    public List<string> BattleTags { get; set; }
}

/// <summary>
/// REST projection of a <see cref="ChatChannel"/> returned by <c>POST /internal/channels</c> (C7 Task
/// 9) — System.Text.Json's default camelCase serialization matches the wire contract mm expects, so no
/// custom naming policy is needed.
/// </summary>
/// <para>
/// <c>Ladder</c> is the STORED classification read back, not an echo of the request: because the flag
/// is sticky-true, "what mm sent" and "what the channel now holds" diverge on exactly the calls worth
/// diagnosing (a create omitting the flag against an already-ladder ref). Additive — mm may ignore it.
/// </para>
public record InternalChannelDto(string Id, string Ref, string Name, DateTime? ExpiresAt, bool Ladder)
{
    public static InternalChannelDto FromChannel(ChatChannel channel) =>
        new(channel.Id, channel.SystemRef, channel.Name, channel.ExpiresAt, channel.Ladder);
}

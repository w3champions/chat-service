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
/// REST projection of a <see cref="ChatChannel"/> returned by <c>POST /internal/channels</c> (C7 Task
/// 9) — System.Text.Json's default camelCase serialization matches the wire contract mm expects, so no
/// custom naming policy is needed.
/// </summary>
public record InternalChannelDto(string Id, string Ref, string Name, DateTime? ExpiresAt)
{
    public static InternalChannelDto FromChannel(ChatChannel channel) =>
        new(channel.Id, channel.SystemRef, channel.Name, channel.ExpiresAt);
}

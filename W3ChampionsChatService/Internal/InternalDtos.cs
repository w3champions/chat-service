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
/// </summary>
public class InternalChannelCreateRequest
{
    public string Kind { get; set; }
    public string Ref { get; set; }
    public string Name { get; set; }
    public List<string> Members { get; set; }
    public bool? Focus { get; set; }
}

/// <summary>
/// <c>PUT /internal/channels/{ref}/members</c> request body (C7 Task 9) — mm's membership delta.
/// <see cref="Add"/>/<see cref="Remove"/> tolerate a null array on the wire: the controller coerces
/// either to an empty list before calling <see cref="MatchChannelService.ApplyMembersDelta"/>, which
/// does NOT null-guard its own list parameters.
/// </summary>
public class InternalMembersDeltaRequest
{
    public List<string> Add { get; set; }
    public List<string> Remove { get; set; }
    public bool? Focus { get; set; }
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

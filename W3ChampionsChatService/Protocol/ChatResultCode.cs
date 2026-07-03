using System.Text.Json.Serialization;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// Typed result code returned by every hub method (program contract §1, C3-plan.md decision 5) —
/// the pinned, exact set. Today's silent rejects (empty send, over-length, etc.) become explicit
/// values here; the ONLY deliberate silent paths in the whole design are shadow-ban drops (C4) and
/// DM declines (C5). Serialized as its string name (not the underlying int) via
/// <see cref="JsonStringEnumConverter"/> so wire payloads/logs stay self-describing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChatResultCode
{
    Ok,
    Throttled,
    NotMember,
    Muted,
    TooLong,
    NotFound,
    PermissionDenied,
}

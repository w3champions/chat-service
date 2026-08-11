using System.Text.Json.Serialization;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// Typed result code returned by every hub method (program contract §1, C3-plan.md decision 5) —
/// the pinned, exact set. Today's silent rejects (empty send, over-length, etc.) become explicit
/// values here; the ONLY deliberate silent paths in the whole design are shadow-ban drops (C4) and
/// DM declines (C5). Serialized as its string name (not the underlying int) via
/// <see cref="JsonStringEnumConverter"/> so wire payloads/logs stay self-describing.
/// <c>UnsupportedCommand</c> (2026-08-11) is the slash-command reject at step 4.5: the service supports
/// no chat commands, and broadcasting a <c>/w</c> verbatim leaks the intended recipient and body to the
/// channel. It gets its own value rather than reusing <c>TooLong</c> because the launcher renders
/// TooLong as a character-limit message, which is actively misleading for an 11-character command.
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
    UnsupportedCommand,
}

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// The coalesced <c>ChannelActivity</c> push an UNFOCUSED member with notification level
/// <see cref="Domain.NotificationLevel.All"/> receives instead of the full <c>MessageReceived</c>
/// payload (the "no full payloads to unfocused" guardrail — C3 acceptance 1). It carries only the
/// channel and the latest sequence, so coalescing a burst into one push is lossless (a later push
/// with the newest <see cref="LastSeq"/> supersedes any it replaced).
/// <para>
/// <see cref="Preview"/> is a forward-declared slot for C5's DM message preview — the field exists in
/// the C3 wire contract so clients can bind it now, but it is ALWAYS null in C3 (only DM channels ever
/// populate it, and only once C5 lands). Typed <c>object</c> deliberately: C3 does not commit to the
/// preview's shape, and a null serializes identically regardless of the eventual type.
/// </para>
/// </summary>
public record ChannelActivityDto(string ChannelId, long LastSeq, object Preview = null);

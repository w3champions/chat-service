namespace W3ChampionsChatService.Protocol;

/// <summary>
/// The coalesced <c>ChannelActivity</c> push an UNFOCUSED member with notification level
/// <see cref="Domain.NotificationLevel.All"/> receives instead of the full <c>MessageReceived</c>
/// payload (the "no full payloads to unfocused" guardrail — C3 acceptance 1). It carries only the
/// channel and the latest sequence, so coalescing a burst into one push is lossless (a later push
/// with the newest <see cref="LastSeq"/> supersedes any it replaced).
/// <para>
/// <see cref="Preview"/> was a forward-declared slot for C5's DM message preview in C3 (field present in
/// the wire contract, always null). C5 (Task 9, D15) fills it: for an accepted <c>Dm</c> channel's
/// activity it carries a <see cref="DmActivityPreviewDto"/> (sender + a bounded excerpt); for every other
/// channel type (<c>GroupDm</c>/<c>Public</c>/<c>System</c>) it remains null (OQ-7, strict Dm-only
/// scope — groups get plain activity). Typed <c>object</c> deliberately: the wire contract does not
/// commit to a single preview shape, and a null serializes identically regardless of the type.
/// </para>
/// </summary>
public record ChannelActivityDto(string ChannelId, long LastSeq, object Preview = null);

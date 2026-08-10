namespace W3ChampionsChatService.Protocol;

/// <summary>
/// The coalesced <c>ChannelActivity</c> push an UNFOCUSED member with notification level
/// <see cref="Domain.NotificationLevel.All"/> receives instead of the full <c>MessageReceived</c>
/// payload (the "no full payloads to unfocused" guardrail — C3 acceptance 1). It carries only the
/// channel and the latest sequence, so coalescing a burst into one push is lossless (a later push
/// with the newest <see cref="LastSeq"/> supersedes any it replaced).
/// <para>
/// <see cref="Preview"/> carries an <see cref="ActivityPreviewDto"/> — sender snapshot, bounded excerpt,
/// and the channel's own <c>ChannelType</c>/<c>SystemKind</c> — for every PREVIEW-ELIGIBLE channel
/// class, and null for every other. Today's eligible set is <c>Dm</c> (C5/OQ-7) plus
/// <c>System</c>+<c>Match</c> (post-game chat's one-time nudge); <c>GroupDm</c>/<c>Public</c>/
/// <c>SemiPublic</c>/<c>System</c>+<c>Clan</c> get plain badge-only activity. A SYSTEM message (null
/// sender, no content) produces no preview in any channel.
/// </para>
/// <para>
/// A CLIENT MUST ROUTE ON THE PREVIEW'S <c>channelType</c>/<c>systemKind</c>, NEVER ON THE FIELD'S
/// PRESENCE. That is not style advice — it is the bug this shape exists to prevent. While the slot was
/// Dm-only, the launcher used "a preview is present" as a proxy for "this is a DM" and raised a DM-grade
/// toast + chat sound + OS notification without ever reading the channel's type; the moment match
/// channels became preview-eligible, every player who closed the score screen would have been flooded
/// with DM-grade notifications for every post-game message. The preview now names its own class
/// precisely so the next class that opts in (GroupDm, clan, …) is a one-line condition change in
/// <see cref="FanOut.FanOutEngine"/> rather than a new field and a new client gate.
/// </para>
/// <para>
/// Typed <c>object</c> deliberately: the wire contract does not commit to a single preview shape, and a
/// null serializes identically regardless of the declared type.
/// </para>
/// </summary>
public record ChannelActivityDto(string ChannelId, long LastSeq, object Preview = null);

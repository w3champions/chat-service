namespace W3ChampionsChatService.Protocol;

/// <summary>
/// The coalesced <c>ChannelActivity</c> push an UNFOCUSED member with notification level
/// <see cref="Domain.NotificationLevel.All"/> receives instead of the full <c>MessageReceived</c>
/// payload (the "no full payloads to unfocused" guardrail — C3 acceptance 1). It carries only the
/// channel and the latest sequence, so coalescing a burst into one push is lossless (a later push
/// with the newest <see cref="LastSeq"/> supersedes any it replaced).
/// <para>
/// TWO preview slots, deliberately. The deployed launcher routes its DM-style toast + chat sound + OS
/// notification on the mere PRESENCE of <see cref="Preview"/> (<c>chat-messages.ts</c>'s
/// <c>ingestChannelActivity</c> early-returns on <c>if (!activity.preview) return;</c> and never reads
/// the channel's type), so widening <see cref="Preview"/> beyond Dm would silently turn every post-game
/// message into a DM-grade notification on clients already in the wild.
/// </para>
/// <list type="bullet">
/// <item><see cref="Preview"/> — FROZEN LEGACY. Carries a <see cref="DmActivityPreviewDto"/> for an
/// accepted <c>Dm</c> channel's user message and NOTHING else: <c>GroupDm</c>/<c>Public</c>/
/// <c>SemiPublic</c>/<c>System</c> (match and clan alike) leave it null (OQ-7's original strict Dm-only
/// scope). Never widen it — that scope is exactly what makes a match channel's activity ship DARK to an
/// old client. DELETABLE once a launcher that reads <see cref="ActivityPreview"/> is the deployed floor
/// (post-game chat Plan C); until then it is the only preview an old client can see.</item>
/// <item><see cref="ActivityPreview"/> — the SUPERSEDING slot. Carries an
/// <see cref="ActivityPreviewDto"/> for EVERY preview-eligible channel class, <c>Dm</c> included, and
/// the preview itself names its <c>ChannelType</c>/<c>SystemKind</c>. A new client reads only this slot
/// and routes on those fields, never on presence — so the next class that opts in (GroupDm, clan, …) is
/// a one-line condition change in <c>FanOutEngine</c> rather than a third field and a third client
/// gate.</item>
/// </list>
/// <para>
/// A <c>Dm</c> therefore carries BOTH (old clients keep their toast, new clients read the typed slot);
/// a match channel carries ONLY <see cref="ActivityPreview"/>; every other class carries neither. A
/// SYSTEM message (null sender, no content) carries neither in any channel.
/// </para>
/// <para>
/// Both slots are typed <c>object</c> deliberately: the wire contract does not commit to a single
/// preview shape, and a null serializes identically regardless of the declared type.
/// <see cref="ActivityPreview"/> is a TRAILING defaulted parameter so every existing positional
/// construction site keeps compiling unchanged.
/// </para>
/// </summary>
public record ChannelActivityDto(
    string ChannelId,
    long LastSeq,
    object Preview = null,
    object ActivityPreview = null);

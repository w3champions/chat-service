using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// The <see cref="ChannelActivityDto.Preview"/> payload — the FROZEN LEGACY slot (spec §7 — "DMs
/// additionally carry a preview"; C5 plan decision D15/T9). Populated ONLY for <c>Dm</c> channels
/// (OQ-7, strict scope), and that scope is now load-bearing rather than merely historical: the deployed
/// launcher routes a DM-grade toast/sound/OS-notification on the mere PRESENCE of <c>preview</c>, so
/// <c>GroupDm</c>/<c>Public</c>/<c>SemiPublic</c>/<c>System</c> activity MUST keep carrying a null
/// <see cref="ChannelActivityDto.Preview"/>. Anything wider goes in
/// <see cref="ChannelActivityDto.ActivityPreview"/> (an <see cref="ActivityPreviewDto"/>), which names
/// its own channel class instead of leaving the client to infer one from presence.
/// <see cref="SenderBattleTag"/>/<see cref="SenderName"/> are the persisted message's sender snapshot
/// (no extra lookup — reused from the same source the focused <c>MessageReceived</c> delivery already
/// built), and <see cref="Excerpt"/> is the message content's first
/// <see cref="ChatLimits.DmPreviewExcerptLength"/> characters (mention-inbox "~120 chars"
/// precedent, spec §5) — a plain bounded substring, no word-boundary trimming.
/// </summary>
public record DmActivityPreviewDto(string SenderBattleTag, string SenderName, string Excerpt);

using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// The <see cref="ChannelActivityDto.ActivityPreview"/> payload — the SUPERSEDING preview slot that
/// replaces <see cref="DmActivityPreviewDto"/>/<see cref="ChannelActivityDto.Preview"/> (post-game chat
/// Plan A Task 6). Populated for EVERY preview-eligible channel class, <c>Dm</c> included, whenever the
/// activity was caused by a <see cref="MessageKind.User"/> message. Today's eligible set is <c>Dm</c>
/// (C5/OQ-7) plus <c>System</c>+<see cref="SystemChannelKind.Match"/> (post-game chat's one-time nudge);
/// <c>GroupDm</c>/<c>Public</c>/<c>SemiPublic</c>/<c>System</c>+<see cref="SystemChannelKind.Clan"/>
/// stay preview-free, and a busy lounge keeps its badge-only treatment.
/// <para>
/// <see cref="ChannelType"/>/<see cref="SystemKind"/> exist so a client routes on WHAT KIND OF ROOM this
/// is rather than on whether a field happens to be present. That presence-as-proxy-for-Dm inference is
/// the exact flaw that froze the legacy slot: a client that reads only this slot and switches on these
/// two fields cannot be broken by a future class opting in, so widening the eligible set stays a
/// one-line condition change in <c>FanOutEngine</c> instead of a new field plus a new client gate.
/// Both ride the wire as their ORDINAL (there is no global <c>JsonStringEnumConverter</c>, and neither
/// enum carries a per-type one), which is deliberate: it matches how <c>ChatChannel.type</c> /
/// <c>ChannelDto</c> / <c>ModerationChannelDto</c> already serialize, so a client may compare this
/// field directly against the channel's own <c>type</c>. Giving this DTO a string form would make the
/// same enum two different things on one wire.
/// </para>
/// <para>
/// <see cref="SenderBattleTag"/>/<see cref="SenderName"/> are the persisted message's sender snapshot
/// (no extra lookup — reused from the same <c>MessageDto</c> the focused <c>MessageReceived</c> delivery
/// already built) and <see cref="Excerpt"/> is <c>Excerpts.Bounded</c> of the content
/// (<see cref="ChatLimits.DmPreviewExcerptLength"/> chars, surrogate-safe). A SYSTEM message has no
/// sender and no content, so it produces no preview in either slot.
/// </para>
/// </summary>
public record ActivityPreviewDto(
    string SenderBattleTag,
    string SenderName,
    string Excerpt,
    ChannelType ChannelType,
    SystemChannelKind? SystemKind);

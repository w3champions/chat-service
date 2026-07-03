namespace W3ChampionsChatService.Protocol;

/// <summary>
/// Server→client push (spec §11) telling the client to drop a channel from its list — e.g. a
/// moderator removal, a DM-request decline, or a channel deletion. Delivered ONLY to the target
/// user's LIVE connection, via <see cref="FanOut.FanOutEngine.PushChannelRemoved"/>; a no-op if the
/// user is currently offline (the membership row is already durably removed by the caller before the
/// push, so an offline user's next <c>SessionState</c> on connect simply omits the channel).
/// <para>
/// CONTRACT COMPLETENESS (C3 Task 18): C5/C7 own the actual trigger — C3 only pins this shape and
/// provides the emit helper; there are no production callers yet, only tests. See
/// <see cref="FanOut.FanOutEngine.PushChannelRemoved"/>'s doc comment for a deliberate scope
/// boundary this helper defers to the eventual C5/C7 caller (whether a forced removal of a
/// currently-focused viewer should also emit <c>ViewersChanged</c> to the channel's remaining
/// viewers).
/// </para>
/// </summary>
public record ChannelRemovedDto(string ChannelId);

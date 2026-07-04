using System;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// One entry of the caller's pending-Dm-request tray (spec §11 SessionState slot; C5 T2/D18). Also
/// the payload carried on the targeted <see cref="ChatEvents.RequestReceived"/> push. Wire-facing:
/// <see cref="FromBattleTag"/> is <c>ChatChannel.RequestInitiatedBy</c> (safe to expose — D3), never
/// anything decline-related (decline lives on the recipient's own membership row and is never
/// serialized to anyone).
/// </summary>
public record PendingDmRequestDto(string ChannelId, string FromBattleTag, DateTime RequestedAt);

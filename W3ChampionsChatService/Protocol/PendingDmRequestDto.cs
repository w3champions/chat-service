using System;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// One entry of the caller's pending-Dm-request tray (spec §11 SessionState slot; C5 T2/D18). Also
/// the payload carried on the targeted <see cref="ChatEvents.RequestReceived"/> push. Wire-facing:
/// <see cref="FromBattleTag"/> is <c>ChatChannel.RequestInitiatedBy</c> (safe to expose — D3), never
/// anything decline-related (decline lives on the recipient's own membership row and is never
/// serialized to anyone).
/// <para>
/// <see cref="SenderIsAdmin"/> (wire <c>senderIsAdmin</c>, always serialized): whether the REQUESTER
/// carries admin flair, so a client can render an admin-originated pending request accordingly. Server-
/// derived only — in the SessionState tray from the requester's cached user_directory flair
/// (<see cref="Domain.ChatProfile.HasAdminIcon"/>, which chat-service forces solely from the trusted JWT
/// <c>IsAdmin</c>), so it exposes nothing beyond the requester's already-public admin name colour + icon.
/// </para>
/// </summary>
public record PendingDmRequestDto(string ChannelId, string FromBattleTag, DateTime RequestedAt, bool SenderIsAdmin = false);

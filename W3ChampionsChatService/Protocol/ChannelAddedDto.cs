using W3ChampionsChatService.Channels;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// Server→client push (spec §11) when a channel is newly added to the target user's channel list —
/// e.g. a lobby/ladder auto-join or an invite acceptance. Delivered ONLY to the user's LIVE
/// connection, via <see cref="FanOut.FanOutEngine.PushChannelAdded"/>; a no-op if the user is
/// currently offline (the channel + membership are already durably persisted by the caller before
/// the push, so an offline user simply picks the channel up on the next <c>SessionState</c> on
/// connect).
/// <para>
/// <see cref="Channel"/> is the raw <see cref="ChatChannel"/> — consistent with
/// <see cref="SessionStateDto"/>'s <see cref="ChannelDto.Channel"/> (which also serializes the raw
/// domain type), because nothing on <see cref="ChatChannel"/> is boundary-private.
/// <see cref="Membership"/> is this user's own membership projection, reusing the Task-7
/// <see cref="MembershipDto"/> shape (<see cref="MembershipDto.From"/> is the shared mapper).
/// <see cref="Focus"/> is a CLIENT DIRECTIVE, not a server-focus-state mirror: <c>true</c> tells the
/// client to focus the channel immediately (e.g. a lobby-join or ladder-load auto-open); <c>false</c>
/// just adds it to the list without switching the client's active view.
/// </para>
/// CONTRACT COMPLETENESS (C3 Task 18): C5/C7 own the actual trigger (channel creation / invite-join
/// flows) — C3 only pins this shape and provides the emit helper; there are no production callers
/// yet, only tests.
/// </summary>
public record ChannelAddedDto(ChatChannel Channel, MembershipDto Membership, bool Focus);

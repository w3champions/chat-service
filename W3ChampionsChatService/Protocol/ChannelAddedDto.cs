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
/// CONTRACT COMPLETENESS (C3 Task 18): C3 pins this shape and provides the emit helper
/// (<see cref="FanOut.FanOutEngine.PushChannelAdded"/>); C5/C7 now drive it in production — C5's
/// <c>ChatHub.OpenDm</c> (DM first-message materialization), <c>ChatHub.CreateGroup</c> and
/// <c>ChatHub.AddGroupMember</c> (GroupDm), plus C7's <c>MatchChannelService.AddMemberWithInvariant</c>
/// (System-match auto-join / one-match-channel swap).
/// </summary>
public record ChannelAddedDto(ChatChannel Channel, MembershipDto Membership, bool Focus);

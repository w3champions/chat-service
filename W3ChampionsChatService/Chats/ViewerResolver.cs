using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;

namespace W3ChampionsChatService.Chats;

/// <summary>
/// Builds a <see cref="ChannelViewerDto"/> for a roster battleTag. The SINGLE place that knows the
/// session → connection → <see cref="ChatUser"/> → <see cref="ChatProfile"/> hop, shared by
/// <c>ChatHub.FocusChannel</c> (initial roster) and <see cref="FanOut.ViewersAccumulator"/> (roster
/// deltas) so the two can never construct roster entries differently.
/// <para>
/// PURELY IN-MEMORY by design: a roster entry is by definition an online AND focused viewer, so its
/// <see cref="ChatUser"/> is already cached in <see cref="ConnectionMapping"/> from that user's own
/// connect. This must never grow a Mongo or website-backend read — <c>FocusChannel</c> is a hot path
/// and the roster can be several hundred entries.
/// </para>
/// <para>
/// Degradation is per-field and never throws: a missing session falls back to the battleTag as the
/// display name (pre-existing behaviour, preserved), and a missing <see cref="ChatUser"/> yields a
/// null <see cref="ChannelViewerDto.Profile"/>. Dropping the entry would be worse — the viewer would
/// silently vanish from the roster.
/// </para>
/// </summary>
public class ViewerResolver(ISessionRegistry sessionRegistry, ConnectionMapping connections)
{
    private readonly ISessionRegistry _sessionRegistry = sessionRegistry;
    private readonly ConnectionMapping _connections = connections;

    public ChannelViewerDto Resolve(string battleTag)
    {
        var session = _sessionRegistry.GetByBattleTag(battleTag);
        if (session == null)
        {
            return new ChannelViewerDto(battleTag, battleTag, null);
        }

        var name = session.Identity?.Name ?? battleTag;
        var chatUser = _connections.GetUser(session.ConnectionId);

        return new ChannelViewerDto(
            battleTag,
            name,
            chatUser == null ? null : ChatProfileMapper.FromChatUser(chatUser));
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.FanOut;

public interface IFlairRefresher
{
    Task Refresh(string battleTag);
}

/// <summary>
/// Re-resolves one player's flair after website-backend reports a change, then pushes it to everyone
/// who can currently see them.
/// <para>
/// Reuses <see cref="IChatAuthenticationService.GetUserFromIdentity"/> rather than reading settings
/// directly, so admin colour/icon forcing, the three-tier fallback and the never-clobber invariant all
/// come for free and cannot drift from the connect path. Because it also refreshes
/// <see cref="ConnectionMapping"/>, the changed player's own subsequent messages carry the new flair
/// within the same connection.
/// </para>
/// <para>
/// A player with no live session is a no-op: their next connect re-enriches anyway. Work is therefore
/// bounded by the set of players currently online in chat, not by website-backend's write volume.
/// </para>
/// </summary>
public class FlairRefresher(
    ISessionRegistry sessionRegistry,
    IChatAuthenticationService chatAuthenticationService,
    ConnectionMapping connections,
    UserDirectoryRepository userDirectory,
    FocusRegistry focusRegistry,
    IHubContext<ChatHub> hubContext,
    TimeProvider timeProvider) : IFlairRefresher
{
    private readonly ISessionRegistry _sessionRegistry = sessionRegistry;
    private readonly IChatAuthenticationService _chatAuthenticationService = chatAuthenticationService;
    private readonly ConnectionMapping _connections = connections;
    private readonly UserDirectoryRepository _userDirectory = userDirectory;
    private readonly FocusRegistry _focusRegistry = focusRegistry;
    private readonly IHubContext<ChatHub> _hubContext = hubContext;
    private readonly TimeProvider _timeProvider = timeProvider;

    public async Task Refresh(string battleTag)
    {
        var session = _sessionRegistry.GetByBattleTag(battleTag);
        if (session == null) return;

        var resolution = await _chatAuthenticationService.GetUserFromIdentity(session.Identity);

        // THE RULE (spec §5). A wb blip degrades to a tier-3 profile with FreshFromWb false. Acting on
        // it would replace a good cached ChatUser and broadcast the default avatar to every viewer —
        // converting a transient upstream hiccup into a visible regression for the whole channel. Doing
        // nothing costs nothing: the next successful ping, or the player's next connect, re-enriches.
        if (!resolution.FreshFromWb) return;

        _connections.RegisterUser(session.ConnectionId, resolution.User);

        await UserDirectoryUpsert.Apply(
            _userDirectory, battleTag, resolution, _timeProvider.GetUtcNow().UtcDateTime);

        var payload = new FlairChangedDto(battleTag, ChatProfileMapper.FromChatUser(resolution.User));

        // Flair is user-scoped, not channel-scoped: the audience is every connection focused on any
        // channel this player is focused on, deduped, plus their own connection unconditionally so a
        // player focused on nothing still sees their own avatar update.
        var targets = new HashSet<string>(StringComparer.Ordinal) { session.ConnectionId };
        foreach (var channelId in _focusRegistry.GetFocusedChannels(session.ConnectionId))
        {
            foreach (var connectionId in _focusRegistry.GetFocusedConnections(channelId))
            {
                targets.Add(connectionId);
            }
        }

        foreach (var connectionId in targets)
        {
            try
            {
                await _hubContext.Clients.Client(connectionId).SendAsync(ChatEvents.FlairChanged, payload);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Fan-out send of FlairChanged failed for connection {ConnectionId} — skipping", connectionId);
            }
        }
    }
}

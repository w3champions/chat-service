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

        // The awaited wb round-trip above is a window in which the player can disconnect, or reconnect
        // under a new connection id. Re-validate against the authoritative session before touching any
        // state: acting on the stale `session` would resurrect a ConnectionMapping entry that
        // ChatHub.OnDisconnectedAsync's `finally` already tore down, and since disconnect fires exactly
        // once, nothing would ever remove it again — an unbounded leak under repeated connect/disconnect
        // races.
        var current = _sessionRegistry.GetByBattleTag(battleTag);
        if (current == null || current.ConnectionId != session.ConnectionId) return;

        // THE RULE. A wb blip degrades to a tier-3 profile with FreshFromWb false. Acting on it would
        // replace a good cached ChatUser and broadcast the default avatar to every viewer — converting a
        // transient upstream hiccup into a visible regression for the whole channel. Doing nothing costs
        // nothing: the next successful ping, or the player's next connect, re-enriches.
        if (!resolution.FreshFromWb) return;

        // Downstream uses the LIVE SESSION IDENTITY's casing, not the raw webhook-supplied `battleTag`
        // parameter — matching the connect path (ChatHub.UpsertDirectory, which always passes
        // identity.BattleTag). The webhook value comes from website-backend's storage casing and nothing
        // reconciles it against the session; DisplayBattleTag is the authoritative display casing read by
        // mention search, so acting on the webhook casing here could overwrite a user's display casing
        // with storage casing on every flair ping.
        var liveBattleTag = current.Identity.BattleTag;

        _connections.RegisterUser(current.ConnectionId, resolution.User);

        // The revalidation above narrows the disconnect race but does not close it: that read and this
        // write are two separate, unsynchronized critical sections with no await between them, so
        // ChatHub.OnDisconnectedAsync can still land in the gap on another thread and have its Remove
        // undone by the RegisterUser call just above. Re-read the authoritative session once more; if it
        // no longer points at this connection, someone else got there first — undo the write and stop.
        // ConnectionMapping.Remove is safe here: it no-ops on an already-absent connectionId, and it is
        // scoped to this connectionId only, so it can never remove a different, live connection's mapping
        // (including another connection for the same battleTag). Returning here — rather than falling
        // through into the directory upsert and fan-out below — matters: those two use this stale
        // request's `resolution`, and a newer refresh for the same battleTag may already have written a
        // fresher one. Idempotent, battleTag-keyed writes don't save you when two refreshes interleave —
        // the older one finishing last still overwrites the newer profile and broadcasts outdated flair
        // to live viewers, not just a dead connection.
        var postWriteSession = _sessionRegistry.GetByBattleTag(battleTag);
        if (postWriteSession == null || postWriteSession.ConnectionId != current.ConnectionId)
        {
            _connections.Remove(current.ConnectionId);
            return;
        }

        await UserDirectoryUpsert.Apply(
            _userDirectory, liveBattleTag, resolution, _timeProvider.GetUtcNow().UtcDateTime);

        var payload = new FlairChangedDto(liveBattleTag, ChatProfileMapper.FromChatUser(resolution.User));

        // Flair is user-scoped, not channel-scoped: the audience is every connection focused on any
        // channel this player is focused on, deduped, plus their own connection unconditionally so a
        // player focused on nothing still sees their own avatar update.
        var targets = new HashSet<string>(StringComparer.Ordinal) { current.ConnectionId };
        foreach (var channelId in _focusRegistry.GetFocusedChannels(current.ConnectionId))
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

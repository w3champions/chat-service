using System;
using System.Linq;
using System.Threading.Tasks;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Memberships;
using Serilog;

namespace W3ChampionsChatService.Chats;

/// <summary>
/// Connect-time clan-channel reconciliation (2026-08-09 clan-channel regression).
/// <para>
/// BACKGROUND. Before the chat revamp, the clan channel was never a server concept at all: the launcher
/// read the player's clan tag off the legacy <c>StartChat</c> payload and locally fabricated a room named
/// <c>clan &lt;tag&gt;</c> (launcher-e <c>src/models/chat.ts</c>, removed in #833). Rooms were ephemeral
/// SignalR groups, so "joining" was just naming one. The revamp (#33) rebuilt channels as PERSISTED
/// <see cref="ChannelMembership"/> rows assembled by
/// <see cref="Protocol.SessionStateAssembler.AssembleAndSeed"/>, and shipped Match/Dm/GroupDm/Public/
/// SemiPublic write paths only. <see cref="SystemChannelKind.Clan"/> survived as a POLICY stub —
/// permanent lifetime (<see cref="ExpiryCalculator.ForChannelShell"/>) and moderation exemption
/// (<see cref="ChannelModeration.IsModeratable"/>) — with nothing that ever creates such a channel or
/// inserts a membership into one. Net effect: the clan channel silently disappeared for every clan
/// member (~2.4k accounts at the time of the fix).
/// </para>
/// <para>
/// SYNC MODEL — CONNECT-TIME ONLY (product decision, Marco, 2026-08-09). Reconciliation is driven by
/// the clan id the connect path has ALREADY resolved: <see cref="ChatUser.ClanTag"/>, populated by
/// <see cref="IChatAuthenticationService.GetUserFromIdentity"/> from wb's
/// <c>GET /api/players/{battleTag}/clan-and-picture</c>. That means ZERO new infrastructure — no poller,
/// no webhook, no extra round-trip: the wb read that already happens for chat flair now also drives
/// routing. Accepted cost: a clan join/leave/disband reaches chat on the user's NEXT chat connect
/// rather than instantly.
/// </para>
/// <para>
/// THE NEVER-CLOBBER INVARIANT. <see cref="ChatAuthenticationService"/> is fail-soft by design: on a wb
/// outage it falls back to the cached directory profile, and on a total miss (tier 3) it returns a plain
/// <see cref="ChatUser"/> whose <see cref="ChatUser.ClanTag"/> is <c>null</c>. A null from that tier means
/// ABSENCE OF DATA, not "this user left their clan" — treating it as authoritative would evict every
/// connecting clan member from their clan channel for the duration of a wb outage. So the REMOVAL half of
/// reconciliation is gated on <see cref="ChatUserResolution.FreshFromWb"/>, exactly mirroring the gate
/// <see cref="ChatHub.UpsertDirectory"/> already applies before replacing a cached Profile. A non-fresh
/// resolution is ADDITIVE-ONLY.
/// </para>
/// <para>
/// ORDERING. This runs in <see cref="ChatHub.OnConnectedAsync"/> BEFORE
/// <see cref="Protocol.SessionStateAssembler.AssembleAndSeed"/>, which reads membership rows straight from
/// Mongo. Reconciling first is what puts the clan channel in the SAME <c>SessionState</c> the client
/// renders on THIS connect — reconciling afterwards would persist the row but leave the user staring at a
/// missing channel until their next reconnect, i.e. the original bug with extra steps. Because the
/// snapshot carries it, no <c>ChannelAdded</c> push is needed (and none is emitted).
/// </para>
/// <para>
/// FAIL-SOFT. Every step is wrapped: a Mongo hiccup or a malformed clan id must never fail an otherwise
/// good connect. The user simply connects without their clan channel and self-heals on the next connect.
/// This matches the posture of every other non-essential connect-path step (directory upsert, relationship
/// prefetch) and deliberately NOT the fatal posture of <c>AssembleAndSeed</c> itself.
/// </para>
/// </summary>
public partial class ChatHub
{
    /// <summary>
    /// Display name for a clan channel shell. The clan id IS the clan tag in wb's data model
    /// (<c>Clan._id</c> / <c>ClanMembership.ClanId</c>, e.g. <c>"EwOk"</c>), so this needs no extra
    /// lookup — deliberate, given the connect-time-only sync model above. Only ever applied on INSERT
    /// (<see cref="ChannelRepository.FindOrCreateSystem"/> uses <c>$setOnInsert</c>), so renaming a clan
    /// does not rewrite an existing shell.
    /// </summary>
    internal static string ClanChannelName(string clanId) => $"Clan {clanId}";

    /// <summary>
    /// Normalizes wb's clan id into either a usable ref or null. Guards the two shapes wb/legacy data
    /// actually produce for "no clan": a genuine null and the literal string <c>"null"</c> — the latter
    /// being the exact sentinel the PRE-revamp launcher screened for
    /// (<c>clanTag &amp;&amp; clanTag !== "null"</c>), so it is a real value seen in the wild, not a
    /// hypothetical.
    /// </summary>
    private static string NormalizeClanRef(string clanId)
    {
        var trimmed = clanId?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    /// <summary>
    /// Brings the caller's System+Clan membership in line with <paramref name="resolution"/>: joins the
    /// channel for their current clan (find-or-create), and — only on a FRESH wb resolution — drops any
    /// clan membership that is no longer theirs. Idempotent: a reconnect with an unchanged clan writes
    /// nothing. See the class doc for the ordering, sync-model and never-clobber rationale.
    /// </summary>
    private async Task ReconcileClanMembership(W3CUserAuthentication identity, ChatUserResolution resolution, DateTime now)
    {
        try
        {
            var clanRef = NormalizeClanRef(resolution.User?.ClanTag);

            // Resolve the clan memberships the user currently holds. LoadForUser is already on the
            // connect path's hot loop (AssembleAndSeed calls it moments later), so this is a cheap
            // repeat read against the same BattleTag-prefixed index, not a new access pattern.
            var memberships = await _membershipRepository.LoadForUser(identity.BattleTag);
            var existingClanChannelIds = memberships.Count == 0
                ? Array.Empty<string>()
                : (await _channelRepository.LoadByIds(memberships.Select(m => m.ChannelId)))
                    .Where(c => c.Type == ChannelType.System && c.SystemKind == SystemChannelKind.Clan)
                    .Select(c => c.Id)
                    .ToArray();

            string targetChannelId = null;
            if (clanRef != null)
            {
                var channel = await _channelRepository.FindOrCreateSystem(
                    SystemChannelKind.Clan, clanRef, ClanChannelName(clanRef), now);
                targetChannelId = channel.Id;

                if (!existingClanChannelIds.Contains(channel.Id))
                {
                    // NotificationLevel.All — a clan channel is a primary, non-leavable lane, so it gets
                    // the same default as a match channel (MatchChannelService.AddMemberWithInvariant),
                    // NOT JoinChannel's opt-in Mentions default for rooms a user picked themselves.
                    // InsertIfAbsent (not Insert) for race-safety against ux_channelId_battleTag when two
                    // sockets for the same battleTag reconcile concurrently.
                    await _membershipRepository.InsertIfAbsent(new ChannelMembership
                    {
                        ChannelId = channel.Id,
                        BattleTag = identity.BattleTag,
                        Role = MembershipRole.Member,
                        NotificationLevel = NotificationLevel.All,
                        JoinedAt = now,
                    });
                    Log.Information(
                        "Clan reconcile: joined {BattleTag} to clan channel {ClanRef}", identity.BattleTag, clanRef);
                }
            }

            // NEVER-CLOBBER (see class doc): only a FRESH wb read is authoritative about clan DEPARTURE.
            // A cached/plain resolution is additive-only — it may join, never evict.
            if (!resolution.FreshFromWb)
            {
                return;
            }

            foreach (var staleChannelId in existingClanChannelIds.Where(id => id != targetChannelId))
            {
                await _membershipRepository.Delete(staleChannelId, identity.BattleTag);
                Log.Information(
                    "Clan reconcile: removed {BattleTag} from stale clan channel {ChannelId}",
                    identity.BattleTag, staleChannelId);
            }
        }
        catch (Exception ex)
        {
            // Non-fatal by design — a clan-channel hiccup must never cost the user their chat session.
            Log.Warning(ex, "Clan-channel reconciliation failed for {BattleTag} — connecting without it", identity.BattleTag);
        }
    }
}

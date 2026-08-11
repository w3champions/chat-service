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
/// FRESHNESS IS AN ACCESS-CONTROL GATE. <see cref="ChatAuthenticationService"/> is fail-soft by design:
/// on a wb outage it falls back to the cached directory profile, and on a total miss (tier 3) it returns
/// a plain <see cref="ChatUser"/> whose <see cref="ChatUser.ClanTag"/> is <c>null</c>. Neither tier is
/// authoritative — a null means ABSENCE OF DATA, not "this user left their clan", and a cached ClanId can
/// name a clan the user has since left. Because a clan channel is PRIVATE, both directions of the
/// decision (grant and revoke) are therefore made ONLY from a genuinely fresh wb read; a non-fresh
/// resolution writes NOTHING and preserves whatever the user already had.
/// <para>
/// PR40 review (P1): this gate is only as trustworthy as the freshness flag feeding it, and that flag was
/// lying. <see cref="WebsiteBackendRepository.GetChatDetails"/> did not check the HTTP status, so a wb
/// 4xx/5xx whose body still deserialized produced a default-valued DTO, no exception, and
/// <c>FreshFromWb: true</c> — an outage indistinguishable from "this user has no clan". That is fixed at
/// the source (the repository now throws on a non-success status or an unusable body) rather than
/// papered over here, because <see cref="ChatHub.UpsertDirectory"/> trusted the same flag and had the
/// same latent never-clobber hole.
/// </para>
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
/// ERROR POSTURE — SPLIT, NOT UNIFORMLY FAIL-SOFT (PR40 review P1). The original blanket catch was wrong
/// in one direction: it also swallowed a failed REMOVAL, leaving the user readable/writable in a clan
/// they had left, which <c>AssembleAndSeed</c> then seeded straight into the session. Revocation (and the
/// read that decides what to revoke) is now FAIL-CLOSED — exceptions propagate and fail the connect,
/// matching the fatal posture <c>AssembleAndSeed</c> already takes for this same collection. Only the
/// JOIN is fail-soft, because its worst case is a missing channel that self-heals next connect, never a
/// wrongly-granted one. See <see cref="ReconcileClanMembership"/> for the per-step breakdown.
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
    /// Brings the caller's System+Clan membership in line with <paramref name="resolution"/>.
    /// <para>
    /// PR40 review — the error posture is SPLIT along the security boundary rather than uniformly
    /// fail-soft, because the two halves of reconciliation fail in opposite directions:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Removing a stale clan membership — FAIL CLOSED (exceptions propagate).</b> A failed
    /// delete leaves the user readable/writable in a clan they are no longer in, and
    /// <see cref="Protocol.SessionStateAssembler.AssembleAndSeed"/> runs microseconds later and seeds that
    /// very row into the session. Swallowing it would silently grant access to a former clan's channel and
    /// its retained history. Letting it propagate fails the connect, which is the SAME posture
    /// AssembleAndSeed itself already takes for this collection — and a Mongo fault that breaks this
    /// delete would almost certainly fail AssembleAndSeed on the next line anyway.</item>
    /// <item><b>Joining the current clan channel — FAIL SOFT (caught and logged).</b> The worst case is a
    /// user briefly missing a channel they are entitled to, which self-heals on the next connect. No
    /// access is wrongly granted, so this must never cost anyone their chat session.</item>
    /// </list>
    /// <para>
    /// The read that decides WHICH rows are stale is likewise fail-closed: acting on a partial view of
    /// the user's memberships is exactly how a stale row survives unnoticed.
    /// </para>
    /// <para>
    /// Idempotent: a reconnect with an unchanged clan writes nothing. See the class doc for the ordering,
    /// sync-model and freshness rationale.
    /// </para>
    /// </summary>
    // internal (not private) purely as a test seam — the assembly already grants InternalsVisibleTo to
    // the test project. Calling it directly is the only way to exercise the displacement gate below,
    // which by definition requires the session registry to have moved on MID-connect.
    internal async Task ReconcileClanMembership(W3CUserAuthentication identity, ChatUserResolution resolution, DateTime now)
    {
        // GATE 1 — FRESHNESS (PR40 review P1, tightened from "additive-only" to "no writes at all").
        // A non-fresh resolution carries flair from the directory cache (tier 2) or nothing at all
        // (tier 3), and the cached ClanId can be arbitrarily stale: directory entries are retained
        // FOREVER (never TTL'd), while CleanupJobs.PruneIdleMemberships deletes the membership rows of
        // users idle > 1 year. So a user who left their clan while inactive and returns during a wb
        // outage has a cached ClanId naming a clan they are no longer in, and no membership row to
        // contradict it — the original additive-only rule would have re-admitted them to that clan's
        // channel AND its retained message history on nothing but unverifiable cached data.
        // Membership in a private channel is an ACCESS-CONTROL decision, so it is now made ONLY from a
        // genuinely fresh wb read. A non-fresh connect preserves whatever the user already had and
        // self-heals on the next fresh one.
        if (!resolution.FreshFromWb)
        {
            Log.Debug(
                "Clan reconcile: skipped for {BattleTag} — resolution is not fresh from wb", identity.BattleTag);
            return;
        }

        // GATE 2 — DISPLACEMENT (PR40 review P2). Registering a newer connection for the same battleTag
        // aborts the older HubCallerContext but does NOT cancel the older OnConnectedAsync task, which
        // keeps running with the clan snapshot IT captured. If the two overlap across a clan change, the
        // loser finishing last would re-add the old membership and delete the new one — durable state
        // decided by scheduling order. TryGetByConnectionId resolves a displaced-but-not-yet-closed
        // connection to nothing (it returns the entry only while it is still the CURRENT one for that
        // battleTag), which is the same fail-closed check the permission filter relies on. This narrows
        // the window to the reads/writes below rather than eliminating it; the residual race is a
        // same-user reconnect landing inside those few milliseconds, which converges on the next connect.
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out _))
        {
            Log.Information(
                "Clan reconcile: skipped for {BattleTag} — connection {ConnectionId} was displaced",
                identity.BattleTag, Context.ConnectionId);
            return;
        }

        var clanRef = NormalizeClanRef(resolution.User?.ClanTag);

        // FAIL-CLOSED READ + REMOVAL (PR40 review P1). Deliberately NOT wrapped in a catch — see the
        // method doc. Resolve the clan memberships the user currently holds; LoadForUser is already on
        // the connect path (AssembleAndSeed calls it moments later), so this is a cheap repeat read
        // against the same BattleTag-prefixed index, not a new access pattern.
        var memberships = await _membershipRepository.LoadForUser(identity.BattleTag);
        var existingClanChannels = memberships.Count == 0
            ? Array.Empty<ChatChannel>()
            : (await _channelRepository.LoadByIds(memberships.Select(m => m.ChannelId)))
                .Where(c => c.Type == ChannelType.System && c.SystemKind == SystemChannelKind.Clan)
                .ToArray();

        // PR41 review (P2): staleness is derived from SystemRef, NOT from the id of a freshly-created
        // target shell. System channels are unique on (SystemKind, SystemRef) — the ux_systemKind_systemRef
        // index — so the clan channel whose SystemRef equals clanRef IS the target; comparing refs is
        // exactly equivalent to comparing ids, without needing the shell to exist yet. Ordinal comparison
        // mirrors the exact-match semantics FindOrCreateSystem's Mongo filter uses. A null clanRef (a fresh
        // wb read saying "no clan") makes EVERY held clan channel stale, which is the intended departure
        // case. This decoupling is what lets shell creation move onto the fail-soft join path below.
        var staleClanChannels = existingClanChannels
            .Where(c => clanRef == null || !string.Equals(c.SystemRef, clanRef, StringComparison.Ordinal))
            .ToArray();

        // Revocation before grant: if this throws, the connect fails having granted nothing new.
        foreach (var stale in staleClanChannels)
        {
            await _membershipRepository.Delete(stale.Id, identity.BattleTag);
            Log.Information(
                "Clan reconcile: removed {BattleTag} from stale clan channel {ChannelId}",
                identity.BattleTag, stale.Id);
        }

        // Nothing to grant: either a fresh wb read says the user is in no clan (the revocations above were
        // the whole job), or they already hold the right clan channel — the idempotent reconnect case.
        var alreadyMember = clanRef != null
            && existingClanChannels.Any(c => string.Equals(c.SystemRef, clanRef, StringComparison.Ordinal));
        if (clanRef == null || alreadyMember)
        {
            return;
        }

        // FAIL-SOFT JOIN — see the method doc. Covers BOTH steps of the grant: the shell find-or-create
        // AND the membership insert. PR41 review (P2) moved the find-or-create in here: creating a shell
        // grants nobody access, so there is no access-control reason to fail closed on it, and leaving it
        // outside made a transient channel-upsert fault reject the whole connection on the hot path every
        // clan member takes on every connect — an availability regression against the behaviour on master,
        // and a contradiction of this method's own stated policy that only revocation is fatal.
        try
        {
            var channel = await _channelRepository.FindOrCreateSystem(
                SystemChannelKind.Clan, clanRef, ClanChannelName(clanRef), now);

            // NotificationLevel.All — a clan channel is a primary, non-leavable lane, so it gets the same
            // default as a match channel (MatchChannelService.AddMemberWithInvariant), NOT JoinChannel's
            // opt-in Mentions default for rooms a user picked themselves. InsertIfAbsent (not Insert) for
            // race-safety against ux_channelId_battleTag when two sockets reconcile concurrently.
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
        catch (Exception ex)
        {
            Log.Warning(
                ex, "Clan-channel join failed for {BattleTag} — connecting without it", identity.BattleTag);
        }
    }
}

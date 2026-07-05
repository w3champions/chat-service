using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Messages;

namespace W3ChampionsChatService.Internal;

/// <summary>
/// C7 Tasks 6-8 — the match-channel domain core the /internal/* match endpoints drive. Owns
/// <see cref="CreateOrGet"/> (idempotent System+Match find-or-create + display-name backfill, the
/// <c>PUT /internal/channels/{ref}</c>-style upsert), <see cref="ApplyMembersDelta"/> (the
/// <c>PUT /internal/channels/{ref}/members</c> delta — tolerant of arriving before the create),
/// <see cref="DeleteChannel"/> (the <c>DELETE /internal/channels/{ref}</c> hard-teardown — tolerant of
/// arriving before the create too), and the shared <see cref="AddMemberWithInvariant"/> that enforces the
/// ONE-MATCH-CHANNEL-PER-USER invariant — every add path (both public add methods) reuses it.
/// <para>
/// Singleton (registered in <see cref="Startup"/>): it holds no per-call state. Its
/// <see cref="ChannelRepository"/>/<see cref="MembershipRepository"/>/<see cref="MessageRepository"/> deps
/// are themselves registered TRANSIENT (<see cref="Startup"/>), so this singleton captures them as a
/// captive dependency — safe ONLY because all three are stateless <c>MongoClient</c> wrappers with no
/// per-call state of their own to leak across calls.
/// </para>
/// <para>
/// SWAP CONSISTENCY — best-effort ordered, NOT DB-atomic. Memberships are separate documents and the repo
/// uses no multi-document transaction, so the "evict the user's other match membership, then add the new
/// one" swap is a sequence of independent writes rather than one atomic operation. It relies on two facts to
/// stay correct in production: (1) mm's per-user match flows are serialized (a player is in one match at a
/// time and mm drives these calls sequentially for that player), and (2) the unique
/// <c>ux_channelId_battleTag</c> index prevents in-channel duplicates regardless. RESIDUAL RACE: two truly
/// concurrent adds of the SAME user to two DIFFERENT match channels could interleave such that each misses
/// the other's not-yet-committed membership, transiently leaving the user with TWO System+Match memberships;
/// this self-heals on the user's next add (the stale-eviction scan removes the extra one) and is unreachable
/// via mm's serialized per-user flows. The strict ordering guarantee this class DOES provide is per-add:
/// within a single <see cref="AddMemberWithInvariant"/> call, <c>ChannelRemoved(old)</c> is emitted STRICTLY
/// BEFORE <c>ChannelAdded(new)</c>, so a user moving A→B never transiently sees both channels.
/// </para>
/// </summary>
public class MatchChannelService(
    ChannelRepository channelRepository,
    MembershipRepository membershipRepository,
    MessageRepository messageRepository,
    FanOutEngine fanOutEngine,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Idempotent create-or-get of the System+Match channel keyed by <paramref name="systemRef"/>, then adds
    /// every <paramref name="members"/> battleTag under the one-match-channel-per-user invariant. Safe to call
    /// repeatedly for the same match (a duplicate mm POST) — a re-get never resets the 24h creation-anchored
    /// expiry, never duplicates a membership, and never re-pushes an already-present member.
    /// <list type="number">
    /// <item>Find-or-create the shell (<see cref="ChannelRepository.FindOrCreateSystem"/>) — sets the 24h TTL
    /// on first create via <c>$setOnInsert</c>; a re-get leaves it untouched.</item>
    /// <item>NAME BACKFILL (§3.3): if the stored name differs from the trimmed <paramref name="name"/>, converge
    /// it via <see cref="ChannelRepository.SetName"/>. This turns a placeholder shell name into the real display
    /// name; it is idempotent (only writes on a genuine difference) and safe because mm never legitimately
    /// renames a ref.</item>
    /// <item>Add each member via <see cref="AddMemberWithInvariant"/> — a duplicate POST that lists extra members
    /// treats the already-present ones as no-ops and only pushes/persists the genuinely new ones (late repair).</item>
    /// </list>
    /// </summary>
    public async Task<ChatChannel> CreateOrGet(string systemRef, string name, IReadOnlyList<string> members, bool focus)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var trimmedName = name.Trim();

        var channel = await channelRepository.FindOrCreateSystem(SystemChannelKind.Match, systemRef, trimmedName, now);

        // Name backfill (§3.3). Only writes on a genuine difference (idempotent); mutating the in-memory copy
        // too keeps the returned channel — and every ChannelAdded emitted for a member below — carrying the
        // backfilled name rather than the stale shell name FindOrCreateSystem read back.
        if (channel.Name != trimmedName)
        {
            await channelRepository.SetName(channel.Id, trimmedName);
            channel.Name = trimmedName;
        }

        foreach (var battleTag in members)
        {
            await AddMemberWithInvariant(channel, battleTag, focus, now);
        }

        return channel;
    }

    /// <summary>
    /// The ONE-MATCH-CHANNEL-PER-USER invariant, shared by every add path (§3.4). Evicts the user's other
    /// live System+Match memberships, then adds them to <paramref name="channel"/> — idempotently.
    /// <list type="number">
    /// <item>Resolve the user's OTHER System+Match memberships (channel Id ≠ <paramref name="channel"/>'s).</item>
    /// <item>For EACH stale one: <see cref="MembershipRepository.Delete"/> THEN
    /// <see cref="FanOutEngine.PushChannelRemoved"/> — in that order, so <c>ChannelRemoved(old)</c> is emitted
    /// STRICTLY BEFORE the <c>ChannelAdded(new)</c> below.</item>
    /// <item>IDEMPOTENCY (acceptance 2): if a membership on the TARGET already exists, return WITHOUT
    /// re-inserting or re-pushing — a duplicate create/add must not duplicate memberships or re-emit.</item>
    /// <item>Otherwise build the membership (Role Member, <see cref="NotificationLevel.All"/> — the spec §7
    /// match default; <c>JoinedAt = now</c>), <see cref="MembershipRepository.InsertIfAbsent"/> (race-safe
    /// against the unique index), then <see cref="FanOutEngine.PushChannelAdded"/> (a no-op live push for an
    /// offline user, whose membership doc is nonetheless durably persisted).</item>
    /// </list>
    /// </summary>
    private async Task AddMemberWithInvariant(ChatChannel channel, string battleTag, bool focus, DateTime now)
    {
        foreach (var staleChannelId in await FindStaleMatchChannelIds(battleTag, channel.Id))
        {
            await membershipRepository.Delete(staleChannelId, battleTag);
            await fanOutEngine.PushChannelRemoved(staleChannelId, battleTag);
        }

        // Idempotency gate: an existing membership on the target means this is a duplicate add — no re-insert,
        // no re-push. InsertIfAbsent below is the belt-and-suspenders guard against the unique index for the
        // (unreachable-via-mm) concurrent-add residual race.
        if (await membershipRepository.Load(channel.Id, battleTag) != null)
        {
            return;
        }

        var membership = new ChannelMembership
        {
            ChannelId = channel.Id,
            BattleTag = battleTag,
            Role = MembershipRole.Member,
            NotificationLevel = NotificationLevel.All,
            JoinedAt = now,
        };
        var persisted = await membershipRepository.InsertIfAbsent(membership);
        await fanOutEngine.PushChannelAdded(channel, persisted, focus);
    }

    /// <summary>
    /// <c>PUT /internal/channels/{ref}/members</c> domain logic (C7 Task 7) — applies an mm-driven
    /// membership delta to the System+Match channel keyed by <paramref name="systemRef"/>, tolerant of the
    /// delta arriving BEFORE the channel's own create (M1 — never a hard 404).
    /// <list type="number">
    /// <item>CREATE-ON-DEMAND (§3.3): if no channel exists yet for <paramref name="systemRef"/>, find-or-create
    /// a shell via <see cref="ChannelRepository.FindOrCreateSystem"/> with a PLACEHOLDER name equal to the ref
    /// itself. The shell's 24h expiry is anchored to its OWN creation time (set by <c>FindOrCreateSystem</c>
    /// via <c>$setOnInsert</c>); a later real <see cref="CreateOrGet"/> backfills the display name and — per
    /// that method's own idempotent $setOnInsert semantics — does NOT reset this expiry.</item>
    /// <item><paramref name="add"/> is processed FIRST, each battleTag via the shared
    /// <see cref="AddMemberWithInvariant"/> — so the one-match-channel-per-user invariant (swap) and the
    /// focus-hinted <c>ChannelAdded</c> push fire on this path exactly as they do from <see cref="CreateOrGet"/>.</item>
    /// <item><paramref name="remove"/> is processed AFTER: per battleTag, <see cref="MembershipRepository.Load"/>
    /// — ABSENT is a silent no-op (no push, mm's delta can legitimately race a membership that already left);
    /// PRESENT is <see cref="MembershipRepository.Delete"/> then <see cref="FanOutEngine.PushChannelRemoved"/>,
    /// whose <see cref="FanOut.FocusRegistry.Unfocus"/> tail IS the server force-unfocus of the removed user's
    /// connection (acceptance 4).</item>
    /// </list>
    /// A battleTag appearing in BOTH lists ends up REMOVED — adds run before removes, so this is
    /// deterministic even though mm never legitimately sends such an overlapping delta.
    /// </summary>
    public async Task ApplyMembersDelta(string systemRef, IReadOnlyList<string> add, IReadOnlyList<string> remove, bool focus)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var channel = await channelRepository.LoadBySystemRef(SystemChannelKind.Match, systemRef)
            ?? await channelRepository.FindOrCreateSystem(SystemChannelKind.Match, systemRef, systemRef, now);

        foreach (var battleTag in add)
        {
            await AddMemberWithInvariant(channel, battleTag, focus, now);
        }

        foreach (var battleTag in remove)
        {
            if (await membershipRepository.Load(channel.Id, battleTag) == null)
            {
                continue;
            }

            await membershipRepository.Delete(channel.Id, battleTag);
            await fanOutEngine.PushChannelRemoved(channel.Id, battleTag);
        }
    }

    /// <summary>
    /// <c>DELETE /internal/channels/{ref}</c> domain logic (C7 Task 8) — hard-tears-down the System+Match
    /// channel keyed by <paramref name="systemRef"/>: its membership rows AND its messages (a HARD purge,
    /// distinct from moderation's TTL-only soft-delete — see <see cref="MessageRepository.DeleteAllForChannel"/>),
    /// then best-effort pushes <c>ChannelRemoved</c> to every member who was online at teardown time.
    /// <list type="number">
    /// <item>TOLERANT OF DELETE-BEFORE-CREATE (§3.3, M1): if no channel exists for <paramref name="systemRef"/>,
    /// return — the controller maps this to a no-op 200 rather than a hard 404 (a 404 would only trigger a
    /// pointless mm retry).</item>
    /// <item>Capture the member list FIRST via <see cref="MembershipRepository.LoadForChannel"/> — their
    /// battleTags are needed for the live pushes below, which must happen AFTER the membership rows (and
    /// hence this read) are gone.</item>
    /// <item>DB teardown, authoritative-first: <see cref="MessageRepository.DeleteAllForChannel"/> →
    /// <see cref="MembershipRepository.DeleteAllForChannel"/> → <see cref="ChannelRepository.Delete"/>.</item>
    /// <item>Then best-effort live pushes: <see cref="FanOutEngine.PushChannelRemoved"/> for each captured
    /// member — the in-memory session/focus/online-member registries are unaffected by the DB deletes above,
    /// and the push itself no-ops for a member who is offline.</item>
    /// </list>
    /// </summary>
    public async Task DeleteChannel(string systemRef)
    {
        var channel = await channelRepository.LoadBySystemRef(SystemChannelKind.Match, systemRef);
        if (channel == null)
        {
            return;
        }

        var memberBattleTags = (await membershipRepository.LoadForChannel(channel.Id))
            .Select(m => m.BattleTag)
            .ToList();

        await messageRepository.DeleteAllForChannel(channel.Id);
        await membershipRepository.DeleteAllForChannel(channel.Id);
        await channelRepository.Delete(channel.Id);

        foreach (var battleTag in memberBattleTags)
        {
            await fanOutEngine.PushChannelRemoved(channel.Id, battleTag);
        }
    }

    /// <summary>
    /// The user's OTHER System+Match channel ids (Id ≠ <paramref name="targetChannelId"/>) — the stale match
    /// memberships the invariant must evict. Loads the user's memberships, resolves them to channels
    /// (<see cref="ChannelRepository.LoadByIds"/>, reused rather than re-queried), and filters to System+Match.
    /// </summary>
    private async Task<IReadOnlyList<string>> FindStaleMatchChannelIds(string battleTag, string targetChannelId)
    {
        var memberships = await membershipRepository.LoadForUser(battleTag);
        if (memberships.Count == 0)
        {
            return Array.Empty<string>();
        }

        var channels = await channelRepository.LoadByIds(memberships.Select(m => m.ChannelId));
        return channels
            .Where(c => c.Type == ChannelType.System
                && c.SystemKind == SystemChannelKind.Match
                && c.Id != targetChannelId)
            .Select(c => c.Id)
            .ToList();
    }
}

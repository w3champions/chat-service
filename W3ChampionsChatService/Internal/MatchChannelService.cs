using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
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
/// <para>
/// 2026-08-05 RECONCILIATION (plan D5, D10): adds <see cref="ApplyRosterAssertion"/> — the authoritative
/// full-set protocol that supersedes <see cref="ApplyMembersDelta"/> — plus a <see cref="MatchChannelRefGate"/>
/// (<see cref="_refGate"/>) that every mutating match-channel path now acquires FIRST, and detach guards
/// (plan D4) on the assertion and legacy delta paths. <see cref="CreateOrGet"/> gains OPTIONAL
/// <c>epoch</c>/<c>seq</c>/<c>detached</c> parameters (plan D10) so a create can also participate in the
/// (epoch, seq) staleness protocol and birth ladder-match channels already detached. The teardown body
/// of <see cref="DeleteChannel"/> is extracted into the private <c>TearDownChannel</c>, shared by
/// <see cref="ApplyEpochSync"/> — the startup mm-crash-recovery sweep (plan D8) that tears down every
/// non-detached match channel absent from mm's freshly-booted <c>liveLobbyRefs</c> and re-anchors the
/// channels it spares to the new epoch.
/// </para>
/// </summary>
public class MatchChannelService(
    ChannelRepository channelRepository,
    MembershipRepository membershipRepository,
    MessageRepository messageRepository,
    FanOutEngine fanOutEngine,
    TimeProvider timeProvider)
{
    // 2026-08-05 reconciliation spec, plan D5: serializes every mutating operation on a single match
    // channel by its systemRef — see MatchChannelRefGate's own doc for the full "why". Owned PRIVATELY
    // (not a constructor parameter) so Startup.cs and every existing `new MatchChannelService(...)` test
    // call site stay unchanged; InternalsVisibleTo still lets MatchChannelRefGateTests exercise the gate
    // type directly, and this class's own tests exercise it indirectly via the public methods below.
    private readonly MatchChannelRefGate _refGate = new();

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
    /// <para>
    /// 2026-08-05 RECONCILIATION (plan D10) — three OPTIONAL trailing parameters, all defaulted so every
    /// existing caller compiles and behaves byte-for-byte unchanged:
    /// <list type="bullet">
    /// <item><paramref name="epoch"/>/<paramref name="seq"/> (must arrive TOGETHER): when present,
    /// <see cref="ChannelRepository.TryAdvanceAssertion"/> is called to stamp (epoch, seq) — REGARDLESS of
    /// the channel's current <see cref="ChatChannel.Detached"/> state (a no-advance there is harmless; the
    /// CAS's own Detached filter and staleness rules already make it a safe no-op). The MEMBER-ADD GATE this
    /// enables is deliberately WEAKER than the assertion staleness gate: adds are skipped iff the channel is
    /// (already) detached, OR the stamped state for this epoch is STRICTLY ahead of this call's seq — an
    /// EQUAL stored seq still PROCEEDS with the adds (it means this exact call's own earlier attempt already
    /// stamped but crashed before adding, and <see cref="AddMemberWithInvariant"/> is idempotent). This is
    /// what stops a late-landing create retry from resurrecting a member a newer roster assertion already
    /// removed.</item>
    /// <item><paramref name="detached"/>: when true, AFTER the member adds above,
    /// <see cref="ChannelRepository.SetDetached"/> freezes the room. Ladder-match channels are born detached
    /// — they are never in mm's <c>liveLobbyRefs</c> (that registry only holds custom lobbies), so without
    /// birth-detach the FIRST epoch sync after any mm restart would tear down every in-progress ladder
    /// game's chat. Idempotent: a retried detached create with the same members changes nothing further.</item>
    /// </list>
    /// </para>
    /// </summary>
    public async Task<ChatChannel> CreateOrGet(
        string systemRef, string name, IReadOnlyList<string> members, bool focus,
        string epoch = null, long? seq = null, bool detached = false)
    {
        using var _ = await _refGate.AcquireAsync(systemRef);
        return await CreateOrGetLocked(systemRef, name, members, focus, epoch, seq, detached);
    }

    private async Task<ChatChannel> CreateOrGetLocked(
        string systemRef, string name, IReadOnlyList<string> members, bool focus,
        string epoch, long? seq, bool detached)
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

        // D10 stamping — called REGARDLESS of Detached (see the method doc); the CAS itself is a safe no-op
        // on a detached channel or a stale (epoch, seq).
        if (epoch != null && seq.HasValue)
        {
            await channelRepository.TryAdvanceAssertion(channel.Id, epoch, seq.Value);
        }

        // D10 member-add gate — deliberately WEAKER than the assertion staleness gate (see method doc):
        // skip iff already detached, OR this epoch's stamped seq is STRICTLY ahead of this call's seq.
        // Read from `channel` as returned by FindOrCreateSystem above — for an EXISTING channel that is
        // exactly a fresh load of current state; the TryAdvanceAssertion call above never mutates it.
        // NOTE (fix round 1, review r1 Minor-3): deliberately NOT the literal "if (channel.Detached) return
        // channel;" early-return shape from Task 3 step 4 — D10 requires the TryAdvanceAssertion stamp
        // attempt above to run regardless of Detached, which an early return before it would prevent.
        var skipAdds = channel.Detached
            || (epoch != null && seq.HasValue && channel.AssertEpoch == epoch && channel.AssertSeq > seq.Value);

        if (!skipAdds)
        {
            foreach (var battleTag in members)
            {
                await AddMemberWithInvariant(channel, battleTag, focus, now);
            }
        }

        if (detached)
        {
            await channelRepository.SetDetached(channel.Id);
            channel.Detached = true;
            Log.Information("CreateOrGet: match channel {Ref} marked detached", systemRef);
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
    /// <para>
    /// DETACH FREEZE (2026-08-05 reconciliation spec, plan D4): if the resolved channel is already
    /// <see cref="ChatChannel.Detached"/>, the whole delta is discarded (logged once at Information) —
    /// membership stays frozen regardless of which protocol (assertion or legacy delta) asks.
    /// </para>
    /// </summary>
    // TRANSITION (2026-08-05 reconciliation spec §"Verification gates"): the DELTA path. mm keeps
    // sending deltas until its own deploy; DELETE THIS ENTIRE MEMBER in the mm-deploy-confirmed
    // cleanup PR — see docs plan Task 6.
    public async Task ApplyMembersDelta(string systemRef, IReadOnlyList<string> add, IReadOnlyList<string> remove, bool focus)
    {
        using var _ = await _refGate.AcquireAsync(systemRef);
        await ApplyMembersDeltaLocked(systemRef, add, remove, focus);
    }

    private async Task ApplyMembersDeltaLocked(string systemRef, IReadOnlyList<string> add, IReadOnlyList<string> remove, bool focus)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var channel = await channelRepository.LoadBySystemRef(SystemChannelKind.Match, systemRef)
            ?? await channelRepository.FindOrCreateSystem(SystemChannelKind.Match, systemRef, systemRef, now);

        if (channel.Detached)
        {
            Log.Information("ApplyMembersDelta: discarded — match channel {Ref} is detached (frozen)", systemRef);
            return;
        }

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
    /// <para>
    /// NOT DETACH-GUARDED, DELIBERATELY (2026-08-05 reconciliation spec, plan D4): unlike
    /// <see cref="ApplyRosterAssertion"/> and <see cref="ApplyMembersDelta"/>, an explicit DELETE still
    /// tears down an already-detached channel — detach freezes ASSERTIONS and SWEEPS, not an explicit
    /// authoritative teardown command. If mm sends one after detaching a channel, it means it.
    /// </para>
    /// </summary>
    public async Task DeleteChannel(string systemRef)
    {
        using var _ = await _refGate.AcquireAsync(systemRef);
        await DeleteChannelLocked(systemRef);
    }

    private async Task DeleteChannelLocked(string systemRef)
    {
        var channel = await channelRepository.LoadBySystemRef(SystemChannelKind.Match, systemRef);
        if (channel == null)
        {
            return;
        }

        await TearDownChannel(channel);
    }

    /// <summary>
    /// The shared match-channel TEARDOWN, authoritative-DB-first then best-effort live pushes.
    /// Used by BOTH the explicit DELETE endpoint and the startup epoch sync, so an mm-crash sweep is
    /// byte-for-byte the same teardown an explicit mm teardown performs (and leaves no orphaned
    /// membership rows for CleanupJobs.SweepOrphanedMemberships to find).
    /// Callers MUST already hold the per-ref gate.
    /// </summary>
    private async Task TearDownChannel(ChatChannel channel)
    {
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
    /// The AUTHORITATIVE full-set roster assertion (2026-08-05 reconciliation spec §1) — replaces the
    /// delta protocol. mm sends the lobby's COMPLETE member set for <paramref name="systemRef"/>; this
    /// converges the stored membership rows onto it, idempotently.
    /// <list type="number">
    /// <item>Serialize on <paramref name="systemRef"/> (plan D5) so two in-flight assertions — or an
    /// assertion racing a transition-era delta — cannot interleave their diffs.</item>
    /// <item>CREATE-ON-DEMAND: an assertion arriving before mm's create POST — or after an epoch sync
    /// tore the channel down (the boot race) — find-or-creates the shell, using <paramref name="name"/>
    /// when provided (so a recreated room never displays its nanoid ref) and the ref as placeholder
    /// otherwise, exactly like <see cref="ApplyMembersDelta"/> (never a 404). On an EXISTING channel
    /// <paramref name="name"/> is ignored — CreateOrGet remains the name authority.</item>
    /// <item>DETACH FREEZE (plan D4): a detached channel discards the assertion outright.</item>
    /// <item>STALENESS GATE (plan D3): <see cref="ChannelRepository.TryAdvanceAssertion"/> admits and
    /// stamps (epoch, seq) atomically; a false return means stale/duplicate/reordered — DISCARD, and
    /// return WITHOUT touching membership. A DIFFERENT stored epoch is anomalous but accepted (a lobby
    /// lives within one mm epoch) — logged Warning; see that method's doc for why discarding would
    /// permanently wedge the channel.</item>
    /// <item>DIFF, case-insensitively (stored battleTags are LOWERCASED, mm sends JWT casing): missing
    /// => <c>AddMemberWithInvariant</c> (keeps the one-match-channel-per-user eviction invariant);
    /// extra => Delete then <c>PushChannelRemoved</c> (whose FocusRegistry.Unfocus tail force-unfocuses
    /// the removed user). Adds run before removes, mirroring <see cref="ApplyMembersDelta"/>.</item>
    /// <item>DETACH LAST: when <paramref name="detached"/>, the final member set is applied FIRST, then
    /// the channel is marked detached — so the freeze rule never has to special-case its own trigger.</item>
    /// </list>
    /// <paramref name="name"/> is nullable (null ⇒ ref placeholder on create-on-demand). There is NO
    /// <c>focus</c> parameter — mm has never sent focus on any internal call, so adds pass
    /// <c>focus: false</c> to <see cref="AddMemberWithInvariant"/>, byte-identical to today's behavior.
    /// </summary>
    public async Task ApplyRosterAssertion(
        string systemRef, string epoch, long seq, IReadOnlyList<string> members, string name, bool detached)
    {
        using var _ = await _refGate.AcquireAsync(systemRef);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var trimmedName = string.IsNullOrWhiteSpace(name) ? systemRef : name.Trim();

        var channel = await channelRepository.LoadBySystemRef(SystemChannelKind.Match, systemRef)
            ?? await channelRepository.FindOrCreateSystem(SystemChannelKind.Match, systemRef, trimmedName, now);

        if (channel.Detached)
        {
            Log.Information("ApplyRosterAssertion: discarded — match channel {Ref} is detached (frozen)", systemRef);
            return;
        }

        // Captured BEFORE the CAS call — the durable backstop below re-checks Detached and staleness
        // atomically, but the anomalous-epoch-mismatch Warning (D3c) needs the PRE-CAS stored epoch to
        // tell "no epoch stored yet" (rule a, not anomalous) apart from "a genuinely different epoch"
        // (rule c, anomalous) — TryAdvanceAssertion's own return value can't distinguish the two.
        var storedEpoch = channel.AssertEpoch;

        if (!await channelRepository.TryAdvanceAssertion(channel.Id, epoch, seq))
        {
            Log.Information(
                "ApplyRosterAssertion: discarded stale/duplicate assertion for match channel {Ref} (epoch {Epoch}, seq {Seq})",
                systemRef, epoch, seq);
            return;
        }

        if (storedEpoch != null && storedEpoch != epoch)
        {
            Log.Warning(
                "ApplyRosterAssertion: anomalous epoch mismatch for match channel {Ref} — re-anchored from stored epoch {StoredEpoch} to incoming {IncomingEpoch}",
                systemRef, storedEpoch, epoch);
        }

        var asserted = new HashSet<string>(members, StringComparer.OrdinalIgnoreCase);
        var current = await membershipRepository.LoadForChannel(channel.Id);

        // Missing/extra are computed case-insensitively (stored battleTags are lowercased, mm sends JWT
        // casing) — a case-only difference between the stored row and the asserted tag must never read as
        // a remove+add churn. `missing` preserves mm's incoming list ORDER (not the set) for deterministic
        // add sequencing. NOTE (fix round 1, review r1 Major-1 residual): if OrdinalIgnoreCase were ever
        // dropped here, no test would fail — AddMemberWithInvariant's normalizing membershipRepository.Load
        // gate (:188) swallows every over-reported "missing" member as a no-op add. That masked failure mode
        // is a silent PERFORMANCE regression (an extra Load round-trip per member per assertion), not a
        // correctness one — do not "simplify" this comparison away on the observation that the suite stays green.
        var missing = members
            .Where(m => !current.Any(row => string.Equals(row.BattleTag, m, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var extra = current.Where(row => !asserted.Contains(row.BattleTag)).ToList();

        // Adds before removes — mirrors ApplyMembersDelta.
        foreach (var battleTag in missing)
        {
            await AddMemberWithInvariant(channel, battleTag, focus: false, now);
        }

        foreach (var row in extra)
        {
            await membershipRepository.Delete(channel.Id, row.BattleTag);
            await fanOutEngine.PushChannelRemoved(channel.Id, row.BattleTag);
        }

        if (detached)
        {
            await channelRepository.SetDetached(channel.Id);
            channel.Detached = true;
            Log.Information("ApplyRosterAssertion: match channel {Ref} detached (frozen) by final roster assertion", systemRef);
        }
    }

    /// <summary>
    /// STARTUP EPOCH SYNC (2026-08-05 reconciliation spec §3) — mm asserts its authoritative world
    /// once at boot under a fresh epoch. Every NON-DETACHED System+Match channel whose SystemRef is
    /// absent from <paramref name="liveLobbyRefs"/> is torn down (memberships deleted, ChannelRemoved
    /// pushed so stuck rows vanish from connected clients immediately, channel doc removed); the
    /// channels that ARE listed are SPARED and passed to <see cref="ChannelRepository.StampAssertionEpoch"/>,
    /// which — CONDITIONALLY, only when the stored <c>AssertEpoch</c> differs from <paramref name="epoch"/>
    /// (absent counts as different) — re-anchors the channel to the new epoch with the seq counter reset to
    /// the 0 sentinel (plan D8), so mm's first assertion under the new epoch applies cleanly. A spared
    /// channel already anchored to THIS sync's own epoch (created or asserted during this same boot, while
    /// the sync itself was still retrying) is left entirely untouched by the re-stamp: resetting its seq
    /// would re-open the duplicate-replay window for assertions already applied under this epoch (Task-4
    /// review INFO-1).
    /// <para>DETACHED CHANNELS ARE EXCLUDED ENTIRELY — filtered server-side by
    /// <see cref="ChannelRepository.LoadNonDetachedMatchChannels"/>, so an in-game/post-game room is
    /// never loaded, never torn down, never re-stamped. Its 24h TTL owns it.</para>
    /// <para>Each channel is processed under its OWN per-ref gate (never one global lock) and RE-LOADED
    /// inside that gate before teardown, so the teardown/spare decision is always made on FRESHLY loaded
    /// state: a channel deleted or detached between the discovery scan and its own turn is skipped
    /// outright (never torn down on stale information), and a channel recreated in that window is judged
    /// on its NEW document rather than the stale scan-time candidate — a recreated channel whose ref is
    /// still absent from <paramref name="liveLobbyRefs"/> IS torn down; recreation does not spare it, it
    /// just means the decision is made on fresh data instead of stale data.</para>
    /// <para>Conversely, a channel created AFTER the discovery scan is not a candidate at all and is never
    /// swept — by definition any lobby mm creates after boot is live under the new epoch, so it survives
    /// to the next assertion or the 24h TTL (plan residual 4).</para>
    /// </summary>
    public async Task ApplyEpochSync(string epoch, IReadOnlyList<string> liveLobbyRefs)
    {
        // Refs are exact Mongo keys (unlike battleTags, which are the case-insensitive thing here) —
        // Ordinal, not OrdinalIgnoreCase.
        var live = new HashSet<string>(liveLobbyRefs, StringComparer.Ordinal);
        var tornDown = 0;
        var spared = 0;

        foreach (var candidate in await channelRepository.LoadNonDetachedMatchChannels())
        {
            using var _ = await _refGate.AcquireAsync(candidate.SystemRef);

            // RE-LOAD inside the gate: the discovery scan above ran outside any per-ref gate, so the
            // candidate could have been torn down, recreated, or detached by another mutating call
            // between the scan and this channel's turn — never act on stale information.
            var channel = await channelRepository.LoadBySystemRef(SystemChannelKind.Match, candidate.SystemRef);
            if (channel == null || channel.Detached)
            {
                continue;
            }

            if (live.Contains(channel.SystemRef))
            {
                await channelRepository.StampAssertionEpoch(channel.Id, epoch);
                spared++;
            }
            else
            {
                await TearDownChannel(channel);
                tornDown++;
            }
        }

        // Unconditional — a boot-time convergence event is exactly what an operator wants in the log,
        // including the healthy tornDown=0 case (mirrors the class's other Information-level summaries).
        Log.Information(
            "Epoch sync applied {Epoch} liveRefCount={LiveRefCount} tornDown={TornDown} spared={Spared}",
            epoch, liveLobbyRefs.Count, tornDown, spared);
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

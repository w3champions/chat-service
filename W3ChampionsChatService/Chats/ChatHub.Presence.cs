using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Relationships;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Chats;

/// <summary>
/// C6 (Task 10, D12): the one-shot presence READ surface — <see cref="GetPresence"/> and
/// <see cref="GetPresenceDetails"/>. Complements Task 9's LIVE presence-interest stream
/// (<see cref="ChatEvents.PresenceChanged"/>, derived from focused Dm/GroupDm membership): a client
/// calls these explicitly (e.g. opening a DM list or friends panel) rather than subscribing to a
/// stream — spec §9's "DM list doesn't stream presence" model. Both are PURE reads: neither ever
/// writes to <c>user_directory</c> or any other collection.
/// <para>
/// GATING (D12, security-relevant): <see cref="GetPresence"/>'s online/offline flag is UNGATED — the
/// same observability a viewer roster or the live <see cref="ChatEvents.PresenceChanged"/> stream
/// already gives anyone with legitimate interest; this just makes it queryable on demand for a batch
/// of tags at once (e.g. a DM list showing several conversation partners together). It never touches
/// the relationship provider.
/// </para>
/// <para>
/// <see cref="GetPresenceDetails"/> additionally returns <c>LastSeenAt</c>, which IS gated: populated
/// ONLY for battleTags that are the CALLER's own FRIENDS per THEIR OWN cached relationship snapshot
/// (<see cref="IRelationshipProvider"/>, C5) — a deliberate, previously-approved asymmetry: the LIVE
/// interest-gated stream (Task 9) is the strict "who sees whom" boundary; here the sensitive datum is
/// specifically the TIMESTAMP, whose only legitimate consumer is the friends panel. A stale-but-present
/// snapshot is ACCEPTED (mirrors the 1:1 delivery block-check precedent — <see cref="ChatHub.Dm"/>'s
/// <c>ApplyPrivateLaneGates</c> — rather than <see cref="CreateGroup"/>'s stricter freshness
/// requirement); only a total miss (<see cref="RelationshipUnavailableException"/>, no cached fallback
/// at all) fails closed — and even then ONLY the <c>LastSeenAt</c> field: <c>Online</c> keeps being
/// honestly reported for every requested tag regardless of relationship-snapshot availability.
/// </para>
/// </summary>
public partial class ChatHub
{
    /// <summary>
    /// C6 (Task 10, D12): one-shot, UNGATED online/offline read for a batch of battleTags. Resolution
    /// order mirrors the rest of the hub's client-bug-vs-typed-result split (e.g.
    /// <see cref="SearchMentionCandidates"/>/<see cref="MarkMentionsRead"/>): fail-closed session FIRST
    /// (there is no identity to authorize the read under) → <see cref="ChatResultCode.PermissionDenied"/>;
    /// THEN the malformed-arg guards — a null <paramref name="battleTags"/> array, or one over
    /// <see cref="ChatLimits.PresenceQueryMaxBattleTags"/>, is a client programming error and throws
    /// <see cref="HubException"/>. An EMPTY array is a valid no-op request, not an error — it returns
    /// <see cref="ChatResultCode.Ok"/> with an empty list. Online is resolved via
    /// <see cref="Sessions.ISessionRegistry.GetByBattleTag"/> — the same zero-extra-state,
    /// case-insensitive check the rest of the hub uses — so a battleTag with no live session is simply
    /// offline; no DB read is involved at all.
    /// </summary>
    public Task<GetPresenceResult> GetPresence(string[] battleTags)
    {
        // 1. Fail-closed: no live session → no identity to authorize the read under.
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out _))
        {
            return Task.FromResult(new GetPresenceResult(ChatResultCode.PermissionDenied));
        }

        // 2. Malformed-arg guards, before any work — client-bug mapping (mirrors MarkMentionsRead's
        // null/over-cap HubException precedent).
        ValidateBattleTagsArgument(battleTags, nameof(GetPresence));
        if (battleTags.Length == 0)
        {
            return Task.FromResult(new GetPresenceResult(ChatResultCode.Ok, Array.Empty<PresenceStatusDto>()));
        }

        // 3. Ungated online read — see the class doc's GATING section.
        var statuses = battleTags.Select(tag => new PresenceStatusDto(tag, IsOnline(tag))).ToList();
        return Task.FromResult(new GetPresenceResult(ChatResultCode.Ok, statuses));
    }

    /// <summary>
    /// C6 (Task 10, D12): one-shot read carrying BOTH the online flag AND the friend-gated
    /// <c>LastSeenAt</c> — the friends-panel leg (acceptance 6). Same boundary checks as
    /// <see cref="GetPresence"/> (fail-closed session FIRST, then null/over-cap →
    /// <see cref="HubException"/>, empty → <see cref="ChatResultCode.Ok"/> with an empty list — the
    /// friend-snapshot fetch below never runs for an empty request, since there is nothing to gate).
    /// <para>
    /// The CALLER's own relationship snapshot is fetched exactly ONCE per call (stale-usable — see the
    /// class doc) and reused for every requested tag's friend check; <see cref="RelationshipSnapshot.IsFriendWith"/>
    /// is itself null-safe, so a null/blank element in <paramref name="battleTags"/> degrades to
    /// "not a friend" rather than throwing. On <see cref="RelationshipUnavailableException"/> (no cached
    /// fallback at all) the snapshot stays null for the whole call — every row's <c>LastSeenAt</c> below
    /// then resolves to null (fail-closed on the sensitive datum ONLY; <c>Online</c> is computed
    /// independently and stays honest).
    /// </para>
    /// <para>
    /// <c>LastSeenAt</c> is sourced from ONE batch <see cref="UserDirectoryRepository.LoadMany"/> read
    /// (never a per-tag lookup) — the same directory Task 3's connect/disconnect upserts populate. A
    /// friend with no directory row at all (or a non-friend, or any tag when the snapshot is
    /// unavailable) resolves to a null <c>LastSeenAt</c>, never a thrown error or an excluded row.
    /// </para>
    /// </summary>
    public async Task<GetPresenceDetailsResult> GetPresenceDetails(string[] battleTags)
    {
        // 1. Fail-closed: no live session → no identity whose friends list could gate LastSeenAt.
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new GetPresenceDetailsResult(ChatResultCode.PermissionDenied);
        }

        // 2. Malformed-arg guards, before any work.
        ValidateBattleTagsArgument(battleTags, nameof(GetPresenceDetails));
        if (battleTags.Length == 0)
        {
            return new GetPresenceDetailsResult(ChatResultCode.Ok, Array.Empty<PresenceDetailsDto>());
        }

        // 3. The sensitive datum's gate: the CALLER's own relationship snapshot (stale-usable — mirrors
        // the 1:1 delivery block-check precedent, ChatHub.Dm.cs ApplyPrivateLaneGates). A total miss
        // (RelationshipUnavailableException) leaves callerSnapshot null, so every row's LastSeenAt below
        // fails closed to null via the null-coalescing IsFriendWith check — never a thrown error, and
        // never a silent "trust a stale/absent snapshot" guess.
        RelationshipSnapshot callerSnapshot = null;
        try
        {
            callerSnapshot = await _relationshipProvider.GetSnapshotAsync(session.Identity.BattleTag);
        }
        catch (RelationshipUnavailableException)
        {
            // Fail closed on LastSeenAt only — see the class doc. Online is unaffected below.
        }

        // 4. ONE batch directory read for LastSeenAt — never a per-tag lookup. Blank/null elements are
        // dropped before the query (LoadMany/UserDirectoryRepository normalizes by lower-casing, which
        // would throw on a null entry) but are still present as a row in the projected result below
        // (via IsOnline/IsFriendWith's own null-safety) rather than causing the whole call to fail.
        var directoryByTag = (await _userDirectory.LoadMany(battleTags.Where(t => !string.IsNullOrWhiteSpace(t))))
            .ToDictionary(e => e.BattleTag, StringComparer.OrdinalIgnoreCase);

        var details = battleTags
            .Select(tag => BuildPresenceDetails(tag, callerSnapshot, directoryByTag))
            .ToList();
        return new GetPresenceDetailsResult(ChatResultCode.Ok, details);
    }

    /// <summary>Projects one requested tag to its <see cref="PresenceDetailsDto"/>: <c>Online</c> is
    /// always honestly resolved; <c>LastSeenAt</c> is populated only when <paramref name="callerSnapshot"/>
    /// is non-null (a snapshot was actually obtained) AND lists <paramref name="battleTag"/> as a friend
    /// AND the directory batch read found a row for it.</summary>
    private PresenceDetailsDto BuildPresenceDetails(
        string battleTag,
        RelationshipSnapshot callerSnapshot,
        IReadOnlyDictionary<string, UserDirectoryEntry> directoryByTag)
    {
        var online = IsOnline(battleTag);
        var isFriend = callerSnapshot != null && callerSnapshot.IsFriendWith(battleTag);
        DateTime? lastSeenAt = isFriend
            && battleTag != null
            && directoryByTag.TryGetValue(battleTag, out var entry)
                ? entry.LastSeenAt
                : null;
        return new PresenceDetailsDto(battleTag, online, lastSeenAt);
    }

    /// <summary>Case-insensitive, zero-DB online check — a battleTag with no live
    /// <see cref="ISessionRegistry"/> entry (including a null/blank one) is simply offline.</summary>
    private bool IsOnline(string battleTag) =>
        !string.IsNullOrWhiteSpace(battleTag) && _sessionRegistry.GetByBattleTag(battleTag) != null;

    private static void ValidateBattleTagsArgument(string[] battleTags, string methodName)
    {
        if (battleTags == null)
        {
            throw new HubException($"{methodName} requires a non-null battleTags array");
        }
        if (battleTags.Length > ChatLimits.PresenceQueryMaxBattleTags)
        {
            throw new HubException(
                $"{methodName}: battleTags exceeds the {ChatLimits.PresenceQueryMaxBattleTags}-tag cap");
        }
    }
}

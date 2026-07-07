using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Chats;

/// <summary>
/// C6 (Task 6, D6): the mention-inbox READ/ACK surface — <see cref="GetMentionInbox"/>,
/// <see cref="MarkMentionsRead"/>, <see cref="MarkAllMentionsRead"/>. The WRITE side (inbox-entry
/// creation + the targeted <c>MentionNotified</c> push) is <see cref="Mentions.MentionFanOut"/>
/// (Task 5); this partial owns everything the CLIENT drives afterward — paging the inbox and
/// acknowledging entries.
/// <para>
/// ACK RULES (pinned — C6-plan.md §2 + D6): acks are CLIENT-DRIVEN and PER-ENTRY. The server NEVER
/// derives a mention read from a channel's <c>lastReadSeq</c> or any other seq — no seq-derived
/// auto-ack, ever. Seeing a NEWER mention must never retroactively ack an OLDER, still-unseen one.
/// This is a DELIBERATE DEPARTURE from how regular channel read-state works elsewhere in this
/// codebase (<see cref="ChatHub.MarkRead"/> IS seq-derived, advancing a single cursor) — a prior
/// design decision explicitly rejected seq-derived mention acks, because seeing a newer mention
/// should never silently clear an older one the user hasn't seen yet. <see cref="MarkMentionsRead"/>
/// is idempotent: acking an already-read id a second time is a no-op — its <c>ReadAt</c> keeps the
/// FIRST-seen value, never overwritten by a later ack. Read entries are KEPT (dimmed client-side)
/// until the 30d TTL — NOTHING in this file ever deletes a <c>mention_inbox</c> row (that is Task 7's
/// job, and only for the moderation-delete scenario).
/// </para>
/// <para>
/// AUTHORIZATION BOUNDARY: <see cref="Mentions.MentionInboxRepository.MarkRead"/>'s Mongo filter ANDs
/// the caller's own lowercased BattleTag alongside the id-membership check — an id belonging to
/// someone else simply does not match that filter, so it is silently skipped: never acked, never an
/// error, and never an oracle a caller could use to probe whether a given id belongs to another user.
/// </para>
/// </summary>
public partial class ChatHub
{
    /// <summary>
    /// C6 (Task 6): pages the CALLER'S OWN mention inbox, newest-first, capped at
    /// <see cref="ChatLimits.MentionInboxMaxEntries"/> (<see cref="Mentions.MentionInboxRepository.LoadForUser"/>
    /// — battleTag is passed straight through, JWT-cased, and normalized internally by the repository).
    /// Fail-closed session → <see cref="ChatResultCode.PermissionDenied"/>; else every entry — read AND
    /// unread; read entries are dimmed client-side, never dropped here — is projected through
    /// <see cref="MentionInboxEntryDto"/>, the explicit boundary-privacy projection that deliberately
    /// never exposes <c>ExpiresAt</c> (server-only TTL bookkeeping — Task 1's convention).
    /// </summary>
    public async Task<GetMentionInboxResult> GetMentionInbox()
    {
        // Fail-closed: no live session → no identity to page an inbox for.
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new GetMentionInboxResult(ChatResultCode.PermissionDenied);
        }

        var entries = await _mentionInboxRepository.LoadForUser(session.Identity.BattleTag);
        var dtos = entries
            .Select(e => new MentionInboxEntryDto(
                e.Id,
                e.ChannelId,
                e.MessageId,
                e.Seq,
                e.AuthorBattleTag,
                e.AuthorName,
                e.Excerpt,
                e.CreatedAt,
                e.ReadAt))
            .ToList();
        return new GetMentionInboxResult(ChatResultCode.Ok, dtos);
    }

    /// <summary>
    /// C6 (Task 6, D6): per-entry idempotent ack. Resolution order mirrors the rest of the hub's
    /// client-bug-vs-typed-result split (e.g. <see cref="GetMessages"/>): fail-closed session FIRST
    /// (there is no identity to ack under), THEN the malformed-arg guards — a null array, or one over
    /// <see cref="ChatLimits.MentionAckBatchMax"/>, is a client programming error and throws
    /// <see cref="HubException"/> rather than returning a typed reject. The actual ack is ONE
    /// conditional <c>UpdateMany</c> (<see cref="Mentions.MentionInboxRepository.MarkRead"/>) whose
    /// filter ANDs the caller's own lowercased tag alongside the id list — see the class doc's
    /// AUTHORIZATION BOUNDARY and no-seq-auto-ack notes. Returns <see cref="ChatResultCode.Ok"/>
    /// REGARDLESS of the matched count (D6 — idempotent and foreign-id-safe; the result carries no
    /// information about which/how-many ids actually matched, so it can never be used as an oracle).
    /// </summary>
    public async Task<ChannelOperationResult> MarkMentionsRead(string[] mentionIds)
    {
        // 1. Fail-closed: no live session → no identity to ack an inbox for.
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        // 2. Malformed-arg guards, before any DB work — client-bug mapping (mirrors GetMessages/OpenDm/
        // CreateGroup's HubException precedent), NOT a typed result.
        if (mentionIds == null)
        {
            throw new HubException("MarkMentionsRead requires a non-null mentionIds array");
        }
        if (mentionIds.Length > ChatLimits.MentionAckBatchMax)
        {
            throw new HubException($"MarkMentionsRead: mentionIds exceeds the {ChatLimits.MentionAckBatchMax}-id batch cap");
        }

        // 3. The owner-filtered, unread-conditional ack — see the class doc for the authorization
        // boundary and idempotency guarantees this single call provides.
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await _mentionInboxRepository.MarkRead(mentionIds, session.Identity.BattleTag, now);

        // 4. Typed ack — ALWAYS Ok, independent of how many ids actually matched (D6).
        return new ChannelOperationResult(ChatResultCode.Ok);
    }

    /// <summary>
    /// C6 (Task 6, D6): marks EVERY unread entry of the caller's OWN inbox read — the same
    /// owner-filtered, unread-conditional update <see cref="MarkMentionsRead"/> uses, without the id
    /// list. Fail-closed session → <see cref="ChatResultCode.PermissionDenied"/>. Entries persist
    /// (never deleted) exactly as the class doc describes.
    /// </summary>
    public async Task<ChannelOperationResult> MarkAllMentionsRead()
    {
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await _mentionInboxRepository.MarkAllRead(session.Identity.BattleTag, now);

        return new ChannelOperationResult(ChatResultCode.Ok);
    }

    /// <summary>
    /// C6 (Task 8, D10): the mention-autocomplete search — a THREE-TIER candidate search served
    /// ENTIRELY from chat's OWN state (this hub's in-memory registries + its own <c>user_directory</c>
    /// Mongo collection). NEVER a website-backend call. Tiers, in this EXACT priority order — a
    /// candidate found in an earlier tier is never re-listed in a later one (dedup is first-tier-wins):
    /// <list type="number">
    /// <item>Tier 1 — active viewers of THIS channel (<see cref="FanOut.FocusRegistry.GetRoster"/>).</item>
    /// <item>Tier 2 — every online user, anywhere, not necessarily viewing this channel
    /// (<see cref="Sessions.ISessionRegistry.GetOnlineBattleTags"/> — the new Task 8 snapshot).</item>
    /// <item>Tier 3 — offline-but-recently-active <c>user_directory</c> matches
    /// (<see cref="Users.UserDirectoryRepository.SearchByNormalizedPrefix"/>), gated to
    /// <see cref="ChatLimits.MentionCandidateActivityWindow"/> (90d, D14) — the ONLY tier the activity
    /// gate applies to; tiers 1-2 are "online right now" and are trivially within the window regardless
    /// of what their directory <c>LastSeenAt</c> bookkeeping happens to say.</item>
    /// </list>
    /// <para>
    /// PRIVATE-LANE SCOPING (<see cref="ChannelType.Dm"/>/<see cref="ChannelType.GroupDm"/> ONLY): every
    /// tier's universe is additionally restricted to the channel's actual DURABLE member set, resolved
    /// via <see cref="Memberships.MembershipRepository.LoadForChannel"/> — a non-member is never offered
    /// as an autocomplete target inside a private conversation (Task 5 already ensures mentioning a
    /// non-member never actually notifies them; this closes the matching UI-noise gap). Public/
    /// SemiPublic/System search the full, unrestricted universe (no such read is made for them).
    /// Tier 3 is resolved DIFFERENTLY in a private lane: rather than the generic UNSCOPED
    /// <see cref="Users.UserDirectoryRepository.SearchByNormalizedPrefix"/> (whose own Mongo-side cap
    /// could otherwise let unrelated directory noise crowd out a genuine member's row before it is ever
    /// fetched — a real member must never be starved out of their own private lane's search), the member
    /// set's directory rows are loaded ONCE up front (<see cref="Users.UserDirectoryRepository.LoadMany"/>)
    /// and the prefix/90d filters are applied to that already-known, bounded set in memory instead.
    /// </para>
    /// <para>
    /// ENRICHMENT: the whole assembled (deduped, capped) candidate list is enriched with display name,
    /// full battleTag, and cached profile. For Public/SemiPublic/System this is ONE additional
    /// <see cref="Users.UserDirectoryRepository.LoadMany"/> batch read over the final candidate list; for
    /// a private lane the member-directory snapshot loaded above for tier 3 ALREADY covers every possible
    /// candidate (every tier is member-scoped there), so no second read is made at all. Either way it is
    /// never a per-candidate lookup, never a website-backend call. A candidate with no directory row (or
    /// a row with no cached <see cref="ChatProfile"/> yet) degrades gracefully: <c>Profile</c> stays null
    /// and <c>Name</c> is still derived from the candidate's own battleTag (the same
    /// <c>tag.Split('#')[0]</c> convention <see cref="ChatUser"/> uses) — never an error, never an
    /// exclusion.
    /// </para>
    /// AUTHORIZATION + resolution order:
    /// <list type="number">
    /// <item>Fail-closed session → <see cref="ChatResultCode.PermissionDenied"/>.</item>
    /// <item>Membership via <see cref="FanOut.OnlineMemberRegistry.TryGetMember"/> (the SAME hot-path,
    /// zero-DB check <c>SendMessage</c> uses) → <see cref="ChatResultCode.NotMember"/> on a miss. This
    /// also yields the channel's <see cref="ChannelType"/> at no extra cost — driving the private-lane
    /// scoping decision above without a second membership read.</item>
    /// <item>Assemble tiers 1-3 in order (each tier skipped once the cap is already met — tier 3's
    /// directory read never runs if tiers 1-2 alone already filled the cap), capped at
    /// <see cref="ChatLimits.MentionSearchMaxResults"/> total.</item>
    /// <item>ONE batch enrichment read, project to <see cref="MentionCandidateDto"/>, return
    /// <see cref="ChatResultCode.Ok"/>.</item>
    /// </list>
    /// </summary>
    public async Task<SearchMentionCandidatesResult> SearchMentionCandidates(string channelId, string prefix)
    {
        // 1. Fail-closed: no live session → no identity to authorize the search under.
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new SearchMentionCandidatesResult(ChatResultCode.PermissionDenied);
        }

        // 2. Membership (hot path, zero DB) — the SAME check SendMessage uses. TryGetMember (rather
        // than the plain IsMember) also yields MemberState.ChannelType, which the private-lane scoping
        // decision below needs, at no extra cost.
        if (!_onlineMemberRegistry.TryGetMember(channelId, Context.ConnectionId, out var callerState))
        {
            return new SearchMentionCandidatesResult(ChatResultCode.NotMember);
        }

        var prefixLower = (prefix ?? string.Empty).Trim().ToLowerInvariant();

        // Private-lane scoping (Dm/GroupDm ONLY): the channel's actual durable member set — a small
        // read (2 rows for a Dm, at most ChatLimits.MaxGroupSize for a GroupDm) — restricts every tier
        // below. null (Public/SemiPublic/System) means "unrestricted universe".
        //
        // memberDirectoryByTag is loaded HERE, up front, and reused for BOTH tier 3's candidate
        // generation AND the final enrichment step (below): in a private lane every eventual candidate
        // is, by construction, a member (memberScope gates every tier), so this ONE snapshot already
        // covers 100% of the candidate list — no second LoadMany is needed. Doing tier 3 THIS way
        // (filtering the already-loaded, fully-known member set in memory) — rather than running the
        // generic UNSCOPED SearchByNormalizedPrefix and hoping a member's row survives its own,
        // unrelated Mongo-side cap — is load-bearing: an unscoped global query could return 20 rows of
        // unrelated directory noise before a genuine (but incidentally later-ordered) member's row is
        // ever fetched, silently starving a real member out of a private lane's own search.
        HashSet<string> memberScope = null;
        Dictionary<string, UserDirectoryEntry> memberDirectoryByTag = null;
        if (callerState.ChannelType is ChannelType.Dm or ChannelType.GroupDm)
        {
            var memberships = await _membershipRepository.LoadForChannel(channelId);
            memberScope = new HashSet<string>(memberships.Select(m => m.BattleTag), StringComparer.OrdinalIgnoreCase);
            var memberEntries = await _userDirectory.LoadMany(memberScope);
            memberDirectoryByTag = memberEntries.ToDictionary(e => e.BattleTag, StringComparer.OrdinalIgnoreCase);
        }

        var candidates = new List<(string BattleTag, int Tier)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Applies the private-lane scope + prefix filters and the dedup/cap bookkeeping shared by every
        // tier below. A battleTag excluded by scope or prefix is never marked "seen" (harmless either
        // way — scope/prefix are identical across tiers — but keeps the dedup set exactly the assembled
        // candidate set).
        void AddTier(IEnumerable<string> battleTags, int tier)
        {
            foreach (var battleTag in battleTags)
            {
                if (candidates.Count >= ChatLimits.MentionSearchMaxResults)
                {
                    return;
                }
                if (memberScope != null && !memberScope.Contains(battleTag))
                {
                    continue;
                }
                if (prefixLower.Length > 0 && !battleTag.ToLowerInvariant().StartsWith(prefixLower, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!seen.Add(battleTag))
                {
                    continue;
                }
                candidates.Add((battleTag, tier));
            }
        }

        // Tier 1: active viewers of THIS channel.
        AddTier(_focusRegistry.GetRoster(channelId), 1);

        // Tier 2: online users anywhere (not necessarily viewing this channel).
        if (candidates.Count < ChatLimits.MentionSearchMaxResults)
        {
            AddTier(_sessionRegistry.GetOnlineBattleTags(), 2);
        }

        // Tier 3: offline-but-recently-active directory matches — the ONLY tier the 90d activity gate
        // applies to (tiers 1-2 are "online now", trivially within the window regardless of their
        // directory LastSeenAt bookkeeping). Skipped entirely once the cap is already met.
        if (candidates.Count < ChatLimits.MentionSearchMaxResults)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var minLastSeenAt = now - ChatLimits.MentionCandidateActivityWindow;

            if (memberDirectoryByTag != null)
            {
                // Private lane: filter the ALREADY-LOADED, fully-known member snapshot in memory —
                // never the generic unscoped global query (see the doc comment above memberScope).
                var privateMatches = memberDirectoryByTag.Values
                    .Where(e => e.LastSeenAt >= minLastSeenAt)
                    .Where(e => (e.NormalizedName ?? string.Empty).StartsWith(prefixLower, StringComparison.Ordinal))
                    .Select(e => e.DisplayBattleTag ?? e.BattleTag);
                AddTier(privateMatches, 3);
            }
            else
            {
                // REVIEW FIX (C6 T8): the query's own limit is capped at how many slots tiers 1/2 have
                // actually LEFT (never the flat MentionSearchMaxResults constant regardless of how full
                // candidates already is), AND the tiers-1/2 battleTags already claimed (`seen`) are
                // excluded server-side (see SearchByNormalizedPrefix's doc) — so every row this query
                // can possibly return is both usable (never re-discarded as a dupe here) and not wasted
                // on a request for more rows than could ever be added. Trimming the limit WITHOUT that
                // exclusion would be unsafe on its own: rows this query would go on to discard as dupes
                // could still rank ahead of a genuinely new match within a smaller window, starving it
                // out exactly like the private lane's pre-fix bug — the exclusion is what makes a
                // smaller limit safe.
                var remaining = Math.Max(0, ChatLimits.MentionSearchMaxResults - candidates.Count);
                var seenLower = seen.Select(tag => tag.ToLowerInvariant()).ToList();
                var directoryMatches = await _userDirectory.SearchByNormalizedPrefix(
                    prefixLower, minLastSeenAt, remaining, seenLower);
                AddTier(directoryMatches.Select(e => e.DisplayBattleTag ?? e.BattleTag), 3);
            }
        }

        // Enrichment: ONE batch directory read across the whole assembled candidate list — except in a
        // private lane, where memberDirectoryByTag (loaded above) ALREADY covers every possible
        // candidate (every tier is member-scoped there), so no second LoadMany is made at all.
        // Skipped entirely for an empty candidate set — nothing to enrich.
        var dtos = new List<MentionCandidateDto>(candidates.Count);
        if (candidates.Count > 0)
        {
            var directoryByTag = memberDirectoryByTag
                ?? (await _userDirectory.LoadMany(candidates.Select(c => c.BattleTag)))
                    .ToDictionary(e => e.BattleTag, StringComparer.OrdinalIgnoreCase);
            dtos.AddRange(candidates.Select(c => BuildCandidateDto(c.BattleTag, c.Tier, directoryByTag)));
        }

        return new SearchMentionCandidatesResult(ChatResultCode.Ok, dtos);
    }

    /// <summary>
    /// Projects one assembled (battleTag, tier) pair to its <see cref="MentionCandidateDto"/> using the
    /// ONE batch-loaded directory snapshot (<paramref name="directoryByTag"/>, keyed case-insensitively
    /// on <see cref="UserDirectoryEntry.BattleTag"/>). A directory hit supplies
    /// <see cref="UserDirectoryEntry.DisplayBattleTag"/> (the authoritative last-known display casing)
    /// and its cached <see cref="ChatProfile"/> — which may itself still be null (a directory row can
    /// exist with no enrichment landed yet, the <c>Search_StubbedProfile_GracefullyAbsent</c> leg). A
    /// miss (no directory row at all) falls back to the candidate's own tier-native battleTag with a
    /// null Profile. <c>Name</c> is ALWAYS derived by splitting the resolved battleTag at '#' — mirrors
    /// <see cref="ChatUser"/>'s own convention — so a missing/unenriched directory row degrades
    /// gracefully instead of erroring or excluding the candidate.
    /// </summary>
    private static MentionCandidateDto BuildCandidateDto(
        string battleTag, int tier, IReadOnlyDictionary<string, UserDirectoryEntry> directoryByTag)
    {
        directoryByTag.TryGetValue(battleTag, out var entry);
        var displayTag = entry?.DisplayBattleTag ?? battleTag;
        var name = displayTag.Split('#')[0];
        return new MentionCandidateDto(displayTag, name, tier, entry?.Profile);
    }
}

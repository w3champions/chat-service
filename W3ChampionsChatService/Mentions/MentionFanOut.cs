using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Relationships;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Mentions;

/// <summary>
/// C6 (Task 5, C6-plan.md D3/D4): the mention fan-out. Called by the send pipeline
/// (<c>ChatHub.SendMessage</c> step 8 — fix round 1 (F2b) moved it after the step-7.75 channel fan-out
/// seam, see that method's doc) once a message is durably persisted, with the validated,
/// deduped mention-tag list produced by the Task 4 markup gate. For each ELIGIBLE target it writes an
/// offline <see cref="MentionInboxEntry"/> and pushes a targeted <see cref="ChatEvents.MentionNotified"/>
/// to that target's live connection.
/// <para>
/// ELIGIBILITY (D3 — this is a LEAK BOUNDARY; a target gets an entry + event IFF ALL hold):
/// <list type="number">
/// <item>The message is NOT shadow. Shadow senders' messages notify NOBODY (the shadow illusion). The
/// PRIMARY guard is the call-site skip (<c>SendMessage</c> does not call this at all when the message is
/// shadow); the <see cref="ChannelMessage.Shadow"/> early-return below is defense-in-depth so a future
/// refactor that drops the call-site skip still cannot leak a shadow message.</item>
/// <item>The target is NOT the sender (case-insensitive) — no self-mention notifications.</item>
/// <item>The target has an actual <c>channel_memberships</c> row for THIS channel
/// (<see cref="MembershipRepository.Load"/>). For Dm/GroupDm this is the hard PRIVACY WALL: an inbox
/// entry carries a ~120-char content excerpt, and a private conversation's excerpt must NEVER reach a
/// non-participant. This wall is the SOLE authority on who is notified: the send-side gate (step 5.25)
/// validates only the mention COUNT cap — never resolvability or membership — so mentioning a non-member
/// (or an unresolvable/garbage tag) is legal content that delivers verbatim and simply notifies nobody
/// here. Follow-up spec §4 EXCEPTION: for <see cref="ChannelType.Public"/> rooms only, a target with NO
/// membership row is still eligible provided the tag resolves to a <see cref="UserDirectoryRepository"/>
/// row — a public room's excerpt is public content, so the membership wall protects nothing there;
/// Dm/GroupDm/SemiPublic/System are unaffected and keep the membership wall exactly as before.</item>
/// <item>The target's membership <see cref="ChannelMembership.NotificationLevel"/> is not
/// <see cref="NotificationLevel.None"/> — "none: silence" (spec §7) is an explicit opt-out that outranks
/// mentions, not just level-All activity.</item>
/// <item>The target's membership is NOT currently decline-suppressed
/// (<see cref="ChannelMembership.DeclinedUntil"/> unset or already elapsed vs. <c>now</c>) — a pending-Dm
/// recipient who DECLINED (C5 D3's 24h soft window) must never be pinged by the initiator's mentions, even
/// though a decline never lowers the membership level. Mirrors
/// <see cref="SessionStateAssembler.BuildPendingDmTray"/>, which hides the same window from the tray.</item>
/// <item>PR36 follow-up (D1): the TARGET has NOT blocked the sender — checked LAST, only for a target
/// that is otherwise eligible per rules (a)-(e) above, via <see cref="IRelationshipProvider.GetSnapshotAsync"/>
/// on the TARGET's own tag (direction matters: sender-side blocking is deliberately out of scope here —
/// see the per-target body for the one-line note). FAIL OPEN on a provider failure: a relationship-service
/// outage DELIVERS the mention rather than suppressing it (never breaks the send pipeline or blanket-mutes
/// every mention). This is a notification-only gate — the message and its mention chip still render
/// normally in the channel for everyone; only the inbox entry + push are withheld from a blocking target.</item>
/// </list>
/// FOCUS IS IRRELEVANT here (unlike C3 activity routing, which suppresses focused members): a focused
/// target STILL gets the entry + event. The server never guesses whether a mention was "seen"; a
/// live-seen mention is acked by the client within seconds (create-then-client-ack, pinned — Task 6
/// owns the ack surface). An OFFLINE eligible target gets the durable entry only (no live connection to
/// push to); <c>GetMentionInbox</c> / <c>SessionState.MentionUnreadCount</c> (Task 6) surface it on
/// their next connect.
/// </para>
/// <para>
/// Singleton (registered in <see cref="Startup"/>): it holds no per-call state and is shared by every
/// hub invocation, mirroring the C3 fan-out registries. It pushes through its OWN
/// <see cref="IHubContext{ChatHub}"/> (targeting the resolved connection), NOT the invoking hub's
/// <c>Clients</c> — a mention target is almost never the sender, so the push crosses connections.
/// </para>
/// <para>
/// PER-TARGET FAULT ISOLATION (mirrors <see cref="FanOut.FanOutEngine.OnMessagePersisted"/>'s
/// per-recipient try/catch idiom): a single target's failed membership read, failed inbox insert, or
/// dead/torn-down socket must NEVER break the sender's already-persisted <c>Ok</c> ack, and must never
/// abort delivery to the OTHER eligible targets — each target's whole body is wrapped, failures
/// log-and-continue. The inbox insert happens BEFORE the push because the entry's id rides in the event
/// payload; if the insert fails there is nothing to notify about, so that target is skipped entirely.
/// </para>
/// <para>
/// Fix round 1 (F2a): <see cref="NotifyAsync"/> runs three STAGES rather than one per-target loop that
/// awaits everything in-line. Stage 1 resolves the cheap eligibility rules (b)-(e) per target, exactly as
/// before (sequential — these are local Mongo point-reads). Stage 2 resolves EVERY otherwise-eligible
/// target's D1 block snapshot CONCURRENTLY (<see cref="Task.WhenAll{TResult}(System.Collections.Generic.IEnumerable{Task{TResult}})"/>),
/// each individually fail-open — at steady state nearly every one is a wb HTTP round-trip (the 5-min
/// cache TTL), so resolving them serially ahead of the C3 channel fan-out could otherwise delay
/// <c>MessageReceived</c> for the whole channel by up to <see cref="ChatLimits.MaxMentionsPerMessage"/>
/// timeouts; concurrently, the worst case is ONE. Stage 3 writes (insert + push) for every non-blocked
/// target, sequentially, under the SAME per-target fault isolation described above.
/// </para>
/// </summary>
public class MentionFanOut(
    IHubContext<ChatHub> hubContext,
    ISessionRegistry sessionRegistry,
    MembershipRepository membershipRepository,
    MentionInboxRepository mentionInboxRepository,
    UserDirectoryRepository userDirectory,
    // PR36 follow-up (D1): the block-enforcement source — GetSnapshotAsync is read for the TARGET's own
    // tag, ONLY for a target that already cleared the cheap eligibility checks below (bounded to at most
    // ChatLimits.MaxMentionsPerMessage calls per message).
    IRelationshipProvider relationshipProvider,
    // PR36 follow-up (D2): the persisted-preference carrier — consulted in the non-member Public branch
    // so a target who opted out (None) before leaving a room stays silenced even without a membership row.
    NotificationPreferenceRepository notificationPreferenceRepository)
{
    // The SignalR delivery channel — pushes the targeted MentionNotified to a specific connection.
    private readonly IHubContext<ChatHub> _hubContext = hubContext;

    // Resolves a mention target's live connection by battleTag (case-insensitive) — null when offline.
    private readonly ISessionRegistry _sessionRegistry = sessionRegistry;

    // The durable (channel, user) store — the membership-wall (D3c) + notification-level (D3d) source.
    private readonly MembershipRepository _membershipRepository = membershipRepository;

    // The offline mention-notification store — one row per eligible target per mentioning message.
    private readonly MentionInboxRepository _mentionInboxRepository = mentionInboxRepository;

    // Follow-up spec §4: resolvability source for PUBLIC-room mentions of non-members — the same D14
    // existence check OpenDm uses. Read ONLY on the public/no-membership branch below.
    private readonly UserDirectoryRepository _userDirectory = userDirectory;

    // PR36 follow-up (D1): the block-enforcement source — see the ctor param doc comment above.
    private readonly IRelationshipProvider _relationshipProvider = relationshipProvider;

    // PR36 follow-up (D2): the persisted-preference carrier — see the ctor param doc comment above.
    private readonly NotificationPreferenceRepository _notificationPreferenceRepository = notificationPreferenceRepository;

    /// <summary>
    /// Fans a persisted, non-shadow message's validated mention tags out to the eligible members —
    /// see the class doc for the six eligibility rules and the fault-isolation contract.
    /// <paramref name="now"/> is the trusted server clock the hub already read once for this send
    /// (threaded in, not re-read, so the entry's CreatedAt/ExpiresAt decide against the same instant).
    /// </summary>
    public async Task NotifyAsync(ChatChannel channel, ChannelMessage message, IReadOnlyList<string> mentionTags, DateTime now)
    {
        // Rule (a), defense-in-depth: a shadow message notifies NOBODY. The PRIMARY guard is the
        // SendMessage call-site skip (it never calls this for a shadow message); this early-return is the
        // leak-boundary belt-and-suspenders — a future refactor that drops the call-site skip still cannot
        // leak a shadow message's mentions.
        if (message.Shadow)
        {
            return;
        }

        // Null-conditional so this pre-loop setup can NEVER throw out of NotifyAsync and break the
        // sender's already-persisted Ok ack (the reason the per-target body below is fault-isolated).
        // BuildSenderSnapshot always populates Sender.BattleTag, so this is purely defensive: a null
        // sender tag simply never self-matches below (tagLower is never null), which is the safe outcome.
        var senderTagLower = message.Sender?.BattleTag?.ToLowerInvariant();

        // STAGE 1 — cheap eligibility (rules (b)-(e) plus the Public non-member directory/pref checks —
        // IsOtherwiseEligibleAsync). Sequential, exactly as before this fix: these are local Mongo
        // point-reads, not the wb round-trip stage 2 batches. Each target's check is still individually
        // fault-isolated (mirrors the old single-catch-per-target loop) so a Mongo hiccup here skips only
        // that target, logs, and never breaks the sender's already-persisted Ok ack.
        var otherwiseEligible = new List<(string Tag, string TagLower)>();
        foreach (var tag in mentionTags)
        {
            var tagLower = tag.ToLowerInvariant();

            // Rule (b): no self-mention notifications (case-insensitive). Checked BEFORE any DB read.
            if (tagLower == senderTagLower)
            {
                continue;
            }

            try
            {
                if (await IsOtherwiseEligibleAsync(channel, tag, now))
                {
                    otherwiseEligible.Add((tag, tagLower));
                }
            }
            catch (Exception ex)
            {
                Log.Warning(
                    ex,
                    "Mention fan-out eligibility check for {Tag} on channel {ChannelId} for message {MessageId} failed — skipping; other targets and the sender ack are unaffected",
                    tag,
                    channel.Id,
                    message.Id);
            }
        }

        if (otherwiseEligible.Count == 0)
        {
            return;
        }

        // STAGE 2 — PR36 follow-up (D1), fix round 1 (F2a): resolve every otherwise-eligible target's
        // block snapshot CONCURRENTLY rather than one at a time. Direction is deliberate: only the
        // TARGET's OWN block list matters here — sender-side blocking (the sender having blocked the
        // target) is out of scope for mention gating and is never consulted. Bounded to at most
        // ChatLimits.MaxMentionsPerMessage concurrent calls for this message. Each call still fails open
        // individually (IsBlockedFailOpenAsync) — a relationship-provider outage on ONE target's fetch
        // never suppresses another target whose own snapshot resolves fine, and never blanket-mutes the
        // whole message or breaks the send pipeline.
        var senderTag = message.Sender?.BattleTag;
        var blockedFlags = await Task.WhenAll(
            otherwiseEligible.Select(target => IsBlockedFailOpenAsync(target.Tag, senderTag, channel.Id, message.Id)));

        // STAGE 3 — write (inbox insert + live push) for every non-blocked target, sequentially. PER-TARGET
        // FAULT ISOLATION (mirrors FanOutEngine.OnMessagePersisted): a single target's failed inbox insert
        // or dead/torn-down socket must NEVER break the sender's already-persisted Ok ack, and must never
        // abort delivery to the OTHER eligible targets. Failures log-and-continue.
        for (var i = 0; i < otherwiseEligible.Count; i++)
        {
            if (blockedFlags[i])
            {
                continue;
            }

            var (tag, tagLower) = otherwiseEligible[i];
            try
            {
                await DeliverAsync(channel, message, tag, tagLower, now);
            }
            catch (Exception ex)
            {
                Log.Warning(
                    ex,
                    "Mention fan-out to {Tag} on channel {ChannelId} for message {MessageId} failed — skipping; other targets and the sender ack are unaffected",
                    tag,
                    channel.Id,
                    message.Id);
            }
        }
    }

    /// <summary>
    /// STAGE 1 body: rules (c)/(d)/(e) plus the Public non-member directory + PR36 (D2) pref checks —
    /// everything EXCEPT the D1 block check (stage 2, batched) and the actual write (stage 3). Returns
    /// false for every outcome the original inline loop used to <c>continue</c> past; a throw here is the
    /// caller's problem to fault-isolate (mirrors the old per-target catch).
    /// </summary>
    private async Task<bool> IsOtherwiseEligibleAsync(ChatChannel channel, string tag, DateTime now)
    {
        // Rule (c): the excerpt PRIVACY WALL — two branches. When a durable channel_memberships row
        // exists, the target is JOINED and rules (d)/(e) below decide. When it does NOT: for every
        // non-Public channel type (Dm/GroupDm/SemiPublic/System) the wall holds and the target is dropped
        // — an inbox entry carries a ~120-char content excerpt, and a private conversation's excerpt must
        // NEVER reach a non-participant; for Public specifically (§4 below) the wall is replaced by a
        // directory-resolvability check instead. Load lowercases the tag internally (C5 T4 key
        // convention), so the display-cased mention tag matches.
        var membership = await _membershipRepository.Load(channel.Id, tag);
        if (membership != null)
        {
            // Rules (d)/(e) — unchanged for JOINED targets: an explicit NotificationLevel.None opt-out
            // outranks mentions, and a decline-suppressed Dm recipient is never pinged.
            if (membership.NotificationLevel == NotificationLevel.None)
            {
                return false;
            }
            if (membership.DeclinedUntil.HasValue && membership.DeclinedUntil.Value > now)
            {
                return false;
            }
            return true;
        }

        if (channel.Type != ChannelType.Public)
        {
            // Rule (c) — UNCHANGED for every non-public type: the Dm/GroupDm excerpt PRIVACY WALL, and
            // SemiPublic/System keep the membership wall too (§4 widens Public only).
            return false;
        }

        // Follow-up spec §4: PUBLIC rooms are mentionable WITHOUT membership — a public room's excerpt
        // is public content, so rule (c)'s wall protects nothing here. The target must still be a
        // RESOLVABLE user (a user_directory row; Load lowercases the display-cased tag), so garbage tags
        // keep producing nothing. PR36 follow-up (D2) narrowed the v1 gap here — see the pref check below.
        var directoryEntry = await _userDirectory.Load(tag);
        if (directoryEntry == null)
        {
            return false;
        }

        // PR36 follow-up (D2): the narrowed v1 gap — a non-member can silence mentions from a room they
        // haven't (re)joined by: join → NotificationLevel.None → leave. Leave hard-deletes the membership
        // row (so rule (c) above no longer sees it), but the LAST EXPLICITLY-SET level persists
        // independently here (written by ChatHub.SetNotificationLevel, and seeded back into a rejoined
        // membership by JoinChannel). Only an explicit None suppresses; any other level, or no pref at all
        // (never explicitly set), delivers normally.
        var pref = await _notificationPreferenceRepository.Load(tag, channel.Id);
        return pref == null || pref.NotificationLevel != NotificationLevel.None;
    }

    /// <summary>
    /// STAGE 2 body: PR36 follow-up (D1), the TARGET's own block-check, individually fail-open.
    /// OUTAGE POSTURE — FAIL OPEN: this catch DELIVERS (returns false / "not blocked") on any failure —
    /// including <see cref="RelationshipUnavailableException"/> once the provider's own stale-cache
    /// fallback is exhausted — rather than suppressing the mention. A relationship outage must never
    /// blanket-mute every mention or break the send pipeline; it only means this one target's block state
    /// couldn't be proven, and we default to delivering rather than guessing a block.
    /// </summary>
    private async Task<bool> IsBlockedFailOpenAsync(string tag, string senderTag, string channelId, string messageId)
    {
        try
        {
            var targetSnapshot = await _relationshipProvider.GetSnapshotAsync(tag);
            return targetSnapshot.HasBlocked(senderTag);
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "Relationship snapshot unavailable for mention target {Tag} on channel {ChannelId} for message {MessageId} — delivering fail-open",
                tag,
                channelId,
                messageId);
            return false;
        }
    }

    /// <summary>
    /// STAGE 3 body: writes the durable inbox entry FIRST — its id rides in the event payload — then the
    /// targeted live push, for one already-eligible, non-blocked target. Expiry via
    /// ExpiryCalculator.ForMentionInboxEntry (30d, always &lt;= the message TTL) — the wiring of the C1
    /// amendment-1 calculator into a production write path. BattleTag is stored LOWERCASED (D8 key
    /// convention); author fields keep the sender's DISPLAY casing.
    /// </summary>
    private async Task DeliverAsync(ChatChannel channel, ChannelMessage message, string tag, string tagLower, DateTime now)
    {
        var entry = new MentionInboxEntry
        {
            BattleTag = tagLower,
            ChannelId = channel.Id,
            MessageId = message.Id,
            Seq = message.Seq,
            AuthorBattleTag = message.Sender.BattleTag,
            AuthorName = message.Sender.Name,
            Excerpt = Excerpts.Bounded(message.Content),
            CreatedAt = now,
            ExpiresAt = ExpiryCalculator.ForMentionInboxEntry(now),
        };
        await _mentionInboxRepository.Insert(entry);

        // Targeted live push — ONLY to this target's own connection (never a broadcast), and only if
        // online. A focused target STILL gets it (the server never guesses "seen"; the client acks —
        // Task 6). An OFFLINE target gets the durable entry above only; Task 6's GetMentionInbox /
        // SessionState.MentionUnreadCount surface it on their next connect. GetByBattleTag is
        // case-insensitive, so the display-cased mention tag resolves the session.
        var session = _sessionRegistry.GetByBattleTag(tag);
        if (session != null)
        {
            var dto = new MentionNotifiedDto(
                entry.Id,
                entry.ChannelId,
                entry.MessageId,
                entry.Seq,
                entry.AuthorBattleTag,
                entry.AuthorName,
                entry.Excerpt,
                entry.CreatedAt);
            await _hubContext.Clients.Client(session.ConnectionId).SendAsync(ChatEvents.MentionNotified, dto);
        }
    }
}

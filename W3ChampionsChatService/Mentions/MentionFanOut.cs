using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Mentions;

/// <summary>
/// C6 (Task 5, C6-plan.md D3/D4): the mention fan-out. Called by the send pipeline
/// (<c>ChatHub.SendMessage</c> step 7.75) once a message is durably persisted, with the validated,
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
/// </summary>
public class MentionFanOut(
    IHubContext<ChatHub> hubContext,
    ISessionRegistry sessionRegistry,
    MembershipRepository membershipRepository,
    MentionInboxRepository mentionInboxRepository,
    UserDirectoryRepository userDirectory)
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

    /// <summary>
    /// Fans a persisted, non-shadow message's validated mention tags out to the eligible members —
    /// see the class doc for the five eligibility rules and the fault-isolation contract.
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

        foreach (var tag in mentionTags)
        {
            var tagLower = tag.ToLowerInvariant();

            // Rule (b): no self-mention notifications (case-insensitive). Checked BEFORE any DB read.
            if (tagLower == senderTagLower)
            {
                continue;
            }

            // PER-TARGET FAULT ISOLATION (mirrors FanOutEngine.OnMessagePersisted): the whole per-target
            // body — membership read, inbox insert, and live push — is wrapped so a single target's Mongo
            // hiccup or dead socket can never break the sender's already-persisted Ok ack or abort delivery
            // to the OTHER eligible targets. Failures log-and-continue.
            try
            {
                // Rule (c): the excerpt PRIVACY WALL — two branches. When a durable channel_memberships
                // row exists, the target is JOINED and rules (d)/(e) below decide. When it does NOT: for
                // every non-Public channel type (Dm/GroupDm/SemiPublic/System) the wall holds and the
                // target is dropped — an inbox entry carries a ~120-char content excerpt, and a private
                // conversation's excerpt must NEVER reach a non-participant; for Public specifically (§4
                // below) the wall is replaced by a directory-resolvability check instead. Load lowercases
                // the tag internally (C5 T4 key convention), so the display-cased mention tag matches.
                var membership = await _membershipRepository.Load(channel.Id, tag);
                if (membership != null)
                {
                    // Rules (d)/(e) — unchanged for JOINED targets: an explicit NotificationLevel.None
                    // opt-out outranks mentions, and a decline-suppressed Dm recipient is never pinged.
                    if (membership.NotificationLevel == NotificationLevel.None)
                    {
                        continue;
                    }
                    if (membership.DeclinedUntil.HasValue && membership.DeclinedUntil.Value > now)
                    {
                        continue;
                    }
                }
                else if (channel.Type == ChannelType.Public)
                {
                    // Follow-up spec §4: PUBLIC rooms are mentionable WITHOUT membership — a public
                    // room's excerpt is public content, so rule (c)'s wall protects nothing here. The
                    // target must still be a RESOLVABLE user (a user_directory row; Load lowercases the
                    // display-cased tag), so garbage tags keep producing nothing. v1 accepted gap: a
                    // non-member cannot silence mentions from a room they haven't joined — join +
                    // NotificationLevel.None (the membership branch above) is the opt-out.
                    var directoryEntry = await _userDirectory.Load(tag);
                    if (directoryEntry == null)
                    {
                        continue;
                    }
                }
                else
                {
                    // Rule (c) — UNCHANGED for every non-public type: the Dm/GroupDm excerpt PRIVACY
                    // WALL, and SemiPublic/System keep the membership wall too (§4 widens Public only).
                    continue;
                }

                // Eligible. Write the offline inbox entry FIRST — its id rides in the event payload. Expiry
                // via ExpiryCalculator.ForMentionInboxEntry (30d, always <= the message TTL) — the wiring
                // of the C1 amendment-1 calculator into a production write path. BattleTag is stored
                // LOWERCASED (D8 key convention); author fields keep the sender's DISPLAY casing.
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

                // Targeted live push — ONLY to this target's own connection (never a broadcast), and only
                // if online. A focused target STILL gets it (the server never guesses "seen"; the client
                // acks — Task 6). An OFFLINE target gets the durable entry above only; Task 6's
                // GetMentionInbox / SessionState.MentionUnreadCount surface it on their next connect.
                // GetByBattleTag is case-insensitive, so the display-cased mention tag resolves the session.
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
}

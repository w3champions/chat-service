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
/// non-participant. Mentioning a resolvable non-member is legal content (the Task 4 gate checks only
/// resolvability, not membership) — it simply notifies nobody.</item>
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
    MentionInboxRepository mentionInboxRepository)
{
    // The SignalR delivery channel — pushes the targeted MentionNotified to a specific connection.
    private readonly IHubContext<ChatHub> _hubContext = hubContext;

    // Resolves a mention target's live connection by battleTag (case-insensitive) — null when offline.
    private readonly ISessionRegistry _sessionRegistry = sessionRegistry;

    // The durable (channel, user) store — the membership-wall (D3c) + notification-level (D3d) source.
    private readonly MembershipRepository _membershipRepository = membershipRepository;

    // The offline mention-notification store — one row per eligible target per mentioning message.
    private readonly MentionInboxRepository _mentionInboxRepository = mentionInboxRepository;

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
                // Rule (c): the excerpt PRIVACY WALL. The target must have a durable channel_memberships
                // row for THIS channel — an inbox entry carries a ~120-char content excerpt, and a private
                // (Dm/GroupDm) conversation's excerpt must NEVER reach a non-participant. Load lowercases
                // the tag internally (C5 T4 key convention), so the display-cased mention tag matches.
                var membership = await _membershipRepository.Load(channel.Id, tag);
                if (membership == null)
                {
                    continue;
                }

                // Rule (d): "none: silence" (spec §7) outranks mentions — an explicit opt-out suppresses
                // the mention too, not just level-All activity.
                if (membership.NotificationLevel == NotificationLevel.None)
                {
                    continue;
                }

                // Rule (e): the C5 decline-suppression window (D3). A pending-Dm recipient who DECLINED
                // keeps their membership at NotificationLevel.All — a decline sets ONLY DeclinedUntil
                // (ChatHub.Dm.DeclineRequest) and never lowers the level — so rules (c)/(d) alone would let
                // the initiator's pending mentions ping straight through the 24h soft-suppression window,
                // contradicting the C5 guarantee that a declined request never pings them. Mirror
                // SessionStateAssembler.BuildPendingDmTray's `DeclinedUntil > now` boundary against the SAME
                // trusted `now` this send already read (not a fresh clock) — the window is temporal, so an
                // elapsed DeclinedUntil resumes normal notification (self-heals in 24h).
                if (membership.DeclinedUntil.HasValue && membership.DeclinedUntil.Value > now)
                {
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

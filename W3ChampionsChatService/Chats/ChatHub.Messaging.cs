using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.Chats;

/// <summary>
/// C3 (Task 11): the durable send pipeline. <see cref="SendMessage(string, string)"/> is the NEW
/// two-arg overload (channelId + content) that validates, rate-limits, mute-gates, DURABLY persists
/// (per-channel seq + expiry), fires the fan-out seam, and returns a typed <see cref="SendMessageResult"/>.
/// It is the sole <c>SendMessage</c> method on <see cref="ChatHub"/> — Task 19 removed the legacy
/// single-arg <c>SendMessage(string)</c> overload it used to coexist with.
/// <para>
/// C3 (Task 16): <see cref="GetMessages"/> — the read-side companion. PULL-ONLY: every result comes
/// back through this method's own typed <see cref="GetMessagesResult"/>; it NEVER pushes a SignalR
/// event (hard guardrail — a live-connected caller who wants updates uses <c>FocusChannel</c> +
/// <c>MessageReceived</c>, not this method).
/// </para>
/// </summary>
public partial class ChatHub
{
    /// <summary>
    /// Sends a message to a channel. Every stage returns its own typed result — there is NO silent
    /// drop path (the only deliberate silence in the whole design is a shadow message reaching nobody
    /// but its author, which is fan-out's job in Task 12; here a shadow send still returns Ok and
    /// persists flagged). The stage order is load-bearing and honored EXACTLY:
    /// <list type="number">
    /// <item>Fail-closed session resolution: an unregistered connection (never authenticated, or its
    /// session was displaced/torn down) → <see cref="ChatResultCode.PermissionDenied"/>.</item>
    /// <item>Trim + length: empty-after-trim OR over <see cref="ChatLimits.MaxMessageLength"/> →
    /// <see cref="ChatResultCode.TooLong"/> (empty→TooLong is a pinned plan decision — the enum has no
    /// InvalidContent value).</item>
    /// <item>Membership via <see cref="FanOut.OnlineMemberRegistry.IsMember"/> (seeded at connect, zero
    /// DB, O(1) reverse-index lookup — no roster copy) → <see cref="ChatResultCode.NotMember"/> if the
    /// caller isn't a member.</item>
    /// <item>Rate limit (<see cref="FanOut.MessageRateLimiter"/>), BEFORE the channel load (security-
    /// review fix: a throttled member must be rejected before any DB read, so a hard-throttled caller
    /// looping SendMessage cannot amplify Mongo reads): not allowed → <see cref="ChatResultCode.Throttled"/>
    /// with the retry-after. On the SINGLE decision that escalates into hard auto-throttle
    /// (<see cref="FanOut.RateLimitDecision.JustAutoThrottled"/>), push exactly one
    /// <see cref="ChatEvents.ThrottleNotice"/> to the caller.</item>
    /// <item>Channel load; missing → <see cref="ChatResultCode.NotFound"/> (the member-of-a-deleted-
    /// channel edge).</item>
    /// <item>Mention markup validation (C6 Task 4, step 5.25 — D1/D2, amended by the "strip &amp; deliver
    /// as plain" decision): <see cref="Mentions.MentionMarkup.ExtractTags"/> extracts the message's
    /// <c>&lt;@BattleTag#123&gt;</c> tokens (deduped case-insensitively, first-occurrence order — a target
    /// mentioned N times counts ONCE toward the cap). The ONLY reject here is the COUNT cap: more than
    /// <see cref="ChatLimits.MaxMentionsPerMessage"/> distinct tags → <see cref="ChatResultCode.TooLong"/>
    /// (an anti-abuse bound on fan-out work / spam; the pinned 7-member enum has no InvalidContent value —
    /// same C3 precedent as empty-after-trim). A message is NEVER rejected for the access or resolvability
    /// of its mentions: an unresolvable/garbage tag, or a tag naming someone who is not a legal render
    /// target of this channel, is legal content — it is never a reason to reject the send. The extracted,
    /// deduped, post-cap tag list is kept in a local and threads forward to both step 5.26 below and the
    /// (Task 5) mention fan-out call site.</item>
    /// <item>Server-canonical mention rendering (step 5.26 — D2, 2026-08-05 decision, mention-
    /// canonicalization brief): for each distinct mentioned target (the same post-cap tag list from step
    /// 5.25), evaluate the RENDERABILITY predicate — a <see cref="Memberships.MembershipRepository"/> row
    /// for this channel, OR (the channel is <see cref="ChannelType.Public"/> AND the tag is
    /// <see cref="Users.UserDirectoryRepository"/>-resolvable) — and rewrite <c>content</c> via
    /// <see cref="Mentions.MentionMarkup.RewriteUnrenderable"/>: a token whose target fails the predicate is
    /// downgraded to its plain-text form (<c>@BattleTag#123</c>, no angle brackets — byte-for-byte the
    /// launcher's pre-existing client-side downgrade text) IN THE PERSISTED CONTENT; every other token's
    /// markup is left untouched. This is Mongo point reads ONLY (membership +, for Public non-members,
    /// directory — never both per target, and never the relationship provider), bounded to at most
    /// <see cref="ChatLimits.MaxMentionsPerMessage"/> (5) targets. Deciding this SERVER-SIDE, once, at
    /// send time — rather than leaving it to each client to guess a roster — means every reader (live
    /// delivery, history paging, a non-member's Public read, an old un-upgraded client) sees IDENTICAL
    /// canonical content; the launcher's own client-side roster-guessing downgrade gate is now redundant
    /// and has been removed. The predicate deliberately does NOT consult blocks, NotificationLevel,
    /// notification preferences, or DeclinedUntil — those suppress the PING (<see cref="Mentions.MentionFanOut"/>,
    /// step 8), never the RENDER: a member who blocked the sender still SEES a normal chip (stripping it
    /// would leak the block to the sender, an explicit Marco pin), and an opted-out member still sees their
    /// own name highlighted. Runs BEFORE the private-lane gates (5.5) and the mute gate (6) so a shadow or
    /// muted send is canonicalized exactly like any other — this is content canonicalization, not
    /// moderation, and applies unconditionally to whatever eventually gets persisted.</item>
    /// <item>Private-lane gates (C5 Task 4, step 5.5) — <see cref="ChannelType.Dm"/>/
    /// <see cref="ChannelType.GroupDm"/> ONLY: block/consent/cap handling that may silently short-circuit
    /// with a fabricated <see cref="ChatResultCode.Ok"/> (<c>FakeSendAck</c>) or the one fail-closed
    /// <see cref="ChatResultCode.Throttled"/> — see <see cref="ApplyPrivateLaneGates"/>.</item>
    /// <item>Mute gate — scoped by <see cref="Channels.ChannelModeration.IsMuteEnforced"/>: PUBLIC
    /// channels and LADDER match rooms (<see cref="ChannelType.System"/> +
    /// <see cref="SystemChannelKind.Match"/> + <see cref="Channels.ChatChannel.Ladder"/>). semiPublic /
    /// dm / groupDm / System+Clan / System+Lobby AND custom-game match rooms are exempt — the legacy
    /// mute scope, widened by exactly the ladder carve-in (a muted player must not be able to talk in a
    /// ladder game's in-game/post-game room; a custom lobby is the host's own room and stays exempt).
    /// Reads <see cref="ConnectionMapping.GetEffectiveMuteStatus"/> (cache-only, zero DB):
    /// <see cref="MuteStatus.Full"/> → <see cref="ChatResultCode.Muted"/>;
    /// <see cref="MuteStatus.Shadow"/> → flag the message and persist (returns Ok — the illusion).</item>
    /// <item>Persist (C1 amendment, mandatory — else the TTL is inert):
    /// <see cref="Channels.ChannelRepository.AllocateSeq"/> atomically $inc LastSeq + $set LastMessageAt,
    /// then insert a <see cref="ChannelMessage"/> carrying the send-time sender snapshot, content,
    /// shadow flag, and the type-derived expiry.</item>
    /// <item>Fan-out seam <see cref="FanOut.FanOutEngine.OnMessagePersisted"/> — delivers the full
    /// <c>MessageReceived</c> payload to the channel's focused viewers, with shadow-author-only routing
    /// (Task 12; per-recipient sends are fault-isolated so one failed push cannot affect this ack).</item>
    /// <item>Return <see cref="ChatResultCode.Ok"/> with the inserted message id + allocated seq.</item>
    /// </list>
    /// <para>
    /// Sender-flair source: the snapshot is built from the flair-bearing <see cref="ChatUser"/> the
    /// connect path already resolved (via <c>IChatAuthenticationService.GetUserFromIdentity</c>) and
    /// cached per connection in <see cref="ConnectionMapping"/> (Task 7's
    /// <c>SessionStateAssembler.SeedLegacyMuteCache</c> → <c>RegisterUser</c>). The send path reads it
    /// with <see cref="ConnectionMapping.GetUser"/> — NO per-message wb round-trip. GetUser is
    /// guaranteed non-null for any connection that completed connect (the same assembler call seeds
    /// both it and the membership registry consulted in step 3); the identity fallback below only
    /// guards a should-never-happen inconsistency so the send path can never NRE.
    /// </para>
    /// </summary>
    public async Task<SendMessageResult> SendMessage(string channelId, string content)
    {
        // 1. Fail-closed: no live session → no identity to send under.
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new SendMessageResult(ChatResultCode.PermissionDenied);
        }

        var connectionId = Context.ConnectionId;

        // 2. Trim + length. Empty-after-trim maps to TooLong by pinned plan decision.
        content = content?.Trim();
        if (string.IsNullOrEmpty(content) || content.Length > ChatLimits.MaxMessageLength)
        {
            return new SendMessageResult(ChatResultCode.TooLong);
        }

        // 3. Membership (hot path, zero DB, O(1) reverse-index lookup — no roster copy under the lock).
        if (!_onlineMemberRegistry.IsMember(connectionId, channelId))
        {
            return new SendMessageResult(ChatResultCode.NotMember);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // 4. Rate limit BEFORE the channel load (security-review fix): reject a throttled member before
        // paying for a DB read. JustAutoThrottled fires once per escalation episode (the single decision
        // that transitions the user into hard auto-throttle) → push exactly one ThrottleNotice.
        // TryAcquire is keyed by battleTag (follow-up spec §1: violations/tier/hard-throttle survive
        // reconnect), so it needs the durable identity, not the ephemeral connectionId.
        var decision = _messageRateLimiter.TryAcquire(session.Identity.BattleTag, channelId, now);
        if (decision.JustAutoThrottled)
        {
            // Moderation-attribution log: the limiter's own line (MessageRateLimiter.TryAcquire) already
            // identifies the battleTag key now that state is re-keyed by it — but it has no visibility
            // into WHICH connection triggered this particular episode. The hub adds that connectionId
            // here so a moderator can correlate the episode to a specific live socket — mirrors the
            // battleTag-attribution style of ChatHubPermissionFilter/BanUser's moderation logs. The
            // limiter's own line stays as-is (MessageRateLimiterTests asserts on it), so this auto-throttle
            // episode is intentionally logged twice — once from the pure limiter (battleTag only) and once
            // from the hub (battleTag + the triggering connectionId).
            Log.Warning(
                "Auto-throttled chat connection {ConnectionId} (battleTag {BattleTag}) after repeated rate-limit violations",
                connectionId,
                session.Identity.BattleTag);
            await Clients.Caller.SendAsync(ChatEvents.ThrottleNotice, new { retryAfterSeconds = decision.RetryAfterSeconds });
        }
        if (!decision.Allowed)
        {
            return new SendMessageResult(ChatResultCode.Throttled, decision.RetryAfterSeconds);
        }

        // 5. Load the channel — a member whose channel doc is gone (deleted) → NotFound.
        var channel = await _channelRepository.Load(channelId);
        if (channel == null)
        {
            return new SendMessageResult(ChatResultCode.NotFound);
        }

        // 5.25 Mention markup validation (C6 Task 4, D1/D2 — amended by the "strip & deliver as plain"
        // decision). The ONLY reject here is the mention COUNT cap; a message is NEVER rejected for the
        // access or resolvability of its mentions. MUST run BEFORE the private-lane gates (5.5) and the
        // mute gate (6): the cap is content-intrinsic and deterministic, so a blocked sender and an
        // unblocked sender get IDENTICAL outcomes for the same over-cap content — running it after 5.5
        // would let that step's silent fake-Ok short-circuit mask a TooLong reject and leak block state
        // (the C5 block-invisibility concern). Likewise a shadow/full-muted sender's over-cap markup must
        // still be rejected normally; a rejection does not break either illusion.
        // ExtractTags dedupes case-insensitively (D1) — a target mentioned 6 times counts ONCE toward the
        // cap. Zero cost on the hot path: no `<@tag>` tokens means the cap check is skipped entirely, so
        // the common case (most messages have no mentions) pays nothing.
        // The cap reject maps to TooLong (the pinned 7-member enum has no InvalidContent — same C3
        // precedent as empty-after-trim).
        // mentionTags (all extracted, deduped, post-cap) threads forward to step 5.26 below AND the
        // (Task 5) mention fan-out call site that lands after DM materialization (7.5) and AFTER
        // FanOutEngine.OnMessagePersisted (7.75, fix round 1 F2b — moved earlier so channel delivery never
        // waits on the mention fan-out's relationship reads).
        var mentionTags = MentionMarkup.ExtractTags(content);
        if (mentionTags.Count > ChatLimits.MaxMentionsPerMessage)
        {
            return new SendMessageResult(ChatResultCode.TooLong);
        }

        // 5.26 Server-canonical mention rendering (D2, 2026-08-05 decision — mention-canonicalization
        // brief). Zero cost when mentionTags is empty (the common case). For each distinct target, decide
        // whether it RENDERS (stays a `<@tag>` chip) or is DOWNGRADED to plain text
        // (`@tag`, no angle brackets — byte-identical to the launcher's pre-existing client-side downgrade
        // text), then rewrite `content` accordingly BEFORE persist — so every future reader of this
        // message (live delivery, history paging, a non-member's Public read, an old un-upgraded client)
        // sees the SAME canonical text; the launcher's own client-side roster-guessing gate is now
        // redundant and has been removed.
        // RENDERABILITY PREDICATE (exact — deliberately mirrors, but is independent of, MentionFanOut's
        // notification eligibility below): the target has a channel_memberships row for THIS channel, OR
        // the channel is Public AND the target is user-directory-resolvable. Mongo point reads only — the
        // channel doc is already loaded (step 5), so this is ONE batched, indexed $in membership read
        // covering every distinct target (fix round 1, finding F3 — MembershipRepository.LoadMemberBattleTags,
        // replacing what was up to MaxMentionsPerMessage (5) sequential membership Loads) plus (Public,
        // non-member only) up to one sequential directory read per still-unresolved target, bounded to
        // MaxMentionsPerMessage (5); the relationship provider is NEVER consulted here. The predicate deliberately IGNORES blocks,
        // NotificationLevel, notification preferences, and DeclinedUntil — those suppress the PING
        // (MentionFanOut, step 8), never the RENDER: a member who has blocked the sender must still SEE a
        // normal chip (stripping it would leak the block state to the sender — an explicit Marco pin), and
        // an opted-out member still sees themself mentioned normally.
        // This runs BEFORE the private-lane gates (5.5) and the mute gate (6) so a shadow/full-muted
        // sender's content is canonicalized exactly like anyone else's — content canonicalization is
        // unconditional, not a moderation decision, and the rewritten `content` is what ultimately gets
        // persisted and fanned out regardless of which path the send takes afterward.
        if (mentionTags.Count > 0)
        {
            // Fix round 1 (finding F3): ONE batched, indexed $in membership read
            // (MembershipRepository.LoadMemberBattleTags) covering every distinct target, replacing what
            // was up to MaxMentionsPerMessage (5) sequential point-reads. The Public/directory fallback
            // stays sequential and bounded, exactly as before — only ever reached for a target this
            // batched check already found has no membership row.
            var memberTags = await _membershipRepository.LoadMemberBattleTags(channel.Id, mentionTags);
            var renderableByTag = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in mentionTags)
            {
                var isRenderable = memberTags.Contains(tag);
                if (!isRenderable && channel.Type == ChannelType.Public)
                {
                    isRenderable = await _userDirectory.Load(tag) != null;
                }
                renderableByTag[tag] = isRenderable;
            }
            content = MentionMarkup.RewriteUnrenderable(
                content, tag => renderableByTag.TryGetValue(tag, out var renderable) && renderable);
        }

        // 5.5 Private-lane gates (C5 Task 4) — Dm/GroupDm ONLY. Runs BEFORE persist so a silent-drop or
        // fail-closed reject writes and delivers nothing. Returns a short-circuit result (a silent
        // FakeSendAck for a blocked/pending-cap/dmPrivacy drop, or the one fail-closed Throttled when the
        // relationship snapshot is entirely unavailable) or null to proceed. It may FLIP channel.RequestState
        // in-memory (reply/auto-accept) so the persist step below computes the +1y accepted-shell expiry.
        if (channel.Type is ChannelType.Dm or ChannelType.GroupDm)
        {
            var shortCircuit = await ApplyPrivateLaneGates(channel, session.Identity.BattleTag, now);
            if (shortCircuit != null)
            {
                return shortCircuit;
            }
        }

        // 6. Mute gate — the ChannelModeration.IsMuteEnforced scope: Public channels and LADDER match
        // rooms (System+Match with Ladder=true). semiPublic / dm / groupDm / System+Clan / System+Lobby
        // and CUSTOM-GAME match rooms stay exempt (the legacy mute scope, minus the ladder carve-in).
        var isShadow = false;
        if (ChannelModeration.IsMuteEnforced(channel))
        {
            var muteStatus = _connections.GetEffectiveMuteStatus(connectionId, now);
            if (muteStatus == MuteStatus.Full)
            {
                return new SendMessageResult(ChatResultCode.Muted);
            }
            // Shadow: persist flagged and return Ok (the illusion) — do NOT reject.
            isShadow = muteStatus == MuteStatus.Shadow;
        }

        // 7. Persist (C1 amendment): allocate the per-channel seq (atomic $inc LastSeq + $set
        // LastMessageAt on the channel doc), then insert the durable message.
        // TOCTOU guard: the channel existed at step 5, but a TTL-backed shell (System/Dm/GroupDm) could
        // be reaped in the gap before AllocateSeq. AllocateSeq then throws (its $inc matched no doc, so
        // NO seq/LastMessageAt was burned) — map that vanished-channel race to the SAME typed NotFound
        // as step 5 rather than letting an untyped exception escape as a generic SignalR error (the
        // pipeline's "every rejection is a typed result" guardrail). A genuine Insert failure below is a
        // real infrastructure error and is deliberately left to propagate.
        // C5 D10 (shell-expiry maintenance, the C1-amendment gap): for Dm/GroupDm the SAME atomic
        // findOneAndUpdate also $sets ExpiresAt to the shell TTL (pending Dm +30d / accepted Dm+GroupDm +1y,
        // computed from the POST-5.5 RequestState). public/semiPublic/System pass null → ExpiresAt is left
        // completely untouched (creation-anchored or permanent — the PublicSend regression pin).
        var shellExpiresAt = channel.Type is ChannelType.Dm or ChannelType.GroupDm
            ? ExpiryCalculator.ForChannelShell(channel, now)
            : (DateTime?)null;
        long seq;
        try
        {
            // WHY a separate atomic $inc on the CHANNEL doc instead of folding seq allocation into the
            // message insert below: MongoDB has no auto-increment, and the seq counter lives on the
            // channel document while each message is its own row in a separate collection — combining
            // "allocate the next seq" and "insert this message" into one atomic unit would need a
            // multi-document transaction. The only gap this leaves is a crash between AllocateSeq
            // succeeding and the Insert below running, which just burns a seq number (a permanent gap in
            // the channel's seq sequence). That's benign: paging (GetMessages) is seq-anchored, not
            // count-anchored, and unread is $max-guarded (MarkRead/UpdateLastReadSeq), so a skipped seq
            // never surfaces as a missing message or a stuck unread count.
            seq = await _channelRepository.AllocateSeq(channelId, now, shellExpiresAt);
        }
        catch (InvalidOperationException)
        {
            return new SendMessageResult(ChatResultCode.NotFound);
        }

        var message = new ChannelMessage
        {
            ChannelId = channelId,
            Seq = seq,
            Sender = BuildSenderSnapshot(connectionId, session),
            Content = content,
            SentAt = now,
            Shadow = isShadow,
            ExpiresAt = ExpiryCalculator.ForChannelMessage(channel.Type, now),
        };
        await _messageRepository.Insert(message);

        // 7.25 Conversation-list projection (ChannelLastMessage): denormalize this message onto the channel
        // doc so SessionState/GetConversations/ChannelAdded can render "who said what, and when" without
        // reading the message collection. Before it, that text only ever existed in a live event, so a
        // client had nothing to render a conversation list from at rest and no way to recover one after a
        // reconnect (w3champions/launcher-e#848).
        //
        // Runs post-insert (the projection describes a durable message, so it must never precede it) and
        // pre-fan-out (a client that reacts to the event by calling GetConversations must not race a
        // not-yet-written projection). Skipped entirely for shadow — a shadow author's text must never
        // reach a non-author, and this projection is channel-global with no per-viewer filtering, so the
        // ONLY safe treatment is to not write one. Scope (accepted Dm + GroupDm) is CarriesLastMessageProjection.
        //
        // The write is a compare-and-set on seq (TryAdvanceLastMessage), so concurrent same-channel sends
        // that reach it out of order settle on the higher seq regardless of arrival order — the same
        // monotonic discipline AllocateSeq gives the counter itself. A false return means a concurrent
        // newer message already won, which is the correct outcome and not an error.
        //
        // BEST-EFFORT, exactly like the fan-out below it: the message is already durable by this point,
        // so a failure here must never propagate and turn a completed send into an error — the caller
        // would get no ack for a message that exists, and a client retry would persist a duplicate.
        // Introducing a second post-persist Mongo write is only acceptable on that condition. The cost
        // of swallowing it is bounded and self-healing: the conversation row renders the previous
        // message until the next one in that channel advances the projection.
        //
        // The ACCEPTED half of the scope is re-decided inside TryAdvanceLastMessage, against the durable
        // channel doc rather than this in-memory snapshot — `channel` was loaded before the send and a
        // concurrent AcceptRequest can commit in between, which would otherwise skip the projection for
        // a conversation that is accepted by the time this runs. Only the shape gate (Dm/GroupDm) and
        // the shadow rule, neither of which any concurrent write can change, are decided here.
        if (!isShadow && CarriesLastMessageProjection(channel))
        {
            try
            {
                await _channelRepository.TryAdvanceLastMessage(channelId, BuildLastMessageProjection(message));
            }
            catch (Exception ex)
            {
                Log.Warning(
                    ex,
                    "Conversation-list projection failed for channel {ChannelId} seq {Seq} — message is persisted and delivery continues; the row stays on the previous message until the next send",
                    channelId,
                    message.Seq);
            }
        }

        // 7.5 Dm recipient materialization + RequestReceived (C5 Task 4, D4) — post-persist, BEFORE fan-out.
        // Lazily creates the counterpart's membership on first delivery (seeding their registry via
        // PushChannelAdded(focus:false)) and fires the targeted RequestReceived consent transition for a
        // fresh/resurfaced pending request. GroupDm needs none of this (all members already have rows).
        if (channel.Type == ChannelType.Dm)
        {
            await MaterializeDmRecipientAndNotify(channel, session.Identity.BattleTag, now);
        }

        // 7.75 Fan-out seam (Task 12 focused delivery + Task 13 activity routing): focused MessageReceived
        // delivery + shadow-author-only routing, then unfocused level-All members are routed to the
        // ActivityCoalescer. `now` is threaded in (not re-read) so the whole send — rate limit, persist,
        // expiry, and fan-out coalescing — decides against the SAME server instant. Per-recipient sends
        // are fault-isolated inside FanOutEngine, so a fan-out hiccup never turns this already-persisted
        // message's ack into an error below.
        // Fix round 1 (F2b): this now runs BEFORE the mention fan-out (step 8, below) — previously it ran
        // after, so a degraded relationship service (mention fan-out's D1 block check, up to
        // MaxMentionsPerMessage wb round-trips) could delay MessageReceived for every OTHER viewer of the
        // channel. OnMessagePersisted consumes nothing the mention fan-out produces (no shared write, no
        // read-after-write dependency), so swapping the order is behavior-preserving for channel delivery;
        // it only changes which of the two a mention target's client observes first.
        // ACCEPTED CEILING (Marco round 2026-08-05, decision I2): the reordering above fixes OTHER
        // viewers only. The SENDER's own ack still awaits step 8's mention fan-out below, whose stage 2
        // resolves every otherwise-eligible target's D1 block snapshot CONCURRENTLY — so a mention-bearing
        // send pays AT MOST ONE wb HTTP timeout (~2s, WebsiteBackendRelationshipSource's client timeout)
        // when wb is degraded, never one per mention. This is a chosen trade, not an unaddressed artifact
        // of the reordering — consistent with the DM-send path's existing wb coupling in
        // ApplyPrivateLaneGates.
        await _fanOutEngine.OnMessagePersisted(channel, message, senderConnectionId: connectionId, isShadow, now);

        // 8. Mention fan-out (C6 Task 5, D3/D4). Runs AFTER the Dm recipient is materialized (7.5) — so a
        // first-message Dm mention of the counterpart finds their just-created membership row — and AFTER
        // the C3 fan-out seam (7.75, fix round 1 F2b — see that step's comment for why). SKIPPED ENTIRELY
        // (zero cost) when the message is shadow — a shadow sender's mentions must notify NOBODY, the
        // shadow illusion (the C3 fan-out above already routes a shadow message to its author only) — or
        // when there are no mention tags (the common case: `mentionTags` came from the step-5.25 gate,
        // empty for a message with no `<@…>` markup, so the hot path pays nothing). MentionFanOut is the
        // SOLE authority on who gets an inbox entry + notification: for each ELIGIBLE target (D3: not the
        // sender). Dm/GroupDm/SemiPublic/System keep the membership PRIVACY WALL — a real
        // channel_memberships row is required (a.k.a. the Dm/GroupDm excerpt wall). Public rooms are the
        // one exception (2026-08-04 follow-up §4): a target with no membership row is still eligible there
        // provided the tag resolves to a UserDirectoryRepository row, since a public room's excerpt is
        // public content and the membership wall protects nothing there. For a JOINED target of any
        // channel type, NotificationLevel != None remains the opt-out.
        // NotifyAsync writes a mention-inbox entry (expiry via ExpiryCalculator.ForMentionInboxEntry — the
        // C1-amendment-1 wiring, 30d and always <= the message TTL) and pushes a targeted MentionNotified.
        // Per-target fault isolation lives inside NotifyAsync (mirrors FanOutEngine's idiom), so a dead
        // target socket or a single failed insert never turns this already-persisted send's Ok ack into an
        // error, nor blocks the other targets. It does NOT shield the SENDER's own ack from latency,
        // though: this call is awaited before step 9 returns, so the ack pays NotifyAsync's stage 2 wb
        // round-trip (block-check snapshots resolved CONCURRENTLY, so at most ONE ~2s wb HTTP timeout when
        // wb is degraded — see the F2b comment above). That is an accepted trade (Marco round 2026-08-05),
        // not an artifact; other viewers' delivery is unaffected because the fan-out engine above already ran.
        if (!isShadow && mentionTags.Count > 0)
        {
            await _mentionFanOut.NotifyAsync(channel, message, mentionTags, now);
        }

        // 9. Typed ack.
        return new SendMessageResult(ChatResultCode.Ok, MessageId: message.Id, Seq: seq);
    }

    /// <summary>
    /// C3 (Task 16): pull-only history paging — the caller receives history ONLY through this method's
    /// own typed <see cref="GetMessagesResult"/>. GetMessages NEVER pushes a SignalR event (hard
    /// guardrail); it is the read-side companion to <see cref="SendMessage(string, string)"/>'s write
    /// pipeline. The resolution order mirrors <see cref="FocusChannel"/>'s NotFound-vs-NotMember split
    /// EXACTLY, and is honored in this order:
    /// <list type="number">
    /// <item>Fail-closed identity: an unregistered connection (never authenticated, or its session was
    /// displaced/torn down) → <see cref="ChatResultCode.PermissionDenied"/> — there is no identity to
    /// page history under.</item>
    /// <item>Read-abuse rate limit (2026-08-05 PR36 feedback, Part 3) — FIRST THING after identity
    /// resolution, strictly before the malformed-arg guard below AND any DB work (fix round 1, finding
    /// F5: this now structurally mirrors <see cref="GetConversations"/>'s "limiter absolutely first"
    /// ordering exactly, rather than the narrower "before the first DB read" ordering this method used
    /// before). ONE per-battleTag token bucket (<see cref="FanOut.ReadRateLimiter"/>) shared with
    /// <see cref="GetConversations"/> — the task report records the launcher-e investigation establishing
    /// that a <see cref="ChatResultCode.Throttled"/> GetMessages reject already degrades gracefully on
    /// both old and current shipped clients (silent no-op, no retry storm, no error modal), which is why
    /// this method is guarded at all. Not allowed → <see cref="ChatResultCode.Throttled"/> with the
    /// retry-after. Limits are generous (burst 60, sustained 5/s — sized for connect fan-out, see
    /// <see cref="ChatLimits.ReadBurst"/>'s doc) — server protection only, not UX pacing (Marco) — so no
    /// legitimate client should ever observe this in normal operation.</item>
    /// <item>Malformed-arg guard, BEFORE any DB work: <paramref name="beforeSeq"/> and
    /// <paramref name="aroundSeq"/> are mutually exclusive paging modes. A caller supplying BOTH is a
    /// client programming error, not a user-facing rejection, so it throws <see cref="HubException"/>
    /// (decision 5's client-error mapping — the same graceful-throw style as
    /// <see cref="Authentication.ChatHubPermissionFilter"/>) rather than a typed result code.</item>
    /// <item>Membership: the hot path reads <see cref="FanOut.OnlineMemberRegistry.IsMember"/> (zero
    /// DB) and, if the caller is a member, pages directly — no channel load. A non-member falls to the
    /// cold path: a single <see cref="Channels.ChannelRepository.Load"/> distinguishes "no such
    /// channel" (<see cref="ChatResultCode.NotFound"/>) from "channel exists, caller just isn't in it"
    /// (<see cref="ChatResultCode.NotMember"/>) — EXCEPT for a <see cref="ChannelType.Public"/> channel
    /// (follow-up spec §4, the mention-inbox jump into an unjoined public room), where a non-member falls
    /// through to the same paging step 5 uses for a member instead of being rejected. That exception
    /// itself excludes a full-banned non-member (<see cref="ConnectionMapping.GetEffectiveMuteStatus"/>
    /// == <see cref="MuteStatus.Full"/>, mirroring <see cref="JoinChannel"/>'s gate), which still gets
    /// <see cref="ChatResultCode.NotMember"/> — the same result a non-member got before this fallthrough
    /// existed, so the ban is not disclosed.</item>
    /// <item>Page: <paramref name="aroundSeq"/> set → <see cref="Messages.MessageRepository.LoadPageAround"/>;
    /// otherwise → <see cref="Messages.MessageRepository.LoadPageBefore"/> (a null
    /// <paramref name="beforeSeq"/> means the latest page). Both repo methods already apply
    /// <see cref="Messages.MessageRepository.UserVisible"/> (soft-deleted excluded; other authors'
    /// shadow messages excluded; the viewer's OWN shadow messages included) and clamp
    /// <paramref name="limit"/> to <see cref="ChatLimits.MessagePageSize"/> — this method passes
    /// <paramref name="limit"/> straight through and does NOT re-filter or re-clamp.</item>
    /// <item>Map each result to <see cref="MessageDto.ForUserDelivery"/> — the SAME forced-false
    /// deleted/shadow projection <see cref="FanOut.FanOutEngine"/> uses on the push path, so the
    /// shadow illusion (C3-plan.md decision 7) can never drift between the read path and the push
    /// path — and return <see cref="ChatResultCode.Ok"/>.</item>
    /// </list>
    /// </summary>
    public async Task<GetMessagesResult> GetMessages(string channelId, long? beforeSeq, long? aroundSeq, int limit)
    {
        // 1. Fail-closed: no live session → no identity to page history under.
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new GetMessagesResult(ChatResultCode.PermissionDenied);
        }
        var battleTag = session.Identity.BattleTag;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // 2. Read-abuse rate limit (2026-08-05 PR36 feedback, Part 3) — FIRST THING, before the
        // malformed-arg guard below and before any DB work (fix round 1, finding F5: structurally
        // mirrors GetConversations' "limiter absolutely first" ordering exactly). Shares ONE
        // per-battleTag bucket with GetConversations — see FanOut/ReadRateLimiter.cs and the task
        // report's scope determination.
        var readDecision = _readRateLimiter.TryAcquire(battleTag, now);
        if (!readDecision.Allowed)
        {
            return new GetMessagesResult(ChatResultCode.Throttled, readDecision.RetryAfterSeconds);
        }

        // 3. Malformed-arg guard, before any DB work: beforeSeq/aroundSeq are mutually exclusive
        // paging modes. Supplying both is a client bug — a graceful HubException, not a typed result.
        if (beforeSeq.HasValue && aroundSeq.HasValue)
        {
            throw new HubException("GetMessages: beforeSeq and aroundSeq are mutually exclusive — supply at most one.");
        }

        var connectionId = Context.ConnectionId;

        // 4. Membership (hot path, zero DB). A non-member falls to the cold path: a single Load
        // distinguishes NotFound from NotMember — EXCEPT for PUBLIC channels (follow-up spec §4's
        // mention-inbox jump): a public room is name-joinable by anyone, so its history is not
        // privileged and a non-member may READ it. Pull-only and UserVisible-filtered exactly like a
        // member's read; joining stays explicit, and FocusChannel/MarkRead keep their membership
        // gates — this is a read-only context allowance, never implicit membership.
        // A full-banned caller is excluded from that allowance: the codebase's full-ban room-scope rule
        // already blocks a full-banned user from JOINING a Public room (ChatHub.Channels.cs's JoinChannel
        // full-ban gate) and hides the catalog entirely (SessionStateAssembler's full-ban catalog-hiding
        // rule) — letting the SAME user read Public history for free via this fallthrough would bypass
        // that rule. Mirrors JoinChannel's gate style exactly (GetEffectiveMuteStatus == Full), but
        // returns the SAME NotMember a non-member got before this task's fallthrough existed, so the ban
        // is indistinguishable from ordinary non-membership — no new information disclosure. Shadow does
        // NOT block reads (only Full does) — a shadow-muted caller keeps the illusion. This check is
        // scoped strictly to the non-member Public fallthrough: a MEMBER's read (including a full-banned
        // member's) is completely unaffected — this is a read-only gate, not a general mute check.
        if (!_onlineMemberRegistry.IsMember(connectionId, channelId))
        {
            var channel = await _channelRepository.Load(channelId);
            if (channel == null)
            {
                return new GetMessagesResult(ChatResultCode.NotFound);
            }
            if (channel.Type != ChannelType.Public)
            {
                return new GetMessagesResult(ChatResultCode.NotMember);
            }
            if (_connections.GetEffectiveMuteStatus(connectionId, now) == MuteStatus.Full)
            {
                return new GetMessagesResult(ChatResultCode.NotMember);
            }
            // Public, not full-banned: fall through to step 5's normal paging below.
        }

        // 5. Page + project. A MODERATOR (C4 D9) reads through the moderator repo variants — no
        // UserVisible filter, so deleted rows and EVERY author's shadow rows come back — and projects
        // with the REAL flags (ForModerator). This is the moderator's own in-channel focused view, so
        // their OWN shadow/deleted rows are flagged too, not illusion-forced (they are a moderator). The
        // membership gate above is UNCHANGED for non-Public channels — a non-member is still rejected
        // regardless of permission there. On a Public channel, though, a NON-MEMBER moderator DOES reach
        // this branch (step 4's fallthrough, full-ban excluded) and gets the SAME privileged ForModerator
        // projection a member-moderator would — a deliberate, narrow consequence of §4's non-member read
        // allowance for Public channels, chosen rather than accidental (pinned by
        // GetMessages_NonMemberModerator_PublicChannel_ReturnsForModeratorProjection). It stays bounded to
        // ONE Public channel per call, keyed by the caller-supplied channelId; the privileged read across
        // EVERY channel regardless of type/membership is still the REST endpoint (Task 7) — this method
        // does not replace or widen that.
        //
        // This branch does NOT re-apply the {Public, SemiPublic, System+Match} scope wall that
        // single-delete/purge/the REST endpoint enforce — it is safe only emergently, by construction
        // of the write paths: a shadow==true row can exist ONLY where the send-path mute gate runs, i.e.
        // ChannelModeration.IsMuteEnforced — Public or a LADDER System+Match room — and a deleted!=null
        // row can exist ONLY in a moderatable channel (both delete paths are themselves scope-walled).
        // BOTH of those sets are SUBSETS of IsModeratable, which is what makes this safe: in any
        // DM/GroupDm/System+Clan/System+Lobby channel a moderator happens to be a member of,
        // ForModerator is byte-identical to ForUserDelivery — no private content or flag leaks. (The
        // ladder carve-in did not widen the exposure: System+Match was already inside IsModeratable, so
        // a ladder room's shadow rows are legitimately moderator-visible.) This safety depends on those
        // write-time invariants holding in the send path and delete paths; it is not re-verified here.
        if (session.HasPermission(EPermission.Moderation))
        {
            var moderatorPage = aroundSeq.HasValue
                ? await _messageRepository.LoadPageAroundForModerator(channelId, aroundSeq.Value, limit)
                : await _messageRepository.LoadPageBeforeForModerator(channelId, beforeSeq, limit);
            var moderatorMessages = moderatorPage.Select(m => MessageDto.ForModerator(channelId, m)).ToList();
            return new GetMessagesResult(ChatResultCode.Ok, Messages: moderatorMessages);
        }

        // Non-moderator: the repo applies UserVisible filtering and clamps the limit — passed straight
        // through, no re-filtering/re-clamping here — then map to the SAME forced-false illusion
        // FanOutEngine uses for push.
        var page = aroundSeq.HasValue
            ? await _messageRepository.LoadPageAround(channelId, battleTag, aroundSeq.Value, limit)
            : await _messageRepository.LoadPageBefore(channelId, battleTag, beforeSeq, limit);
        var messages = page.Select(m => MessageDto.ForUserDelivery(channelId, m)).ToList();
        return new GetMessagesResult(ChatResultCode.Ok, Messages: messages);
    }

    /// <summary>
    /// C3 (Task 17): advances the caller's per-channel read cursor in BOTH the durable Mongo
    /// membership row (drives unread on reconnect/SessionState —
    /// <see cref="Memberships.MembershipRepository.UpdateLastReadSeq"/>) and the in-memory
    /// <see cref="FanOut.OnlineMemberRegistry"/> (drives the live <see cref="FanOut.ActivityCoalescer"/>
    /// unread-suppression re-check at emit time). The resolution order is honored EXACTLY:
    /// <list type="number">
    /// <item>Fail-closed identity: an unregistered connection (never authenticated, or its session was
    /// displaced/torn down) → <see cref="ChatResultCode.PermissionDenied"/> — there is no identity to
    /// mark a channel read under.</item>
    /// <item>Membership via <see cref="FanOut.OnlineMemberRegistry.IsMember"/> (hot path, zero DB, O(1)
    /// reverse-index lookup — mirrors <see cref="SendMessage(string, string)"/>'s step 3) →
    /// <see cref="ChatResultCode.NotMember"/> if the caller isn't a member.</item>
    /// <item>Channel load (needed for the clamp ceiling below); missing → <see cref="ChatResultCode.NotFound"/>
    /// — the member-of-a-deleted-channel edge. Defensive/beyond this task's named tests, but the SAME
    /// guard <see cref="SendMessage(string, string)"/> applies on its own is-member-true branch (its
    /// step 5). <see cref="GetMessages"/> and <see cref="FocusChannel"/> do NOT share this edge — both
    /// only load the channel on their non-member cold path, so a member of an orphaned channel never
    /// reaches a channel load there.</item>
    /// <item>Clamp: <c>Math.Min(seq, channel.LastSeq)</c> — a client must never mark a channel read
    /// past its actual last message, or unread would go negative and mask future messages.</item>
    /// <item>Advance BOTH stores with the CLAMPED value (dual-store monotonic invariant below), then
    /// return <see cref="ChatResultCode.Ok"/>.</item>
    /// </list>
    /// <para>
    /// DUAL-STORE MONOTONIC INVARIANT: <see cref="Memberships.MembershipRepository.UpdateLastReadSeq"/>
    /// is already a Mongo <c>$max</c> — a lower/stale seq is silently a DB no-op. The registry's plain
    /// <see cref="FanOut.OnlineMemberRegistry.SetLastReadSeq"/>, by contrast, is an unconditional
    /// overwrite (kept exactly as-is — its own contract, its own <c>FanOutRegistryTests</c> coverage).
    /// Calling THAT here would let a stale/out-of-order MarkRead regress the in-memory registry BELOW
    /// the durable DB cursor even though the DB itself never moved — the two stores would diverge, and
    /// <see cref="FanOut.ActivityCoalescer"/>'s emit-time unread recompute (which reads ONLY the
    /// registry) would over-count unread and wrongly re-suppress an already-caught-up member. So this
    /// method calls the registry's dedicated monotonic sibling,
    /// <see cref="FanOut.OnlineMemberRegistry.AdvanceLastReadSeq"/> — same no-op-if-absent contract,
    /// <c>Math.Max</c> instead of overwrite — keeping BOTH stores monotonic together.
    /// </para>
    /// <para>
    /// NO SERVER-SIDE THROTTLE (deliberate): spec §13's 5s <see cref="ChatLimits.MarkReadThrottle"/> is
    /// the pinned CLIENT coalescing contract (spec §7, "Client coalesces…") — the CLIENT debounces its
    /// own MarkRead calls; the server does not hard-enforce it here. A hard server-side reject would
    /// break the client's legitimate final-flush-on-unfocus (a MarkRead that must go through even if it
    /// lands &lt;5s after the previous one), and blanket per-connection method abuse is already the
    /// SignalR-level rate limiter's job, not this method's.
    /// </para>
    /// </summary>
    public async Task<ChannelOperationResult> MarkRead(string channelId, long seq)
    {
        // 1. Fail-closed: no live session → no identity to mark a channel read under.
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        var connectionId = Context.ConnectionId;

        // 2. Membership (hot path, zero DB, O(1) reverse-index lookup).
        if (!_onlineMemberRegistry.IsMember(connectionId, channelId))
        {
            return new ChannelOperationResult(ChatResultCode.NotMember);
        }

        // 3. Load the channel — needed for the clamp ceiling. A member whose channel doc is gone
        // (deleted) → NotFound, mirroring SendMessage's same is-member-true-branch guard (GetMessages
        // and FocusChannel don't share this edge — they only load the channel on the non-member path).
        var channel = await _channelRepository.Load(channelId);
        if (channel == null)
        {
            return new ChannelOperationResult(ChatResultCode.NotFound);
        }

        // 4. Clamp: never mark read past the channel's actual last message.
        var clamped = Math.Min(seq, channel.LastSeq);
        var battleTag = session.Identity.BattleTag;

        // 5. Advance BOTH stores with the SAME clamped value — see the dual-store monotonic invariant
        // in the doc comment above for why AdvanceLastReadSeq (not SetLastReadSeq) is used here.
        await _membershipRepository.UpdateLastReadSeq(channelId, battleTag, clamped);
        _onlineMemberRegistry.AdvanceLastReadSeq(channelId, connectionId, clamped);

        // 6. Typed ack.
        return new ChannelOperationResult(ChatResultCode.Ok);
    }

    /// <summary>
    /// The send-time sender snapshot { battleTag, name, flair } from the connect-time cached
    /// <see cref="ChatUser"/> (no wb round-trip). Falls back to the session identity (no flair) only if
    /// the cached ChatUser is unexpectedly absent — a should-never-happen inconsistency the connect
    /// invariant rules out, guarded here purely so the send path cannot NRE.
    /// </summary>
    private MessageSender BuildSenderSnapshot(string connectionId, Sessions.ChatSession session)
    {
        var chatUser = _connections.GetUser(connectionId);
        if (chatUser == null)
        {
            return new MessageSender { BattleTag = session.Identity.BattleTag, Name = session.Identity.Name };
        }

        return new MessageSender
        {
            BattleTag = chatUser.BattleTag,
            Name = chatUser.Name,
            // D9: delegates to the single shared ChatUser→ChatProfile mapper (Domain/ChatProfileMapper.cs) —
            // the SAME mapper SessionStateAssembler.ToChatProfile uses — so the two can never drift.
            Flair = ChatProfileMapper.FromChatUser(chatUser),
        };
    }

    /// <summary>
    /// Which channels carry the <see cref="ChannelLastMessage"/> conversation-list projection: the two
    /// conversation shapes, nothing else. See <see cref="ChannelLastMessage"/> for why the set is this
    /// narrow — the public/system exclusion keeps room content out of the <c>PublicCatalog</c> shells that
    /// ship to NON-members.
    /// <para>
    /// This is the TYPE half of the scope only. The consent wall — a pending Dm is not projected, so a
    /// recipient never sees a stranger's text before accepting, the same rule that suppresses their
    /// <c>ChannelActivity</c> — is enforced inside
    /// <see cref="ChannelRepository.TryAdvanceLastMessage"/>'s own filter instead, because it is the half
    /// a concurrent write can change between the send loading this channel and the projection landing.
    /// A channel's TYPE is immutable, so deciding that here is safe by construction.
    /// </para>
    /// <para>
    /// A Dm accepted AFTER messages already exist is not backfilled: the projection appears with the
    /// conversation's next message. Backfilling would mean reading the message collection on the accept
    /// path to publish text the recipient has, until that moment, deliberately not been shown.
    /// </para>
    /// </summary>
    internal static bool CarriesLastMessageProjection(ChatChannel channel) =>
        channel.Type is ChannelType.Dm or ChannelType.GroupDm;

    /// <summary>
    /// The single message→projection mapping. <see cref="Excerpts.Bounded"/> is the SAME helper that builds
    /// <see cref="DmActivityPreviewDto.Excerpt"/>, so the excerpt a client renders from the live event and
    /// the one it renders from the snapshot can never disagree about the same message.
    /// </summary>
    private static ChannelLastMessage BuildLastMessageProjection(ChannelMessage message) => new()
    {
        Seq = message.Seq,
        SenderBattleTag = message.Sender.BattleTag,
        SenderName = message.Sender.Name,
        Excerpt = Excerpts.Bounded(message.Content),
        SentAt = message.SentAt,
    };
}

using System;
using System.Collections.Generic;

namespace W3ChampionsChatService.Domain;

/// <summary>
/// Spec §13 limits — VERBATIM and hard-coded by explicit product decision ("We don't need
/// env-configurable. Just hard-coded consts."). All constants live here from C1 even where
/// enforcement lands in later items (C2 ticket TTL, C3 rate buckets/focus, C5 DM caps, C6 mentions).
/// </summary>
public static class ChatLimits
{
    /// <summary>Message length cap (chars).</summary>
    public const int MaxMessageLength = 512;

    /// <summary>Messages per channel: burst 5, then 1/sec.</summary>
    public const int PerChannelBurst = 5;
    public static readonly TimeSpan PerChannelSustainedInterval = TimeSpan.FromSeconds(1);

    /// <summary>Messages global per user: 10 per 5s.</summary>
    public const int GlobalMessageBurst = 10;
    public static readonly TimeSpan GlobalMessageWindow = TimeSpan.FromSeconds(5);

    /// <summary>Stranger-DM initiations: reject when ≥10 non-accepted initiations in past 8h.</summary>
    public const int StrangerDmInitiationCap = 10;
    public static readonly TimeSpan StrangerDmInitiationWindow = TimeSpan.FromHours(8);

    /// <summary>Pending conversation depth: 25 stored messages until accepted.</summary>
    public const int PendingConversationMaxMessages = 25;

    /// <summary>Mentions per message.</summary>
    public const int MaxMentionsPerMessage = 5;

    /// <summary>Group size.</summary>
    public const int MaxGroupSize = 100;

    /// <summary>Group/semi-public creation per user per hour.</summary>
    public const int ChannelCreationPerHour = 5;
    public static readonly TimeSpan ChannelCreationWindow = TimeSpan.FromHours(1);

    /// <summary>Focused set size.</summary>
    public const int MaxFocusedChannels = 10;

    /// <summary>Public + semiPublic memberships per user.</summary>
    public const int MaxPublicMembershipsPerUser = 50;

    /// <summary>Connections per battleTag.</summary>
    public const int MaxConnectionsPerBattleTag = 1;

    /// <summary>MarkRead throttle per channel (client-driven).</summary>
    public static readonly TimeSpan MarkReadThrottle = TimeSpan.FromSeconds(5);

    /// <summary>ChannelActivity coalescing per channel/connection; suppressed at unread >100.</summary>
    public static readonly TimeSpan ChannelActivityCoalesce = TimeSpan.FromSeconds(10);
    public const int ChannelActivitySuppressUnreadThreshold = 100;

    /// <summary>ViewersChanged flush interval per channel.</summary>
    public static readonly TimeSpan ViewersChangedFlush = TimeSpan.FromSeconds(5);

    /// <summary>Auth ticket TTL (one-time).</summary>
    public static readonly TimeSpan TicketTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether <c>POST /auth/session</c> enforces the JWT's <c>exp</c> when minting a connect ticket.
    /// Toggle in the identification-service <c>AuthorizationController.ENFORCE_*</c> spirit — one const,
    /// flip it here, no env plumbing (the ChatLimits philosophy).
    ///
    /// <para>Currently <c>false</c>. identification-service issues JWTs with a 7-day lifetime
    /// (<c>W3CUserAuthentication.Create</c>: <c>expires: DateTime.UtcNow.AddDays(7)</c>) and exposes NO
    /// refresh/renew endpoint — the only way to obtain a new token is a full Blizzard OAuth re-login.
    /// Meanwhile website-backend deliberately does NOT validate lifetime on its player-facing surfaces,
    /// notably its SignalR connect (<c>WebsiteBackendHub</c>, <c>GetUserByToken(accessToken, false)</c>).
    /// With this ON, chat was the strict outlier: a user past day 7 still browsed the website and still
    /// looked logged in, but could not connect to chat — surfacing as an unexplained auth error.
    /// OFF makes our connect handshake match the equivalent website-backend surface.</para>
    ///
    /// <para>SCOPE — this governs the TICKET-MINT path ONLY. Two things are deliberately unaffected:
    /// signature verification (an unverifiable token is ALWAYS rejected — the invariant that keeps this
    /// toggle safe), and the moderation REST surface
    /// (<see cref="Authentication.UserHasPermissionFilter"/>), which keeps enforcing <c>exp</c> and
    /// keeps returning <c>AUTH_TOKEN_EXPIRED</c>. That split mirrors website-backend exactly: lax at
    /// hub connect, strict on the permission filter.</para>
    ///
    /// <para>KNOWN COST: <see cref="Sessions.ChatSession.HasPermission"/> gates moderation HUB methods
    /// on the claims carried by the consumed ticket, so while this is off, an expired-but-validly-signed
    /// admin token retains its in-hub moderation powers until the holder's permissions change upstream.
    /// website-backend's hub already carries this same posture. The durable fix is a refresh endpoint in
    /// identification-service; flip this back to <c>true</c> once one exists.</para>
    /// </summary>
    public const bool EnforceJwtLifetimeOnTicketMint = false;

    /// <summary>Ticket mint rate limit (C2): fixed window (<see cref="TicketMintWindow"/>). Values are a
    /// C2 plan decision (not spec §13). <see cref="TicketMintPerBattleTagLimit"/> = 10/min caps
    /// SUCCESSFUL-or-not mint attempts per validated battleTag, tolerating reconnect flapping while
    /// bounding per-user abuse.
    ///
    /// <para><see cref="TicketMintPerIpLimit"/> = 30/window is a pre-validation DoS shield that, after the
    /// F1 reconnect-storm rework, caps ONLY REJECTED mint attempts per source IP — auth failures
    /// (bad/expired token) and per-battleTag-throttled attempts. A SUCCESSFUL mint (valid, non-expired
    /// JWT under the per-battleTag cap) does NOT charge this budget, so a legitimate mass reconnect of
    /// thousands of DISTINCT valid battleTags behind one shared/NAT'd proxy IP is never IP-throttled
    /// (each is still bounded by the per-battleTag cap). It stays a hard-coded const by the ChatLimits
    /// philosophy; the forwarded-headers TRUST boundary (Startup) is likewise hardcoded to the sibling
    /// services' convention, so the shield keys on the real client IP.</para></summary>
    public const int TicketMintPerBattleTagLimit = 10;
    public const int TicketMintPerIpLimit = 30;
    public static readonly TimeSpan TicketMintWindow = TimeSpan.FromMinutes(1);

    /// <summary>GetMessages page size cap (C3 plan decision, Task 1 — not spec §13; requested
    /// limits above this are clamped down, never rejected).</summary>
    public const int MessagePageSize = 100;

    /// <summary>GET /api/moderation/channels page size cap (C4 Task 7, D9 — not spec §13; requested
    /// limits above this are clamped down, never rejected, default 100). Distinct from
    /// <see cref="MessagePageSize"/>: this pages CHANNELS (coarse-grained, one row per room/match),
    /// not messages, so a larger ceiling is appropriate.</summary>
    public const int ModerationChannelsPageSize = 500;

    /// <summary>Auto-throttle escalation trigger (C3 plan decision, Task 1; trigger UNCHANGED by the
    /// 2026-08-04 follow-up spec §1): repeated rate-limit violations within the window escalate to a
    /// hard per-user throttle, plus a moderation log entry.</summary>
    public const int AutoThrottleViolationThreshold = 5;
    public static readonly TimeSpan AutoThrottleWindow = TimeSpan.FromSeconds(60);

    /// <summary>Escalating auto-throttle tiers (2026-08-04 follow-up spec §1, hard-coded — no env
    /// plumbing per the parent-spec rule): first trigger 10s, second 30s, third and beyond 60s (cap
    /// — the last element applies to every later trigger). The ladder resets to the first tier after
    /// <see cref="AutoThrottleTierDecay"/> without a new trigger.
    /// <para>INVARIANT (pinned by <c>ChatLimitsTests</c>): every element here must stay strictly less
    /// than <see cref="AutoThrottleTierDecay"/> — <see cref="FanOut.MessageRateLimiter"/>'s quiescent
    /// prune treats the decay horizon as "definitely idle, safe to evict", which only holds if no tier's
    /// hard-throttle penalty can still be running that long after a user's last touch.</para></summary>
    public static readonly IReadOnlyList<TimeSpan> AutoThrottleTierDurations =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];
    public static readonly TimeSpan AutoThrottleTierDecay = TimeSpan.FromMinutes(10);

    /// <summary>Relationship (friends/blocked) snapshot cache TTL (C5 plan decision, T1 — not spec §13).
    /// The provider serves a cached snapshot without refetching for this long; spec §6 notes the
    /// relationship view "self-heals in minutes", so a stale snapshot after a wb-side change corrects
    /// within one TTL even if a change-ping (C7) is missed. Chosen at 5 minutes: long enough to keep the
    /// per-connect warm fetch and the per-decision reads off the wire, short enough that block/friend
    /// changes take effect quickly.</summary>
    public static readonly TimeSpan RelationshipCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>Retriable back-off (seconds) surfaced when a relationship-gated action fails closed
    /// because no usable snapshot is available (C5 plan decision, T1 — not spec §13). Carried on the
    /// typed <c>Throttled</c> reject so the client retries after the provider has had a chance to
    /// re-fetch (≈ one wb round-trip plus jitter), rather than being silently dropped.</summary>
    public const int RelationshipRetryAfterSeconds = 30;

    /// <summary>Spec-pinned (§4): a decline is soft + temporal — it leaves the recipient's tray and
    /// suppresses notifications for 24h, after which the sender's next message surfaces a fresh
    /// request. NOT a spec-numbers-config knob; hard-coded like every other spec §13 value.</summary>
    public static readonly TimeSpan DmDeclineSuppression = TimeSpan.FromHours(24);

    /// <summary>Group size floor. Spec-pinned (§4: "3–100 members") — <see cref="MaxGroupSize"/> is
    /// the existing ceiling half of that same range; this const exists so the floor has a name too.</summary>
    public const int GroupMinSize = 3;

    /// <summary>Group display-name length cap (chars). C5 plan decision (T2) — not spec §13 text;
    /// hard-coded, adjust here only.</summary>
    public const int GroupNameMaxLength = 64;

    /// <summary>DM activity-preview excerpt length (chars) — C5 plan decision (T2/D15), reusing the
    /// mention-inbox excerpt precedent's "~120 chars" (spec §5) rather than inventing a new number.
    /// Not itself spec §13 text; hard-coded, adjust here only.</summary>
    public const int DmPreviewExcerptLength = 120;

    /// <summary>Mention-candidate directory activity gate (C6 plan decision, D14) — spec-pinned
    /// (§7: "lastSeenAt ≥ now−90d"). Applies to Tier 3 (directory) search results only; Tiers 1-2
    /// (active viewers/online users) are online now, trivially within the window.</summary>
    public static readonly TimeSpan MentionCandidateActivityWindow = TimeSpan.FromDays(90);

    /// <summary>SearchMentionCandidates total result cap across all three tiers (C6 plan decision,
    /// D14 — not spec §13; hard-coded, adjust here only).</summary>
    public const int MentionSearchMaxResults = 20;

    /// <summary>MarkMentionsRead batch-id array size cap (C6 plan decision, D14 — not spec §13;
    /// requests above this are rejected with <c>HubException</c>, never silently clamped).</summary>
    public const int MentionAckBatchMax = 100;

    /// <summary>GetMentionInbox result cap, newest-first (C6 plan decision, D14 — not spec §13; the
    /// 30d mention-inbox TTL bounds the underlying collection anyway).</summary>
    public const int MentionInboxMaxEntries = 200;

    /// <summary>GetPresence/GetPresenceDetails battleTag-array size cap (C6 plan decision, D14 —
    /// not spec §13; requests above this are rejected with <c>HubException</c>).</summary>
    public const int PresenceQueryMaxBattleTags = 200;

    /// <summary>2026-08-04 follow-up spec §6: connect-snapshot bound for ACCEPTED, non-blocked 1:1 Dm
    /// shells — the N most-recent ride SessionState; older ones ride only with unread > 0 (or via
    /// GetConversations pagination). Pending requests, blocked shells, GroupDm, and every non-DM
    /// channel are never bounded by this.</summary>
    public const int DmSnapshotRecentConversations = 30;

    /// <summary>2026-08-05 PR36 feedback, Part 2 — safety ceiling against pathological accounts on the
    /// connect-snapshot rule-(e) tail (<see cref="Protocol.SessionStateAssembler.SelectSnapshotMemberships"/>):
    /// the ordered (recency-desc) scan keeps at most this many OLDER-with-unread 1:1 Dm shells beyond the
    /// <see cref="DmSnapshotRecentConversations"/> most-recent window; anything beyond is still reachable
    /// via <c>GetConversations</c> pagination. The Messages badge may undercount ONLY for an account with
    /// MORE than this many unread older 1:1 conversations at once — an accepted trade-off (Marco,
    /// 2026-08-05), never a silently-wrong count for anyone under the cap.</summary>
    public const int DmSnapshotMaxOlderUnread = 100;

    /// <summary>GetConversations page-size cap (2026-08-04 follow-up spec §6 — not spec §13; requested
    /// limits above this are clamped down, never rejected — the <see cref="MessagePageSize"/> precedent).</summary>
    public const int ConversationsPageSize = 50;

    /// <summary>C7 HMAC freshness window (brief Design decision 2): a request is rejected when
    /// |now − timestamp| exceeds this window. Pinned default — M1/W2 build against this exact 300s
    /// value as a cross-repo contract, so a test asserts it verbatim.</summary>
    public static readonly TimeSpan InternalSignatureFreshnessWindow = TimeSpan.FromSeconds(300);

    /// <summary>C7 internal-endpoint raw-body size cap (Task 3's HMAC filter hard-stops buffering
    /// here before signature verification) — defense-in-depth against an oversized frame forcing
    /// needless allocation ahead of validation (same rationale as the SignalR receive-size cap in
    /// Startup.cs). Not itself brief-pinned text; hard-coded, adjust here only.</summary>
    public const int InternalMaxBodyBytes = 64 * 1024;

    /// <summary>C7 `members`/`add`/`remove` array size cap per internal-API call (brief Design
    /// decision 3's endpoint bodies) — plan decision, not itself brief-pinned text; hard-coded,
    /// adjust here only.</summary>
    public const int InternalMaxMembersPerCall = 64;

    /// <summary>C7 `{ref}` length cap backing the dot-segment defense regex
    /// <c>\A[A-Za-z0-9_-]{1,64}\z</c> (M1 security finding; anchored with <c>\A</c>/<c>\z</c> rather
    /// than <c>^</c>/<c>$</c> so a trailing newline cannot bypass the character class): callers
    /// URL-encode a <c>nanoid(10)</c>, but the server independently re-validates every ref rather than
    /// trusting the caller.</summary>
    public const int InternalRefMaxLength = 64;

    /// <summary>C7 internal-endpoint channel <c>name</c> length cap (chars) — brief Design decision 3's
    /// endpoint bodies pin this field; plan decision, not itself brief-pinned text; hard-coded, adjust
    /// here only.</summary>
    public const int InternalChannelNameMaxLength = 100;

    /// <summary>
    /// 2026-08-05 reconciliation spec §3: `liveLobbyRefs` array cap on POST /internal/channels/epoch-sync.
    /// Plan decision, not spec-pinned text; hard-coded, adjust here only. Sized to stay comfortably
    /// inside <see cref="InternalMaxBodyBytes"/> (a 64-char ref plus JSON quoting/comma is ≈68 bytes, so
    /// 512 refs ≈ 35 KB of a 64 KB budget) while sitting far above any realistic count of simultaneously
    /// OPEN (not-yet-started) lobbies — mm creates a lobby moments before its game starts, so the live
    /// set is tens, not hundreds. An over-cap body is a 400: mm would retry, which is the correct
    /// fail-loud behavior for "chat cannot safely reconcile a world it only partially received".
    /// </summary>
    public const int InternalMaxLiveRefsPerSync = 512;

    /// <summary>2026-08-05 PR36 feedback, Part 3: <see cref="FanOut.ReadRateLimiter"/>'s single per-
    /// battleTag token bucket, shared across every READ-shaped hub method it guards
    /// (<c>GetConversations</c>, <c>GetMessages</c>). This is a server-protection abuse guard, not UX
    /// pacing (Marco).
    /// <para>
    /// Fix round 1, finding F1: sized for the CONNECT FAN-OUT shape, not a per-user-action handful of
    /// loads — the original 30 undercounted this. The connect snapshot carries no messages, so a client
    /// re-seeds every focused surface on EVERY (re)connect: up to launcher <c>MAX_FOCUSED_CHANNELS</c>
    /// (10) expanded DM windows in parallel, plus the active channel, plus the match embed (~12
    /// surfaces). SignalR's default first reconnect retry is 0ms, so a single socket flap pays that
    /// reseed TWICE within ~2s (~24 calls) before tray scroll or channel clicks push it any higher. 60 ≈
    /// 2× that worst-case flap with slack — still far above anything a single legitimate user action
    /// needs, so no well-behaved client should ever observe a denial in normal operation.
    /// </para>
    /// Not spec §13 text; hard-coded, adjust here only.</summary>
    public const int ReadBurst = 60;

    /// <summary>Sustained refill rate (tokens/second) for the <see cref="ReadBurst"/> bucket above.</summary>
    public const int ReadRefillPerSecond = 5;

    /// <summary>Quiescent-prune horizon for <see cref="FanOut.ReadRateLimiter"/> — mirrors
    /// <see cref="FanOut.MessageRateLimiter"/>'s <see cref="AutoThrottleTierDecay"/>-anchored sweep, but this
    /// limiter has no violation ladder to protect, only the single bucket's fullness. INVARIANT (pinned
    /// by <c>ChatLimitsTests</c>): this MUST stay strictly greater than the bucket's own full-refill time
    /// (<see cref="ReadBurst"/> / <see cref="ReadRefillPerSecond"/> seconds = 12s) — otherwise an entry
    /// pruned at this horizon and silently recreated fresh (full capacity) would NOT be behaviour-
    /// preserving relative to a live bucket that had only refilled for that same idle duration.</summary>
    public static readonly TimeSpan ReadRateLimiterPruneHorizon = TimeSpan.FromMinutes(2);

    /// <summary>Fix round 1 (finding F3): minimum interval between two <see cref="FanOut.ReadRateLimiter"/>
    /// "read rate limit denied" log lines for the SAME user. Denials were previously invisible to
    /// operators — combined with the silent client failure mode on a denial, "no legitimate client ever
    /// hits it" was unfalsifiable in production. A sustained-denial user must not spam the log on every
    /// call, so this bounds it to once per window. Not spec §13 text; hard-coded, adjust here only.</summary>
    public static readonly TimeSpan ReadRateLimiterDenyLogInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Maximum battleTags the flair-refresh coalescer will hold between flushes. At the cap it DROPS
    /// new tags rather than growing: a dropped refresh degrades to the reconnect backstop, whereas an
    /// unbounded set would let a website-backend write storm consume memory here.
    /// </summary>
    public const int FlairRefreshPendingCap = 512;

    /// <summary>
    /// One flush tick's flair-refresh budget: <see cref="FanOut.FlairRefreshCoalescer.Flush"/> drains at
    /// most this many pending battleTags per call, leaving any remainder pending for the next tick.
    /// <para>
    /// Now that the drain runs on its own <see cref="FanOut.FlairRefreshFlushService"/> (fix round, P1) —
    /// not the shared <see cref="FanOut.FanOutFlushService"/> loop — this budget no longer protects that
    /// loop's cadence; an unbounded drain can no longer stall unrelated live-chat fan-out regardless of
    /// burst size. What it still protects is website-backend itself: each refresh is an HTTP round trip
    /// plus a Mongo load/upsert plus per-connection SignalR sends, so an unbounded drain would let a large
    /// burst (e.g. a bulk clan delete notifying every online former member) fire dozens-to-hundreds of
    /// concurrent-ish website-backend requests from a single tick. This is safe to bound because the
    /// coalescer's semantics already tolerate a tag being refreshed a tick later.
    /// </para>
    /// </summary>
    public const int FlairRefreshPerTickBudget = 32;
}

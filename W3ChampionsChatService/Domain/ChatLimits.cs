using System;

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
    /// philosophy — NOT env-configurable; the env knobs live at the forwarded-headers TRUST boundary
    /// (Startup) so the shield keys on the real client IP, not on how many rejections to allow.</para></summary>
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

    /// <summary>Auto-throttle escalation (C3 plan decision, Task 1). Spec §13 pins only "60s
    /// automatic throttle"; the trigger threshold/window are NOT spec-pinned — cheap to change
    /// (C3-plan.md Open question 3). Repeated rate-limit violations within the window escalate to
    /// a hard per-connection throttle for the duration below, plus a moderation log entry.</summary>
    public const int AutoThrottleViolationThreshold = 5;
    public static readonly TimeSpan AutoThrottleWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan AutoThrottleDuration = TimeSpan.FromSeconds(60);

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
}

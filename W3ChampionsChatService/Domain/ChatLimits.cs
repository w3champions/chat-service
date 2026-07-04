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

    /// <summary>Ticket mint rate limit (C2): fixed window per validated battleTag and per source IP.
    /// Values are a C2 plan decision (not spec §13): 10/min per battleTag tolerates reconnect
    /// flapping; 30/min per IP tolerates NAT'd LAN venues.</summary>
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
}

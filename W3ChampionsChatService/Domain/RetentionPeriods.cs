using System;

namespace W3ChampionsChatService.Domain;

/// <summary>
/// Pinned retention windows (spec §5 / C1 brief design-decision 5). Hard-coded by
/// explicit product decision — do NOT make these env-configurable.
/// </summary>
public static class RetentionPeriods
{
    public static readonly TimeSpan ChannelMessages = TimeSpan.FromDays(30);
    public static readonly TimeSpan DirectMessages = TimeSpan.FromDays(90);
    public static readonly TimeSpan MentionInbox = TimeSpan.FromDays(30);
    public static readonly TimeSpan MatchChannel = TimeSpan.FromHours(24);
    public static readonly TimeSpan DmShell = TimeSpan.FromDays(365);
    public static readonly TimeSpan PendingDmShell = TimeSpan.FromDays(30);
    public static readonly TimeSpan IdleMembership = TimeSpan.FromDays(365);
    public static readonly TimeSpan CleanupInterval = TimeSpan.FromDays(7);
}

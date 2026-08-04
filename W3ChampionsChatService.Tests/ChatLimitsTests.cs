using System;
using System.Linq;
using NUnit.Framework;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Tests;

public class ChatLimitsTests
{
    [Test]
    public void Values_MatchSpecSection13Verbatim()
    {
        Assert.AreEqual(512, ChatLimits.MaxMessageLength);
        Assert.AreEqual(5, ChatLimits.PerChannelBurst);
        Assert.AreEqual(TimeSpan.FromSeconds(1), ChatLimits.PerChannelSustainedInterval);
        Assert.AreEqual(10, ChatLimits.GlobalMessageBurst);
        Assert.AreEqual(TimeSpan.FromSeconds(5), ChatLimits.GlobalMessageWindow);
        Assert.AreEqual(10, ChatLimits.StrangerDmInitiationCap);
        Assert.AreEqual(TimeSpan.FromHours(8), ChatLimits.StrangerDmInitiationWindow);
        Assert.AreEqual(25, ChatLimits.PendingConversationMaxMessages);
        Assert.AreEqual(5, ChatLimits.MaxMentionsPerMessage);
        Assert.AreEqual(100, ChatLimits.MaxGroupSize);
        Assert.AreEqual(5, ChatLimits.ChannelCreationPerHour);
        Assert.AreEqual(10, ChatLimits.MaxFocusedChannels);
        Assert.AreEqual(50, ChatLimits.MaxPublicMembershipsPerUser);
        Assert.AreEqual(1, ChatLimits.MaxConnectionsPerBattleTag);
        Assert.AreEqual(TimeSpan.FromSeconds(5), ChatLimits.MarkReadThrottle);
        Assert.AreEqual(TimeSpan.FromSeconds(10), ChatLimits.ChannelActivityCoalesce);
        Assert.AreEqual(100, ChatLimits.ChannelActivitySuppressUnreadThreshold);
        Assert.AreEqual(TimeSpan.FromSeconds(5), ChatLimits.ViewersChangedFlush);
        Assert.AreEqual(TimeSpan.FromSeconds(60), ChatLimits.TicketTtl);
    }

    [Test]
    public void MintRateLimitConstants_MatchC2PlanDecision3()
    {
        // C2 plan decision 3 — NOT spec §13; hard-coded, adjust here only.
        Assert.AreEqual(10, ChatLimits.TicketMintPerBattleTagLimit);
        Assert.AreEqual(30, ChatLimits.TicketMintPerIpLimit);
        Assert.AreEqual(TimeSpan.FromMinutes(1), ChatLimits.TicketMintWindow);
    }

    [Test]
    public void MessagePageSizeAndAutoThrottleConstants_MatchC3PlanDecisionTask1()
    {
        // C3 plan decision (Task 1) — NOT spec §13; hard-coded, adjust here only.
        Assert.AreEqual(100, ChatLimits.MessagePageSize);
        Assert.AreEqual(5, ChatLimits.AutoThrottleViolationThreshold);
        Assert.AreEqual(TimeSpan.FromSeconds(60), ChatLimits.AutoThrottleWindow);
        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60) },
            ChatLimits.AutoThrottleTierDurations);
        Assert.AreEqual(TimeSpan.FromMinutes(10), ChatLimits.AutoThrottleTierDecay);
    }

    [Test]
    public void AutoThrottleTierDurations_AreAllBelowTierDecay()
    {
        // Pins the invariant documented at AutoThrottleTierDurations' declaration: MessageRateLimiter's
        // quiescent prune treats AutoThrottleTierDecay as "definitely idle, safe to evict" for a user's
        // ENTIRE state (buckets, violations, ladder, and any active hard throttle). That is only true if
        // no tier duration can outlive the decay horizon — otherwise a still-hard-throttled user could be
        // pruned mid-penalty and get a silent clean slate on their very next send.
        Assert.IsTrue(
            ChatLimits.AutoThrottleTierDurations.All(tier => tier < ChatLimits.AutoThrottleTierDecay),
            "every AutoThrottleTierDurations entry must be strictly less than AutoThrottleTierDecay, or " +
            "MessageRateLimiter's quiescent prune could evict a still-hard-throttled user's state early");
    }

    [Test]
    public void ChannelCreationWindow_MatchesC3PlanDecisionTask10()
    {
        // C3 plan decision (Task 10) — the window backing ChannelCreationPerHour's "per hour" (not
        // itself spec §13 text, mirrors TicketMintWindow's role for the mint limits above).
        Assert.AreEqual(TimeSpan.FromHours(1), ChatLimits.ChannelCreationWindow);
    }

    [Test]
    public void MentionAndPresenceConstants_MatchC6PlanDecisionTask1D14()
    {
        // Spec-pinned (§7: "lastSeenAt ≥ now−90d") — Tier 3 (directory) search results only.
        Assert.AreEqual(TimeSpan.FromDays(90), ChatLimits.MentionCandidateActivityWindow);
        // Plan decisions (C6 plan Task 1, D14) — NOT spec §13 text; hard-coded, adjust here only.
        Assert.AreEqual(20, ChatLimits.MentionSearchMaxResults);
        Assert.AreEqual(100, ChatLimits.MentionAckBatchMax);
        Assert.AreEqual(200, ChatLimits.MentionInboxMaxEntries);
        Assert.AreEqual(200, ChatLimits.PresenceQueryMaxBattleTags);
    }

    [Test]
    public void ConversationsPageSize_IsAtLeastLauncherPageSize()
    {
        // Cross-repo contract (Task 8 fix round, finding 3): the launcher's client-side page size
        // (CONVERSATIONS_PAGE_SIZE = 30, launcher-e chat plumbing) drives GetConversations' Count &lt;
        // limit end-detection (see GetConversationsResult's doc comment). That end-detection is only
        // sound when a caller's requested limit never exceeds this cap — a launcher paging at 30 while
        // this cap ever dropped below 30 would get a silently-clamped, potentially-short page and could
        // stop pagination early. Pinned here so a future change to either constant is caught at CI time.
        Assert.GreaterOrEqual(ChatLimits.ConversationsPageSize, 30,
            "the launcher's CONVERSATIONS_PAGE_SIZE = 30 depends on ChatLimits.ConversationsPageSize " +
            "staying >= 30 for GetConversations' Count < limit end-detection to remain sound");
    }

    [Test]
    public void DefaultChatRooms_NormalizeToDistinctKeys()
    {
        // C3 Task 3: Guards the static SessionStateAssembler.CatalogOrder initialization invariant.
        // If any two DefaultChatRooms.Rooms entries normalize to the same key (via ChannelNames.Normalize:
        // trim + ToLowerInvariant), the ToDictionary call at static init throws KeyAlreadyExistsException
        // → TypeInitializationException on every connect (full outage). This test pins the invariant at CI time.
        Assert.IsNotEmpty(DefaultChatRooms.Rooms, "DefaultChatRooms.Rooms must be non-empty");
        var normalizedNames = DefaultChatRooms.Rooms.Select(ChannelNames.Normalize).ToList();
        var distinctCount = normalizedNames.Distinct().Count();
        Assert.AreEqual(normalizedNames.Count, distinctCount,
            "all DefaultChatRooms.Rooms entries must normalize to distinct keys, or " +
            "SessionStateAssembler.CatalogOrder's static ToDictionary initialization will crash with TypeInitializationException");
    }
}

using System;
using NUnit.Framework;
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
}

using System;
using NUnit.Framework;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Tests;

public class ExpiryCalculatorTests
{
    private static readonly DateTime Now = new(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void ChannelMessages_Expire30DaysAfterSend()
    {
        Assert.AreEqual(Now.AddDays(30), ExpiryCalculator.ForChannelMessage(ChannelType.Public, Now));
        Assert.AreEqual(Now.AddDays(30), ExpiryCalculator.ForChannelMessage(ChannelType.SemiPublic, Now));
        Assert.AreEqual(Now.AddDays(30), ExpiryCalculator.ForChannelMessage(ChannelType.System, Now));
    }

    [Test]
    public void DmAndGroupMessages_Expire90DaysAfterSend()
    {
        Assert.AreEqual(Now.AddDays(90), ExpiryCalculator.ForChannelMessage(ChannelType.Dm, Now));
        Assert.AreEqual(Now.AddDays(90), ExpiryCalculator.ForChannelMessage(ChannelType.GroupDm, Now));
    }

    [Test]
    public void MentionInbox_Expires30DaysAfterCreation()
    {
        Assert.AreEqual(Now.AddDays(30), ExpiryCalculator.ForMentionInboxEntry(Now));
    }

    [Test]
    public void MatchAndLobbyChannels_Expire24HoursAfterCreation()
    {
        var match = new ChatChannel { Type = ChannelType.System, SystemKind = SystemChannelKind.Match };
        var lobby = new ChatChannel { Type = ChannelType.System, SystemKind = SystemChannelKind.Lobby };
        Assert.AreEqual(Now.AddHours(24), ExpiryCalculator.ForChannelShell(match, Now));
        Assert.AreEqual(Now.AddHours(24), ExpiryCalculator.ForChannelShell(lobby, Now));
    }

    [Test]
    public void AcceptedDmAndGroupShells_Expire1YearAfterLastMessage()
    {
        var dm = new ChatChannel { Type = ChannelType.Dm, RequestState = DmRequestState.Accepted };
        var group = new ChatChannel { Type = ChannelType.GroupDm };
        Assert.AreEqual(Now.AddDays(365), ExpiryCalculator.ForChannelShell(dm, Now));
        Assert.AreEqual(Now.AddDays(365), ExpiryCalculator.ForChannelShell(group, Now));
    }

    [Test]
    public void PendingDmShells_Expire30DaysAfterLastActivity()
    {
        var pending = new ChatChannel { Type = ChannelType.Dm, RequestState = DmRequestState.Pending };
        Assert.AreEqual(Now.AddDays(30), ExpiryCalculator.ForChannelShell(pending, Now));
    }

    [Test]
    public void PublicClanAndSemiPublicChannels_NeverExpire()
    {
        var publicChannel = new ChatChannel { Type = ChannelType.Public };
        var clan = new ChatChannel { Type = ChannelType.System, SystemKind = SystemChannelKind.Clan };
        var semiPublic = new ChatChannel { Type = ChannelType.SemiPublic };
        Assert.IsNull(ExpiryCalculator.ForChannelShell(publicChannel, Now));
        Assert.IsNull(ExpiryCalculator.ForChannelShell(clan, Now));
        Assert.IsNull(ExpiryCalculator.ForChannelShell(semiPublic, Now)); // weekly GC job, not TTL
    }
}

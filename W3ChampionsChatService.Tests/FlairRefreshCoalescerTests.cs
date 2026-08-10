using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;

namespace W3ChampionsChatService.Tests;

public class FlairRefreshCoalescerTests
{
    private class RecordingRefresher : IFlairRefresher
    {
        public List<string> Refreshed { get; } = new();
        public bool Throw { get; set; }

        public Task Refresh(string battleTag)
        {
            Refreshed.Add(battleTag);
            if (Throw) throw new System.InvalidOperationException("refresh exploded");
            return Task.CompletedTask;
        }
    }

    private RecordingRefresher _refresher;
    private FlairRefreshCoalescer _coalescer;

    [SetUp]
    public void SetupBeforeEach()
    {
        _refresher = new RecordingRefresher();
        _coalescer = new FlairRefreshCoalescer(_refresher);
    }

    [Test]
    public async Task Flush_RefreshesEachRecordedBattleTagOnce()
    {
        _coalescer.RecordChange("peter#123");
        _coalescer.RecordChange("alice#456");

        await _coalescer.Flush();

        Assert.That(_refresher.Refreshed, Is.EquivalentTo(new[] { "peter#123", "alice#456" }));
    }

    [Test]
    public async Task Flush_CollapsesABurstForOneBattleTagIntoASingleRefresh()
    {
        // Five writes in one tick — e.g. a reward grant that touches colour then icons — must cost one
        // website-backend round-trip, not five.
        for (var i = 0; i < 5; i++) _coalescer.RecordChange("peter#123");

        await _coalescer.Flush();

        Assert.That(_refresher.Refreshed, Is.EqualTo(new[] { "peter#123" }));
    }

    [Test]
    public async Task RecordChange_IsCaseInsensitive()
    {
        _coalescer.RecordChange("Peter#123");
        _coalescer.RecordChange("peter#123");

        await _coalescer.Flush();

        Assert.That(_refresher.Refreshed, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Flush_DrainsThePendingSet()
    {
        _coalescer.RecordChange("peter#123");
        await _coalescer.Flush();
        await _coalescer.Flush();

        Assert.That(_refresher.Refreshed, Is.EqualTo(new[] { "peter#123" }));
        Assert.That(_coalescer.PendingCount, Is.EqualTo(0));
    }

    [Test]
    public void RecordChange_AtCapacity_DropsRatherThanGrows()
    {
        for (var i = 0; i < ChatLimits.FlairRefreshPendingCap + 50; i++)
        {
            _coalescer.RecordChange($"player{i}#1");
        }

        Assert.That(_coalescer.PendingCount, Is.EqualTo(ChatLimits.FlairRefreshPendingCap));
    }

    [Test]
    public async Task Flush_TakesOnlyThePerTickBudget_LeavingTheRemainderPendingForTheNextTick()
    {
        // One tick's worth plus a few more, so the first Flush must leave a genuine remainder rather
        // than happening to drain everything anyway.
        var total = ChatLimits.FlairRefreshPerTickBudget + 5;
        for (var i = 0; i < total; i++) _coalescer.RecordChange($"player{i}#1");

        await _coalescer.Flush();

        Assert.That(_refresher.Refreshed, Has.Count.EqualTo(ChatLimits.FlairRefreshPerTickBudget),
            "a single Flush must refresh no more than one tick's budget");
        Assert.That(_coalescer.PendingCount, Is.EqualTo(5),
            "the remainder beyond the budget must stay pending for the next tick");

        await _coalescer.Flush();

        Assert.That(_refresher.Refreshed, Has.Count.EqualTo(total),
            "a second Flush must refresh the leftover remainder");
        Assert.That(_coalescer.PendingCount, Is.EqualTo(0));
    }

    [Test]
    public void RecordChange_IgnoresBlankTags()
    {
        _coalescer.RecordChange(null);
        _coalescer.RecordChange("   ");

        Assert.That(_coalescer.PendingCount, Is.EqualTo(0));
    }

    [Test]
    public void Flush_WhenOneRefreshThrows_StillRefreshesTheRest()
    {
        _refresher.Throw = true;
        _coalescer.RecordChange("peter#123");
        _coalescer.RecordChange("alice#456");

        Assert.DoesNotThrowAsync(() => _coalescer.Flush());

        Assert.That(_refresher.Refreshed, Has.Count.EqualTo(2),
            "one player's failed refresh must not cancel everyone else's");
    }
}

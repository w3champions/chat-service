using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using NUnit.Framework;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Self-tests for <see cref="HubPushCaptureHarness"/> itself — this is shared test infrastructure
/// that every future fan-out task (13, 14, 15) will build on, so its capture/accessor behavior is
/// pinned here rather than trusted implicitly.
/// </summary>
public class HubPushCaptureHarnessTests
{
    [Test]
    public async Task SignalsFor_RecordsSendsToThatConnection_InOrder()
    {
        var harness = new HubPushCaptureHarness();

        await harness.HubContext.Clients.Client("conn-1").SendAsync("Envelope", "first");
        await harness.HubContext.Clients.Client("conn-1").SendAsync("Envelope", "second");

        var signals = harness.SignalsFor("conn-1");

        Assert.AreEqual(2, signals.Count);
        Assert.AreEqual(("Envelope", (object)"first"), signals[0]);
        Assert.AreEqual(("Envelope", (object)"second"), signals[1]);
    }

    [Test]
    public async Task SignalsFor_UnknownConnection_ReturnsEmpty()
    {
        var harness = new HubPushCaptureHarness();

        await harness.HubContext.Clients.Client("conn-1").SendAsync("Envelope", "payload");

        Assert.IsEmpty(harness.SignalsFor("conn-does-not-exist"));
    }

    [Test]
    public async Task PayloadFor_ReturnsFirstMatchingPayload()
    {
        var harness = new HubPushCaptureHarness();

        await harness.HubContext.Clients.Client("conn-1").SendAsync("Envelope", "first");
        await harness.HubContext.Clients.Client("conn-1").SendAsync("Envelope", "second");

        Assert.AreEqual("first", harness.PayloadFor("conn-1", "Envelope"));
    }

    [Test]
    public void PayloadFor_NoMatchingSend_ReturnsNull()
    {
        var harness = new HubPushCaptureHarness();

        Assert.IsNull(harness.PayloadFor("conn-1", "Envelope"));
    }

    [Test]
    public async Task SignalCount_CountsOnlyMatchingMethodForThatConnection()
    {
        var harness = new HubPushCaptureHarness();

        await harness.HubContext.Clients.Client("conn-1").SendAsync("Envelope", "a");
        await harness.HubContext.Clients.Client("conn-1").SendAsync("Envelope", "b");
        await harness.HubContext.Clients.Client("conn-1").SendAsync("Other", "c");
        await harness.HubContext.Clients.Client("conn-2").SendAsync("Envelope", "d");

        Assert.AreEqual(2, harness.SignalCount("conn-1", "Envelope"));
        Assert.AreEqual(1, harness.SignalCount("conn-1", "Other"));
        Assert.AreEqual(1, harness.SignalCount("conn-2", "Envelope"));
        Assert.AreEqual(0, harness.SignalCount("conn-2", "Other"));
    }

    [Test]
    public async Task AllSignals_PreservesCrossConnectionSendOrder()
    {
        var harness = new HubPushCaptureHarness();

        await harness.HubContext.Clients.Client("conn-1").SendAsync("Envelope", "a");
        await harness.HubContext.Clients.Client("conn-2").SendAsync("Envelope", "b");
        await harness.HubContext.Clients.Client("conn-1").SendAsync("Envelope", "c");

        var all = harness.AllSignals;

        Assert.AreEqual(3, all.Count);
        Assert.AreEqual(("conn-1", "Envelope", (object)"a"), all[0]);
        Assert.AreEqual(("conn-2", "Envelope", (object)"b"), all[1]);
        Assert.AreEqual(("conn-1", "Envelope", (object)"c"), all[2]);
    }
}

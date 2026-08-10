using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Internal;

namespace W3ChampionsChatService.Tests;

public class InternalProfileChangesControllerTests
{
    private class NoOpRefresher : IFlairRefresher
    {
        public System.Threading.Tasks.Task Refresh(string battleTag) => System.Threading.Tasks.Task.CompletedTask;
    }

    private FlairRefreshCoalescer _coalescer;
    private InternalProfileChangesController _controller;

    [SetUp]
    public void SetupBeforeEach()
    {
        _coalescer = new FlairRefreshCoalescer(new NoOpRefresher());
        _controller = new InternalProfileChangesController(_coalescer)
        {
            // No-TestServer controller idiom shared with InternalRelationshipChangesControllerTests:
            // Post logs via InternalHmacAuthFilter.ResolveCaller(HttpContext), which NREs against the
            // default null HttpContext a directly-constructed controller otherwise has.
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static InternalProfileChangeRequest Request(params string[] battleTags) =>
        new() { BattleTags = battleTags.ToList() };

    [Test]
    public void Post_EnqueuesEveryBattleTag()
    {
        var result = _controller.Post(Request("peter#123", "alice#456"));

        Assert.That(result, Is.InstanceOf<OkResult>());
        Assert.That(_coalescer.PendingCount, Is.EqualTo(2));
    }

    [Test]
    public void Post_AtTheCap_IsAccepted()
    {
        var tags = Enumerable.Range(0, ChatLimits.InternalMaxMembersPerCall).Select(i => $"player{i}#1").ToArray();

        Assert.That(_controller.Post(Request(tags)), Is.InstanceOf<OkResult>());
    }

    [Test]
    public void Post_OverTheCap_IsRejectedWithNoPartialProcessing()
    {
        var tags = Enumerable.Range(0, ChatLimits.InternalMaxMembersPerCall + 1).Select(i => $"player{i}#1").ToArray();

        Assert.That(_controller.Post(Request(tags)), Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(_coalescer.PendingCount, Is.EqualTo(0));
    }

    [Test]
    public void Post_WithNullRequest_IsRejected()
    {
        Assert.That(_controller.Post(null), Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public void Post_WithNoBattleTags_IsRejected()
    {
        Assert.That(_controller.Post(new InternalProfileChangeRequest { BattleTags = null }), Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(_controller.Post(Request()), Is.InstanceOf<BadRequestObjectResult>());
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("peter\u0000123")]
    [TestCase("peter\u2028123")]
    [TestCase("peter\u2029123")]
    public void Post_WithAnInvalidBattleTag_RejectsTheWholeBatch(string invalid)
    {
        // No partial processing: one bad entry rejects the batch, and nothing is enqueued.
        Assert.That(_controller.Post(Request("peter#123", invalid)), Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(_coalescer.PendingCount, Is.EqualTo(0));
    }
}

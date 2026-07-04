using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Mutes;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C4 Task 7 pinning suite: <c>/api/loungeMute</c> must stay BYTE-IDENTICAL through the chat-service
/// rewrite — the website-backend proxies it directly and depends on the exact routes and the exact
/// serialized <see cref="LoungeMute"/> field set. This file adds regression fences only; it does NOT
/// change <see cref="MuteController"/> or <see cref="LoungeMute"/> (both stay byte-identical). It also
/// pins that the OLD ChatHistory-backed GET /api/chat/{chatroom} endpoint is fully gone.
/// </summary>
public class LoungeMuteRestContractTests : IntegrationTestBase
{
    private MuteRepository _muteRepository;
    private MuteController _controller;

    [SetUp]
    public void SetupBeforeEach()
    {
        _muteRepository = new MuteRepository(MongoClient);
        var harness = new MuteReconciliationTestHarness(new ConnectionMapping(), _muteRepository);
        _controller = new MuteController(_muteRepository, harness.Service);
    }

    [Test]
    public void Routes_AreExactly_apiLoungeMute_GET_POST_DELETEbTag()
    {
        var controllerType = typeof(MuteController);
        var classRoute = controllerType.GetCustomAttribute<RouteAttribute>();
        Assert.IsNotNull(classRoute);
        Assert.AreEqual("api/loungeMute", classRoute.Template);

        var get = controllerType.GetMethod(nameof(MuteController.GetLoungeMutes));
        var getRoute = get.GetCustomAttribute<HttpGetAttribute>();
        Assert.IsNotNull(getRoute, "GET must stay [HttpGet(\"\")]");
        Assert.AreEqual("", getRoute.Template);

        var post = controllerType.GetMethod(nameof(MuteController.AddLoungeMute));
        var postRoute = post.GetCustomAttribute<HttpPostAttribute>();
        Assert.IsNotNull(postRoute, "POST must stay [HttpPost(\"\")]");
        Assert.AreEqual("", postRoute.Template);

        var del = controllerType.GetMethod(nameof(MuteController.DeleteLoungeMute));
        var delRoute = del.GetCustomAttribute<HttpDeleteAttribute>();
        Assert.IsNotNull(delRoute, "DELETE must stay [HttpDelete(\"{bTag}\")]");
        Assert.AreEqual("{bTag}", delRoute.Template);
    }

    [Test]
    public async Task Get_SerializesFullLoungeMuteShape()
    {
        await _muteRepository.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = "Target#123",
            endDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O"),
            author = "mod#1",
            reason = "spam",
            isShadowBan = true,
        });

        var result = await _controller.GetLoungeMutes() as OkObjectResult;
        var mutes = result.Value as List<LoungeMute>;
        var mute = mutes.Single();

        // wb byte-compat: the exact business field set MuteRepository.AddLoungeMute/GetMutedPlayer
        // round-trips through the REST surface unchanged.
        Assert.AreEqual("target#123", mute.battleTag, "AddLoungeMute lowercases the stored battleTag");
        Assert.AreEqual("mod#1", mute.author);
        Assert.AreEqual("spam", mute.reason);
        Assert.IsTrue(mute.isShadowBan);
        Assert.AreEqual(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), mute.endDate);
        Assert.IsTrue((DateTime.UtcNow - mute.insertDate).Duration() < TimeSpan.FromMinutes(1));

        var propertyNames = typeof(LoungeMute).GetProperties().Select(p => p.Name).ToHashSet();
        CollectionAssert.IsSubsetOf(
            new[] { "battleTag", "endDate", "insertDate", "author", "reason", "isShadowBan" }, propertyNames,
            "the six wb-consumed business fields must all still exist on LoungeMute");
    }

    [Test]
    public async Task Post_MissingFields_400s()
    {
        var missingBattleTag = await _controller.AddLoungeMute(new LoungeMuteRequest { battleTag = "", endDate = "2026-08-01T00:00:00Z" });
        Assert.IsInstanceOf<BadRequestObjectResult>(missingBattleTag, "an empty battleTag must 400");

        var missingEndDate = await _controller.AddLoungeMute(new LoungeMuteRequest { battleTag = "target#123", endDate = "" });
        Assert.IsInstanceOf<BadRequestObjectResult>(missingEndDate, "an empty endDate must 400");
    }

    [Test]
    public async Task Delete_Absent_404()
    {
        var result = await _controller.DeleteLoungeMute("nobody#123");

        Assert.IsInstanceOf<NotFoundObjectResult>(result, "deleting a mute that doesn't exist must 404");
    }

    [Test]
    public void OldEndpoint_ApiChatChatroom_IsGone()
    {
        var assembly = typeof(MuteController).Assembly;
        var routeTemplates = assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>())
            .Select(r => r.Template)
            .ToList();

        Assert.IsFalse(routeTemplates.Any(t => t.Equals("api/chat", StringComparison.OrdinalIgnoreCase)),
            "the legacy ChatHistory-backed GET /api/chat/{chatroom} route must be fully removed");
        Assert.IsNull(assembly.GetType("W3ChampionsChatService.Chats.ChatController"),
            "ChatController must be deleted outright, not merely unrouted");
    }
}

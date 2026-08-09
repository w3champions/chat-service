using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Chats;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// PR40 review (P1): <see cref="WebsiteBackendRepository.GetChatDetails"/> must FAIL on an unusable wb
/// response rather than returning a default-valued DTO.
/// <para>
/// Why this matters beyond a tidier error path: the return value feeds
/// <see cref="ChatAuthenticationService.GetUserFromIdentity"/>, which stamps a NON-throwing result
/// <c>FreshFromWb: true</c>. Two callers then treat that flag as authoritative —
/// <c>ChatHub.ReconcileClanMembership</c> (which reads "no clan" as a clan DEPARTURE and deletes the
/// membership) and <c>ChatHub.UpsertDirectory</c> (which replaces a cached Profile). A wb 5xx whose body
/// still deserializes therefore used to look exactly like "this player genuinely has no clan".
/// </para>
/// </summary>
[TestFixture]
public class WebsiteBackendRepositoryTests
{
    /// <summary>Serves one canned response to whatever the repository requests.</summary>
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private static WebsiteBackendRepository RepositoryReturning(HttpStatusCode status, string body)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new StubHandler(status, body)));
        return new WebsiteBackendRepository(factory.Object);
    }

    [Test]
    public void GetChatDetails_OnErrorStatusWithDeserializableBody_Throws()
    {
        // The exact shape that used to slip through: a JSON error envelope binds happily to
        // ChatDetailsDto, leaving every property (including ClanId) null — with NO exception.
        var repository = RepositoryReturning(HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}");

        Assert.ThrowsAsync<HttpRequestException>(
            async () => await repository.GetChatDetails("peter#123"),
            "a wb 500 must throw so GetUserFromIdentity falls back with FreshFromWb: false — otherwise an "
            + "outage is indistinguishable from 'this player has no clan' and evicts them from their clan channel");
    }

    [Test]
    public void GetChatDetails_OnNotFound_Throws()
    {
        var repository = RepositoryReturning(HttpStatusCode.NotFound, "{}");

        Assert.ThrowsAsync<HttpRequestException>(async () => await repository.GetChatDetails("peter#123"));
    }

    [Test]
    public void GetChatDetails_OnSuccessWithEmptyBody_Throws()
    {
        // A 200 with an empty body deserializes to a NULL DTO — malformed, not "no clan".
        var repository = RepositoryReturning(HttpStatusCode.OK, "");

        Assert.ThrowsAsync<InvalidOperationException>(async () => await repository.GetChatDetails("peter#123"));
    }

    [Test]
    public async Task GetChatDetails_OnSuccess_ReturnsTheClanId()
    {
        var repository = RepositoryReturning(HttpStatusCode.OK, "{\"clanId\":\"EwOk\"}");

        var details = await repository.GetChatDetails("peter#123");

        Assert.AreEqual("EwOk", details.ClanId, "the happy path must be unchanged by the new guards");
    }

    [Test]
    public async Task GetChatDetails_OnSuccessWithNullClan_ReturnsNullClanId()
    {
        // A genuine 200 saying the player is in no clan stays a NON-throwing null ClanId — this is the
        // one case that IS authoritative absence, and reconciliation must still act on it.
        var repository = RepositoryReturning(HttpStatusCode.OK, "{\"clanId\":null}");

        var details = await repository.GetChatDetails("peter#123");

        Assert.IsNull(details.ClanId);
    }
}

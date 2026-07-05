using System.Linq;
using System.Net.Http;
using NUnit.Framework;
using W3ChampionsChatService.Relationships;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C5/W2: <see cref="WebsiteBackendRelationshipSource"/> must attach the shared outbound secret wb's
/// <c>ChatServiceSecretAuthFilter</c> requires (header <c>x-chat-relationships-secret</c>) — without it
/// every relationship read blanket-401s and <see cref="RelationshipProvider"/> fails closed. Exercises
/// the <see cref="WebsiteBackendRelationshipSource.BuildRequest"/> test seam directly (assembly has
/// InternalsVisibleTo) rather than performing any real HTTP call or mocking
/// <see cref="System.Net.Http.HttpMessageHandler"/>.
/// </summary>
[TestFixture]
public class WebsiteBackendRelationshipSourceTests
{
    [Test]
    public void BuildRequest_AttachesSecretHeader_WhenConfigured()
    {
        var settings = new RelationshipsSourceAuthSettings("shared-secret");
        var source = new WebsiteBackendRelationshipSource(settings);

        var request = source.BuildRequest("Peon#123");

        Assert.That(request.Headers.Contains(WebsiteBackendRelationshipSource.HeaderName), Is.True,
            "the outbound request must carry the shared secret header when the settings are configured");
        Assert.That(
            request.Headers.GetValues(WebsiteBackendRelationshipSource.HeaderName).Single(),
            Is.EqualTo("shared-secret"));
    }

    [Test]
    public void BuildRequest_OmitsSecretHeader_WhenNotConfigured()
    {
        var settings = new RelationshipsSourceAuthSettings(null);
        var source = new WebsiteBackendRelationshipSource(settings);

        var request = source.BuildRequest("Peon#123");

        Assert.That(request.Headers.Contains(WebsiteBackendRelationshipSource.HeaderName), Is.False,
            "an unconfigured secret must send NO header at all (never an empty-string header) so wb's " +
            "fail-closed filter 401s exactly as it does today, rather than the client crashing/throwing");
    }

    [Test]
    public void BuildRequest_RouteAndMethod_AreUnaffectedByAuthHeaderPresence()
    {
        var configured = new WebsiteBackendRelationshipSource(new RelationshipsSourceAuthSettings("secret"))
            .BuildRequest("Peon#123");
        var unconfigured = new WebsiteBackendRelationshipSource(new RelationshipsSourceAuthSettings(null))
            .BuildRequest("Peon#123");

        Assert.That(configured.RequestUri, Is.EqualTo(unconfigured.RequestUri),
            "attaching (or omitting) the auth header must not change the requested route");
        Assert.That(configured.Method, Is.EqualTo(HttpMethod.Get));
    }
}

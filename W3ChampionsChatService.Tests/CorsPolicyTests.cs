using Microsoft.AspNetCore.Cors.Infrastructure;
using NUnit.Framework;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// <see cref="Startup.ConfigureCors"/>: any origin may call the REST endpoints, never with the visitor's credentials.
/// The hub authenticates with a one-time ticket and the internal API with HMAC; nothing reads a cookie.
/// </summary>
[TestFixture]
public class CorsPolicyTests
{
    [Test]
    public void AnyOriginMayCallWithoutCredentials()
    {
        var builder = new CorsPolicyBuilder();
        Startup.ConfigureCors(builder);
        var policy = builder.Build();

        Assert.That(policy.AllowAnyOrigin, Is.True);
        Assert.That(policy.AllowAnyHeader, Is.True);
        Assert.That(policy.AllowAnyMethod, Is.True);
        Assert.That(policy.SupportsCredentials, Is.False);
    }
}

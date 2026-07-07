using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Builder;
using NUnit.Framework;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// F1/A2: the forwarded-headers TRUST boundary is env-configurable so ops can pin the real topology
/// (edge gateway → Traefik passthrough → nginx-proxy in Docker) without a code change, while the app
/// ships only the knobs + safe defaults and hardcodes NO prod IPs. These drive the PURE parsing core
/// (<see cref="Startup.ApplyForwardedHeadersTrustConfig"/>) directly with the raw env-string inputs, so
/// no process environment variables are mutated and there is no cross-test interference. They pin: (a)
/// unset/blank inputs leave the ASP.NET defaults untouched (current behavior is the safe default), (b)
/// valid proxies/networks are ADDED to (not replacing) the defaults and the hop limit is applied, and
/// (c) malformed entries are skipped without crashing, while the valid entries alongside them still apply.
/// </summary>
[TestFixture]
public class StartupForwardedHeadersTests
{
    [Test]
    public void AllUnsetOrBlank_LeavesAspNetDefaultsUntouched()
    {
        var options = new ForwardedHeadersOptions();
        var baselineProxies = options.KnownProxies.Count;
        var baselineNetworks = options.KnownNetworks.Count;
        var baselineLimit = options.ForwardLimit;

        Startup.ApplyForwardedHeadersTrustConfig(options, knownProxiesCsv: null, knownNetworksCsv: "", forwardLimitRaw: "   ");

        Assert.AreEqual(baselineProxies, options.KnownProxies.Count, "unset FORWARDED_KNOWN_PROXIES must not touch the defaults");
        Assert.AreEqual(baselineNetworks, options.KnownNetworks.Count, "unset FORWARDED_KNOWN_NETWORKS must not touch the defaults");
        Assert.AreEqual(baselineLimit, options.ForwardLimit, "unset FORWARDED_LIMIT must leave ForwardLimit at its default");
    }

    [Test]
    public void ValidProxiesNetworksAndLimit_AreParsedAndAddedToDefaults()
    {
        var options = new ForwardedHeadersOptions();
        var baselineProxies = options.KnownProxies.Count;
        var baselineNetworks = options.KnownNetworks.Count;

        Startup.ApplyForwardedHeadersTrustConfig(
            options,
            knownProxiesCsv: "10.0.0.1, 203.0.113.7",
            knownNetworksCsv: "10.0.0.0/8, 192.168.0.0/16",
            forwardLimitRaw: "3");

        Assert.AreEqual(baselineProxies + 2, options.KnownProxies.Count, "two valid proxies must be ADDED to the defaults, not replace them");
        Assert.IsTrue(options.KnownProxies.Any(p => p.Equals(IPAddress.Parse("10.0.0.1"))));
        Assert.IsTrue(options.KnownProxies.Any(p => p.Equals(IPAddress.Parse("203.0.113.7"))));

        Assert.AreEqual(baselineNetworks + 2, options.KnownNetworks.Count, "two valid CIDRs must be ADDED to the defaults");
        Assert.IsTrue(options.KnownNetworks.Any(n => n.Prefix.Equals(IPAddress.Parse("10.0.0.0")) && n.PrefixLength == 8));
        Assert.IsTrue(options.KnownNetworks.Any(n => n.Prefix.Equals(IPAddress.Parse("192.168.0.0")) && n.PrefixLength == 16));

        Assert.AreEqual(3, options.ForwardLimit, "a valid FORWARDED_LIMIT must set ForwardLimit");
    }

    [Test]
    public void MalformedEntries_AreSkipped_ValidOnesStillApplied_NoThrow()
    {
        var options = new ForwardedHeadersOptions();
        var baselineProxies = options.KnownProxies.Count;
        var baselineNetworks = options.KnownNetworks.Count;
        var baselineLimit = options.ForwardLimit;

        // Each list mixes ONE valid entry with several malformed ones (bad IP, out-of-range octet,
        // non-CIDR, out-of-range prefix length, trailing-slash) plus a non-integer limit. The valid
        // entries must apply; the malformed ones must be skipped; startup must never crash.
        Assert.DoesNotThrow(() => Startup.ApplyForwardedHeadersTrustConfig(
            options,
            knownProxiesCsv: "10.0.0.1, not-an-ip, 999.999.999.999",
            knownNetworksCsv: "10.0.0.0/8, garbage, 10.0.0.0/40, 10.0.0.0/",
            forwardLimitRaw: "not-a-number"));

        Assert.AreEqual(baselineProxies + 1, options.KnownProxies.Count, "only the one valid proxy is added; the malformed ones are skipped");
        Assert.IsTrue(options.KnownProxies.Any(p => p.Equals(IPAddress.Parse("10.0.0.1"))));

        Assert.AreEqual(baselineNetworks + 1, options.KnownNetworks.Count, "only the one valid CIDR is added; the malformed ones are skipped");
        Assert.IsTrue(options.KnownNetworks.Any(n => n.Prefix.Equals(IPAddress.Parse("10.0.0.0")) && n.PrefixLength == 8));

        Assert.AreEqual(baselineLimit, options.ForwardLimit, "a malformed FORWARDED_LIMIT leaves ForwardLimit at its default");
    }

    [Test]
    public void BuildForwardedHeadersOptions_KeepsXForwardedForAndProto()
    {
        // The env-configurable path must not regress the base behavior: XFF + XFProto stay enabled.
        var options = Startup.BuildForwardedHeadersOptions();

        Assert.IsTrue(options.ForwardedHeaders.HasFlag(Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor));
        Assert.IsTrue(options.ForwardedHeaders.HasFlag(Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto));
    }
}

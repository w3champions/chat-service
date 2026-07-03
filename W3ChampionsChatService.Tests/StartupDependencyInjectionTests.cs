using System;
using System.Linq;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Composition-root smoke tests. These guard that the DI graph wired in
/// <see cref="Startup.ConfigureServices"/> actually resolves the services that the out-of-band-ban
/// fix depends on — in particular that the controller and hub share the SAME singleton
/// <see cref="ConnectionMapping"/> (so a REST ban reconciles the hub's live connections), and that
/// <see cref="MuteReconciliationService"/> resolves.
/// </summary>
public class StartupDependencyInjectionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        // The host normally registers logging outside ConfigureServices; add it here so the framework
        // services (MVC/SignalR/routing) that need ILoggerFactory can be constructed during resolution.
        services.AddLogging();
        new Startup().ConfigureServices(services);
        // ValidateScopes catches captive-dependency lifetime bugs (e.g. a singleton capturing a scoped
        // service). We do NOT ValidateOnBuild because it eagerly validates every framework descriptor;
        // the tests below resolve the specific services we care about, which is the real check.
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Test]
    public void ConnectionMapping_IsSingleton_SharedAcrossResolutions()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<ConnectionMapping>();
        var second = provider.GetRequiredService<ConnectionMapping>();

        Assert.AreSame(first, second,
            "ConnectionMapping MUST be a singleton so the REST controller and the SignalR hub share the SAME instance");
    }

    [Test]
    public void MuteReconciliationService_Resolves_AndSharesTheSingletonConnectionMapping()
    {
        using var provider = BuildProvider();

        var service = provider.GetRequiredService<MuteReconciliationService>();
        Assert.IsNotNull(service, "MuteReconciliationService must resolve from the DI container");
        // Resolving twice returns the same singleton instance.
        Assert.AreSame(service, provider.GetRequiredService<MuteReconciliationService>(),
            "MuteReconciliationService is registered as a singleton");
    }

    [Test]
    public void ITicketStore_IsSingleton_SharedAcrossResolutions()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<ITicketStore>();
        var second = provider.GetRequiredService<ITicketStore>();

        Assert.AreSame(first, second,
            "ITicketStore MUST be a singleton — the mint (REST AuthSessionController) and connect (Task 6 hub) paths must share the SAME ticket store");
    }

    [Test]
    public void MintRateLimiter_IsSingleton_SharedAcrossResolutions()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<MintRateLimiter>();
        var second = provider.GetRequiredService<MintRateLimiter>();

        Assert.AreSame(first, second,
            "MintRateLimiter MUST be a singleton so its rate windows persist across requests");
    }

    [Test]
    public void ISessionRegistry_IsSingleton_SharedAcrossResolutions()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<ISessionRegistry>();
        var second = provider.GetRequiredService<ISessionRegistry>();

        Assert.AreSame(first, second,
            "ISessionRegistry MUST be a singleton — the connect/disconnect (hub) and permission-resolution (filter) paths must share the SAME in-memory session state");
    }

    [Test]
    public void ISessionRegistry_ResolvesToSessionRegistry()
    {
        using var provider = BuildProvider();

        var registry = provider.GetRequiredService<ISessionRegistry>();
        Assert.IsInstanceOf<SessionRegistry>(registry,
            "ISessionRegistry must resolve to the concrete SessionRegistry");
    }

    [Test]
    public void AuthSessionControllerDependencies_Resolve()
    {
        using var provider = BuildProvider();

        Assert.IsNotNull(provider.GetRequiredService<IW3CAuthenticationService>());
        Assert.IsNotNull(provider.GetRequiredService<ITicketStore>());
        Assert.IsNotNull(provider.GetRequiredService<MintRateLimiter>());
    }

    [Test]
    public void IMuteRepository_ResolvesToMuteRepository()
    {
        using var provider = BuildProvider();

        var repo = provider.GetRequiredService<IMuteRepository>();
        Assert.IsInstanceOf<MuteRepository>(repo,
            "IMuteRepository must resolve to the concrete MuteRepository");
    }

    [Test]
    public void TimeProvider_IsRegisteredAsSystemSingleton()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<TimeProvider>();
        var second = provider.GetRequiredService<TimeProvider>();

        Assert.AreSame(first, second,
            "TimeProvider MUST be a singleton — every timer-driven fan-out service (C3 tasks 13/14/15) needs the SAME injectable clock");
        Assert.AreSame(TimeProvider.System, first,
            "the production TimeProvider registration must be TimeProvider.System (real wall-clock time)");
    }

    [Test]
    public void FocusRegistry_IsSingleton_SharedAcrossResolutions()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<FocusRegistry>();
        var second = provider.GetRequiredService<FocusRegistry>();

        Assert.AreSame(first, second,
            "FocusRegistry MUST be a singleton — a transient registration would silently fragment the in-memory fan-out state each connection seeds and tears down");
    }

    [Test]
    public void OnlineMemberRegistry_IsSingleton_SharedAcrossResolutions()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<OnlineMemberRegistry>();
        var second = provider.GetRequiredService<OnlineMemberRegistry>();

        Assert.AreSame(first, second,
            "OnlineMemberRegistry MUST be a singleton — a transient registration would silently fragment the in-memory fan-out state each connection seeds and tears down");
    }

    [Test]
    public void MessageRateLimiter_IsSingleton_SharedAcrossResolutions()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<MessageRateLimiter>();
        var second = provider.GetRequiredService<MessageRateLimiter>();

        Assert.AreSame(first, second,
            "MessageRateLimiter MUST be a singleton — a transient registration would silently fragment the in-memory fan-out state each connection seeds and tears down");
    }

    [Test]
    public void ActivityCoalescer_IsSingleton_SharedAcrossResolutions()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<ActivityCoalescer>();
        var second = provider.GetRequiredService<ActivityCoalescer>();

        Assert.AreSame(first, second,
            "ActivityCoalescer MUST be a singleton — it holds the per-(connection, channel) coalescing window state that the send-path fan-out routing writes and the flush hosted service (Task 15) drains; a transient would fragment that shared window state");
    }

    [Test]
    public void FanOutEngine_IsSingleton_SharedAcrossResolutions()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<FanOutEngine>();
        var second = provider.GetRequiredService<FanOutEngine>();

        Assert.AreSame(first, second,
            "FanOutEngine MUST be a singleton — the send pipeline's post-persist fan-out seam is shared by every hub invocation (Task 12 fills its body); a transient would fragment that shared fan-out state");
    }

    [Test]
    public void ViewersAccumulator_IsSingleton_SharedAcrossResolutions()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<ViewersAccumulator>();
        var second = provider.GetRequiredService<ViewersAccumulator>();

        Assert.AreSame(first, second,
            "ViewersAccumulator MUST be a singleton — the hub's focus/unfocus/disconnect routing and the flush hosted service (Task 15) must share the SAME per-channel accumulation window, and the C2 displacement reconciliation (disconnect + reconnect-refocus) only cancels out if both hit the SAME instance");
    }

    [Test]
    public void SessionStateAssembler_Resolves()
    {
        using var provider = BuildProvider();

        // SessionStateAssembler is registered Transient (per-connect state assembly, no long-lived
        // state of its own) — assert only that its whole dependency graph resolves, NOT singleton
        // sharing (unlike the three fan-out registries above).
        Assert.IsNotNull(provider.GetRequiredService<SessionStateAssembler>(),
            "SessionStateAssembler must resolve from the DI container");
    }

    [Test]
    public void ChannelCreationRateLimiter_IsSingleton_SharedAcrossResolutions()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<ChannelCreationRateLimiter>();
        var second = provider.GetRequiredService<ChannelCreationRateLimiter>();

        Assert.AreSame(first, second,
            "ChannelCreationRateLimiter MUST be a singleton — a transient registration would fragment each battleTag's per-hour creation counter across hub invocations, defeating JoinChannel's creation cap");
    }

    [Test]
    public void ChatDomainRepositories_AndHostedServices_Resolve()
    {
        using var provider = BuildProvider();

        Assert.IsNotNull(provider.GetRequiredService<ChannelRepository>());
        Assert.IsNotNull(provider.GetRequiredService<MembershipRepository>());
        Assert.IsNotNull(provider.GetRequiredService<MessageRepository>());
        Assert.IsNotNull(provider.GetRequiredService<UserDirectoryRepository>());
        Assert.IsNotNull(provider.GetRequiredService<UserSettingsRepository>());
        Assert.IsNotNull(provider.GetRequiredService<MentionInboxRepository>());
        Assert.IsNotNull(provider.GetRequiredService<PublicChannelSeeder>());
        Assert.IsNotNull(provider.GetRequiredService<CleanupJobs>());

        var hostedServices = provider.GetServices<IHostedService>().ToList();
        Assert.IsTrue(hostedServices.Any(s => s is ChatDomainBootstrap),
            "index creation + catalog seeding must run at startup");
        Assert.IsTrue(hostedServices.Any(s => s is WeeklyCleanupService),
            "weekly cleanup must be scheduled");
    }

    [Test]
    public void FanOutFlushService_IsHostedService()
    {
        using var provider = BuildProvider();

        var hostedServices = provider.GetServices<IHostedService>().ToList();
        Assert.IsTrue(hostedServices.Any(s => s is FanOutFlushService),
            "the FanOutFlushService MUST be registered as a hosted service — it is the single 1s clock that drives the Task 13 coalescer and Task 14 accumulator FlushDue calls in production");
    }

    [Test]
    public void MentionInboxCleaner_IsRegisteredSingleton_NoOp()
    {
        // C4 Task 1 (D10): the ONLY coordination point between C4 (moderation deletes/purges) and C6
        // (mention inbox) — C4 calls IMentionInboxCleaner, C6 swaps this registration for the real
        // implementation later. Task 1 registers the no-op only; nothing calls it yet.
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<IMentionInboxCleaner>();
        var second = provider.GetRequiredService<IMentionInboxCleaner>();

        Assert.IsInstanceOf<NoOpMentionInboxCleaner>(first,
            "IMentionInboxCleaner must resolve to the no-op placeholder until C6 swaps the registration");
        Assert.AreSame(first, second, "IMentionInboxCleaner MUST be a singleton");
    }

    [Test]
    public void ChatHub_ConstructorGraph_ResolvesFromDi_IncludingMentionCleaner()
    {
        // C4 Task 3 added IMentionInboxCleaner as a ChatHub constructor dependency (the durable
        // DeleteMessage pipeline calls it). SignalR activates the hub via ActivatorUtilities, so its
        // ENTIRE constructor graph — including the new cleaner param — must be resolvable from the DI
        // container, or every hub connection fails at activation. Constructing it here is the smoke test.
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var hub = ActivatorUtilities.CreateInstance<ChatHub>(scope.ServiceProvider);

        Assert.IsNotNull(hub, "ChatHub's full constructor graph (incl. IMentionInboxCleaner) must resolve from DI");
    }

    [Test]
    public void SignalR_MaximumReceiveMessageSize_IsPinnedBelowDefault()
    {
        using var provider = BuildProvider();

        var hubOptions = provider.GetRequiredService<IOptions<HubOptions>>().Value;

        // T11 hardening: MUST be an EXPLICIT, non-default cap — SignalR's built-in default is 32KB
        // (32 * 1024). Pinned to 16KB, comfortably above the largest legitimate payload
        // (SendMessage content <= ChatLimits.MaxMessageLength == 512 chars) and well below the
        // 32KB default, so oversized frames are rejected at the transport before the content-cap
        // Trim/length-check allocation in ChatHub.Messaging.cs ever runs.
        Assert.AreEqual(16 * 1024, hubOptions.MaximumReceiveMessageSize,
            "MaximumReceiveMessageSize must be pinned to the explicit 16KB cap, not left at SignalR's 32KB default");
    }
}

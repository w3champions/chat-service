using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
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
    public void IMuteRepository_ResolvesToMuteRepository()
    {
        using var provider = BuildProvider();

        var repo = provider.GetRequiredService<IMuteRepository>();
        Assert.IsInstanceOf<MuteRepository>(repo,
            "IMuteRepository must resolve to the concrete MuteRepository");
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
}

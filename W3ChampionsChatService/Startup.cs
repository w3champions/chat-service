using System;
using Serilog;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Settings;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        Log.Information("Adding services");
        services.AddControllers();

        var mongoConnectionString = Environment.GetEnvironmentVariable("MONGO_CONNECTION_STRING") ?? "mongodb://157.90.1.251:3513";
        var mongoClient = new MongoClient(mongoConnectionString.Replace("'", ""));
        services.AddSingleton(mongoClient);

        // SECURITY: the hub permission filter enforces Moderation on the moderator-only hub methods.
        // The MVC [UserHasPermission] attribute is inert on SignalR, so this filter is the real gate.
        services.AddSignalR(options => { options.AddFilter<ChatHubPermissionFilter>(); });

        services.AddTransient<SettingsRepository>();
        services.AddTransient<IChatAuthenticationService, ChatAuthenticationService>();
        services.AddTransient<IW3CAuthenticationService, W3CAuthenticationService>();
        services.AddTransient<IWebsiteBackendRepository, WebsiteBackendRepository>();
        services.AddTransient<IMuteRepository, MuteRepository>();
        services.AddTransient<UserHasPermissionFilter>();
        services.AddTransient<ChatHubPermissionFilter>();

        // C1 chat domain foundation (additive — old hub keeps running on ChatHistory)
        services.AddTransient<ChannelRepository>();
        services.AddTransient<MembershipRepository>();
        services.AddTransient<MessageRepository>();
        services.AddTransient<UserDirectoryRepository>();
        services.AddTransient<UserSettingsRepository>();
        services.AddTransient<MentionInboxRepository>();
        services.AddTransient<PublicChannelSeeder>();
        services.AddTransient<CleanupJobs>();
        services.AddHostedService<ChatDomainBootstrap>();   // indexes + catalog seeding at boot
        services.AddHostedService<WeeklyCleanupService>();  // weekly GC + membership pruning

        // C2 auth v2
        // Singletons: both hold in-memory state that the mint (REST AuthSessionController) and
        // connect (Task 6 hub) paths must share — a per-request/transient instance would silently
        // break the mint→connect handoff and the rate windows.
        services.AddSingleton<ITicketStore, TicketStore>();
        services.AddSingleton<MintRateLimiter>();
        // Authoritative battleTag→connection map (one active connection per battleTag). Singleton:
        // the connect/disconnect (Task 6 hub) and permission-resolution (Task 7 filter) paths must
        // share the SAME in-memory session state.
        services.AddSingleton<ISessionRegistry, SessionRegistry>();

        // C3 hub protocol core
        // Singleton: the injectable-clock foundation for every timer-driven fan-out service (tasks
        // 13, 14, 15) — tests substitute Microsoft.Extensions.Time.Testing.FakeTimeProvider so those
        // services never depend on real wall-clock delays.
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<ConnectionMapping>();
        services.AddSingleton<ChatHistory>();
        // Reconciles the live mute cache from every ban WRITE path (hub + REST controller).
        // Singleton: it only holds the singleton ConnectionMapping + IHubContext<ChatHub>.
        services.AddSingleton<MuteReconciliationService>();
        Log.Information("Services added");
    }

    public void Configure(IApplicationBuilder app)
    {
        Log.Information("Configuring service");
        // without that, nginx forwarding in docker wont work
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        });
        app.UseRouting();
        app.UseCors(builder =>
            builder
                .AllowAnyHeader()
                .AllowAnyMethod()
                .SetIsOriginAllowed(_ => true)
                .AllowCredentials());

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
            endpoints.MapHub<ChatHub>("/chatHub");
        });
        Log.Information("Chat Service started");
    }
}

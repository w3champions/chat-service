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
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;
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
        services.AddSignalR(options =>
        {
            options.AddFilter<ChatHubPermissionFilter>();
            // T11 hardening: pin an EXPLICIT receive-size cap well below SignalR's 32KB default.
            // The largest legitimate client→server payload is SendMessage(channelId, content) with
            // content <= ChatLimits.MaxMessageLength (512 chars); every other hub arg (channelIds,
            // battleTags, seqs, notification levels) is tiny by comparison. 16KB gives generous
            // headroom over a 512-char message even at multi-byte UTF-8 (up to 4 bytes/char) plus
            // JSON/MessagePack framing overhead, while rejecting oversized frames at the SignalR
            // transport BEFORE they reach the content-cap Trim/length-check allocation in
            // ChatHub.Messaging.cs — defense-in-depth against a client sending an oversized frame to
            // force needless buffering/allocation ahead of the app-level validation.
            options.MaximumReceiveMessageSize = 16 * 1024;
        });

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

        // Task 8 hub deps. The assembler is per-connect state assembly (no long-lived state of its
        // own) → Transient. The three fan-out registries hold shared in-memory state that the connect
        // path seeds and the disconnect path (plus later Join/Leave/Focus/rate-limit paths) mutate, so
        // every hub invocation MUST share the SAME instance → Singleton, exactly like the C2
        // ConnectionMapping/SessionRegistry above; a transient would silently fragment fan-out state.
        // Task 15 owns the REMAINING fan-out DI (the flush hosted service) + its DI-coverage tests — do
        // NOT re-register the singletons below (or the FanOutEngine / ViewersAccumulator) there. NOTE:
        // the ActivityCoalescer (Task 13) and the ViewersAccumulator (Task 14) are registered HERE, not
        // in Task 15, because they are each a constructor dependency of an already-registered consumer
        // (FanOutEngine takes the coalescer for activity routing; ChatHub takes the accumulator for
        // focus/unfocus/disconnect viewer-roster routing) — a dependency of an already-resolvable
        // consumer MUST itself be DI-resolvable now or that consumer's resolution fails. Task 15 wires
        // both into the flush hosted service; it does not re-register them.
        services.AddTransient<SessionStateAssembler>();
        services.AddSingleton<FocusRegistry>();
        services.AddSingleton<OnlineMemberRegistry>();
        services.AddSingleton<MessageRateLimiter>();

        // Task 13: the coalescing/suppressing sink for unfocused level-All ChannelActivity. Singleton —
        // it holds the per-(connection, channel) coalescing window state that the fan-out routing (every
        // send) writes and the flush hosted service (Task 15) drains; a transient would fragment it.
        services.AddSingleton<ActivityCoalescer>();

        // Task 14: the batched ViewersChanged sink. Singleton — it holds the per-channel accumulation
        // window (changed battleTags + their pre-window viewing baseline) that the hub's focus/unfocus/
        // disconnect routing writes and the flush hosted service (Task 15) drains ≤ every 5s; a transient
        // would fragment that shared window state AND break the C2 displacement reconciliation (the
        // disconnect and the reconnect-refocus must hit the SAME accumulator instance to cancel out).
        services.AddSingleton<ViewersAccumulator>();

        // Task 11: the send pipeline's post-persist fan-out seam. Singleton — it holds no per-call
        // state and is shared by every hub invocation (Task 12 focused delivery + Task 13 activity
        // routing; the flush machinery + the sibling accumulator singleton stay Task 15's).
        services.AddSingleton<FanOutEngine>();

        // Task 15: the single production driver behind the Task 13/14 aggregators. Hosted service — its
        // 1s PeriodicTimer is the ONLY thing that calls FlushDue in production, draining the coalescer
        // (10s) and the accumulator (5s) on the injected TimeProvider clock. Without it the pure,
        // deterministic-time sinks above would never fire outside tests.
        services.AddHostedService<FanOutFlushService>();

        // Task 10: JoinChannel's implicit-semiPublic-creation throttle. Singleton — a transient
        // registration would fragment each battleTag's per-hour creation counter across hub
        // invocations, defeating the cap. A SEPARATE singleton from MintRateLimiter, not a second
        // instance of it (see FanOut/ChannelCreationRateLimiter.cs doc comment).
        services.AddSingleton<ChannelCreationRateLimiter>();

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

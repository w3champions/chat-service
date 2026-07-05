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
using W3ChampionsChatService.Internal;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Relationships;
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

        // D9 (C6 Task 3): registers IHttpClientFactory — WebsiteBackendRepository is rebuilt on it,
        // killing the per-call `new HttpClient()` socket-exhaustion anti-pattern (a fresh HttpClient
        // allocates its own handler/socket pool every call; the factory pools handlers across calls).
        services.AddHttpClient();

        services.AddTransient<IChatAuthenticationService, ChatAuthenticationService>();
        services.AddTransient<IW3CAuthenticationService, W3CAuthenticationService>();
        services.AddTransient<IWebsiteBackendRepository, WebsiteBackendRepository>();
        services.AddTransient<IMuteRepository, MuteRepository>();
        services.AddTransient<UserHasPermissionFilter>();
        services.AddTransient<ChatHubPermissionFilter>();
        // C7 Task 3: the /internal/* HMAC auth-realm boundary. Transient (mirrors the two filters above)
        // — InternalHmacAuthAttribute (an IFilterFactory) resolves a fresh instance per request and stamps
        // its per-endpoint caller allow-list before the filter runs. Its deps (InternalCallerSecrets,
        // TimeProvider) are the singletons registered below / at line ~101.
        services.AddTransient<InternalHmacAuthFilter>();

        // C1 chat domain foundation
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

        // C4 Task 1 (D10) / C6 Task 7: the ONLY coordination point between moderation deletes/purges
        // (ChatHub.DeleteMessage/PurgeMessagesFromUser) and the mention inbox (C6). Registered as the
        // real MentionInboxCleaner (a straightforward mention_inbox DeleteMany) — C4's NoOpMentionInboxCleaner
        // placeholder is kept around only for tests that don't care about mention-inbox behavior and
        // construct a hub directly with it; it is no longer the production registration. Singleton:
        // holds no state, matches the sibling repository registrations' lifetime.
        services.AddSingleton<IMentionInboxCleaner, MentionInboxCleaner>();

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

        // C6 (Task 5, D4/D15): the mention fan-out seam. Singleton — it holds no per-call state and is
        // shared by every hub invocation (mirrors FanOutEngine); it pushes MentionNotified through its own
        // IHubContext<ChatHub> and reads/writes the durable membership + mention-inbox stores.
        services.AddSingleton<MentionFanOut>();

        // C6 (Task 5, D11/D15): the presence-interest index. Registered NOW (with the ctor growth) so
        // ChatHub resolves; its FIRST consumer is Task 9. Singleton — it will hold the shared in-memory
        // presence-interest state (connection focus → watched members) every hub invocation mutates; a
        // transient would fragment it exactly like the other fan-out registries above.
        services.AddSingleton<PresenceInterestRegistry>();

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
        // Reconciles the live mute cache from every ban WRITE path (hub + REST controller).
        // Singleton: it only holds the singleton ConnectionMapping + IHubContext<ChatHub>.
        services.AddSingleton<MuteReconciliationService>();

        // C5 (Task 1, D1/D2/D19): the relationship (friends/blocked) provider + its swappable read source.
        // The provider is a SINGLETON — it holds the in-memory friends/blocked cache that every hub
        // invocation (block/friend gates in later C5 tasks, the connect-time warm prefetch, and C7's
        // Invalidate change-ping) must share; a transient would fragment the cache and defeat both the TTL
        // and the last-known fallback. The source is transient (the singleton provider captures one
        // instance for its lifetime; WebsiteBackendRelationshipSource is stateless behind a shared static
        // HttpClient). The wb route the source targets does not exist yet (W2) — until it lands,
        // relationship-gated paths fail closed retriable.
        services.AddSingleton<IRelationshipProvider, RelationshipProvider>();
        services.AddTransient<IRelationshipSource, WebsiteBackendRelationshipSource>();

        // C5 (Task 3, D7/D19): the in-memory stranger-DM initiation cap tracker. Singleton — it holds the
        // per-initiator 8h event windows that every OpenDm invocation reads/writes and the accept
        // transitions (T4/T6) free; a transient would fragment each initiator's counter across hub
        // invocations, defeating the cap (the ChannelCreationRateLimiter singleton rationale).
        services.AddSingleton<DmInitiationTracker>();

        // C7 Task 1 (brief Design decision 2): the env-only per-caller HMAC secret surface for the
        // /internal/* REST endpoints (later C7 tasks). Deliberately NO `?? fallback` unlike the
        // mongoConnectionString read above — an unset secret must disable that caller, never
        // silently become an empty/guessable one. Singleton: it is immutable, read-only config
        // resolved once at startup and shared by every request through the later HMAC filter.
        var internalSecretMm = Environment.GetEnvironmentVariable("INTERNAL_SECRET_MM");
        if (string.IsNullOrWhiteSpace(internalSecretMm))
        {
            Log.Warning("INTERNAL_SECRET_MM is not set — the mm caller of the /internal/* API is disabled");
        }

        var internalSecretWb = Environment.GetEnvironmentVariable("INTERNAL_SECRET_WB");
        if (string.IsNullOrWhiteSpace(internalSecretWb))
        {
            Log.Warning("INTERNAL_SECRET_WB is not set — the wb caller of the /internal/* API is disabled");
        }

        services.AddSingleton(new InternalCallerSecrets(internalSecretMm, internalSecretWb));

        // C7 Task 6: the match-channel domain core (idempotent CreateOrGet + the one-match-channel-per-user
        // AddMemberWithInvariant) the later /internal/* match endpoints drive. Singleton: it holds no per-call
        // state. Its ChannelRepository/MembershipRepository/MessageRepository deps are themselves registered
        // TRANSIENT, so this singleton captures them as a captive dependency — safe ONLY because all three are
        // stateless MongoClient wrappers with no per-call state of their own to leak across calls.
        services.AddSingleton<MatchChannelService>();

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

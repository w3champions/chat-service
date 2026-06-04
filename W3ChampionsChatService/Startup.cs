using System;
using Serilog;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Settings;

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
        services.AddHttpContextAccessor();

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

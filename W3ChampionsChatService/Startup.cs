using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Bans;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Settings;

namespace W3ChampionsChatService
{
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();

            var mongoConnectionString = Environment.GetEnvironmentVariable("MONGO_CONNECTION_STRING") ?? "mongodb://176.28.16.249:3513";
            var mongoClient = new MongoClient(mongoConnectionString.Replace("'", ""));
            services.AddSingleton(mongoClient);

            services.AddSignalR();

            services.AddTransient<SettingsRepository>();
            services.AddTransient<IChatAuthenticationService, ChatAuthenticationService>();
            services.AddTransient<IW3CAuthenticationService, W3CAuthenticationService>();
            services.AddTransient<BanRepository>();
            services.AddTransient<CheckIfBattleTagIsAdminFilter>();

            services.AddSingleton<ConnectionMapping>();
            services.AddSingleton<ChatHistory>();
            services.AddSingleton<TokenCache>();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
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
        }
    }
}
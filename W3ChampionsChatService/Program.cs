using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace W3ChampionsChatService;

public class Program
{
    public static void Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning) // Filter out verbose HTTP client logs
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning) // Filter out verbose System.Net.Http logs
            .WriteTo.Console(new JsonFormatter(), restrictedToMinimumLevel: LogEventLevel.Information) // Write to Console to allow log scraping
            .CreateLogger();
        try
        {
            Log.Information("Starting Chat Service");
            CreateHostBuilder(args).Build().Run();
        }
        catch (Exception e)
        {
            // Emit startup failures (e.g. missing MONGO_CONNECTION_STRING) as structured JSON before exiting.
            Log.Fatal(e, "Host terminated unexpectedly");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseSerilog() // Tell the AspNetCore host to use Serilog for all logging
            .ConfigureWebHostDefaults(webBuilder => { webBuilder.UseStartup<Startup>(); });
}

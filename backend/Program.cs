using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Auth;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Middlewares;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Tools;
using NzbWebDAV.WebDav;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.Websocket;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace NzbWebDAV;

class Program
{
    static async Task Main(string[] args)
    {
        // Update thread-pool
        var coreCount = Environment.ProcessorCount;
        var minThreads = Math.Max(coreCount * 2, 50); // 2x cores, minimum 50
        var maxThreads = Math.Max(coreCount * 50, 1000); // 50x cores, minimum 1000
        ThreadPool.SetMinThreads(minThreads, minThreads);
        ThreadPool.SetMaxThreads(maxThreads, maxThreads);

        // Initialize logger
        var defaultLevel = LogEventLevel.Information;
        var envLevel = Environment.GetEnvironmentVariable("LOG_LEVEL");
        var level = Enum.TryParse<LogEventLevel>(envLevel, true, out var parsed) ? parsed : defaultLevel;
        var levelSwitch = new LoggingLevelSwitch(level);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .MinimumLevel.Override("NWebDAV", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Mvc", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Routing", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.DataProtection", LogEventLevel.Error)
            .WriteTo.Console(theme: AnsiConsoleTheme.Code)
            .WriteTo.Sink(InMemoryLogSink.Instance)
            .CreateLogger();

        // Log build version to verify correct build is running
        Log.Warning("═══════════════════════════════════════════════════════════════");
        Log.Warning("  NzbDav Backend Starting - BUILD v2025-12-04-QUEUE-REORDER");
        Log.Warning("  FEATURE: Queue items can now be reordered via UI (move to top/bottom)");
        Log.Warning("═══════════════════════════════════════════════════════════════");

        // Run Arr History Tester if requested
        if (args.Contains("--test-arr-history"))
        {
            await ArrHistoryTester.RunAsync(args).ConfigureAwait(false);
            return;
        }

        // Run repair simulation if requested
        if (args.Contains("--simulate-repair"))
        {
            await RepairSimulation.RunAsync().ConfigureAwait(false);
            return;
        }

        // initialize database
        await using var databaseContext = new DavDatabaseContext();

        // run database migration, if necessary.
        if (args.Contains("--db-migration"))
        {
            var argIndex = args.ToList().IndexOf("--db-migration");
            var targetMigration = args.Length > argIndex + 1 ? args[argIndex + 1] : null;
            await databaseContext.Database.MigrateAsync(targetMigration, SigtermUtil.GetCancellationToken()).ConfigureAwait(false);
            return;
        }

        // initialize the config-manager
        var configManager = new ConfigManager();
        await configManager.LoadConfig().ConfigureAwait(false);

        // Sync log level from config
        var configLevel = configManager.GetLogLevel();
        if (configLevel != null) levelSwitch.MinimumLevel = configLevel.Value;

        // Update log level on config change
        configManager.OnConfigChanged += (_, eventArgs) =>
        {
            if (eventArgs.NewConfig.TryGetValue("general.log-level", out var val)
                && Enum.TryParse<LogEventLevel>(val, true, out var newLevel))
            {
                levelSwitch.MinimumLevel = newLevel;
                Log.Information($"Log level updated to {newLevel}");
            }
        };

        // initialize websocket-manager
        var websocketManager = new WebsocketManager();

        // initialize webapp
        var builder = WebApplication.CreateBuilder(args);
        var maxRequestBodySize = EnvironmentUtil.GetLongVariable("MAX_REQUEST_BODY_SIZE") ?? 100 * 1024 * 1024;
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxRequestBodySize);
        builder.Host.UseSerilog();
        builder.Services.AddControllers();
        builder.Services.AddHealthChecks();
        builder.Services
            .AddWebdavBasicAuthentication(configManager)
            .AddSingleton(configManager)
            .AddSingleton(websocketManager)
            .AddSingleton<BandwidthService>()
            .AddSingleton<ProviderErrorService>()
            .AddSingleton<UsenetStreamingClient>()
            .AddSingleton<QueueManager>()
            .AddSingleton<ArrMonitoringService>()
            .AddSingleton<HealthCheckService>()
            .AddScoped<DavDatabaseContext>()
            .AddScoped<DavDatabaseClient>()
            .AddScoped<DatabaseStore>()
            .AddScoped<IStore, DatabaseStore>()
            .AddScoped<GetAndHeadHandlerPatch>()
            .AddScoped<SabApiController>()
            .AddNWebDav(opts =>
            {
                opts.Handlers["GET"] = typeof(GetAndHeadHandlerPatch);
                opts.Handlers["HEAD"] = typeof(GetAndHeadHandlerPatch);
                opts.Filter = opts.GetFilter();
                opts.RequireAuthentication = !WebApplicationAuthExtensions
                    .IsWebdavAuthDisabled();
            });

        // force instantiation of services
        var app = builder.Build();
        app.Services.GetRequiredService<ArrMonitoringService>();
        app.Services.GetRequiredService<HealthCheckService>();
        app.Services.GetRequiredService<BandwidthService>();

        // Backfill JobNames for missing article events (Background, delayed)
        _ = Task.Run(async () =>
        {
            // Wait for 10 seconds to allow application to start
            await Task.Delay(TimeSpan.FromSeconds(10), app.Lifetime.ApplicationStopping);
            
            var providerErrorService = app.Services.GetRequiredService<ProviderErrorService>();
            
            // Critical for UI performance
            await providerErrorService
                .BackfillSummariesAsync(app.Lifetime.ApplicationStopping);

            await providerErrorService
                .BackfillDavItemIdsAsync(app.Lifetime.ApplicationStopping);

            await providerErrorService
                .CleanupOrphanedErrorsAsync(app.Lifetime.ApplicationStopping);

            // Start the OrganizedLinksUtil refresh service after initial setup
            OrganizedLinksUtil.StartRefreshService(app.Services, app.Services.GetRequiredService<ConfigManager>(), app.Lifetime.ApplicationStopping);

            // Initial call to InitializeAsync is part of the refresh service now,
            // so we don't need a separate call here. The refresh service will trigger it.
        }, app.Lifetime.ApplicationStopping);

        // run
        app.UseMiddleware<ExceptionMiddleware>();
        // ReservedConnectionsMiddleware removed - using GlobalOperationLimiter instead
        app.UseWebSockets();
        app.MapHealthChecks("/health");
        app.Map("/ws", websocketManager.HandleRoute);
        app.MapControllers();
        app.UseWebdavBasicAuthentication();
        app.UseNWebDav();
        app.Lifetime.ApplicationStopping.Register(SigtermUtil.Cancel);
        await app.RunAsync().ConfigureAwait(false);
    }
}
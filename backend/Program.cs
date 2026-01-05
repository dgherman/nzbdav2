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
        Log.Warning("  NzbDav Backend Starting - BUILD v2026-01-05-CORRUPTION-RECOVERY");
        Log.Warning("  FEATURE: Smart Corruption Recovery with Graceful Degradation");
        Log.Warning("  - YENC CRC32 validation on all segments");
        Log.Warning("  - Automatic retry with exponential backoff (3 attempts)");
        Log.Warning("  - Graceful degradation: zero-filled segments on failure");
        Log.Warning("  - Corruption tracking for health check triggering");
        Log.Warning("  - Stream continues playing with minor glitches vs total failure");
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
            Log.Warning("Starting database migration with PRAGMA optimizations...");

            // Apply PRAGMA optimizations for faster migrations (5-10x speedup)
            Log.Warning("  → Applying PRAGMA journal_mode = WAL");
            await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;").ConfigureAwait(false);

            Log.Warning("  → Applying PRAGMA synchronous = NORMAL");
            await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA synchronous = NORMAL;").ConfigureAwait(false);

            Log.Warning("  → Applying PRAGMA cache_size = -64000 (64MB cache)");
            await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA cache_size = -64000;").ConfigureAwait(false);

            Log.Warning("  → Applying PRAGMA temp_store = MEMORY");
            await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA temp_store = MEMORY;").ConfigureAwait(false);

            Log.Warning("  → Applying PRAGMA mmap_size = 268435456 (256MB)");
            await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA mmap_size = 268435456;").ConfigureAwait(false);

            // Clear stale migration locks from previous failed attempts
            Log.Warning("  → Clearing any stale migration locks...");
            await databaseContext.Database.ExecuteSqlRawAsync("DELETE FROM __EFMigrationsLock WHERE 1=1;").ConfigureAwait(false);

            Log.Warning("  → Running migrations...");
            var argIndex = args.ToList().IndexOf("--db-migration");
            var targetMigration = args.Length > argIndex + 1 ? args[argIndex + 1] : null;
            await databaseContext.Database.MigrateAsync(targetMigration).ConfigureAwait(false);

            Log.Warning("Database migration finished successfully!");
            return;
        }

        // Apply runtime database optimizations for better query performance
        Log.Debug("Applying database runtime optimizations...");
        await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;").ConfigureAwait(false);
        await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA synchronous = NORMAL;").ConfigureAwait(false);
        await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA cache_size = -64000;").ConfigureAwait(false);
        await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA temp_store = MEMORY;").ConfigureAwait(false);
        await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA mmap_size = 268435456;").ConfigureAwait(false);
        await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout = 5000;").ConfigureAwait(false);

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
            .AddSingleton<NzbProviderAffinityService>()
            .AddSingleton<ProviderErrorService>()
            .AddSingleton<UsenetStreamingClient>()
            .AddSingleton<QueueManager>()
            .AddSingleton<ArrMonitoringService>()
            .AddSingleton<HealthCheckService>()
            .AddSingleton<NzbAnalysisService>()
            .AddHostedService<DatabaseMaintenanceService>()
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
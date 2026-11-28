using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Mcp;
using TradeFoundry.Services;
using TradeFoundry.Services.Ai;
using TradeFoundry.Services.Backup;

var desktopMode = DesktopRuntime.IsDesktopMode(args);
var embeddedMode = DesktopRuntime.IsEmbeddedMode(args);
var applicationArgs = DesktopRuntime.RemoveLaunchSwitches(args);
var desktopUrl = DesktopRuntime.Url;
Mutex? desktopMutex = null;

if (desktopMode)
{
    desktopMutex = new Mutex(initiallyOwned: true, DesktopRuntime.InstanceName(DesktopRuntime.MutexName, desktopUrl), out var createdNew);
    if (!createdNew)
    {
        if (!embeddedMode)
        {
            DesktopRuntime.TryOpenBrowser(desktopUrl);
        }

        desktopMutex.Dispose();
        return;
    }
}

var hostRestart = new HostRestart();
var runtimeInfo = new HostRuntimeInfo(desktopMode);
WebApplication? currentApp = null;
string? lastParentCommand = null;
var parentGone = false;

if (embeddedMode)
{
    // The tray helper owns this process: stop gracefully when it asks, or when it goes away.
    // Meanwhile it reports available updates, which the layout shows as a notification. The reader
    // outlives each host, so a restart after a restore keeps listening.
    _ = DesktopRuntime.ReadParentCommandsAsync(Console.In, command =>
        {
            if (string.Equals(command, DesktopRuntime.BackupNowCommand, StringComparison.OrdinalIgnoreCase))
            {
                currentApp?.Services.GetRequiredService<BackupService>().TryStart(BackupTrigger.Manual, out _, out _);
                return;
            }

            lastParentCommand = command;
            currentApp?.Services.GetRequiredService<DesktopUpdateNotice>().Apply(command);
        })
        .ContinueWith(_ =>
        {
            parentGone = true;
            currentApp?.Lifetime.StopApplication();
        }, TaskScheduler.Default);
}

try
{
    var firstStart = true;
    while (true)
    {
        var app = BuildApplication(firstStart);
        // A restore, start clean or put back runs here, before anything has opened the database.
        // Resolving TradeFoundryDb here would open the database, so the paths come from the options.
        var storage = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value;
        PendingDataOperation.Apply(
            TradeFoundryDb.ResolveDataDirectory(storage),
            TradeFoundryDb.ResolveDatabaseFileName(storage),
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TradeFoundry.Restore"));
        currentApp = app;
        if (lastParentCommand is not null) app.Services.GetRequiredService<DesktopUpdateNotice>().Apply(lastParentCommand);
        if (parentGone) break;
        await app.RunAsync();
        currentApp = null;
        await app.DisposeAsync();
        if (parentGone || !hostRestart.TryConsume()) break;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        firstStart = false;
    }
}
finally
{
    desktopMutex?.Dispose();
}

WebApplication BuildApplication(bool firstStart)
{
    var builder = desktopMode
        ? WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = applicationArgs,
            ContentRootPath = AppContext.BaseDirectory
        })
        : WebApplication.CreateBuilder(applicationArgs);

    if (desktopMode)
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DataDirectory"] = DesktopRuntime.DefaultDataDirectory,
            ["Backup:DefaultFolder"] = DesktopRuntime.DefaultBackupDirectory
        });
        builder.Configuration.AddJsonFile(DesktopRuntime.UserConfigurationPath, optional: true, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(applicationArgs);
        // Added last so no user setting can widen it: desktop mode answers only to loopback names.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = DesktopRuntime.AllowedHostsFor(desktopUrl)
        });
        builder.WebHost.UseUrls(desktopUrl);
    }

    var dataProtectionKeysDirectory = builder.Configuration["DataProtection:KeysDirectory"];
    if (!string.IsNullOrWhiteSpace(dataProtectionKeysDirectory))
    {
        var fullKeysDirectory = Path.GetFullPath(dataProtectionKeysDirectory);
        Directory.CreateDirectory(fullKeysDirectory);
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(fullKeysDirectory));
    }

    builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
    builder.Services.Configure<BenchmarkOptions>(builder.Configuration.GetSection("Benchmark"));
    builder.Services.Configure<McpOptions>(builder.Configuration.GetSection("Mcp"));
    builder.Services.Configure<AiOptions>(builder.Configuration.GetSection("Ai"));
    builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection("Backup"));
    builder.Services.AddSingleton<TradeFoundryDb>();
    builder.Services.AddSingleton<AppActivityGate>();
    builder.Services.AddSingleton<BackupSecrets>();
    builder.Services.AddSingleton<IBackupCloudFactory, B2CloudFactory>();
    builder.Services.AddSingleton<BackupService>();
    builder.Services.AddHostedService(services => services.GetRequiredService<BackupService>());
    builder.Services.AddSingleton(hostRestart);
    builder.Services.AddSingleton(runtimeInfo);
    builder.Services.AddSingleton(new HostInstance(Guid.NewGuid().ToString("N")));
    builder.Services.AddSingleton<SessionEpoch>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<BackupScheduler>();
    builder.Services.AddHostedService(services => services.GetRequiredService<BackupScheduler>());
    builder.Services.AddSingleton<RestoreService>();
    builder.Services.AddHostedService(services => services.GetRequiredService<RestoreService>());
    builder.Services.AddSingleton<ImportService>();
    builder.Services.AddSingleton<ImportNotificationFeed>();
    builder.Services.AddSingleton<ImportFolderMonitor>();
    builder.Services.AddSingleton<MarketDayFeatureCacheService>();
    builder.Services.AddSingleton<MarketProbabilityService>(services => new MarketProbabilityService(
        services.GetRequiredService<TradeFoundryDb>(),
        services.GetRequiredService<MarketDayFeatureCacheService>()));
    builder.Services.AddSingleton<TradingSetupService>();
    builder.Services.AddSingleton<PatternDiscoveryWorker>();
    builder.Services.AddHostedService(services => services.GetRequiredService<PatternDiscoveryWorker>());
    builder.Services.AddSingleton<McpTokenService>();
    builder.Services.AddSingleton<JournalAnalysisService>();
    builder.Services.AddSingleton<OrderExecutionSummaryService>();
    builder.Services.AddSingleton<TradeReviewService>(services => new TradeReviewService(
        services.GetRequiredService<TradeFoundryDb>(),
        services.GetRequiredService<TradingSetupService>()));
    builder.Services.AddSingleton<DailyJournalImageService>();
    builder.Services.AddSingleton<AccountLedgerService>();
    builder.Services.AddSingleton<PublicationExportService>();
    builder.Services.AddSingleton<PublicationWorkflow>();
    builder.Services.AddHttpClient("Publication", client => client.Timeout = TimeSpan.FromMinutes(3))
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.AddSingleton<ExpenseService>();
    builder.Services.AddSingleton<TradeFoundry.Services.Statements.StatementReconciliationService>();
    builder.Services.AddHttpClient("ChatGpt", client => client.Timeout = TimeSpan.FromMinutes(2))
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
    builder.Services.AddSingleton(services => new ChatGptOAuth(() => services.GetRequiredService<IHttpClientFactory>().CreateClient("ChatGpt")));
    builder.Services.AddSingleton<ChatGptConnectionStore>();
    builder.Services.AddSingleton<ChatGptSignInCoordinator>();
    builder.Services.AddSingleton<IDayReviewImageResolver, DayReviewImageResolver>();
    builder.Services.AddSingleton<DayReviewBundleBuilder>();
    builder.Services.AddSingleton<AiPromptLibrary>();
    builder.Services.AddSingleton<AiReviewService>();
    builder.Services.AddSingleton<IAiReviewProvider>(services =>
        string.Equals(services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>().Value.Provider, AiOptions.FakeProvider, StringComparison.OrdinalIgnoreCase)
            ? new FakeAiReviewProvider()
            : new ChatGptPlanProvider(
                services.GetRequiredService<ChatGptConnectionStore>(),
                services.GetRequiredService<ChatGptOAuth>(),
                () => services.GetRequiredService<IHttpClientFactory>().CreateClient("ChatGpt"),
                services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>(),
                services.GetRequiredService<ILogger<ChatGptPlanProvider>>()));
    builder.Services.AddSingleton<DesktopUpdateNotice>();
    builder.Services.AddHostedService<McpHostedService>();
    builder.Services.AddHostedService<ImportFolderMonitor>(services => services.GetRequiredService<ImportFolderMonitor>());
    builder.Services.AddHttpClient<YahooFinanceBenchmarkProvider>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TradeFoundry/1.0 local benchmark refresh");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    });
    builder.Services.AddScoped<IBenchmarkDataProvider>(services => services.GetRequiredService<YahooFinanceBenchmarkProvider>());
    builder.Services.AddScoped<BenchmarkRefreshService>();
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "tradefoundry.auth";
            options.LoginPath = "/Login";
            options.AccessDeniedPath = "/Login";
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            // A restore replaces the owner account; sign-ins from before it must not carry over.
            options.Events.OnValidatePrincipal = async context =>
            {
                var epoch = context.HttpContext.RequestServices.GetRequiredService<SessionEpoch>();
                if (!epoch.Accepts(context.Principal?.FindFirst(SessionEpoch.ClaimType)?.Value))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
            };
        });
    builder.Services.AddAuthorization();
    builder.Services.AddRazorPages(options =>
    {
        options.Conventions.AddPageRoute("/Index", "/journal/{journalId:guid}/overview");
        options.Conventions.AddPageRoute("/NotFound", "/Error/404");
    });

    var app = builder.Build();

    // Configure the HTTP request pipeline.
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error/500");
    }

    app.UseStatusCodePagesWithReExecute("/Error/{0}");

    app.UseStaticFiles();
    app.UseMiddleware<MaintenanceMiddleware>();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapRazorPages();
    // Anonymous, so it says only whether backups need attention, never why.
    app.MapGet(DesktopRuntime.HealthPath, (HttpContext context, AppActivityGate gate, HostInstance instance, BackupService backups, BackupScheduler scheduler) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        var backup = backups.Progress.Running ? "running" : scheduler.Warning is null ? "ok" : "attention";
        return Results.Json(new { status = gate.InMaintenance ? "maintenance" : "ready", instance = instance.Id, backup });
    }).AllowAnonymous();
    app.MapGet(DesktopRuntime.BackupsPath, (TradeFoundryDb database) =>
        database.GetJournals().FirstOrDefault() is { } journal
            ? Results.Redirect($"/journal/{journal.Id:D}/settings/backups")
            : Results.Redirect("/restore")).RequireAuthorization();
    // Exempt from maintenance and reads only memory, so the Backups page can follow a backup through it.
    app.MapGet(MaintenanceMiddleware.StatusPath, (HttpContext context, BackupService backups) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        var progress = backups.Progress;
        return Results.Json(new
        {
            running = progress.Running,
            runId = progress.RunId,
            stage = progress.Stage,
            fraction = progress.Fraction,
            maintenance = progress.Maintenance,
            lastStatus = progress.LastStatus,
            lastMessage = progress.LastMessage
        });
    }).RequireAuthorization();
    // Streams an uploaded backup straight to staging; the only route with a raised size limit.
    app.MapPost("/restore/upload", async (HttpContext context, RestoreService restore, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException)
        {
            return Results.Json(new { message = "The page expired. Reload it and try again." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var access = restore.CheckAccess(context.User.Identity?.IsAuthenticated == true, context.Request.Headers["X-Restore-Code"]);
        if (access != RestoreAccess.Allowed)
        {
            return Results.Json(new { message = access == RestoreAccess.NotClean ? "This installation already has journals." : "Not allowed to restore." }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = 256L * 1024 * 1024 * 1024;
        var (started, message) = await restore.StageUploadAsync(context.Request.Body, context.RequestAborted);
        return Results.Json(new { started, message }, statusCode: started ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
    }).AllowAnonymous();
    app.MapGet("/publication-assets/{name}", (string name) => TradeFoundry.Publishing.PublicationAssets.Script(name) is { } script
        ? Results.Content(script, "text/javascript") : Results.NotFound());
    // Open pages poll this for automatic imports that finished in the background (see site.js).
    app.MapGet("/import-notifications", (long? after, ImportNotificationFeed feed) => Results.Json(new
    {
        latest = feed.LatestSequence,
        items = feed.Since(after ?? 0).Select(item => new
        {
            sequence = item.Sequence,
            key = "auto-import:" + item.Key.ToString("D"),
            severity = item.Severity,
            message = item.Message,
            href = $"/journal/{item.JournalId:D}/imports"
        })
    })).RequireAuthorization();


    if (desktopMode && !embeddedMode && firstStart)
    {
        app.Lifetime.ApplicationStarted.Register(() => DesktopRuntime.TryOpenBrowser(desktopUrl));
    }

    return app;
}

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
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService(services => services.GetRequiredService<BackupService>());
builder.Services.AddSingleton<ImportService>();
builder.Services.AddSingleton<ImportNotificationFeed>();
builder.Services.AddSingleton<ImportFolderMonitor>();
builder.Services.AddSingleton<MarketDayFeatureCacheService>();
builder.Services.AddSingleton<MarketProbabilityService>(services => new MarketProbabilityService(
    services.GetRequiredService<TradeFoundryDb>(),
    services.GetRequiredService<MarketDayFeatureCacheService>()));
builder.Services.AddSingleton<TradingSetupService>();
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
app.MapGet(DesktopRuntime.HealthPath, (HttpContext context, AppActivityGate gate) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Json(new { status = gate.InMaintenance ? "maintenance" : "ready" });
}).AllowAnonymous();
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

if (desktopMode && !embeddedMode)
{
    app.Lifetime.ApplicationStarted.Register(() => DesktopRuntime.TryOpenBrowser(desktopUrl));
}

if (embeddedMode)
{
    // The tray helper owns this process: stop gracefully when it asks, or when it goes away.
    // Meanwhile it reports available updates, which the layout shows as a notification.
    var updateNotice = app.Services.GetRequiredService<DesktopUpdateNotice>();
    _ = DesktopRuntime.ReadParentCommandsAsync(Console.In, command => updateNotice.Apply(command))
        .ContinueWith(_ => app.Lifetime.StopApplication(), TaskScheduler.Default);
}

try
{
    app.Run();
}
finally
{
    desktopMutex?.Dispose();
}

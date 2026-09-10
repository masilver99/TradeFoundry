using Microsoft.AspNetCore.Authentication.Cookies;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Mcp;
using TradeFoundry.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<BenchmarkOptions>(builder.Configuration.GetSection("Benchmark"));
builder.Services.Configure<McpOptions>(builder.Configuration.GetSection("Mcp"));
builder.Services.AddSingleton<TradeFoundryDb>();
builder.Services.AddSingleton<ImportService>();
builder.Services.AddSingleton<McpTokenService>();
builder.Services.AddSingleton<JournalAnalysisService>();
builder.Services.AddHostedService<McpHostedService>();
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
    options.Conventions.AddPageRoute("/Trade", "/journal/{journalId:guid}/trades/{id:guid}");
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
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

app.Run();

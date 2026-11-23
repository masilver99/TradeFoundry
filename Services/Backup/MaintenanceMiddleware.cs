using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using TradeFoundry.Core;

namespace TradeFoundry.Services.Backup;

/// <summary>
/// Holds every request in an <see cref="AppActivityGate"/> lease. During maintenance:
/// <list type="bullet">
/// <item>reads (GET, HEAD) get 503 at once: a page that reloads itself for navigation, JSON otherwise;</item>
/// <item>writes wait, before their body is read, and then run normally, or get 503 after
/// <see cref="HoldLimit"/>. A held write is never answered with a redirect or a 200, because
/// autosave would take that as saved.</item>
/// </list>
/// Exempt paths never touch the database and keep answering, so the tray and the Backups page can
/// follow progress.
/// </summary>
public sealed class MaintenanceMiddleware(RequestDelegate next, AppActivityGate gate)
{
    public static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(90);
    public const string StatusPath = "/backup-status";
    private const int RetryAfterSeconds = 5;

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsExempt(context.Request.Path))
        {
            await next(context);
            return;
        }

        if (!gate.TryEnter(out var lease))
        {
            if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            {
                await WriteUnavailableAsync(context);
                return;
            }

            using var hold = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            hold.CancelAfter(HoldLimit);
            try
            {
                lease = await gate.EnterAsync(longRunning: false, hold.Token);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                await WriteUnavailableAsync(context);
                return;
            }
        }

        using (lease)
        {
            await next(context);
        }
    }

    public static bool IsExempt(PathString path) =>
        path.Equals(DesktopRuntime.HealthPath, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(StatusPath, StringComparison.OrdinalIgnoreCase);

    private static async Task WriteUnavailableAsync(HttpContext context)
    {
        if (context.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages) statusCodePages.Enabled = false;
        var response = context.Response;
        response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        response.Headers.CacheControl = "no-store";
        if (HttpMethods.IsHead(context.Request.Method)) return;

        if (IsNavigation(context.Request))
        {
            response.ContentType = "text/html; charset=utf-8";
            await response.WriteAsync(MaintenancePage);
        }
        else
        {
            response.ContentType = "application/json; charset=utf-8";
            await response.WriteAsync(JsonSerializer.Serialize(new
            {
                maintenance = true,
                message = "TradeFoundry is making a backup. Try again in a moment."
            }));
        }
    }

    private static bool IsNavigation(HttpRequest request)
    {
        var mode = request.Headers["Sec-Fetch-Mode"].ToString();
        if (mode.Length > 0) return string.Equals(mode, "navigate", StringComparison.OrdinalIgnoreCase);
        return !request.Headers.ContainsKey("X-Requested-With") &&
               request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    // Self-contained: the normal layout reads the database.
    private const string MaintenancePage = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Backing up · TradeFoundry</title>
        <style>
        :root { color-scheme: light dark; --bg: #f5f7fb; --fg: #1d273b; --muted: #667085; --card: #fff; --line: #dce1e7; }
        @media (prefers-color-scheme: dark) { :root { --bg: #151b27; --fg: #e6e9ef; --muted: #9aa4b2; --card: #1d2433; --line: #2c3547; } }
        body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: var(--bg); color: var(--fg); font: 15px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
        main { max-width: 26rem; margin: 1rem; padding: 2rem; background: var(--card); border: 1px solid var(--line); border-radius: 8px; }
        h1 { font-size: 1.15rem; margin: 0 0 .5rem; }
        p { margin: 0; color: var(--muted); }
        .bar { height: 3px; margin-top: 1.25rem; overflow: hidden; border-radius: 2px; background: var(--line); }
        .bar::after { content: ""; display: block; width: 35%; height: 100%; background: #206bc4; animation: slide 1.4s ease-in-out infinite; }
        @keyframes slide { from { transform: translateX(-100%); } to { transform: translateX(300%); } }
        @media (prefers-reduced-motion: reduce) { .bar::after { animation: none; width: 100%; } }
        </style>
        </head>
        <body>
        <main role="status">
        <h1>Making a backup</h1>
        <p>TradeFoundry is paused for a moment while it copies your journal. This page reloads by itself when it is done.</p>
        <div class="bar"></div>
        </main>
        <script>
        (function poll() {
          fetch("/health", { cache: "no-store" })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (s) { if (s && s.status === "ready") location.reload(); else setTimeout(poll, 2000); })
            .catch(function () { setTimeout(poll, 2000); });
        })();
        </script>
        </body>
        </html>
        """;
}

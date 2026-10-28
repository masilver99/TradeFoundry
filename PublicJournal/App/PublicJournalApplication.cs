using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeFoundry.Publishing;

namespace TradeFoundry.PublicJournal;

public static class PublicJournalApplication
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ApplicationName = typeof(PublicJournalApplication).Assembly.FullName });
        var token = builder.Configuration["Publishing:Token"];
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 4096) throw new InvalidOperationException("Configure Publishing:Token with 32 to 4,096 random characters.");
        if (token.Any(c => c < 33 || c > 126)) throw new InvalidOperationException("Publishing:Token must use printable ASCII characters without spaces.");
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = PublicationJson.MaxUploadBytes);
        builder.Services.AddSingleton(new PublicationStore(builder.Configuration["Storage:DataDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "data")));
        builder.Services.AddRazorPages();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            // The pinned Lightweight Charts 5.2.0 attribution logo injects this exact stylesheet.
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; img-src 'self'; style-src 'self' 'sha256-3pRED1tOXas1FXFoPb9TGCjmYe9XQsmO9OV23khV2nY='; script-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                var authorization = context.Request.Headers.Authorization.ToString();
                if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || authorization.Length > 4103
                    || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..])), tokenHash))
                { context.Response.StatusCode = 401; return; }
            }
            try { await next(); }
            catch (PublicationConflictException ex) { await Error(context, 409, ex.Message); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException or ArgumentException or NullReferenceException)
            {
                if (!context.Request.Path.StartsWithSegments("/api")) throw;
                await Error(context, 400, ex is InvalidOperationException ? ex.Message : "Malformed publication payload.");
            }
            catch (BadHttpRequestException ex) { await Error(context, ex.StatusCode, "Publication request is invalid or exceeds the upload limit."); }
        });
        app.MapGet("/publication.css", () => Results.Content(PublicationPresentation.Style(), "text/css"));
        app.MapGet("/publication-assets/{name}", (string name) => PublicationAssets.Script(name) is { } script
            ? Results.Content(script, "text/javascript") : Results.NotFound());
        app.MapGet("/favicon.ico", () => Results.NoContent());
        app.MapGet("/health", (PublicationStore store) => { store.Status(); return Results.Ok(new { status = "ok" }); });
        app.MapGet("/api/v1/publication", (PublicationStore store) => Results.Json(store.Status(), PublicationJson.Options));
        app.MapPut("/api/v1/publication", async (HttpContext context, PublicationStore store, ILogger<PublicationStore> logger) =>
        {
            var upload = await Read<PublicationUpload>(context);
            context.RequestAborted.ThrowIfCancellationRequested();
            var status = store.Publish(upload);
            logger.LogInformation("Publication activated: snapshot {SnapshotId}, revision {Revision}", status.SnapshotId, status.Revision);
            return Results.Json(status, PublicationJson.Options);
        });
        app.MapDelete("/api/v1/publication", async (HttpContext context, PublicationStore store, ILogger<PublicationStore> logger) =>
        {
            var status = store.Unpublish(await Read<PublicationDelete>(context));
            logger.LogInformation("Publication removed at revision {Revision}", status.Revision);
            return Results.Json(status, PublicationJson.Options);
        });
        app.MapGet("/images/{snapshotId:guid}/{imageId}", (Guid snapshotId, string imageId, PublicationStore store) =>
            store.Image(snapshotId, imageId) is { } image ? Results.File(image.Content, image.ContentType) : Results.NotFound());
        app.MapRazorPages();
        return app;
    }

    private static async Task<T> Read<T>(HttpContext context)
    {
        if (!context.Request.HasJsonContentType()) throw new InvalidOperationException("Use application/json for publication requests.");
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted);
            if (read == 0) break;
            if (buffer.Length + read > PublicationJson.MaxUploadBytes) throw new BadHttpRequestException("Upload too large.", 413);
            buffer.Write(chunk, 0, read);
        }
        return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), PublicationJson.Options) ?? throw new JsonException("Missing publication payload.");
    }

    private static Task Error(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = message });
    }
}

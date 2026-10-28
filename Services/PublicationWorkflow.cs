using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Publishing;

namespace TradeFoundry.Services;

public sealed class PublicationWorkflow(TradeFoundryDb database, PublicationExportService exporter,
    IDataProtectionProvider protection, IHttpClientFactory clients)
{
    private readonly IDataProtector _protector = protection.CreateProtector("TradeFoundry.PublicationToken.v1");
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public async Task SaveSettings(Guid journalId, PublishingSettings settings, string? token, CancellationToken cancellationToken)
    {
        await Locked(journalId, async () =>
        {
            var destination = Destination(settings.DestinationUrl);
            if (string.IsNullOrWhiteSpace(settings.Title) || settings.Title.Length > 150 || settings.Introduction.Length > 5000)
                throw new InvalidOperationException("Provide a public title up to 150 characters and an introduction up to 5,000 characters.");
            var current = database.GetPublishingState(journalId);
            var secret = string.IsNullOrWhiteSpace(token) ? current.ProtectedToken : _protector.Protect(token.Trim());
            if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("Enter the website's publishing token.");
            if (!string.IsNullOrWhiteSpace(token) && token.Trim().Length is < 32 or > 4096)
                throw new InvalidOperationException("Publishing tokens must contain between 32 and 4,096 characters.");
            if (!string.IsNullOrWhiteSpace(token) && token.Trim().Any(c => c < 33 || c > 126))
                throw new InvalidOperationException("Publishing tokens must use printable ASCII characters without spaces.");
            database.SavePublishingSettings(journalId, settings with { DestinationUrl = destination.AbsoluteUri, Title = settings.Title.Trim(), Introduction = settings.Introduction.Trim() }, secret);
            await Task.CompletedTask;
        }, cancellationToken);
    }

    public async Task RefreshStatus(Guid journalId, CancellationToken cancellationToken) => await Locked(journalId, async () =>
    {
        var state = database.GetPublishingState(journalId);
        var status = await Request<PublicationStatus>(state, HttpMethod.Get, null, cancellationToken);
        database.SavePublicationStatus(journalId, status, state.Draft?.Snapshot.SnapshotId == status.SnapshotId);
    }, cancellationToken);

    public async Task Preview(Guid journalId, CancellationToken cancellationToken) => await Locked(journalId, async () =>
    {
        var state = database.GetPublishingState(journalId);
        var status = await Request<PublicationStatus>(state, HttpMethod.Get, null, cancellationToken);
        database.SavePublicationStatus(journalId, status);
        if (status.BoundJournalId is not null && status.BoundJournalId != journalId)
            throw new InvalidOperationException("This website is bound to a different journal. Reset its binding from that journal before publishing here.");
        var snapshot = exporter.Build(journalId, state.Settings);
        var upload = new PublicationUpload(status.Revision, snapshot);
        if (JsonSerializer.SerializeToUtf8Bytes(upload, PublicationJson.Options).Length > PublicationJson.MaxUploadBytes)
            throw new InvalidOperationException("The publication exceeds the 128 MB upload limit. Reduce embedded image sizes before publishing.");
        database.SavePublicationDraft(journalId, upload);
    }, cancellationToken);

    public async Task Publish(Guid journalId, Guid snapshotId, CancellationToken cancellationToken) => await Locked(journalId, async () =>
    {
        var state = database.GetPublishingState(journalId);
        if (state.Draft is null || state.Draft.Snapshot.SnapshotId != snapshotId)
            throw new InvalidOperationException("The preview changed or no longer exists. Review the current preview before publishing.");
        var status = await Request<PublicationStatus>(state, HttpMethod.Put, state.Draft, cancellationToken);
        database.SavePublicationStatus(journalId, status, published: true);
    }, cancellationToken);

    public async Task Unpublish(Guid journalId, long expectedRevision, bool resetBinding, CancellationToken cancellationToken) => await Locked(journalId, async () =>
    {
        var state = database.GetPublishingState(journalId);
        var status = await Request<PublicationStatus>(state, HttpMethod.Delete, new PublicationDelete(expectedRevision, journalId, resetBinding), cancellationToken);
        database.SavePublicationStatus(journalId, status);
    }, cancellationToken);

    private async Task Locked(Guid id, Func<Task> work, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(id, _ => new(1));
        await gate.WaitAsync(cancellationToken);
        try { await work(); } finally { gate.Release(); }
    }

    public static Uri Destination(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Use an HTTPS website URL without credentials, query, or fragment. HTTP is allowed only for local loopback testing.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    private async Task<T> Request<T>(PublishingState state, HttpMethod method, object? body, CancellationToken cancellationToken)
    {
        var destination = Destination(state.Settings.DestinationUrl);
        string token;
        try { token = _protector.Unprotect(state.ProtectedToken); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException)
        { throw new InvalidOperationException("The saved publishing token cannot be read. Enter and save it again."); }
        using var request = new HttpRequestMessage(method, new Uri(destination, "api/v1/publication"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: PublicationJson.Options);
        using var response = await clients.CreateClient("Publication").SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new InvalidOperationException("The website rejected the publishing token. Check the saved token and server configuration.");
            if (response.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge) throw new InvalidOperationException("The website or reverse proxy rejected the upload size. Check its request body limit.");
            var message = $"Publishing request failed (HTTP {(int)response.StatusCode}).";
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (json.RootElement.TryGetProperty("error", out var error) && error.GetString() is { } detail) message = detail[..Math.Min(detail.Length, 1000)];
            }
            catch (JsonException) { }
            throw new InvalidOperationException(message);
        }
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The destination returned a web page or another non-JSON response instead of the publication API. Use the public journal website's base URL and check that /api/v1/publication reaches the public journal app.");
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (typeof(T) == typeof(PublicationStatus) && (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("revision", out _)
                || !document.RootElement.TryGetProperty("boundJournalId", out _)
                || !document.RootElement.TryGetProperty("snapshotId", out _)
                || !document.RootElement.TryGetProperty("publishedUtc", out _)))
                throw new JsonException("Missing publication status fields.");
            return document.RootElement.Deserialize<T>(PublicationJson.Options) ?? throw new JsonException("Missing publication status.");
        }
        catch (JsonException)
        { throw new InvalidOperationException("The public website returned invalid or incompatible publication status JSON. Check the destination URL and that the public journal app is running a compatible version."); }
        catch (IOException)
        { throw new InvalidOperationException("The public website's response could not be read completely. Check the network connection and reverse proxy, then retry."); }
    }
}

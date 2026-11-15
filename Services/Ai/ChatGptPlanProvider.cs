using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;

namespace TradeFoundry.Services.Ai;

/// <summary>
/// Runs requests against the owner's ChatGPT plan through the public Responses API. Requests always use
/// <c>store: false</c> and <c>stream: true</c> and never send <c>temperature</c> (rejected on the plan route).
/// </summary>
public sealed class ChatGptPlanProvider(
    ChatGptConnectionStore store,
    ChatGptOAuth oauth,
    Func<HttpClient> httpClients,
    IOptions<AiOptions> options,
    ILogger<ChatGptPlanProvider> logger,
    TimeProvider? timeProvider = null) : IAiReviewProvider
{
    private const string BaseUrl = "https://api.openai.com/v1";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ModelCacheLifetime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private (string Subject, IReadOnlyList<AiModel> Models, DateTimeOffset FetchedAt)? _modelCache;

    public string Id => AiOptions.ChatGptProvider;
    public string DisplayName => "ChatGPT plan";

    public AiConnectionStatus GetStatus()
    {
        var connection = store.Load();
        return new AiConnectionStatus
        {
            Connected = connection.Connected,
            CanRun = connection.Sharing,
            AccountEmail = connection.Email,
            AccountName = connection.DisplayName,
            Model = connection.Model,
            ReasoningEffort = connection.ReasoningEffort,
            ImageDetail = connection.ImageDetail,
            Problem = connection.CredentialsUnreadable
                ? "The saved ChatGPT credentials could not be decrypted on this machine. Connect again."
                : connection.Connected && !connection.Sharing
                    ? "ChatGPT plan usage was not granted. Connect again and allow TradeFoundry to use your plan."
                    : null,
        };
    }

    public void SavePreferences(string? model, string? reasoningEffort, string? imageDetail) =>
        store.SavePreferences(model, reasoningEffort, AiImageDetail.Normalize(imageDetail));

    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var connection = store.Load();
        if (_modelCache is { } cached && cached.Subject == connection.Subject && _time.GetUtcNow() - cached.FetchedAt < ModelCacheLifetime)
            return cached.Models;

        var token = await GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw Failure(body, response);

        var models = ParseModelCatalog(body);
        _modelCache = (connection.Subject ?? string.Empty, models, _time.GetUtcNow());
        return models;
    }

    /// <summary>Parses the plan route's <c>{ models: [...] }</c> catalog, keeping entries with <c>visibility: "list"</c>.</summary>
    internal static IReadOnlyList<AiModel> ParseModelCatalog(string body)
    {
        var invalid = new AiProviderException("invalid_model_catalog", "ChatGPT returned an unexpected model list. Try again.", true);
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException) { throw invalid; }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw invalid;

        var result = new List<AiModel>();
        foreach (var model in models.EnumerateArray())
        {
            if (model.String("visibility") != "list") continue;
            var slug = model.String("slug");
            if (string.IsNullOrWhiteSpace(slug) || slug.Length > 200) throw invalid;
            var modalities = model.TryGetProperty("input_modalities", out var m) && m.ValueKind == JsonValueKind.Array
                ? m.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                : [];
            var levels = model.TryGetProperty("supported_reasoning_levels", out var l) && l.ValueKind == JsonValueKind.Array
                ? l.EnumerateArray().Select(x => x.String("effort")).OfType<string>().ToArray()
                : [];
            int? contextWindow = model.TryGetProperty("context_window", out var c) && c.TryGetInt32(out var window) ? window : null;
            result.Add(new AiModel(slug, model.String("display_name") ?? slug, modalities.Contains("image"), levels,
                model.String("default_reasoning_level"), contextWindow, model.String("description")));
        }
        return result;
    }

    public async Task<AiResult> StreamAsync(AiRequest request, Action<string>? onDelta, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, options.Value.RequestTimeoutMinutes)));
        var stopwatch = Stopwatch.StartNew();

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/responses")
        {
            Content = new StringContent(BuildRequestBody(request).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Accept.ParseAdd("text/event-stream");

        try
        {
            using var response = await SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) throw Failure(await response.Content.ReadAsStringAsync(deadline.Token), response);

            // The direct route can return valid SSE without a Content-Type; reject only an explicit mismatch.
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
                throw new AiProviderException("invalid_stream", "ChatGPT did not return the expected response stream.", true, (int)response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var (text, final) = await ReadEventStreamAsync(stream, onDelta, RequestId(response), deadline.Token);
            return new AiResult(text, final.String("model"), ReadUsage(final), stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException("cancelled", "The request was cancelled.");
        }
        catch (OperationCanceledException)
        {
            throw new AiProviderException("timeout", "ChatGPT took too long to respond. Try again.", true);
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var connection = store.Load();
        store.ClearConnection();
        _modelCache = null;
        await oauth.RevokeAsync(connection, cancellationToken);
    }

    /// <summary>Returns a valid access token, refreshing once (serialized) when it is near expiry.</summary>
    internal async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var connection = store.Load();
        EnsureUsable(connection);
        if (connection.Credentials!.ExpiresAt > _time.GetUtcNow() + RefreshMargin) return connection.Credentials.AccessToken;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Another request may have refreshed while this one waited; a rotated refresh token must never be reused.
            connection = store.Load();
            EnsureUsable(connection);
            if (connection.Credentials!.ExpiresAt > _time.GetUtcNow() + RefreshMargin) return connection.Credentials.AccessToken;

            var refreshed = await oauth.RefreshAsync(connection, rotation => store.SaveCredentials(rotation.Scopes, rotation.Credentials), cancellationToken);
            return refreshed.Credentials.AccessToken;
        }
        catch (AiProviderException ex) when (ex.RequiresReconnect)
        {
            logger.LogWarning("ChatGPT connection requires reconnecting ({Code}).", ex.Code);
            store.ClearConnection();
            throw;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static void EnsureUsable(ChatGptConnection connection)
    {
        if (!connection.Connected) throw new AiProviderException("not_connected", "ChatGPT is not connected. Connect it in Settings → AI review.");
        if (!connection.Sharing) throw new AiProviderException("sharing_not_granted", "ChatGPT plan usage was not granted. Connect again and allow it.");
    }

    internal static JsonObject BuildRequestBody(AiRequest request)
    {
        var input = new JsonArray();
        foreach (var message in request.Messages)
        {
            var role = message.Role switch { AiRole.Assistant => "assistant", AiRole.Developer => "developer", _ => "user" };
            JsonNode content;
            if (message.Content is [AiTextContent only])
            {
                content = only.Text;
            }
            else
            {
                if (message.Role == AiRole.Assistant && message.Content.Any(part => part is not AiTextContent))
                    throw new ArgumentException("Assistant messages can contain text only.", nameof(request));
                var parts = new JsonArray();
                foreach (var part in message.Content)
                {
                    parts.Add(part switch
                    {
                        AiTextContent text => new JsonObject { ["type"] = message.Role == AiRole.Assistant ? "output_text" : "input_text", ["text"] = text.Text },
                        AiImageContent image => ImagePart(image),
                        _ => throw new ArgumentException($"Unsupported content {part.GetType().Name}.", nameof(request)),
                    });
                }
                content = parts;
            }
            input.Add(new JsonObject { ["role"] = role, ["content"] = content });
        }

        var body = new JsonObject { ["model"] = request.Model, ["input"] = input };
        if (!string.IsNullOrWhiteSpace(request.Instructions)) body["instructions"] = request.Instructions;
        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort)) body["reasoning"] = new JsonObject { ["effort"] = request.ReasoningEffort };
        if (request.OutputSchema is { } schema)
        {
            body["text"] = new JsonObject
            {
                ["format"] = new JsonObject { ["type"] = "json_schema", ["name"] = schema.Name, ["strict"] = true, ["schema"] = schema.Schema.DeepClone() },
            };
        }
        body["store"] = false;
        body["stream"] = true;
        return body;

        static JsonObject ImagePart(AiImageContent image)
        {
            var part = new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:{image.ContentType};base64,{Convert.ToBase64String(image.Data)}" };
            if (!string.IsNullOrWhiteSpace(image.Detail)) part["detail"] = image.Detail;
            return part;
        }
    }

    /// <summary>Reads Responses SSE events. Succeeds only on <c>response.completed</c>.</summary>
    internal static async Task<(string Text, JsonElement Final)> ReadEventStreamAsync(Stream stream, Action<string>? onDelta, string? requestId, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = new StringBuilder();
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart());
                continue;
            }
            if (line.Length != 0 || data.Length == 0) continue;

            var payload = data.ToString();
            data.Clear();
            if (payload == "[DONE]") break;

            JsonElement evt;
            try
            {
                using var document = JsonDocument.Parse(payload);
                evt = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new AiProviderException("invalid_stream", "The response stream contained an invalid event. Try again.", true, requestId: requestId);
            }

            switch (evt.String("type"))
            {
                case "response.output_text.delta" when evt.String("delta") is { } delta:
                    text.Append(delta);
                    if (text.Length > 16 * 1024 * 1024) throw new AiProviderException("response_too_large", "The response was too large.");
                    onDelta?.Invoke(delta);
                    break;
                case "response.failed":
                    throw ChatGptErrors.FromJson(evt.TryGetProperty("response", out var failed) ? failed : evt, null, requestId);
                case "error":
                    throw ChatGptErrors.FromJson(evt, null, requestId);
                case "response.incomplete":
                    throw new AiProviderException("response_incomplete", "ChatGPT stopped before completing the response. Try again.", true, requestId: requestId);
                case "response.completed":
                    return (text.ToString(), evt.TryGetProperty("response", out var completed) ? completed : evt);
            }
        }
        throw new AiProviderException("stream_interrupted", "The response ended before completion. Try again.", true, requestId: requestId);
    }

    private static AiUsage ReadUsage(JsonElement final)
    {
        long? Read(params string[] path)
        {
            var node = final;
            foreach (var part in path)
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(part, out node)) return null;
            return node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var value) ? value : null;
        }
        return new AiUsage(Read("usage", "input_tokens"), Read("usage", "output_tokens"), Read("usage", "output_tokens_details", "reasoning_tokens"));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        try { return await httpClients().SendAsync(request, completion, cancellationToken); }
        catch (HttpRequestException) { throw new AiProviderException("network_error", "Could not reach ChatGPT. Check your connection and try again.", true); }
    }

    private AiProviderException Failure(string body, HttpResponseMessage response)
    {
        var error = ChatGptErrors.FromBody(body, (int)response.StatusCode, RequestId(response));
        // Log the code and status only: bodies can echo submitted journal content.
        logger.LogWarning("ChatGPT request failed: {Code} HTTP {Status} request {RequestId}.", error.Code, error.Status, error.RequestId);
        if (error.Status == 401) _modelCache = null;
        return error;
    }

    private static string? RequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
}

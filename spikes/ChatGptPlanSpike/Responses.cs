using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatGptPlanSpike;

public sealed record ModelInfo(string Slug, string DisplayName, JsonElement Raw);

public sealed record ResponseResult(string Text, string? Model, long? InputTokens, long? OutputTokens, long? ReasoningTokens, TimeSpan Elapsed, string? RequestId);

/// <summary>Port of the DevKit's models.ts and responses.ts using raw HTTP + SSE (no OpenAI SDK).</summary>
public static class Responses
{
    private const string BaseUrl = "https://api.openai.com/v1";

    public static async Task<(IReadOnlyList<ModelInfo> Listed, string RawBody)> ListModelsAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await OAuth.Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw ApiErrors.From(body, (int)response.StatusCode, RequestId(response));

        var root = JsonDocument.Parse(body).RootElement;
        // The plan route returns { models: [...] } with slug/display_name/visibility (not the Platform { data: [...] } shape).
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new ChatGptException("invalid_model_catalog", "ChatGPT returned an unexpected model catalog.", true, (int)response.StatusCode);
        var listed = models.EnumerateArray()
            .Where(m => m.Str("visibility") == "list" && !string.IsNullOrWhiteSpace(m.Str("slug")))
            .Select(m => new ModelInfo(m.Str("slug")!, m.Str("display_name") ?? m.Str("slug")!, m.Clone()))
            .ToList();
        return (listed, body);
    }

    /// <summary>
    /// POST /v1/responses with store=false and stream=true (required on the plan route). Succeeds only on response.completed.
    /// </summary>
    public static async Task<ResponseResult> StreamAsync(string accessToken, JsonObject body, Action<string>? onDelta, CancellationToken ct)
    {
        body["store"] = false;
        body["stream"] = true;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var stopwatch = Stopwatch.StartNew();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/responses")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await OAuth.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        var requestId = RequestId(response);
        if (!response.IsSuccessStatusCode)
            throw ApiErrors.From(await response.Content.ReadAsStringAsync(deadline.Token), (int)response.StatusCode, requestId);

        // The direct route can return valid SSE without a Content-Type; reject only an explicit mismatch.
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new ChatGptException("invalid_stream", $"Expected an event stream but got {mediaType}.", true, (int)response.StatusCode, requestId);

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = new StringBuilder();
        var data = new StringBuilder();
        JsonElement? completed = null;

        while (completed is null && await reader.ReadLineAsync(deadline.Token) is { } line)
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
            try { evt = JsonDocument.Parse(payload).RootElement; }
            catch (JsonException) { throw new ChatGptException("invalid_stream", "The response stream contained an invalid event.", true, requestId: requestId); }

            switch (evt.Str("type"))
            {
                case "response.output_text.delta" when evt.Str("delta") is { } delta:
                    text.Append(delta);
                    onDelta?.Invoke(delta);
                    break;
                case "response.failed":
                    throw ApiErrors.From(evt.TryGetProperty("response", out var failed) ? failed : evt, (int)response.StatusCode, requestId);
                case "error":
                    throw ApiErrors.From(evt, (int)response.StatusCode, requestId);
                case "response.incomplete":
                    throw new ChatGptException("response_incomplete", "ChatGPT stopped before completing the response.", true, requestId: requestId);
                case "response.completed":
                    completed = evt.TryGetProperty("response", out var done) ? done.Clone() : evt.Clone();
                    break;
            }
        }

        if (completed is not { } final)
            throw new ChatGptException("stream_interrupted", "The response ended before completion.", true, requestId: requestId);

        long? Usage(params string[] path)
        {
            var node = final;
            foreach (var part in path)
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(part, out node)) return null;
            return node.ValueKind == JsonValueKind.Number ? node.GetInt64() : null;
        }

        return new ResponseResult(text.ToString(), final.Str("model"),
            Usage("usage", "input_tokens"), Usage("usage", "output_tokens"), Usage("usage", "output_tokens_details", "reasoning_tokens"),
            stopwatch.Elapsed, requestId);
    }

    private static string? RequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;

    // ---- Request body helpers ----

    public static JsonObject TextRequest(string model, string prompt, string? instructions = null)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
        };
        if (instructions is not null) body["instructions"] = instructions;
        return body;
    }

    public static JsonObject ImageRequest(string model, string prompt, IEnumerable<string> dataUrls, string? detail = null, string? instructions = null)
    {
        var content = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = prompt });
        foreach (var url in dataUrls)
        {
            var image = new JsonObject { ["type"] = "input_image", ["image_url"] = url };
            if (detail is not null) image["detail"] = detail;
            content.Add(image);
        }
        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
        };
        if (instructions is not null) body["instructions"] = instructions;
        return body;
    }

    public static string DataUrl(byte[] bytes, string contentType) => $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
}

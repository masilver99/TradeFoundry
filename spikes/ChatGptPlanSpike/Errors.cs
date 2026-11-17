using System.Text.Json;

namespace ChatGptPlanSpike;

/// <summary>Port of the DevKit's errors.ts mapping (packages/local/src/errors.ts).</summary>
public sealed class ChatGptException(string code, string message, bool retryable = false, int? status = null, string? requestId = null, string? serverDetail = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public int? Status { get; } = status;
    public string? RequestId { get; } = requestId;
    /// <summary>Raw server message. Spike-only diagnostic: production code must not surface it (it can echo submitted input).</summary>
    public string? ServerDetail { get; } = serverDetail;

    public override string ToString() =>
        $"[{Code}] {Message}" + (Status is { } s ? $" (HTTP {s})" : "") + (RequestId is { } r ? $" request={r}" : "") +
        (string.IsNullOrWhiteSpace(ServerDetail) ? "" : $"\n    server: {ServerDetail}");
}

public static class ApiErrors
{
    private static readonly Dictionary<string, (string Message, bool Retryable)> Known = new()
    {
        ["subscription_sharing_user_not_eligible"] = ("Token sharing is not available for this ChatGPT account or workspace.", false),
        ["subscription_sharing_usage_limit_exceeded"] = ("A ChatGPT sharing limit has been reached. Check your usage and app limits in ChatGPT Settings.", false),
        ["subscription_sharing_usage_unavailable"] = ("ChatGPT usage could not be checked. Try again shortly.", true),
        ["subscription_sharing_unsupported_capability"] = ("This request includes a capability that token sharing does not support.", false),
        ["subscription_sharing_route_not_supported"] = ("This request uses a route that token sharing does not support.", false),
        ["subscription_sharing_invalid_user"] = ("ChatGPT could not validate the selected account's subscription context.", false),
        ["subscription_sharing_user_unavailable"] = ("Your ChatGPT account or workspace is temporarily unavailable. Try again shortly.", true),
        ["subscription_sharing_v2_client_not_enabled"] = ("This app is not enabled for token sharing.", false),
        ["chatpass_v2_scope_not_authorized"] = ("The granted ChatGPT permissions do not authorize this request.", false),
        ["chatpass_v2_invalid_authorization_context"] = ("The ChatGPT authorization context does not permit this request.", false),
        ["invalid_client"] = ("ChatGPT rejected the app's client configuration.", false),
        ["invalid_grant"] = ("ChatGPT did not accept this authorization grant. Sign in again.", false),
        ["invalid_refresh_token"] = ("Your ChatGPT connection can no longer be renewed. Sign in again.", false),
        ["token_expired"] = ("Your ChatGPT connection can no longer be renewed. Sign in again.", false),
        ["refresh_token_expired"] = ("Your ChatGPT connection can no longer be renewed. Sign in again.", false),
        ["refresh_token_invalidated"] = ("Your ChatGPT connection can no longer be renewed. Sign in again.", false),
        ["refresh_token_reused"] = ("Your ChatGPT connection can no longer be renewed. Sign in again.", false),
        ["invalid_token"] = ("ChatGPT did not accept this credential.", false),
        ["invalid_api_key"] = ("ChatGPT did not accept this credential.", false),
        ["access_denied"] = ("Sign-in was not completed.", false),
        ["model_not_found"] = ("This model is not available for your ChatGPT connection.", false),
    };

    public static ChatGptException From(string? body, int? status, string? requestId)
    {
        JsonElement detail = default;
        var hasDetail = false;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                detail = JsonDocument.Parse(body).RootElement.Clone();
                hasDetail = detail.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException) { }
        }
        return From(hasDetail ? detail : null, status, requestId);
    }

    public static ChatGptException From(JsonElement? body, int? status, string? requestId)
    {
        var detail = body is { ValueKind: JsonValueKind.Object } b ? b : default;
        for (var depth = 0; depth < 4 && detail.ValueKind == JsonValueKind.Object; depth++)
        {
            if (detail.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object) detail = e;
            else if (detail.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.Object) detail = d;
            else break;
        }

        string? code = null, serverMessage = null;
        if (detail.ValueKind == JsonValueKind.Object)
        {
            code = detail.Str("error") ?? detail.Str("code");
            serverMessage = detail.Str("message") ?? detail.Str("error_description");
            if (detail.Str("param") is { } param) serverMessage = $"{serverMessage} (param: {param})";
        }
        code ??= "api_error";

        if (Known.TryGetValue(code, out var known))
            return new ChatGptException(code, known.Message, known.Retryable, status, requestId, serverMessage);
        return status switch
        {
            400 or 422 => new ChatGptException(code, "ChatGPT rejected this request.", false, status, requestId, serverMessage),
            401 => new ChatGptException(code, "ChatGPT did not accept the connection's identity or direct permission.", false, status, requestId, serverMessage),
            403 => new ChatGptException(code, "A ChatGPT policy or permission restriction prevented this request.", false, status, requestId, serverMessage),
            429 => new ChatGptException(code, "Too many requests. Wait a moment before trying again.", true, status, requestId, serverMessage),
            >= 500 => new ChatGptException(code, "ChatGPT is temporarily unavailable.", true, status, requestId, serverMessage),
            _ => new ChatGptException(code, "ChatGPT could not complete this request.", false, status, requestId, serverMessage),
        };
    }
}

public static class JsonExtensions
{
    public static string? Str(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

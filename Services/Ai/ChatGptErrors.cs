using System.Text.Json;

namespace TradeFoundry.Services.Ai;

/// <summary>Maps OAuth and Responses errors to user-safe messages. Port of the Sign in with ChatGPT DevKit's errors.ts.</summary>
public static class ChatGptErrors
{
    public const string ManageUsageUrl = "https://chatgpt.com/settings/usage";

    private const string Reconnect = "Your ChatGPT connection can no longer be renewed. Connect again.";

    private static readonly Dictionary<string, (string Message, bool Retryable)> Known = new(StringComparer.Ordinal)
    {
        ["subscription_sharing_user_not_eligible"] = ("ChatGPT plan usage is not available for this account or workspace.", false),
        ["subscription_sharing_usage_limit_exceeded"] = ("A ChatGPT usage limit has been reached. Check your usage and app limits in ChatGPT settings.", false),
        ["subscription_sharing_usage_unavailable"] = ("ChatGPT usage could not be checked. Try again shortly.", true),
        ["subscription_sharing_unsupported_capability"] = ("This request uses a capability that ChatGPT plan usage does not support.", false),
        ["subscription_sharing_route_not_supported"] = ("This request uses a route that ChatGPT plan usage does not support.", false),
        ["subscription_sharing_invalid_user"] = ("ChatGPT could not validate this account's subscription. Check the connection and its permissions.", false),
        ["subscription_sharing_user_unavailable"] = ("Your ChatGPT account or workspace is temporarily unavailable. Try again shortly.", true),
        ["subscription_sharing_v2_client_not_enabled"] = ("This app is not enabled for ChatGPT plan usage.", false),
        ["chatpass_v2_scope_not_authorized"] = ("The granted ChatGPT permissions do not allow this request. Connect again and allow plan usage.", false),
        ["chatpass_v2_invalid_authorization_context"] = ("The ChatGPT authorization does not permit this request. Connect again.", false),
        ["invalid_client"] = ("ChatGPT rejected TradeFoundry's saved registration.", false),
        ["invalid_grant"] = ("ChatGPT did not accept this authorization. Connect again.", false),
        ["invalid_refresh_token"] = (Reconnect, false),
        ["token_expired"] = (Reconnect, false),
        ["refresh_token_expired"] = (Reconnect, false),
        ["refresh_token_invalidated"] = (Reconnect, false),
        ["refresh_token_reused"] = (Reconnect, false),
        ["invalid_token"] = ("ChatGPT did not accept the saved credential. Connect again.", false),
        ["invalid_api_key"] = ("ChatGPT did not accept the saved credential. Connect again.", false),
        ["access_denied"] = ("Sign-in was not completed. You can try again when you are ready.", false),
        ["model_not_found"] = ("This model is not available for your ChatGPT connection. Choose another model.", false),
    };

    private static readonly Dictionary<string, string> Legacy = new(StringComparer.Ordinal)
    {
        ["subscription_sharing_v2_user_not_eligible"] = "subscription_sharing_user_not_eligible",
        ["subscription_sharing_v2_route_not_supported"] = "subscription_sharing_route_not_supported",
        ["subscription_sharing_v2_invalid_user"] = "subscription_sharing_invalid_user",
        ["subscription_sharing_v2_user_unavailable"] = "subscription_sharing_user_unavailable",
    };

    public static AiProviderException FromBody(string? body, int? status, string? requestId)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                return FromJson(document.RootElement, status, requestId);
            }
            catch (JsonException)
            {
            }
        }
        return FromJson(default, status, requestId);
    }

    public static AiProviderException FromJson(JsonElement body, int? status, string? requestId)
    {
        var detail = body;
        for (var depth = 0; depth < 4 && detail.ValueKind == JsonValueKind.Object; depth++)
        {
            if (detail.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object) detail = error;
            else if (detail.TryGetProperty("detail", out var nested) && nested.ValueKind == JsonValueKind.Object) detail = nested;
            else break;
        }

        // OAuth errors use a string `error`; Responses errors use `error.code`.
        var code = SafeIdentifier(detail.String("error") ?? detail.String("code")) ?? "api_error";
        var safeRequestId = SafeIdentifier(requestId, 160);
        if (Known.TryGetValue(code, out var known) || (Legacy.TryGetValue(code, out var modern) && Known.TryGetValue(modern, out known)))
            return new AiProviderException(code, known.Message, known.Retryable, status, safeRequestId);

        var param = SafeIdentifier(detail.String("param"));
        return status switch
        {
            400 or 422 => new AiProviderException(code, param is null ? "ChatGPT rejected this request." : $"ChatGPT rejected the request's {param} setting.", false, status, safeRequestId),
            401 => new AiProviderException(code, "ChatGPT did not accept the connection. Connect again.", false, status, safeRequestId),
            403 => new AiProviderException(code, "A ChatGPT policy or permission restriction prevented this request.", false, status, safeRequestId),
            429 => new AiProviderException(code, "Too many requests. Wait a moment before trying again.", true, status, safeRequestId),
            >= 500 => new AiProviderException(code, "ChatGPT is temporarily unavailable. Try again shortly.", true, status, safeRequestId),
            _ => new AiProviderException(code, "ChatGPT could not complete this request.", false, status, safeRequestId),
        };
    }

    private static string? SafeIdentifier(string? value, int maxLength = 100)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength) return null;
        if (!(char.IsAsciiLetterOrDigit(value[0]) || value[0] == '_')) return null;
        return value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '[' or ']' or '-') ? value : null;
    }

    internal static string? String(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

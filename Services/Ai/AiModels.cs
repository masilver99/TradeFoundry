using System.Text.Json.Nodes;

namespace TradeFoundry.Services.Ai;

/// <summary>An AI provider that can run a review request. Implementations never receive or expose raw credentials to pages.</summary>
public interface IAiReviewProvider
{
    string Id { get; }
    string DisplayName { get; }
    AiConnectionStatus GetStatus();
    Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellationToken);
    Task<AiResult> StreamAsync(AiRequest request, Action<string>? onDelta, CancellationToken cancellationToken);
    void SavePreferences(string? model, string? reasoningEffort, string? imageDetail);
    Task DisconnectAsync(CancellationToken cancellationToken);
}

public sealed record AiConnectionStatus
{
    public bool Connected { get; init; }
    /// <summary>The account granted plan usage (the DevKit's <c>sharing</c> flag). AI features are gated on this, not on sign-in alone.</summary>
    public bool CanRun { get; init; }
    public string? AccountEmail { get; init; }
    public string? AccountName { get; init; }
    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
    /// <summary><c>auto</c> (null), <c>high</c>, or <c>low</c>: how closely the model inspects screenshots.</summary>
    public string? ImageDetail { get; init; }
    public string? Problem { get; init; }
}

public sealed record AiModel(
    string Slug,
    string DisplayName,
    bool SupportsImages,
    IReadOnlyList<string> ReasoningLevels,
    string? DefaultReasoningLevel,
    int? ContextWindow,
    string? Description);

public enum AiRole
{
    User,
    Assistant,
    Developer
}

public abstract record AiContent;

public sealed record AiTextContent(string Text) : AiContent;

public static class AiImageDetail
{
    public const string Auto = "auto";
    public const string High = "high";
    public const string Low = "low";

    public static bool IsValid(string? value) => value is null or "" or Auto or High or Low;

    /// <summary>Null means the provider default (auto).</summary>
    public static string? Normalize(string? value) => value is High or Low ? value : null;
}

public sealed record AiImageContent(byte[] Data, string ContentType, string? Detail = null) : AiContent;

public sealed record AiMessage(AiRole Role, IReadOnlyList<AiContent> Content)
{
    public static AiMessage Text(AiRole role, string text) => new(role, [new AiTextContent(text)]);
}

/// <summary>Strict JSON-schema output. The schema must follow the Responses API strict-mode rules.</summary>
public sealed record AiJsonSchema(string Name, JsonNode Schema);

public sealed record AiRequest
{
    public required string Model { get; init; }
    public string? Instructions { get; init; }
    public required IReadOnlyList<AiMessage> Messages { get; init; }
    public string? ReasoningEffort { get; init; }
    public AiJsonSchema? OutputSchema { get; init; }
}

public sealed record AiUsage(long? InputTokens, long? OutputTokens, long? ReasoningTokens);

public sealed record AiResult(string Text, string? Model, AiUsage Usage, TimeSpan Elapsed);

/// <summary>
/// A provider failure with a stable code and a user-safe message. Server messages are deliberately not exposed:
/// validation errors can echo submitted journal content.
/// </summary>
public sealed class AiProviderException(string code, string message, bool retryable = false, int? status = null, string? requestId = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public int? Status { get; } = status;
    public string? RequestId { get; } = requestId;

    public bool IsUsageLimit => Code is "subscription_sharing_usage_limit_exceeded";

    /// <summary>The saved grant can no longer be used; the owner must connect again.</summary>
    public bool RequiresReconnect => Code is "invalid_grant" or "invalid_refresh_token" or "token_expired" or "refresh_token_expired"
        or "refresh_token_invalidated" or "refresh_token_reused" or "account_mismatch" or "not_connected";
}

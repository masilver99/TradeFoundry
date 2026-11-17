namespace TradeFoundry.Core;

public static class AiReviewRunStatus
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// One AI review request and its result. Runs are immutable once finished: they record exactly what prompt, model,
/// and evidence bundle produced the output, and never modify imported evidence or the trader's own review layer.
/// </summary>
public sealed class AiReviewRun
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public string Scope { get; init; } = "day";
    public DateOnly ReviewDate { get; init; }
    public string? ReviewKey { get; init; }
    public string PromptId { get; init; } = string.Empty;
    public string PromptName { get; init; } = string.Empty;
    /// <summary>The full instructions sent, so later prompt edits never change what a past run means.</summary>
    public string PromptSnapshot { get; init; } = string.Empty;
    public string ExtraInstruction { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string? ReasoningEffort { get; init; }
    public string BundleHash { get; init; } = string.Empty;
    /// <summary>JSON manifest of what was sent: trade count, journal size, images included and excluded.</summary>
    public string BundleManifestJson { get; init; } = "{}";
    public string Status { get; init; } = AiReviewRunStatus.Running;
    public string OutputText { get; init; } = string.Empty;
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? ReasoningTokens { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }

    public bool IsFinished => Status != AiReviewRunStatus.Running;
}

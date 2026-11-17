using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services.Ai;

public sealed record DayReviewStartRequest(string? PromptId, string? ExtraInstruction, AiImageSelection? Images = null);

public sealed record AiReviewProgress(Guid RunId, string Status, int CharactersReceived, double ElapsedSeconds, string? ErrorMessage);

public sealed record DayReviewPreview(
    DayReviewManifest Manifest,
    AiConnectionStatus Connection,
    AiModel? Model,
    string? ReasoningEffort,
    IReadOnlyList<AiReviewPrompt> Prompts,
    string DefaultPromptId,
    IReadOnlyList<AiReviewRun> Runs,
    Guid? ActiveRunId,
    string? Problem,
    int MaxImages);

/// <summary>
/// Runs AI day reviews in the background and records each one immutably in <c>ai_review_runs</c>.
/// One run per journal day at a time; progress is kept in memory while a run streams.
/// </summary>
public sealed class AiReviewService
{
    public const int MaxExtraInstructionLength = 2000;
    private const int MaxConcurrentRuns = 2;

    private readonly TradeFoundryDb _database;
    private readonly TradeReviewService _reviews;
    private readonly DayReviewBundleBuilder _bundles;
    private readonly IAiReviewProvider _provider;
    private readonly AiPromptLibrary _prompts;
    private readonly AiOptions _options;
    private readonly ILogger<AiReviewService> _logger;
    private readonly ConcurrentDictionary<Guid, ActiveRun> _active = new();

    private sealed class ActiveRun(Guid journalId, DateOnly date, CancellationTokenSource cancellation)
    {
        public Guid JournalId { get; } = journalId;
        public DateOnly Date { get; } = date;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Stopwatch Elapsed { get; } = Stopwatch.StartNew();
        public int Characters;
        public Task Completion = Task.CompletedTask;
    }

    public AiReviewService(TradeFoundryDb database, TradeReviewService reviews, DayReviewBundleBuilder bundles, IAiReviewProvider provider,
        AiPromptLibrary prompts, IOptions<AiOptions> options, ILogger<AiReviewService> logger)
    {
        _prompts = prompts;
        _database = database;
        _reviews = reviews;
        _bundles = bundles;
        _provider = provider;
        _options = options.Value;
        _logger = logger;
        var interrupted = _database.FailInterruptedAiReviewRuns();
        if (interrupted > 0) _logger.LogInformation("Marked {Count} interrupted AI review run(s) as failed.", interrupted);
    }

    public bool Enabled => _options.Enabled;

    public async Task<DayReviewPreview> PreviewDayAsync(Guid journalId, DateOnly date, CancellationToken cancellationToken)
    {
        var day = _reviews.GetDay(journalId, date);
        var connection = _provider.GetStatus();
        var manifest = _bundles.Describe(day, null, MaxImages, connection.ImageDetail).Manifest;
        AiModel? model = null;
        string? problem = connection.Problem;
        if (connection.CanRun)
        {
            try { model = await ResolveModelAsync(connection, cancellationToken); }
            catch (AiProviderException ex) { problem = ex.Message; }
        }
        var runs = _database.GetAiReviewRunsForDay(journalId, date);
        var activeRunId = _active.FirstOrDefault(pair => pair.Value.JournalId == journalId && pair.Value.Date == date).Key;
        return new DayReviewPreview(manifest, connection, model, model is null ? null : EffectiveEffort(connection, model),
            _prompts.List(), _prompts.GetDefault().Id, runs, activeRunId == Guid.Empty ? null : activeRunId, problem, MaxImages);
    }

    /// <summary>Builds the bundle, records the run, and starts streaming in the background. Returns the new run.</summary>
    public async Task<AiReviewRun> StartDayReviewAsync(Guid journalId, DateOnly date, DayReviewStartRequest request, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) throw new AiProviderException("disabled", "AI review is turned off.");
        if (_active.Values.FirstOrDefault(run => run.JournalId == journalId && run.Date == date) is not null)
            throw new AiProviderException("already_running", "A review of this day is already running.");
        if (_active.Count >= MaxConcurrentRuns)
            throw new AiProviderException("busy", "Other AI reviews are still running. Try again when they finish.", retryable: true);

        var extra = (request.ExtraInstruction ?? string.Empty).Trim();
        if (extra.Length > MaxExtraInstructionLength)
            throw new AiProviderException("instruction_too_long", $"Keep the extra instruction under {MaxExtraInstructionLength} characters.");
        var prompt = _prompts.Find(request.PromptId) ?? _prompts.GetDefault();

        var connection = _provider.GetStatus();
        if (!connection.CanRun) throw new AiProviderException("not_connected", connection.Problem ?? "Connect an AI provider in Settings → AI review first.");
        var model = await ResolveModelAsync(connection, cancellationToken)
            ?? throw new AiProviderException("no_models", "No models are available for this connection.");
        var effort = EffectiveEffort(connection, model);

        var day = _reviews.GetDay(journalId, date);
        var bundle = _bundles.Describe(day, request.Images, MaxImages, connection.ImageDetail);
        if (!bundle.Manifest.HasContent) throw new AiProviderException("empty_day", "There is nothing to review on this day yet.");
        if (!model.SupportsImages && bundle.Manifest.IncludedImageCount > 0)
            throw new AiProviderException("model_text_only", $"{model.DisplayName} doesn't accept images. Choose another model in Settings → AI review or exclude the images.");

        var context = new AiPromptContext(date, day.Journal.Name, day.Journal.Currency, day.Journal.TimeZone, bundle.Manifest.Trades.Count);
        var instructions = AiReviewPrompts.BuildInstructions(prompt, extra, context);
        var (aiRequest, hash) = await _bundles.BuildRequestAsync(bundle, model.Slug, effort, instructions, cancellationToken, connection.ImageDetail);

        var run = new AiReviewRun
        {
            Id = Guid.NewGuid(),
            JournalId = journalId,
            Scope = "day",
            ReviewDate = date,
            PromptId = prompt.Id,
            PromptName = prompt.Name,
            PromptSnapshot = instructions,
            ExtraInstruction = extra,
            Provider = _provider.Id,
            Model = model.Slug,
            ReasoningEffort = effort,
            BundleHash = hash,
            BundleManifestJson = bundle.Manifest.ToJson(),
            Status = AiReviewRunStatus.Running,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        var active = new ActiveRun(journalId, date, new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(2, _options.RequestTimeoutMinutes + 1))));
        if (!_active.TryAdd(run.Id, active)) throw new InvalidOperationException("Run id collision.");
        try
        {
            _database.InsertAiReviewRun(run);
        }
        catch
        {
            _active.TryRemove(run.Id, out _);
            active.Cancellation.Dispose();
            throw;
        }
        active.Completion = Task.Run(() => ExecuteAsync(run.Id, aiRequest, active), CancellationToken.None);
        return run;
    }

    public AiReviewProgress? GetProgress(Guid journalId, Guid runId)
    {
        if (_active.TryGetValue(runId, out var active) && active.JournalId == journalId)
            return new AiReviewProgress(runId, AiReviewRunStatus.Running, Volatile.Read(ref active.Characters), active.Elapsed.Elapsed.TotalSeconds, null);
        var run = _database.GetAiReviewRun(journalId, runId);
        return run is null ? null : new AiReviewProgress(runId, run.Status, run.OutputText.Length,
            run.CompletedUtc is { } done ? (done - run.CreatedUtc).TotalSeconds : 0, run.ErrorMessage);
    }

    public bool Cancel(Guid journalId, Guid runId)
    {
        if (!_active.TryGetValue(runId, out var active) || active.JournalId != journalId) return false;
        active.Cancellation.Cancel();
        return true;
    }

    public AiReviewRun? GetRun(Guid journalId, Guid runId) => _database.GetAiReviewRun(journalId, runId);

    public bool DeleteRun(Guid journalId, Guid runId) => _database.DeleteAiReviewRun(journalId, runId);

    /// <summary>Waits for a background run to finish (tests and shutdown).</summary>
    internal Task WaitForRunAsync(Guid runId) => _active.TryGetValue(runId, out var active) ? active.Completion : Task.CompletedTask;

    private int MaxImages => Math.Clamp(_options.MaxImagesPerReview, 0, 50);

    private async Task ExecuteAsync(Guid runId, AiRequest request, ActiveRun active)
    {
        try
        {
            var result = await _provider.StreamAsync(request, delta => Interlocked.Add(ref active.Characters, delta.Length), active.Cancellation.Token);
            var parsed = DayReviewOutput.TryParse(result.Text);
            if (parsed is null) _logger.LogWarning("AI review {RunId} returned output that does not match the day review schema.", runId);
            _database.FinishAiReviewRun(runId, AiReviewRunStatus.Completed, result.Text,
                result.Usage.InputTokens, result.Usage.OutputTokens, result.Usage.ReasoningTokens, null, null);
        }
        catch (AiProviderException ex) when (ex.Code == "cancelled")
        {
            _database.FinishAiReviewRun(runId, AiReviewRunStatus.Cancelled, string.Empty, null, null, null, ex.Code, "Review cancelled.");
        }
        catch (AiProviderException ex)
        {
            _database.FinishAiReviewRun(runId, AiReviewRunStatus.Failed, string.Empty, null, null, null, ex.Code, ex.Message);
        }
        catch (OperationCanceledException)
        {
            _database.FinishAiReviewRun(runId, AiReviewRunStatus.Cancelled, string.Empty, null, null, null, "cancelled", "Review cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI review {RunId} failed unexpectedly.", runId);
            _database.FinishAiReviewRun(runId, AiReviewRunStatus.Failed, string.Empty, null, null, null, "unexpected", "The review failed unexpectedly. Try again.");
        }
        finally
        {
            _active.TryRemove(runId, out _);
            active.Cancellation.Dispose();
        }
    }

    private async Task<AiModel?> ResolveModelAsync(AiConnectionStatus connection, CancellationToken cancellationToken)
    {
        var models = await _provider.ListModelsAsync(cancellationToken);
        return models.FirstOrDefault(m => m.Slug == connection.Model)
            ?? models.FirstOrDefault(m => m.SupportsImages)
            ?? models.FirstOrDefault();
    }

    private static string? EffectiveEffort(AiConnectionStatus connection, AiModel model) =>
        connection.ReasoningEffort is { } effort && model.ReasoningLevels.Contains(effort) ? effort : null;
}

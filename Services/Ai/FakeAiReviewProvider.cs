using System.Diagnostics;
using System.Text;
using TradeFoundry.Core;

namespace TradeFoundry.Services.Ai;

/// <summary>Deterministic offline provider for tests and smoke hosts (<c>Ai:Provider=fake</c>). Never touches the network.</summary>
public sealed class FakeAiReviewProvider : IAiReviewProvider
{
    public const string ModelSlug = "fake-vision";
    private string? _model = ModelSlug;
    private string? _reasoningEffort;

    public string Id => AiOptions.FakeProvider;
    public string DisplayName => "Offline test provider";

    public AiConnectionStatus GetStatus() => new()
    {
        Connected = true,
        CanRun = true,
        AccountName = "Offline test provider",
        Model = _model,
        ReasoningEffort = _reasoningEffort,
    };

    public Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AiModel>>([new AiModel(ModelSlug, "Fake vision", true, ["low", "medium", "high"], "low", 100_000, "Deterministic offline responses.")]);

    public Task<AiResult> StreamAsync(AiRequest request, Action<string>? onDelta, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var texts = request.Messages.SelectMany(m => m.Content).OfType<AiTextContent>().ToArray();
        var images = request.Messages.SelectMany(m => m.Content).OfType<AiImageContent>().Count();
        var characters = texts.Sum(t => t.Text.Length) + (request.Instructions?.Length ?? 0);
        var text = request.OutputSchema is null
            ? $"Fake review: {request.Messages.Count} message(s), {images} image(s), {characters} characters."
            : $$"""{"fake":true,"messages":{{request.Messages.Count}},"images":{{images}}}""";
        foreach (var chunk in text.Chunk(16)) onDelta?.Invoke(new string(chunk));
        return Task.FromResult(new AiResult(text, request.Model, new AiUsage(characters / 4 + images * 1000, text.Length / 4, 0), stopwatch.Elapsed));
    }

    public void SavePreferences(string? model, string? reasoningEffort)
    {
        _model = string.IsNullOrWhiteSpace(model) ? ModelSlug : model;
        _reasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

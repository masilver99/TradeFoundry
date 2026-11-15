using System.Diagnostics;
using System.Text.Json.Nodes;
using TradeFoundry.Core;

namespace TradeFoundry.Services.Ai;

/// <summary>Deterministic offline provider for tests and smoke hosts (<c>Ai:Provider=fake</c>). Never touches the network.</summary>
public sealed class FakeAiReviewProvider : IAiReviewProvider
{
    public const string ModelSlug = "fake-vision";
    private string? _model = ModelSlug;
    private string? _reasoningEffort;
    private string? _imageDetail;

    public string Id => AiOptions.FakeProvider;
    public string DisplayName => "Offline test provider";

    public AiConnectionStatus GetStatus() => new()
    {
        Connected = true,
        CanRun = true,
        AccountName = "Offline test provider",
        Model = _model,
        ReasoningEffort = _reasoningEffort,
        ImageDetail = _imageDetail,
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
            : SchemaInstance(request.OutputSchema.Schema, $"Fake review of {request.Messages.Count} message(s) with {images} image(s).").ToJsonString();
        foreach (var chunk in text.Chunk(16)) onDelta?.Invoke(new string(chunk));
        return Task.FromResult(new AiResult(text, request.Model, new AiUsage(characters / 4 + images * 1000, text.Length / 4, 0), stopwatch.Elapsed));
    }

    public void SavePreferences(string? model, string? reasoningEffort, string? imageDetail)
    {
        _imageDetail = AiImageDetail.Normalize(imageDetail);
        _model = string.IsNullOrWhiteSpace(model) ? ModelSlug : model;
        _reasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Builds a minimal instance that satisfies a strict JSON schema: first enum value, one array item, the given text for strings.</summary>
    internal static JsonNode SchemaInstance(JsonNode schema, string text)
    {
        if (schema["enum"] is JsonArray choices && choices.Count > 0) return choices[0]!.DeepClone();
        var type = schema["type"] is JsonArray types ? types.Select(t => t!.GetValue<string>()).First(t => t != "null") : schema["type"]?.GetValue<string>();
        switch (type)
        {
            case "object":
                var result = new JsonObject();
                if (schema["properties"] is JsonObject properties)
                    foreach (var (name, property) in properties) result[name] = SchemaInstance(property!, text);
                return result;
            case "array":
                return schema["items"] is { } items ? new JsonArray(SchemaInstance(items, text)) : new JsonArray();
            case "integer":
            case "number":
                return JsonValue.Create(0);
            case "boolean":
                return JsonValue.Create(false);
            default:
                return JsonValue.Create(text);
        }
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ImageMagick;
using TradeFoundry.Core;

namespace TradeFoundry.Services.Ai;

public sealed record AiBundleImage
{
    /// <summary>Stable selection key: <c>daily:{imageId}</c>, <c>trade:{attachmentId}</c>, or <c>chart:{reviewKey}</c>.</summary>
    public string Id { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string? ReviewKey { get; init; }
    public int? TradeNumber { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Caption { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public long Bytes { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int EstimatedTokens { get; init; }
    /// <summary>Whether the image is sent when the trader hasn't chosen. Generated charts default off for trades with their own screenshot.</summary>
    public bool DefaultIncluded { get; init; } = true;
    public bool Included { get; init; }
    public string? ExcludedReason { get; init; }
    /// <summary>Chart bar interval, for generated charts.</summary>
    public string? Interval { get; init; }
    [JsonIgnore] public string? Path { get; init; }
    [JsonIgnore] public Func<byte[]?>? Render { get; init; }
    [JsonIgnore] public bool Available => Path is not null || Render is not null;
}

/// <summary>The trader's image choices. Images that weren't offered when they chose fall back to their defaults.</summary>
public sealed record AiImageSelection(IReadOnlySet<string> Offered, IReadOnlySet<string> Included)
{
    public bool? IsIncluded(string id) => Offered.Contains(id) ? Included.Contains(id) : null;

    public static AiImageSelection Excluding(params string[] ids) => new(new HashSet<string>(ids), new HashSet<string>());
}

public sealed record AiBundleTrade(int Number, string ReviewKey, string Label);

public sealed record DayReviewManifest
{
    public DateOnly Date { get; init; }
    public int CompletedTrades { get; init; }
    public int OpenTrades { get; init; }
    public int JournalCharacters { get; init; }
    public int EvidenceCharacters { get; init; }
    public IReadOnlyList<AiBundleTrade> Trades { get; init; } = [];
    public IReadOnlyList<AiBundleImage> Images { get; init; } = [];
    public int EstimatedInputTokens { get; init; }
    public string? ImageDetail { get; init; }
    public int IncludedImageCount => Images.Count(i => i.Included);
    public bool HasContent => CompletedTrades + OpenTrades > 0 || JournalCharacters > 0 || Images.Count > 0;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static DayReviewManifest? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<DayReviewManifest>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed record DayReviewBundle(string EvidenceText, DayReviewManifest Manifest);

/// <summary>Resolves stored journal images to files. Kept behind an interface so bundles can be tested without a file store.</summary>
public interface IDayReviewImageResolver
{
    (string Path, string ContentType, long Length)? ResolveDailyImage(Guid journalId, DateOnly date, Guid imageId);
    string? ResolveTradeAttachment(Guid journalId, TradeReviewAttachment attachment);
    BarQueryResult? GetTradeBars(Trade trade);
}

public sealed class DayReviewImageResolver(DailyJournalImageService dailyImages, TradeReviewService reviews, TradeFoundry.Data.TradeFoundryDb database) : IDayReviewImageResolver
{
    public BarQueryResult? GetTradeBars(Trade trade)
    {
        try { return TradeBarQueries.GetTradeBarQuery(database, trade, null); }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException) { return null; }
    }

    public (string Path, string ContentType, long Length)? ResolveDailyImage(Guid journalId, DateOnly date, Guid imageId) =>
        dailyImages.Get(journalId, date, imageId) is { } image && dailyImages.GetPath(journalId, image) is { } path
            ? (path, image.ContentType, image.Length)
            : null;

    public string? ResolveTradeAttachment(Guid journalId, TradeReviewAttachment attachment) => reviews.GetAttachmentPath(journalId, attachment);
}

/// <summary>
/// Turns one trading day into the evidence a reviewer model sees: a structured text document (journal, rating,
/// calculations, every trade with its executions and the trader's own review) plus labeled screenshots. Each image is
/// introduced by a text part naming its trade so the model can reconcile charts with recorded executions.
/// </summary>
public sealed partial class DayReviewBundleBuilder(IDayReviewImageResolver images)
{
    public const int DefaultMaxImages = 12;
    /// <summary>Measured: a 1920×1080 chart screenshot cost about 3,300 input tokens at default detail.</summary>
    public const int EstimatedTokensPerImage = 3300;
    private const int MaxImageEdge = 2048;

    public DayReviewBundle Describe(DailyReviewModel day, AiImageSelection? selection = null, int maxImages = DefaultMaxImages, string? imageDetail = null)
    {
        var journal = day.Journal;
        var zone = ResolveZone(journal.TimeZone);
        var trades = day.CompletedTrades.Concat(day.OpenTrades)
            .OrderBy(t => t.Trade.EntryUtc).ThenBy(t => t.Trade.Sequence).ThenBy(t => t.Trade.Id)
            .ToArray();

        // Images first, so the evidence text can refer to their labels. The trader's own screenshots come before
        // generated charts so they win when the image limit applies.
        var candidates = new List<AiBundleImage>();
        foreach (var (imageId, altText) in DailyJournalImageReferences(day.DailyJournal.Text))
        {
            if (images.ResolveDailyImage(journal.Id, day.Date, imageId) is not { } resolved) continue;
            candidates.Add(WithDimensions(new AiBundleImage
            {
                Id = $"daily:{imageId:D}", Kind = "daily", Caption = altText, ContentType = resolved.ContentType,
                Bytes = resolved.Length, Path = resolved.Path,
            }));
        }
        for (var index = 0; index < trades.Length; index++)
        {
            foreach (var attachment in trades[index].Attachments.OrderBy(a => a.CreatedUtc))
            {
                candidates.Add(WithDimensions(new AiBundleImage
                {
                    Id = $"trade:{attachment.Id:D}", Kind = "trade", ReviewKey = trades[index].Trade.ReviewKey, TradeNumber = index + 1,
                    Caption = attachment.Caption, ContentType = attachment.ContentType, Bytes = attachment.Length,
                    Path = images.ResolveTradeAttachment(journal.Id, attachment),
                }));
            }
        }
        for (var index = 0; index < trades.Length; index++)
        {
            var trade = trades[index].Trade;
            if (images.GetTradeBars(trade) is not { Bars.Count: > 0 } bars) continue;
            var number = index + 1;
            var title = $"Trade {number} · {(string.IsNullOrWhiteSpace(trade.Instrument) ? trade.Symbol : trade.Instrument)} {trade.Direction} {trade.Quantity} · {day.Date:yyyy-MM-dd}";
            candidates.Add(new AiBundleImage
            {
                Id = $"chart:{trade.ReviewKey}", Kind = "chart", ReviewKey = trade.ReviewKey, TradeNumber = number,
                ContentType = "image/png", Width = TradeChartImageRenderer.Width, Height = TradeChartImageRenderer.Height,
                Interval = bars.ResolvedInterval, DefaultIncluded = trades[index].Attachments.Count == 0,
                Render = () => TradeChartImageRenderer.Render(trade, bars.Bars, zone, bars.ResolvedInterval, title),
            });
        }

        var labeled = new List<AiBundleImage>();
        var label = 0;
        foreach (var candidate in candidates)
        {
            var chosen = selection?.IsIncluded(candidate.Id) ?? candidate.DefaultIncluded;
            string? reason = !candidate.Available ? "file missing"
                : !chosen ? (selection?.IsIncluded(candidate.Id) is null && !candidate.DefaultIncluded ? "off by default" : "excluded")
                : label >= maxImages ? $"over the {maxImages}-image limit"
                : null;
            labeled.Add(candidate with
            {
                Included = reason is null,
                ExcludedReason = reason,
                Label = reason is null ? $"Image {++label}" : string.Empty,
                EstimatedTokens = EstimateImageTokens(candidate.Width, candidate.Height, imageDetail),
            });
        }

        var journalText = LexicalPlainText.Extract(day.DailyJournal.Text).Trim();
        var evidence = BuildEvidence(day, trades, labeled, journalText, zone);
        var manifest = new DayReviewManifest
        {
            Date = day.Date,
            CompletedTrades = day.CompletedTradeCount,
            OpenTrades = day.OpenTradeCount,
            JournalCharacters = journalText.Length,
            EvidenceCharacters = evidence.Length,
            Trades = trades.Select((t, i) => new AiBundleTrade(i + 1, t.Trade.ReviewKey,
                $"{(string.IsNullOrWhiteSpace(t.Trade.Instrument) ? t.Trade.Symbol : t.Trade.Instrument)} {t.Trade.Direction} {TimeZoneInfo.ConvertTime(t.Trade.EntryUtc, zone):HH:mm}")).ToList(),
            Images = labeled,
            EstimatedInputTokens = EstimateTextTokens(evidence) + labeled.Where(i => i.Included).Sum(i => i.EstimatedTokens),
            ImageDetail = AiImageDetail.Normalize(imageDetail),
        };
        return new DayReviewBundle(evidence, manifest);
    }

    /// <summary>Rough: about four characters per token, plus the instructions and output schema.</summary>
    public static int EstimateTextTokens(string evidence) => evidence.Length / 4 + 1200;

    /// <summary>
    /// Rough per-image input cost. Calibrated on one measurement (a 1920×1080 screenshot ≈ 3,300 tokens at default detail)
    /// and scaled by pixel area after the 2048-pixel downscale. Low detail costs a small flat amount.
    /// </summary>
    public static int EstimateImageTokens(int? width, int? height, string? imageDetail)
    {
        if (imageDetail == AiImageDetail.Low) return 100;
        if (width is not > 0 || height is not > 0) return EstimatedTokensPerImage;
        var scale = Math.Min(1.0, MaxImageEdge / (double)Math.Max(width.Value, height.Value));
        var pixels = width.Value * scale * height.Value * scale;
        return (int)Math.Clamp(Math.Round(pixels / (1920.0 * 1080.0) * EstimatedTokensPerImage), 300, 6000);
    }

    private static AiBundleImage WithDimensions(AiBundleImage image)
    {
        if (image.Path is null) return image;
        try
        {
            var info = new MagickImageInfo(image.Path);
            return image with { Width = (int)info.Width, Height = (int)info.Height };
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException)
        {
            return image;
        }
    }

    /// <summary>Reads the included images and assembles the request. Returns a hash identifying the exact evidence sent.</summary>
    public async Task<(AiRequest Request, string BundleHash)> BuildRequestAsync(
        DayReviewBundle bundle, string model, string? reasoningEffort, string instructions, CancellationToken cancellationToken, string? imageDetail = null)
    {
        var detail = AiImageDetail.Normalize(imageDetail);
        var content = new List<AiContent> { new AiTextContent(bundle.EvidenceText) };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(bundle.EvidenceText));

        foreach (var image in bundle.Manifest.Images.Where(i => i.Included))
        {
            byte[] data;
            string contentType;
            if (image.Render is { } render)
            {
                data = render() ?? throw new InvalidOperationException($"The chart for Trade {image.TradeNumber} could not be drawn.");
                contentType = "image/png";
            }
            else
            {
                (data, contentType) = PrepareImage(await File.ReadAllBytesAsync(image.Path!, cancellationToken), image.ContentType);
            }
            hash.AppendData(SHA256.HashData(data));
            content.Add(new AiTextContent(ImageIntroduction(image)));
            content.Add(new AiImageContent(data, contentType, detail));
        }

        var request = new AiRequest
        {
            Model = model,
            ReasoningEffort = reasoningEffort,
            Instructions = instructions,
            OutputSchema = new AiJsonSchema(AiReviewPrompts.DayReviewSchemaName, AiReviewPrompts.DayReviewSchema()),
            Messages = [new AiMessage(AiRole.User, content)],
        };
        hash.AppendData(Encoding.UTF8.GetBytes(instructions));
        return (request, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    internal static string ImageIntroduction(AiBundleImage image)
    {
        var text = new StringBuilder(image.Label).Append(": ");
        text.Append(image.Kind switch
        {
            "daily" => "screenshot from the trader's daily journal",
            "chart" => $"candle chart of Trade {image.TradeNumber} ({image.ReviewKey}) drawn by TradeFoundry from imported {image.Interval} bars. " +
                "The triangle on the left marks the entry and the one on the right the exit, on the bars that contain them; the dotted line joins them; " +
                "dashed red and green lines are the initial stop and target. These are the recorded executions, not indicator signals",
            _ => $"screenshot attached to Trade {image.TradeNumber} ({image.ReviewKey})",
        });
        if (!string.IsNullOrWhiteSpace(image.Caption)) text.Append(". Trader's caption: \"").Append(OneLine(image.Caption)).Append('"');
        return text.Append('.').ToString();
    }

    /// <summary>Downscales very large screenshots; small ones are sent unchanged so chart labels stay legible.</summary>
    internal static (byte[] Data, string ContentType) PrepareImage(byte[] bytes, string contentType)
    {
        try
        {
            using var image = new MagickImage(bytes);
            if (Math.Max(image.Width, image.Height) <= MaxImageEdge) return (bytes, contentType);
            image.Resize(new MagickGeometry(MaxImageEdge, MaxImageEdge) { IgnoreAspectRatio = false });
            image.Format = MagickFormat.WebP;
            image.Quality = 90;
            return (image.ToByteArray(), "image/webp");
        }
        catch (MagickException)
        {
            return (bytes, contentType);
        }
    }

    /// <summary>Image nodes in the daily journal's Lexical state, in document order.</summary>
    internal static IReadOnlyList<(Guid ImageId, string AltText)> DailyJournalImageReferences(string? lexicalState)
    {
        var result = new List<(Guid, string)>();
        if (string.IsNullOrWhiteSpace(lexicalState)) return result;
        try
        {
            using var document = JsonDocument.Parse(lexicalState);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("root", out var root)) Walk(root);
        }
        catch (JsonException)
        {
        }
        return result.DistinctBy(r => r.Item1).ToList();

        void Walk(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return;
            if (node.String("type") == "daily-journal-image" && Guid.TryParse(node.String("attachmentId"), out var id))
                result.Add((id, node.String("altText")?.Trim() ?? string.Empty));
            if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                foreach (var child in children.EnumerateArray()) Walk(child);
        }
    }

    private static string BuildEvidence(DailyReviewModel day, DailyReviewTrade[] trades, IReadOnlyList<AiBundleImage> images, string journalText, TimeZoneInfo zone)
    {
        var journal = day.Journal;
        var currency = string.IsNullOrWhiteSpace(journal.Currency) ? "USD" : journal.Currency;
        var summary = day.Summary;
        var text = new StringBuilder();
        text.Append("# Trading day ").Append(day.Date.ToString("yyyy-MM-dd (dddd)", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("Journal: ").Append(OneLine(journal.Name)).Append(" · context: ").Append(journal.ExecutionContext)
            .Append(" · currency: ").Append(currency).Append(" · times are ").Append(zone.Id).Append(" local time\n\n");

        text.Append("## Day summary (TradeFoundry calculations)\n");
        text.Append("- Completed trades: ").Append(summary.CompletedCount).Append("; open trades: ").Append(summary.OpenCount).Append('\n');
        if (day.DailyTags.Tags.Count > 0) text.Append("- Trader's tags for the day: ").Append(string.Join(", ", day.DailyTags.Tags.Select(tag => OneLine(tag.Name)))).Append('\n');
        text.Append("- Realized net P&L: ").Append(Money(summary.RealizedNetPnl, currency)).Append('\n');
        text.Append("- Realized drawdown (peak to trough of closed-trade net P&L): ").Append(Money(summary.RealizedDrawdown, currency)).Append('\n');
        if (summary.WorstMae is { } worstMae)
            text.Append("- Worst MAE: ").Append(Money(worstMae, currency)).Append(" (MAE recorded on ").Append(summary.MaeCoverage).Append(" of ").Append(summary.CompletedCount).Append(" completed trades)\n");
        text.Append("- Trades the trader has reviewed: ").Append(summary.ReviewedCount).Append(" of ").Append(summary.TotalCount).Append('\n');
        if (summary.Equity.Count > 1)
            text.Append("- Cumulative realized net P&L after each completed trade, in exit order: ")
                .Append(string.Join(", ", summary.Equity.Skip(1).Select(v => v.ToString("0.00", CultureInfo.InvariantCulture)))).Append('\n');
        text.Append('\n');

        text.Append("## Trader's day rating\n");
        if (day.DailyRating.Rating is { } rating)
        {
            text.Append(rating).Append("/5 (").Append(DailyRating.Label(rating)).Append(')');
            if (!string.IsNullOrWhiteSpace(day.DailyRating.Description)) text.Append(": \"").Append(day.DailyRating.Description.Trim()).Append('"');
            text.Append("\n\n");
        }
        else
        {
            text.Append("Not rated.\n\n");
        }

        text.Append("## Trader's daily journal\n");
        text.Append(journalText.Length == 0 ? "No daily journal entry.\n" : $"\"\"\"\n{journalText}\n\"\"\"\n");
        var dailyImages = images.Where(i => i.Kind == "daily").ToArray();
        if (dailyImages.Length > 0) text.Append("Journal images: ").Append(string.Join("; ", dailyImages.Select(ImageReference))).Append('\n');
        text.Append('\n');

        var setups = trades.SelectMany(t => t.SetupWorkspace.Evaluations).Select(e => e.Setup).DistinctBy(s => s.Id).ToArray();
        if (setups.Length > 0)
        {
            text.Append("## Setups referenced today\n");
            foreach (var setup in setups)
            {
                text.Append("- ").Append(OneLine(setup.Name));
                if (!string.IsNullOrWhiteSpace(setup.ShortDescription)) text.Append(": ").Append(OneLine(setup.ShortDescription));
                text.Append('\n');
            }
            text.Append('\n');
        }

        text.Append("## Trades in entry order\n");
        if (trades.Length == 0) text.Append("No trades on this day.\n");
        for (var index = 0; index < trades.Length; index++)
            AppendTrade(text, index + 1, trades[index], images.Where(i => i.Kind is "trade" or "chart" && i.TradeNumber == index + 1).ToArray(), zone, currency);
        // Journal content must not be able to close the evidence block and pose as instructions.
        var body = EvidenceTag().Replace(text.ToString(), match => match.Value.Replace('<', '‹').Replace('>', '›'));
        return $"<journal_evidence>\n{body}</journal_evidence>";
    }

    [GeneratedRegex(@"</?\s*journal_evidence\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex EvidenceTag();

    private static void AppendTrade(StringBuilder text, int number, DailyReviewTrade item, AiBundleImage[] tradeImages, TimeZoneInfo zone, string currency)
    {
        var trade = item.Trade;
        var p = CultureInfo.InvariantCulture;
        text.Append("\n### Trade ").Append(number).Append(" · review key ").Append(trade.ReviewKey).Append('\n');
        text.Append("- ").Append(OneLine(string.IsNullOrWhiteSpace(trade.Instrument) ? trade.Symbol : $"{trade.Instrument} ({trade.Symbol})"))
            .Append(", ").Append(trade.Direction).Append(' ').Append(trade.Quantity).Append(trade.Quantity == 1 ? " contract" : " contracts")
            .Append(", status ").Append(trade.Status).Append('\n');
        text.Append("- Entry ").Append(Local(trade.EntryUtc, zone)).Append(" @ ").Append(trade.EntryPrice.ToString(p));
        if (trade.ExitUtc is { } exit)
        {
            text.Append("; exit ").Append(Local(exit, zone));
            if (trade.ExitPrice is { } exitPrice) text.Append(" @ ").Append(exitPrice.ToString(p));
            if (!string.IsNullOrWhiteSpace(trade.ExitType)) text.Append(" (").Append(trade.ExitType).Append(')');
            if (trade.Duration is { } duration) text.Append("; held ").Append(Duration(duration));
        }
        text.Append('\n');
        text.Append("- Net P&L ").Append(Money(trade.NetPnl, currency)).Append(" (gross ").Append(Money(trade.GrossPnl, currency))
            .Append(", fees ").Append(Money(trade.Fees, currency)).Append("); points ").Append(trade.GrossPoints.ToString(p));
        if (trade.RMultiple is { } r) text.Append("; R multiple ").Append(r.ToString("0.##", p));
        text.Append('\n');

        var risk = new List<string>();
        if (trade.InitialStopPrice is { } stop) risk.Add($"initial stop {stop.ToString(p)}");
        if (trade.InitialTargetPrice is { } target) risk.Add($"initial target {target.ToString(p)}");
        if (trade.InitialRiskCurrency is { } riskCurrency) risk.Add($"initial risk {Money(riskCurrency, currency)}");
        else if (trade.InitialRiskPoints is { } riskPoints) risk.Add($"initial risk {riskPoints.ToString(p)} points");
        if (trade.MaePoints is { } mae) risk.Add($"MAE {mae.ToString(p)} points");
        if (trade.MfePoints is { } mfe) risk.Add($"MFE {mfe.ToString(p)} points");
        if (trade.EntryChasePoints is { } entryChase) risk.Add($"entry chase {entryChase.ToString(p)} points");
        if (trade.ExitChasePoints is { } exitChase) risk.Add($"exit chase {exitChase.ToString(p)} points");
        if (risk.Count > 0) text.Append("- ").Append(string.Join("; ", risk)).Append('\n');

        var review = item.Annotation;
        var reviewLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(review.Setup)) reviewLines.Add($"setup label: {OneLine(review.Setup)}");
        if (review.Tags.Count > 0) reviewLines.Add($"tags: {string.Join(", ", review.Tags.Select(OneLine))}");
        if (review.PlannedEntryPrice is { } plannedEntry) reviewLines.Add($"planned entry {plannedEntry.ToString(p)}");
        if (review.PlannedStopPrice is { } plannedStop) reviewLines.Add($"planned stop {plannedStop.ToString(p)}");
        if (review.PlannedTargetPrice is { } plannedTarget) reviewLines.Add($"planned target {plannedTarget.ToString(p)}");
        if (!string.IsNullOrWhiteSpace(review.PlanAdherence)) reviewLines.Add($"plan adherence: {OneLine(review.PlanAdherence)}");
        if (review.ProcessRating is { } processRating) reviewLines.Add($"trader's process rating {processRating}/5");
        if (reviewLines.Count > 0) text.Append("- Trader's review: ").Append(string.Join("; ", reviewLines)).Append('\n');
        AppendQuoted(text, "Mistakes the trader noted", review.Mistakes);
        AppendQuoted(text, "Lessons the trader noted", review.Lessons);
        AppendQuoted(text, "Trader's notes", LexicalPlainText.Extract(review.ReviewNote));
        AppendQuoted(text, "Imported order note", trade.Note);

        foreach (var evaluation in item.SetupWorkspace.Evaluations)
        {
            text.Append("- Setup checklist: ").Append(OneLine(evaluation.Setup.Name)).Append(" v").Append(evaluation.Version.Version)
                .Append(" (").Append(evaluation.Association.Role.ToString().ToLowerInvariant()).Append(")\n");
            foreach (var criterion in evaluation.Criteria)
            {
                text.Append("  - [").Append(StateLabel(criterion.EvaluationState)).Append("] ")
                    .Append(criterion.Criterion.CriterionType.ToString().ToLowerInvariant()).Append(' ')
                    .Append(criterion.Criterion.Stage.ToString().ToLowerInvariant()).Append(": ").Append(OneLine(criterion.Criterion.Name));
                if (!string.IsNullOrWhiteSpace(criterion.Note)) text.Append(" (note: ").Append(OneLine(criterion.Note)).Append(')');
                text.Append('\n');
            }
        }

        var screenshots = tradeImages.Where(i => i.Kind == "trade").ToArray();
        if (screenshots.Length > 0) text.Append("- Screenshots: ").Append(string.Join("; ", screenshots.Select(ImageReference))).Append('\n');
        if (tradeImages.FirstOrDefault(i => i.Kind == "chart" && i.Included) is { } chart)
            text.Append("- Bar chart: ").Append(chart.Label).Append(" (").Append(chart.Interval).Append(" bars, drawn from imported data)\n");
    }

    private static string ImageReference(AiBundleImage image)
    {
        var reference = image.Included ? image.Label : $"(not sent: {image.ExcludedReason})";
        return string.IsNullOrWhiteSpace(image.Caption) ? reference : $"{reference} \"{OneLine(image.Caption)}\"";
    }

    private static void AppendQuoted(StringBuilder text, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        text.Append("- ").Append(label).Append(":\n  \"\"\"\n  ").Append(value.Trim().ReplaceLineEndings("\n  ")).Append("\n  \"\"\"\n");
    }

    private static string StateLabel(CriterionEvaluationState state) => state switch
    {
        CriterionEvaluationState.Met => "met",
        CriterionEvaluationState.NotMet => "not met",
        CriterionEvaluationState.Unknown => "not assessed",
        _ => state.ToString().ToLowerInvariant(),
    };

    private static TimeZoneInfo ResolveZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }

    private static string Local(DateTimeOffset utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(utc, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes}m" : duration.TotalMinutes >= 1 ? $"{duration.Minutes}m {duration.Seconds}s" : $"{duration.Seconds}s";

    private static string Money(decimal value, string currency) => $"{value.ToString("0.00", CultureInfo.InvariantCulture)} {currency}";

    private static string OneLine(string? value) => (value ?? string.Empty).ReplaceLineEndings(" ").Trim();
}

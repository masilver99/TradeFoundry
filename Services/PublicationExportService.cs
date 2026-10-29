using System.Text.Json;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Publishing;

namespace TradeFoundry.Services;

public sealed class PublicationExportService(TradeFoundryDb database, DailyJournalImageService images, Microsoft.Extensions.Options.IOptions<BenchmarkOptions>? benchmarkOptions = null)
{
    public PublicationSnapshot Build(Guid journalId, PublishingSettings settings)
    {
        var journal = database.GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        var trades = database.GetAllTrades(journalId);
        var account = AccountLedgerService.Build(journal, database.GetAccountTransactions(journalId), trades, database.GetAccountBalanceEvents(journalId)).Summary;
        var sourceImages = new Dictionary<string, PublicationImage>();
        var entries = database.GetPublicationJournalEntries(journalId).ToDictionary(entry => entry.Date,
            entry => ConvertContent(entry.Text, id =>
            {
                if (!Guid.TryParse(id, out var imageId)) throw new InvalidOperationException($"Invalid image reference in journal entry {entry.Date:yyyy-MM-dd}.");
                var image = images.Get(journalId, entry.Date, imageId);
                var path = image is null ? null : images.GetPath(journalId, image);
                if (path is null) throw new InvalidOperationException($"Missing journal image in entry {entry.Date:yyyy-MM-dd}. Restore or remove it before publishing.");
                var key = imageId.ToString("N");
                try
                {
                    if (new FileInfo(path).Length is <= 0 or > PublicationJson.MaxImageBytes) throw new InvalidOperationException($"Journal image in entry {entry.Date:yyyy-MM-dd} is empty or exceeds 10 MB.");
                    if (!sourceImages.ContainsKey(key)) sourceImages.Add(key, new(key, image!.ContentType, File.ReadAllBytes(path)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { throw new InvalidOperationException($"The journal image in entry {entry.Date:yyyy-MM-dd} could not be read. Check that the image is available and TradeFoundry has permission to read it.", ex); }
                return key;
            }));
        var snapshot = BuildSnapshot(journal, trades, account, entries, sourceImages.Values.ToList(), settings, DateTimeOffset.UtcNow, BuildTradeChart);
        var indicators = TearSheetMetrics.Build(trades, [], database.GetAccountBalanceEvents(journalId),
            database.GetBenchmarkPoints(journalId, benchmarkOptions?.Value.DefaultSymbol ?? "^GSPC"), journal.StartingEquity, journal.TimeZone, journal.Currency);
        var metrics = indicators.Groups.SelectMany(group => group.Indicators).ToArray();
        return snapshot with { Dashboard = snapshot.Dashboard with
        {
            Alpha = metrics.FirstOrDefault(metric => metric.Label == "Alpha")?.Value,
            AnnualizedAlpha = metrics.FirstOrDefault(metric => metric.Label == "Alpha (ann.)")?.Value
        }};
    }

    public static PublicationSnapshot BuildSnapshot(Journal journal, IEnumerable<Trade> trades, AccountSummary account,
        IReadOnlyDictionary<DateOnly, List<PublicationNode>> entries, List<PublicationImage> images, PublishingSettings settings, DateTimeOffset now,
        Func<Trade, PublicationTradeChart?>? chartForTrade = null)
    {
        var trading = DailyTradeAggregation.Build(trades.Where(t => t.JournalId == journal.Id), journal.TimeZone).Where(d => d.CompletedTradeCount > 0).ToDictionary(d => d.Date);
        var dates = trading.Keys.Concat(entries.Where(e => HasContent(e.Value)).Select(e => e.Key)).Distinct().OrderBy(d => d).ToArray();
        var days = dates.Select(date =>
        {
            var rows = trading.TryGetValue(date, out var day) ? day.CompletedTrades.Select(t => new PublicationTrade(t.Symbol, t.Direction, t.Quantity, t.NetPnl)
            { Chart = chartForTrade?.Invoke(t) }).ToList() : [];
            return new PublicationDay
            {
                Date = date, NetPnl = rows.Sum(t => t.NetPnl), Trades = rows,
                Wins = rows.Count(t => t.NetPnl > 0), Losses = rows.Count(t => t.NetPnl < 0), Breakevens = rows.Count(t => t.NetPnl == 0),
                Content = entries.GetValueOrDefault(date) ?? []
            };
        }).ToList();
        var completed = days.SelectMany(d => d.Trades).ToArray();
        var wins = completed.Where(t => t.NetPnl > 0).Sum(t => t.NetPnl);
        var losses = -completed.Where(t => t.NetPnl < 0).Sum(t => t.NetPnl);
        var asOf = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneCatalog.Resolve(journal.TimeZone)).DateTime);
        decimal cumulative = 0;
        var snapshot = new PublicationSnapshot
        {
            JournalId = journal.Id, GeneratedUtc = now, AsOfDate = asOf, Title = settings.Title.Trim(), GoogleTagScript = settings.GoogleTagScript,
            IntroductionContent = ConvertContent(settings.Introduction,
                _ => throw new InvalidOperationException("Introduction images are not supported.")),
            Currency = journal.Currency, TimeZone = journal.TimeZone, Days = days, Images = images,
            Months = days.GroupBy(d => new DateOnly(d.Date.Year, d.Date.Month, 1)).Select(g => new PublicationMonth(g.Key, g.Sum(d => d.NetPnl), g.Sum(d => d.Trades.Count))).ToList(),
            Equity = days.Where(d => d.Trades.Count > 0).Select(d => new PublicationEquityPoint(d.Date, cumulative += d.NetPnl)).ToList(),
            Dashboard = new()
            {
                Balance = account.CurrentBalance,
                BalanceNote = account.CurrentBalance is null ? "Opening balance is unavailable." : account.HasInferredBaseline ? "Opening balance inferred from imported balance evidence." : "Opening balance plus cash flows and realized trading results.",
                LifetimeNetPnl = completed.Sum(t => t.NetPnl), CurrentMonthNetPnl = days.Where(d => d.Date.Year == asOf.Year && d.Date.Month == asOf.Month).Sum(d => d.NetPnl),
                CompletedTrades = completed.Length, WinRate = completed.Length == 0 ? null : (decimal)completed.Count(t => t.NetPnl > 0) / completed.Length * 100m,
                ProfitFactor = losses == 0 ? null : wins / losses
            }
        };
        PublicationValidation.Validate(snapshot);
        return snapshot;
    }

    private PublicationTradeChart? BuildTradeChart(Trade trade)
    {
        if (trade.ExitUtc is not { } exitUtc || trade.ExitPrice is not { } exitPrice) return null;
        var query = database.GetBarWindow(trade.JournalId, trade.Symbol, trade.EntryUtc.AddMinutes(-250), exitUtc.AddMinutes(250), "5m");
        if (query.ResolvedInterval != "5m") return null;
        var bars = query.Bars.OrderBy(b => b.EventUtc).ToArray();
        var entryIndex = Array.FindIndex(bars, b => b.EventUtc <= trade.EntryUtc && trade.EntryUtc < b.EventUtc.AddMinutes(5));
        var exitIndex = Array.FindIndex(bars, b => b.EventUtc <= exitUtc && exitUtc < b.EventUtc.AddMinutes(5));
        if (entryIndex < 0 || exitIndex < entryIndex) return null;
        var span = exitIndex - entryIndex + 1;
        var count = Math.Min(bars.Length, Math.Max(50, span + 10));
        if (count > 2000) return null;
        var first = Math.Clamp(entryIndex - (count - span) / 2, 0, bars.Length - count);
        return new()
        {
            Bars = bars.Skip(first).Take(count).Select(b => new PublicationChartBar(b.EventUtc.ToUnixTimeSeconds(), b.Open, b.High, b.Low, b.Close, b.Volume ?? 0)).ToList(),
            Entry = new(bars[entryIndex].EventUtc.ToUnixTimeSeconds(), trade.EntryPrice),
            Exit = new(bars[exitIndex].EventUtc.ToUnixTimeSeconds(), exitPrice)
        };
    }

    private static bool HasContent(IEnumerable<PublicationNode> nodes) => nodes.Any(node => node.Type == "image" || !string.IsNullOrWhiteSpace(node.Text) || HasContent(node.Children));

    public static List<PublicationNode> ConvertContent(string state, Func<string, string> resolveImage)
    {
        if (string.IsNullOrWhiteSpace(state)) return [];
        if (!state.TrimStart().StartsWith('{')) return [new() { Children = [new() { Type = "text", Text = state }] }];
        JsonDocument document;
        try { document = JsonDocument.Parse(state, new JsonDocumentOptions { MaxDepth = 48 }); }
        catch (JsonException)
        {
            if (state.TrimStart().StartsWith('{')) throw new InvalidOperationException("A daily journal entry contains malformed editor content.");
            return [new() { Children = [new() { Type = "text", Text = state }] }];
        }
        using (document)
        {
            if (!document.RootElement.TryGetProperty("root", out var root))
                throw new InvalidOperationException("A daily journal entry contains unsupported editor content.");
            return Children(root);
        }

        List<PublicationNode> Children(JsonElement element) => element.TryGetProperty("children", out var children)
            ? children.EnumerateArray().Select(ConvertNode).ToList() : [];
        string String(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        PublicationNode ConvertNode(JsonElement element)
        {
            var type = String(element, "type");
            var outputType = type switch
            {
                "paragraph" or "quote" or "code" or "text" or "linebreak" or "tab" => type,
                "heading" => String(element, "tag"),
                "list" => String(element, "tag"),
                "listitem" => "li",
                "link" or "autolink" => "link",
                "daily-journal-image" => "image",
                _ => throw new InvalidOperationException($"Unsupported daily journal content type: {type}. Update the publisher before publishing this entry.")
            };
            var format = element.TryGetProperty("format", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
            return new()
            {
                Type = outputType, Text = String(element, "text"), Format = format,
                Alignment = String(element, "format"), Url = outputType == "link" ? String(element, "url") : "",
                ImageId = outputType == "image" ? resolveImage(String(element, "attachmentId")) : "",
                Alt = outputType == "image" ? String(element, "altText") : "",
                Width = outputType == "image" && element.TryGetProperty("width", out var width) && width.ValueKind == JsonValueKind.Number ? width.GetInt32() : 0,
                ListStart = outputType == "ol" && element.TryGetProperty("start", out var start) && start.ValueKind == JsonValueKind.Number ? start.GetInt32() : 1,
                Children = Children(element)
            };
        }
    }
}




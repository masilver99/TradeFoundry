using System.Text.Json;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Publishing;

namespace TradeFoundry.Services;

public sealed class PublicationExportService(TradeFoundryDb database, DailyJournalImageService images, Microsoft.Extensions.Options.IOptions<BenchmarkOptions>? benchmarkOptions = null)
{
    /// <summary>Builds the outgoing snapshot; an active <paramref name="filter"/> limits trades, daily entries and statistics to its period and direction.</summary>
    public PublicationSnapshot Build(Guid journalId, PublishingSettings settings, JournalFilterScope? filter = null)
    {
        var journal = database.GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        var allTrades = database.GetAllTrades(journalId);
        // The balance is the account's real current balance, so it always comes from the whole ledger.
        var account = AccountLedgerService.Build(journal, database.GetAccountTransactions(journalId), allTrades, database.GetAccountBalanceEvents(journalId)).Summary;
        var filtered = filter is { IsActive: true };
        var trades = filtered ? filter!.Apply(allTrades) : allTrades;
        var sourceImages = new Dictionary<string, PublicationImage>();
        var entries = database.GetPublicationJournalEntries(journalId).Where(entry => !filtered || filter!.IncludesDate(entry.Date)).ToDictionary(entry => entry.Date,
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
        var reviews = database.GetTradeReviewAnnotations(journalId);
        var setups = new TradingSetupService(database);
        var attachments = new TradeReviewService(database);
        var tradeEntries = trades.Where(trade => trade.Status.Equals("closed", StringComparison.OrdinalIgnoreCase) && trade.ExitUtc.HasValue)
            .ToDictionary(trade => trade.ReviewKey, trade => reviews.TryGetValue(trade.ReviewKey, out var review)
                ? ConvertContent(review.ReviewNote, id =>
                {
                    if (!Guid.TryParse(id, out var imageId)) throw new InvalidOperationException("Invalid image reference in a trade journal entry.");
                    var image = attachments.GetAttachment(journalId, imageId);
                    if (image is null || image.ReviewKey != trade.ReviewKey)
                        throw new InvalidOperationException("A trade journal image is missing or belongs to another trade. Restore or remove it before publishing.");
                    var path = attachments.GetAttachmentPath(journalId, image);
                    if (path is null) throw new InvalidOperationException("A trade journal image is missing. Restore or remove it before publishing.");
                    var key = imageId.ToString("N");
                    try
                    {
                        if (new FileInfo(path).Length is <= 0 or > PublicationJson.MaxImageBytes)
                            throw new InvalidOperationException("A trade journal image is empty or exceeds 10 MB.");
                        if (!sourceImages.ContainsKey(key)) sourceImages.Add(key, new(key, image.ContentType, File.ReadAllBytes(path)));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { throw new InvalidOperationException("A trade journal image could not be read. Restore or remove it before publishing.", ex); }
                    return key;
                }) : []);
        var snapshot = BuildSnapshot(journal, trades, account, entries, sourceImages.Values.ToList(), settings, DateTimeOffset.UtcNow, BuildTradeChart,
            trade => tradeEntries.GetValueOrDefault(trade.ReviewKey) ?? [], trade =>
            {
                var label = reviews.GetValueOrDefault(trade.ReviewKey)?.Setup ?? string.Empty;
                var definitions = setups.GetTradeSetupWorkspace(journalId, trade.Id).Evaluations
                    .OrderBy(item => item.Association.Role).ThenBy(item => item.Setup.Name, StringComparer.Ordinal)
                    .Select(item => new PublicationSetup(item.Setup.Name, item.Version.Version, item.Association.Role.ToString(),
                        item.Criteria.Select(criterion => new PublicationSetupCriterion(criterion.Criterion.Name,
                            criterion.Criterion.CriterionType.ToString(), criterion.Criterion.Stage.ToString(), criterion.EvaluationState.ToString())).ToList())).ToList();
                return string.IsNullOrWhiteSpace(label) && definitions.Count == 0 ? null : new PublicationTradeSetupInfo(label, definitions);
            });
        var balanceEvents = filtered ? [] : database.GetAccountBalanceEvents(journalId);
        var benchmarkSource = database.GetBenchmarkPoints(journalId, benchmarkOptions?.Value.DefaultSymbol ?? "^GSPC");
        var indicators = TearSheetMetrics.Build(trades, [], balanceEvents, benchmarkSource, journal.StartingEquity, journal.TimeZone, journal.Currency);
        var comparison = indicators.Benchmark;
        var baseline = journal.StartingEquity ?? balanceEvents.Where(e => e.Balance.HasValue).OrderBy(e => e.EventUtc).ThenBy(e => e.RowNumber).Select(e => e.Balance).FirstOrDefault();
        var withBalances = snapshot with
        {
            // The opening balance comes from the whole ledger, like the dashboard balance, so monthly returns are against the real account.
            Months = OpeningBalances(journal, allTrades, account.OpeningBalance, snapshot.Months),
            Dashboard = snapshot.Dashboard with
            {
                Alpha = comparison.AlphaPercent is { } alpha ? $"{alpha.ToString("0.00", Invariant)}%" : null,
                AnnualizedAlpha = comparison.AnnualizedAlpha is { } annualized ? $"{(annualized * 100m).ToString("0.0", Invariant)}%" : null
            },
            Benchmark = Benchmark(comparison, benchmarkSource, baseline, snapshot),
            TearSheet = settings.IncludeTearSheet ? TearSheet(indicators) : null
        };
        PublicationValidation.Validate(withBalances);
        return withBalances;
    }

    private static readonly System.Globalization.CultureInfo Invariant = System.Globalization.CultureInfo.InvariantCulture;

    private List<PublicationMonth> OpeningBalances(Journal journal, IReadOnlyList<Trade> trades, decimal? accountOpening, IEnumerable<PublicationMonth> months)
    {
        var zone = TimeZoneCatalog.Resolve(journal.TimeZone);
        var transactions = database.GetAccountTransactions(journal.Id);
        var balances = database.GetAccountBalanceEvents(journal.Id);
        DateTimeOffset Start(DateOnly date) => new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone));
        return months.Select(month =>
        {
            var period = AccountLedgerService.Build(journal, transactions, trades, balances,
                pageSize: 1, windowStartUtc: Start(month.Month), windowEndUtcExclusive: Start(month.Month.AddMonths(1))).Period;
            // The ledger reports no balance before its first entry; when the ledger starts inside this month, it opens at the account's opening balance.
            var opening = period is null ? null : period.DisplayOpeningBalance ?? (period.RowCount > 0 ? accountOpening : null);
            return month with { OpeningBalance = opening };
        }).ToList();
    }

    /// <summary>Benchmark closes over the published trading period, plus the comparison statistics.</summary>
    public static PublicationBenchmark? Benchmark(TearSheetBenchmark comparison, IEnumerable<BenchmarkPoint> source, decimal? startingBalance, PublicationSnapshot snapshot)
    {
        if (comparison.Ticker is not { Length: > 0 and <= 32 } ticker || snapshot.Equity.Count == 0) return null;
        var first = snapshot.Equity[0].Date.AddDays(-7);
        var points = source.Where(p => p.Value > 0m && string.Equals(p.Symbol, ticker, StringComparison.OrdinalIgnoreCase))
            .Select(p => (Date: DateOnly.FromDateTime(p.EventUtc.UtcDateTime.Date), p.Value, p.EventUtc))
            .Where(p => p.Date >= first && p.Date <= snapshot.AsOfDate)
            .GroupBy(p => p.Date).OrderBy(g => g.Key)
            .Select(g => new PublicationBenchmarkPoint(g.Key, g.OrderBy(p => p.EventUtc).Last().Value)).ToList();
        if (points.Count < 2) return null;
        return new()
        {
            Symbol = ticker, StartingBalance = startingBalance is > 0m ? startingBalance : null, Points = points,
            StrategyReturnPercent = comparison.StrategyReturnPercent, BenchmarkReturnPercent = comparison.BenchmarkReturnPercent,
            AlphaPercent = comparison.AlphaPercent, AnnualizedAlphaPercent = comparison.AnnualizedAlpha * 100m,
            Beta = comparison.Beta, Correlation = comparison.Correlation
        };
    }

    private static readonly string[] PublishedGroups = ["indicators-performance", "indicators-trade-dynamics", "indicators-calendar", "indicators-r-multiple"];

    /// <summary>Tear-sheet groups suitable for the public site; execution needs order data and the benchmark has its own section.</summary>
    public static List<PublicationMetricGroup>? TearSheet(TearSheetIndicatorSet indicators)
    {
        static string Clip(string? value, int max) => value is null ? "" : value.Length <= max ? value : value[..max];
        static PublicationMetricGroup Group(string title, string description, IEnumerable<TearSheetIndicator> source) => new(Clip(title, 120), Clip(description, 500),
            source.Where(i => i.Tone != "muted" && i.Value != "—" && !string.IsNullOrWhiteSpace(i.Label)).Take(60)
                .Select(i => new PublicationMetric(Clip(i.Label, 120), Clip(i.Value, 60), Clip(i.Note, 300), i.Tone is "positive" or "negative" or "warning" ? i.Tone : "neutral")).ToList());
        var groups = new[] { Group("Risk & stability", "Drawdown, volatility and tail-risk measures from daily results.", indicators.RiskStability) }
            .Concat(PublishedGroups.Select(id => indicators.Groups.FirstOrDefault(g => g.Id == id)).OfType<TearSheetIndicatorGroup>()
                .Select(g => Group(g.Title, g.Description, g.Indicators)))
            .Where(g => g.Metrics.Count > 0).ToList();
        return groups.Count == 0 ? null : groups;
    }

    public static PublicationSnapshot BuildSnapshot(Journal journal, IEnumerable<Trade> trades, AccountSummary account,
        IReadOnlyDictionary<DateOnly, List<PublicationNode>> entries, List<PublicationImage> images, PublishingSettings settings, DateTimeOffset now,
        Func<Trade, PublicationTradeChart?>? chartForTrade = null, Func<Trade, List<PublicationNode>>? journalForTrade = null,
        Func<Trade, PublicationTradeSetupInfo?>? setupForTrade = null)
    {
        var journalTrades = trades.Where(t => t.JournalId == journal.Id).ToArray();
        var trading = DailyTradeAggregation.Build(journalTrades, journal.TimeZone).Where(d => d.CompletedTradeCount > 0).ToDictionary(d => d.Date);
        var intradayLows = DailyHeatMetrics.Build(journalTrades, journal.TimeZone).Days.ToDictionary(d => d.Date, d => d.WorstPoint);
        var dates = trading.Keys.Concat(entries.Where(e => HasContent(e.Value)).Select(e => e.Key)).Distinct().OrderBy(d => d).ToArray();
        var days = dates.Select(date =>
        {
            var rows = trading.TryGetValue(date, out var day) ? day.CompletedTrades.Select(t => WithExecution(new PublicationTrade(t.Symbol, t.Direction, t.Quantity, t.NetPnl)
            { Chart = chartForTrade?.Invoke(t), JournalContent = journalForTrade?.Invoke(t) ?? [], Setup = setupForTrade?.Invoke(t) }, t)).ToList() : [];
            return new PublicationDay
            {
                Date = date, NetPnl = rows.Sum(t => t.NetPnl), Trades = rows,
                IntradayLow = rows.Count > 0 && intradayLows.TryGetValue(date, out var low) ? low : null,
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
                Balance = account.DisplayBalance,
                BalanceNote = account.DisplayBalance is null ? "Opening balance is unavailable." : account.IsBrokerAnchored ? "Latest broker-reported balance plus any later recorded activity." : account.HasInferredBaseline ? "Opening balance inferred from imported balance evidence." : "Opening balance plus cash flows and realized trading results.",
                LifetimeNetPnl = completed.Sum(t => t.NetPnl), CurrentMonthNetPnl = days.Where(d => d.Date.Year == asOf.Year && d.Date.Month == asOf.Month).Sum(d => d.NetPnl),
                CompletedTrades = completed.Length, WinRate = completed.Length == 0 ? null : (decimal)completed.Count(t => t.NetPnl > 0) / completed.Length * 100m,
                ProfitFactor = losses == 0 ? null : wins / losses
            }
        };
        PublicationValidation.Validate(snapshot);
        return snapshot;
    }

    private static PublicationTrade WithExecution(PublicationTrade published, Trade trade) =>
        trade.ExitUtc is { } exit && trade.ExitPrice is { } exitPrice && trade.EntryUtc <= exit
            ? published with { EntryUtc = trade.EntryUtc, ExitUtc = exit, EntryPrice = trade.EntryPrice, ExitPrice = exitPrice }
            : published;

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




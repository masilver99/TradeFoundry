using System.Globalization;

namespace TradeFoundry.Core;

public static class TradeFoundryConstants
{
    public const string OwnerUserId = "owner";
    public const string SierraChart = "sierra_chart";
    public const string TradingView = "tradingview";
    public const string Benchmark = "benchmark";
    public const string Ohlcv = "ohlcv";
    public const string YahooFinanceBenchmarkSource = "Yahoo Finance chart API";
    public const string SierraFills = "Sierra Chart fills";
    public const string SierraOhlcBars = "Sierra Chart OHLC bars";
    public const string TradingViewAccount = "TradingView account history";
    public const string TradingViewStrategy = "TradingView Strategy Tester";
    public const string OhlcvBars = "OHLCV bars";
    public const string BenchmarkSeries = "Benchmark daily series";

    public static string SourceApplicationName(string key) => key switch
    {
        SierraChart => "Sierra Chart",
        TradingView => "TradingView",
        Benchmark => "Benchmark series",
        Ohlcv => "OHLCV bars",
        _ => string.IsNullOrWhiteSpace(key) ? "Unknown source" : key
    };
}

public sealed class AppUser
{
    public string Id { get; init; } = TradeFoundryConstants.OwnerUserId;
    public string DisplayName { get; init; } = "Owner";
}

public sealed class Journal
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ExecutionContext { get; init; } = "live";
    public string Labels { get; init; } = string.Empty;
    public string DescriptionLexicalStateJson { get; init; } = string.Empty;
    public string TimeZone { get; init; } = "UTC";
    public string Currency { get; init; } = "USD";
    public string GroupingPolicy { get; init; } = "flat_to_flat";
    public decimal? StartingEquity { get; init; }
    public string ImportWatchDirectory { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
}

public sealed class MaeTargetSettings
{
    public decimal? DefaultPerContract { get; init; }
    public IReadOnlyDictionary<string, decimal> InstrumentTargets { get; init; }
        = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

    public decimal? Resolve(string instrument)
    {
        var root = InstrumentCatalog.ExtractRoot(instrument);
        return InstrumentTargets.TryGetValue(root, out var target) ? target : DefaultPerContract;
    }
}

public sealed class WatchedImportFileStatus
{
    public Guid JournalId { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset UpdatedUtc { get; init; }
    public Guid? ImportBatchId { get; init; }
}

public sealed class WatchedImportRequest
{
    public Guid JournalId { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
}

public sealed class ImportBatch
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string SourceApplication { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public DateTimeOffset ImportedUtc { get; init; }
    public int TotalRows { get; init; }
    public int NewRows { get; init; }
    public int DuplicateRows { get; init; }
    public string Status { get; init; } = "completed";
    public string Message { get; init; } = string.Empty;
}

public sealed class Fill
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public Guid ImportBatchId { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public string SourceKey { get; init; } = string.Empty;
    public string ActivityType { get; init; } = "Fills";
    public string OrderActionSource { get; init; } = string.Empty;
    public DateTimeOffset EventUtc { get; init; }
    public DateTimeOffset? TransactionUtc { get; init; }
    public string SourceTimeText { get; init; } = string.Empty;
    public string SourceTimeframe { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string Account { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public decimal Price { get; init; }
    public decimal? Price2 { get; init; }
    public int? FilledQuantity { get; init; }
    public string OpenClose { get; init; } = string.Empty;
    public string OrderType { get; init; } = string.Empty;
    public string OrderStatus { get; init; } = string.Empty;
    public string ParentOrderId { get; init; } = string.Empty;
    public decimal? High { get; init; }
    public decimal? Low { get; init; }
    public string Note { get; init; } = string.Empty;
    public int? PositionQuantity { get; init; }
    public string OrderId { get; init; } = string.Empty;
    public string ServiceOrderId { get; init; } = string.Empty;
    public string ExchangeOrderId { get; init; } = string.Empty;
    public string FillExecutionId { get; init; } = string.Empty;
    public string ClientOrderId { get; init; } = string.Empty;
    public string TimeInForce { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public bool? IsAutomated { get; init; }
    public decimal? AccountBalance { get; init; }
    public decimal Fees { get; init; }
    public decimal? ExchangeFeePerContract { get; init; }
    public decimal? NfaFeePerContract { get; init; }
    public decimal? ClearingFeePerContract { get; init; }
    public int RowNumber { get; init; }
    public string Instrument { get; init; } = string.Empty;
    public decimal PointValue { get; init; }
    public decimal TickSize { get; init; }
}

public sealed record TradeFillEvidence(DateTimeOffset EventUtc, string Side, int Quantity, decimal Price, decimal Fees, string SourceType);

public sealed class OrderEvent
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public Guid ImportBatchId { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public string SourceKey { get; init; } = string.Empty;
    public string OrderActionSource { get; init; } = string.Empty;
    public DateTimeOffset EventUtc { get; init; }
    public DateTimeOffset? TransactionUtc { get; init; }
    public string SourceTimeText { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string Account { get; init; } = string.Empty;
    public string InternalOrderId { get; init; } = string.Empty;
    public string ServiceOrderId { get; init; } = string.Empty;
    public string ParentOrderId { get; init; } = string.Empty;
    public string ExchangeOrderId { get; init; } = string.Empty;
    public string FillExecutionId { get; init; } = string.Empty;
    public string OrderType { get; init; } = string.Empty;
    public string OrderStatus { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
    public string OpenClose { get; init; } = string.Empty;
    public decimal? Price { get; init; }
    public decimal? Price2 { get; init; }
    public int? Quantity { get; init; }
    public int? FilledQuantity { get; init; }
    public decimal? FillPrice { get; init; }
    public int? PositionQuantity { get; init; }
    public string Note { get; init; } = string.Empty;
    public string ClientOrderId { get; init; } = string.Empty;
    public string TimeInForce { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public bool? IsAutomated { get; init; }
    public decimal Fees { get; init; }
    public int RowNumber { get; init; }
    public string Instrument { get; init; } = string.Empty;
}

public sealed class AccountBalanceEvent
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public Guid ImportBatchId { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public string SourceKey { get; init; } = string.Empty;
    public DateTimeOffset EventUtc { get; init; }
    public DateTimeOffset? TransactionUtc { get; init; }
    public string SourceTimeText { get; init; } = string.Empty;
    public string Account { get; init; } = string.Empty;
    public decimal? Balance { get; init; }
    public string Note { get; init; } = string.Empty;
    public int RowNumber { get; init; }
}

public sealed class BenchmarkPoint
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public Guid? ImportBatchId { get; init; }
    public Guid SeriesId { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public string SourceKey { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public DateTimeOffset EventUtc { get; init; }
    public decimal Value { get; init; }
    public string SourceTimeText { get; init; } = string.Empty;
    public int RowNumber { get; init; }
}

public sealed class BenchmarkSeriesStatus
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public string Symbol { get; init; } = string.Empty;
    public string Interval { get; init; } = "1d";
    public string Provider { get; init; } = string.Empty;
    public string SourceUrl { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? LastFetchedUtc { get; init; }
    public DateTimeOffset? LastAttemptedUtc { get; init; }
    public DateOnly? RequestedStart { get; init; }
    public DateOnly? RequestedEnd { get; init; }
    public string LastError { get; init; } = string.Empty;
    public int PointCount { get; init; }
    public DateOnly? FirstDate { get; init; }
    public DateOnly? LastDate { get; init; }

    public bool IsAutomatic => Provider.Equals(TradeFoundryConstants.YahooFinanceBenchmarkSource, StringComparison.OrdinalIgnoreCase);
}

public sealed class BenchmarkDownloadPoint
{
    public DateTimeOffset EventUtc { get; init; }
    public decimal Value { get; init; }
    public string SourceTimeText { get; init; } = string.Empty;
}

public sealed class Trade
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public Guid? ImportBatchId { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public string SourceKey { get; init; } = string.Empty;
    public string GroupingPolicy { get; init; } = "flat_to_flat";
    public int Sequence { get; init; }
    public string Symbol { get; init; } = string.Empty;
    public string Account { get; init; } = string.Empty;
    public string Direction { get; init; } = string.Empty;
    public DateTimeOffset EntryUtc { get; init; }
    public DateTimeOffset? ExitUtc { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal? ExitPrice { get; init; }
    public int Quantity { get; init; }
    public int ClosedQuantity { get; init; }
    public decimal GrossPoints { get; init; }
    public decimal AveragePoints { get; init; }
    public decimal GrossPnl { get; init; }
    public decimal ExchangeFees { get; init; }
    public decimal NfaFees { get; init; }
    public decimal ClearingFees { get; init; }
    public decimal Fees { get; init; }
    public decimal NetPnl { get; init; }
    public decimal? MaePoints { get; init; }
    public decimal? MfePoints { get; init; }
    public decimal PointValue { get; init; }
    public decimal TickSize { get; init; }
    public decimal? InitialStopPrice { get; init; }
    public decimal? InitialTargetPrice { get; init; }
    public decimal? InitialRiskPoints { get; init; }
    public decimal? InitialRiskCurrency { get; init; }
    public decimal? RMultiple { get; init; }
    public string ExitType { get; init; } = string.Empty;
    public decimal? EntryOrderPrice { get; init; }
    public decimal? ExitOrderPrice { get; init; }
    public decimal? EntryChasePoints { get; init; }
    public decimal? ExitChasePoints { get; init; }
    public string Status { get; init; } = "closed";
    public string Note { get; init; } = string.Empty;
    public string Instrument { get; init; } = string.Empty;
    public string ReviewKey { get; init; } = string.Empty;
    public string SourceTimeframe { get; init; } = string.Empty;
    public bool HasReviewImages { get; init; }
    public bool HasReviewNotes { get; init; }
    public TimeSpan? Duration => ExitUtc.HasValue ? ExitUtc.Value - EntryUtc : null;
    public TimeSpan? TimeInTrade => Duration;
    public double? TimeInTradeSeconds => Duration?.TotalSeconds;
    public bool IsWinner => NetPnl > 0m;
}

public sealed class McpAccessToken
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string TokenPrefix { get; init; } = string.Empty;
    public string Scopes { get; init; } = "read";
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? LastUsedUtc { get; init; }
    public DateTimeOffset? RevokedUtc { get; init; }
    public IReadOnlyList<Guid> JournalIds { get; init; } = Array.Empty<Guid>();
    public bool IsRevoked => RevokedUtc.HasValue;
}

public sealed class CreatedMcpAccessToken
{
    public McpAccessToken Token { get; init; } = new();
    public string Secret { get; init; } = string.Empty;
}

public sealed class Bar
{
    public Guid Id { get; init; }
    public Guid SeriesId { get; init; }
    public string Symbol { get; init; } = string.Empty;
    public string Interval { get; init; } = "source";
    public DateTimeOffset EventUtc { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public long? Volume { get; init; }
    public long? NumberOfTrades { get; init; }
    public long? BidVolume { get; init; }
    public long? AskVolume { get; init; }
}

public sealed class BarSeriesInfo
{
    public string Symbol { get; init; } = string.Empty;
    public string Interval { get; init; } = BarIntervals.Source;
    public int IntervalMinutes { get; init; }
    public DateTimeOffset? FirstEventUtc { get; init; }
    public DateTimeOffset? LastEventUtc { get; init; }
    public long BarCount { get; init; }
    public string Label => BarIntervals.Label(Interval);
}

public sealed class BarQueryResult
{
    public IReadOnlyList<Bar> Bars { get; init; } = Array.Empty<Bar>();
    public IReadOnlyList<BarSeriesInfo> AvailableSeries { get; init; } = Array.Empty<BarSeriesInfo>();
    public string RequestedInterval { get; init; } = BarIntervals.Source;
    public string ResolvedInterval { get; init; } = BarIntervals.Source;
    public bool IsConsolidated { get; init; }
    public bool IsCoarserThanRequested { get; init; }
    public string AvailabilityNote { get; init; } = string.Empty;
}

public sealed class BarHistoryPage
{
    public IReadOnlyList<Bar> Bars { get; init; } = Array.Empty<Bar>();
    public string ResolvedInterval { get; init; } = BarIntervals.Source;
    public bool HasMore { get; init; }
}

public sealed class OhlcCalendar
{
    public DateOnly StartMonth { get; init; }
    public IReadOnlyList<OhlcCalendarMonth> Months { get; init; } = Array.Empty<OhlcCalendarMonth>();
    public DateOnly PreviousStartMonth => StartMonth.AddMonths(-3);
    public DateOnly NextStartMonth => StartMonth.AddMonths(3);
    public string RangeLabel => Months.Count == 0
        ? StartMonth.ToString("MMMM yyyy", CultureInfo.InvariantCulture)
        : $"{StartMonth:MMM yyyy} – {Months[^1].Month:MMM yyyy}";
}

public sealed class OhlcCalendarMonth
{
    public DateOnly Month { get; init; }
    public IReadOnlyList<OhlcCalendarCell> Cells { get; init; } = Array.Empty<OhlcCalendarCell>();
    public string Label => Month.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
}

public sealed class OhlcCalendarCell
{
    public DateOnly? Date { get; init; }
    public string Coverage { get; init; } = "none";
    public long BarCount { get; init; }
    public int SeriesCount { get; init; }
    public string StatusLabel => Coverage switch
    {
        "full" => "Full data",
        "partial" => "Partial data",
        _ => "No data"
    };
    public string Title => !Date.HasValue
        ? string.Empty
        : $"{Date.Value.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)} · {StatusLabel} · {BarCount:N0} bar(s) across {SeriesCount} series";
}

public sealed class OhlcClearResult
{
    public long BarCount { get; init; }
    public long SeriesCount { get; init; }
    public long ImportBatchCount { get; init; }
}

public sealed class JournalOverview
{
    public Journal Journal { get; init; } = new();
    public IReadOnlyList<Trade> RecentTrades { get; init; } = Array.Empty<Trade>();
    public IReadOnlyList<ImportBatch> RecentImports { get; init; } = Array.Empty<ImportBatch>();
    public IReadOnlyList<string> Symbols { get; init; } = Array.Empty<string>();
    public IReadOnlyList<DailyPnl> DailyPnl { get; init; } = Array.Empty<DailyPnl>();
    public IReadOnlyList<EquityPoint> Equity { get; init; } = Array.Empty<EquityPoint>();
    public decimal NetPnl { get; init; }
    public decimal GrossPnl { get; init; }
    public decimal Points { get; init; }
    public decimal FeeAdjustedPoints { get; init; }
    public decimal ExchangeFees { get; init; }
    public decimal NfaFees { get; init; }
    public decimal ClearingFees { get; init; }
    public decimal Fees { get; init; }
    public decimal? StartingEquity { get; init; }
    public int ClosedTradeCount { get; init; }
    public int OpenTradeCount { get; init; }
    public int TradingDays { get; init; }
    public int WinningTrades { get; init; }
    public int LosingTrades { get; init; }
    public decimal WinRate => ClosedTradeCount == 0 ? 0m : (decimal)WinningTrades / ClosedTradeCount * 100m;
    public decimal ProfitFactor { get; init; }
    public double? EquityRSquared
    {
        get
        {
            // Fit cumulative net P&L against closed-trade order, matching the overview equity chart.
            if (Equity.Count < 3) return null;

            var meanPnl = Equity.Average(point => (double)point.CumulativePnl);
            var meanIndex = (Equity.Count - 1) / 2d;
            double covariance = 0d;
            double pnlVariance = 0d;
            double indexVariance = 0d;
            for (var index = 0; index < Equity.Count; index++)
            {
                var centeredIndex = index - meanIndex;
                var centeredPnl = (double)Equity[index].CumulativePnl - meanPnl;
                covariance += centeredIndex * centeredPnl;
                pnlVariance += centeredPnl * centeredPnl;
                indexVariance += centeredIndex * centeredIndex;
            }

            if (pnlVariance == 0d) return null;
            return Math.Clamp(covariance * covariance / (indexVariance * pnlVariance), 0d, 1d);
        }
    }
}

public sealed class EquityPoint
{
    public DateTimeOffset ExitUtc { get; init; }
    public decimal CumulativePnl { get; init; }
}

public enum AccountTransactionType
{
    Deposit,
    Withdrawal
}

public sealed class AccountTransaction
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public AccountTransactionType Type { get; init; }
    public DateTimeOffset EffectiveUtc { get; init; }
    public decimal Amount { get; init; }
    public string Note { get; init; } = string.Empty;
    public int Revision { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public DateTimeOffset? DeletedUtc { get; init; }
}

public sealed class AccountTransactionDraft
{
    public AccountTransactionType Type { get; init; }
    public DateTimeOffset EffectiveUtc { get; init; }
    public decimal Amount { get; init; }
    public string Note { get; init; } = string.Empty;
}

public sealed class AccountTransactionInput
{
    public AccountTransactionType Type { get; set; } = AccountTransactionType.Deposit;
    public DateTime EffectiveLocal { get; set; }
    public decimal Amount { get; set; }
    public string Note { get; set; } = string.Empty;
}

public sealed class AccountTransactionMutationResult
{
    public bool Saved { get; init; }
    public bool Conflict { get; init; }
    public bool NotFound { get; init; }
    public AccountTransaction? Transaction { get; init; }
}

public sealed class AccountTransactionHistoryEntry
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public Guid TransactionId { get; init; }
    public int Revision { get; init; }
    public string Action { get; init; } = string.Empty;
    public string BeforeJson { get; init; } = "{}";
    public string AfterJson { get; init; } = "{}";
    public DateTimeOffset CreatedUtc { get; init; }
}

public sealed class AccountSummary
{
    public Guid JournalId { get; init; }
    public decimal? CurrentBalance { get; init; }
    public decimal? OpeningBalance { get; init; }
    public string OpeningBalanceSource { get; init; } = string.Empty;
    public decimal RealizedProfit { get; init; }
    public decimal TotalDeposits { get; init; }
    public decimal TotalWithdrawals { get; init; }
    public decimal? RealizedTwrPercent { get; init; }
    public decimal? LatestReportedBalance { get; init; }
    public decimal? LatestReportedVariance { get; init; }
    public DateTimeOffset? LatestActivityUtc { get; init; }
    public bool HasActivity { get; init; }
    public bool HasInferredBaseline { get; init; }
    public bool HasPreBaselineActivity { get; init; }
    public string StatusMessage { get; init; } = string.Empty;
}

public sealed class AccountLedgerEntry
{
    public Guid? TransactionId { get; init; }
    public Guid? TradeId { get; init; }
    public Guid? ImportBatchId { get; init; }
    public Guid? SnapshotId { get; init; }
    public string TradeReviewKey { get; init; } = string.Empty;
    public DateTimeOffset EffectiveUtc { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Account { get; init; } = string.Empty;
    public decimal? Amount { get; init; }
    public decimal? CalculatedBalance { get; init; }
    public decimal? ReportedBalance { get; init; }
    public decimal? Variance { get; init; }
    public int Revision { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsOpeningBaseline { get; init; }
    public bool IsBeforeBaseline { get; init; }
    public string Note { get; init; } = string.Empty;
}

public sealed class AccountLedgerPage
{
    public AccountSummary Summary { get; init; } = new();
    public IReadOnlyList<AccountLedgerEntry> Entries { get; init; } = Array.Empty<AccountLedgerEntry>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int PageCount => TotalCount == 0 ? 0 : (int)Math.Ceiling((double)TotalCount / PageSize);
}

public sealed class TradeQuery
{
    public Guid JournalId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
    public string Search { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string Direction { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Sort { get; init; } = "entry_desc";
}

public sealed class ImportQuery
{
    public Guid JournalId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public string Search { get; init; } = string.Empty;
}

public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int PageCount => TotalCount == 0 ? 0 : (int)Math.Ceiling((double)TotalCount / PageSize);
}

public sealed class DailyPnl
{
    public DateOnly Date { get; init; }
    public decimal NetPnl { get; init; }
    public int TradeCount { get; init; }
}

public sealed class DailyTradeSummary
{
    public DateOnly Date { get; init; }
    public IReadOnlyList<Trade> CompletedTrades { get; init; } = Array.Empty<Trade>();
    public IReadOnlyList<Trade> OpenTrades { get; init; } = Array.Empty<Trade>();
    public decimal RealizedNetPnl => CompletedTrades.Sum(trade => trade.NetPnl);
    public decimal RealizedGrossPoints => CompletedTrades.Sum(trade => trade.GrossPoints);
    public decimal? RealizedMaeCurrency
    {
        get
        {
            var values = CompletedTrades
                .Where(trade => trade.MaePoints.HasValue)
                .Select(trade =>
                {
                    var instrument = string.IsNullOrWhiteSpace(trade.Instrument) ? trade.Symbol : trade.Instrument;
                    var pointValue = trade.PointValue > 0m ? trade.PointValue : InstrumentCatalog.Resolve(instrument).PointValue;
                    var quantity = Math.Max(1, trade.ClosedQuantity > 0 ? trade.ClosedQuantity : trade.Quantity);
                    return Math.Abs(trade.MaePoints!.Value) * pointValue * quantity;
                })
                .ToArray();
            // A daily MAE card represents the worst single completed position, not cumulative MAE across trades.
            return values.Length > 0 ? values.Max() : null;
        }
    }
    public int CompletedTradeCount => CompletedTrades.Count;
    public int OpenTradeCount => OpenTrades.Count;
    public int TotalTradeCount => CompletedTradeCount + OpenTradeCount;
}

public sealed class TradeReviewAnnotation
{
    public Guid JournalId { get; init; }
    public string ReviewKey { get; init; } = string.Empty;
    public int Revision { get; init; }
    public string ReviewNote { get; init; } = string.Empty;
    public string Setup { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public decimal? PlannedEntryPrice { get; init; }
    public decimal? PlannedStopPrice { get; init; }
    public decimal? PlannedTargetPrice { get; init; }
    public decimal? PlannedRiskPoints { get; init; }
    public decimal? PlannedRiskCurrency { get; init; }
    public decimal? ExchangeFees { get; init; }
    public decimal? NfaFees { get; init; }
    public decimal? ClearingFees { get; init; }
    public decimal? AllInCommission { get; init; }
    public string PlanAdherence { get; init; } = string.Empty;
    public int? ProcessRating { get; init; }
    public string Mistakes { get; init; } = string.Empty;
    public string Lessons { get; init; } = string.Empty;
    public DateTimeOffset? UpdatedUtc { get; init; }
    public bool HasContent => !string.IsNullOrWhiteSpace(ReviewNote)
        || !string.IsNullOrWhiteSpace(Setup)
        || Tags.Count > 0
        || PlannedEntryPrice.HasValue
        || PlannedStopPrice.HasValue
        || PlannedTargetPrice.HasValue
        || PlannedRiskPoints.HasValue
        || PlannedRiskCurrency.HasValue
        || ExchangeFees.HasValue
        || NfaFees.HasValue
        || ClearingFees.HasValue
        || AllInCommission.HasValue
        || !string.IsNullOrWhiteSpace(PlanAdherence)
        || ProcessRating.HasValue
        || !string.IsNullOrWhiteSpace(Mistakes)
        || !string.IsNullOrWhiteSpace(Lessons);
}

public sealed class TradeReviewPatch
{
    public string ReviewNote { get; set; } = string.Empty;
    public string Setup { get; set; } = string.Empty;
    public string TagsText { get; set; } = string.Empty;
    public decimal? PlannedEntryPrice { get; set; }
    public decimal? PlannedStopPrice { get; set; }
    public decimal? PlannedTargetPrice { get; set; }
    public decimal? PlannedRiskPoints { get; set; }
    public decimal? PlannedRiskCurrency { get; set; }
    public decimal? ExchangeFees { get; set; }
    public decimal? NfaFees { get; set; }
    public decimal? ClearingFees { get; set; }
    public decimal? AllInCommission { get; set; }
    public string PlanAdherence { get; set; } = string.Empty;
    public int? ProcessRating { get; set; }
    public string Mistakes { get; set; } = string.Empty;
    public string Lessons { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public int ExpectedRevision { get; set; }
}

public sealed class TradeReviewHistoryEntry
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public string ReviewKey { get; init; } = string.Empty;
    public int Revision { get; init; }
    public string Action { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string BeforeJson { get; init; } = "{}";
    public string AfterJson { get; init; } = "{}";
    public DateTimeOffset CreatedUtc { get; init; }
}

public sealed class TradeReviewAttachment
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public string ReviewKey { get; init; } = string.Empty;
    public string StorageKey { get; init; } = string.Empty;
    public string OriginalFileName { get; init; } = string.Empty;
    public string Caption { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public long Length { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
}

public sealed class DailyReviewTrade
{
    public Trade Trade { get; init; } = new();
    public ImportBatch? ImportBatch { get; init; }
    public TradeReviewAnnotation Annotation { get; init; } = new();
    public IReadOnlyList<TradeReviewAttachment> Attachments { get; init; } = Array.Empty<TradeReviewAttachment>();
    public IReadOnlyList<TradeReviewHistoryEntry> History { get; init; } = Array.Empty<TradeReviewHistoryEntry>();
}

public sealed class DailyReviewModel
{
    public Journal Journal { get; init; } = new();
    public DateOnly Date { get; init; }
    public DailyJournalEntry DailyJournal { get; init; } = new();
    public IReadOnlyList<DailyReviewTrade> CompletedTrades { get; init; } = Array.Empty<DailyReviewTrade>();
    public IReadOnlyList<DailyReviewTrade> OpenTrades { get; init; } = Array.Empty<DailyReviewTrade>();
    public decimal RealizedNetPnl => CompletedTrades.Sum(item => item.Trade.NetPnl);
    public int CompletedTradeCount => CompletedTrades.Count;
    public int OpenTradeCount => OpenTrades.Count;
    public DateOnly? PreviousDate { get; init; }
    public DateOnly? NextDate { get; init; }
}

public sealed class DailyJournalEntry
{
    public Guid JournalId { get; init; }
    public DateOnly Date { get; init; }
    public int Revision { get; init; }
    public string Text { get; init; } = string.Empty;
    public DateTimeOffset? UpdatedUtc { get; init; }
    public bool HasContent => !string.IsNullOrWhiteSpace(Text);
}

public sealed class DailyJournalSaveResult
{
    public bool Saved { get; init; }
    public bool Conflict { get; init; }
    public DailyJournalEntry Entry { get; init; } = new();
}

public sealed class TradeReviewSaveResult
{
    public bool Saved { get; init; }
    public bool Conflict { get; init; }
    public TradeReviewAnnotation Annotation { get; init; } = new();
}

public sealed class ImportResult
{
    public ImportBatch Batch { get; init; } = new();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string? ResolvedBarInterval { get; init; }
    public string ResolvedTimeZone { get; init; } = "UTC";
}

public sealed class FillDraft
{
    public string SourceType { get; init; } = string.Empty;
    public string SourceKey { get; init; } = string.Empty;
    public string ActivityType { get; init; } = "Fills";
    public string OrderActionSource { get; init; } = string.Empty;
    public DateTimeOffset EventUtc { get; init; }
    public DateTimeOffset? TransactionUtc { get; init; }
    public string SourceTimeText { get; init; } = string.Empty;
    public string SourceTimeframe { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string Account { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public decimal Price { get; init; }
    public decimal? Price2 { get; init; }
    public int? FilledQuantity { get; init; }
    public string OpenClose { get; init; } = string.Empty;
    public string OrderType { get; init; } = string.Empty;
    public string OrderStatus { get; init; } = string.Empty;
    public string ParentOrderId { get; init; } = string.Empty;
    public decimal? High { get; init; }
    public decimal? Low { get; init; }
    public string Note { get; init; } = string.Empty;
    public int? PositionQuantity { get; init; }
    public string OrderId { get; init; } = string.Empty;
    public string ServiceOrderId { get; init; } = string.Empty;
    public string ExchangeOrderId { get; init; } = string.Empty;
    public string FillExecutionId { get; init; } = string.Empty;
    public string ClientOrderId { get; init; } = string.Empty;
    public string TimeInForce { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public bool? IsAutomated { get; init; }
    public decimal? AccountBalance { get; init; }
    public decimal Fees { get; init; }
    public decimal? ExchangeFeePerContract { get; init; }
    public decimal? NfaFeePerContract { get; init; }
    public decimal? ClearingFeePerContract { get; init; }
    public int RowNumber { get; init; }
    public string Instrument { get; init; } = string.Empty;
    public decimal PointValue { get; init; }
    public decimal TickSize { get; init; }
}

public sealed class OrderEventDraft
{
    public string SourceType { get; init; } = string.Empty;
    public string SourceKey { get; init; } = string.Empty;
    public string OrderActionSource { get; init; } = string.Empty;
    public DateTimeOffset EventUtc { get; init; }
    public DateTimeOffset? TransactionUtc { get; init; }
    public string SourceTimeText { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string Account { get; init; } = string.Empty;
    public string InternalOrderId { get; init; } = string.Empty;
    public string ServiceOrderId { get; init; } = string.Empty;
    public string ParentOrderId { get; init; } = string.Empty;
    public string ExchangeOrderId { get; init; } = string.Empty;
    public string FillExecutionId { get; init; } = string.Empty;
    public string OrderType { get; init; } = string.Empty;
    public string OrderStatus { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
    public string OpenClose { get; init; } = string.Empty;
    public decimal? Price { get; init; }
    public decimal? Price2 { get; init; }
    public int? Quantity { get; init; }
    public int? FilledQuantity { get; init; }
    public decimal? FillPrice { get; init; }
    public int? PositionQuantity { get; init; }
    public string Note { get; init; } = string.Empty;
    public string ClientOrderId { get; init; } = string.Empty;
    public string TimeInForce { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public bool? IsAutomated { get; init; }
    public decimal Fees { get; init; }
    public int RowNumber { get; init; }
    public string Instrument { get; init; } = string.Empty;
}

public sealed class AccountBalanceDraft
{
    public string SourceType { get; init; } = string.Empty;
    public string SourceKey { get; init; } = string.Empty;
    public DateTimeOffset EventUtc { get; init; }
    public DateTimeOffset? TransactionUtc { get; init; }
    public string SourceTimeText { get; init; } = string.Empty;
    public string Account { get; init; } = string.Empty;
    public decimal? Balance { get; init; }
    public string Note { get; init; } = string.Empty;
    public int RowNumber { get; init; }
}

public sealed class BenchmarkPointDraft
{
    public string SourceType { get; init; } = TradeFoundryConstants.BenchmarkSeries;
    public string SourceKey { get; init; } = string.Empty;
    public string Symbol { get; init; } = "SPY";
    public DateTimeOffset EventUtc { get; init; }
    public decimal Value { get; init; }
    public string SourceTimeText { get; init; } = string.Empty;
    public int RowNumber { get; init; }
}

public sealed class BarDraft
{
    public string Symbol { get; init; } = string.Empty;
    public string Interval { get; init; } = "source";
    public DateTimeOffset EventUtc { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public long? Volume { get; init; }
    public long? NumberOfTrades { get; init; }
    public long? BidVolume { get; init; }
    public long? AskVolume { get; init; }
}

public sealed class ImportedTradeDraft
{
    public string SourceType { get; init; } = TradeFoundryConstants.TradingViewStrategy;
    public string SourceKey { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string Account { get; init; } = "TradingView";
    public string Direction { get; init; } = "Long";
    public DateTimeOffset EntryUtc { get; init; }
    public DateTimeOffset? ExitUtc { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal? ExitPrice { get; init; }
    public int Quantity { get; init; }
    public decimal GrossPoints { get; init; }
    public decimal GrossPnl { get; init; }
    public decimal ExchangeFees { get; init; }
    public decimal NfaFees { get; init; }
    public decimal ClearingFees { get; init; }
    public decimal Fees { get; init; }
    public decimal NetPnl { get; init; }
    public decimal? InitialStopPrice { get; init; }
    public decimal? InitialTargetPrice { get; init; }
    public decimal? InitialRiskPoints { get; init; }
    public decimal? InitialRiskCurrency { get; init; }
    public decimal? RMultiple { get; init; }
    public string ExitType { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public string Instrument { get; init; } = string.Empty;
    public decimal PointValue { get; init; }
    public decimal TickSize { get; init; }
    public string SourceTimeframe { get; init; } = string.Empty;
}

public sealed class ParsedRecord
{
    public string SourceType { get; init; } = string.Empty;
    public string SourceKey { get; init; } = string.Empty;
    public int RowNumber { get; init; }
    public string PayloadJson { get; init; } = "{}";
    public FillDraft? Fill { get; init; }
    public OrderEventDraft? OrderEvent { get; init; }
    public AccountBalanceDraft? AccountBalance { get; init; }
    public BenchmarkPointDraft? Benchmark { get; init; }
    public BarDraft? Bar { get; init; }
}

public sealed class ParsedImport
{
    public string SourceType { get; init; } = string.Empty;
    public string SourceApplication { get; init; } = string.Empty;
    public string? ResolvedBarInterval { get; set; }
    public List<ParsedRecord> Records { get; } = new();
    public List<ImportedTradeDraft> Trades { get; } = new();
    public List<string> Warnings { get; } = new();
}

public sealed class InstrumentSpec
{
    public string Root { get; init; } = string.Empty;
    public decimal TickSize { get; init; }
    public decimal PointValue { get; init; }
}

public static class NumberFormat
{
    public static string Decimal(decimal value) => value.ToString("0.####################", CultureInfo.InvariantCulture);
}

using System.ComponentModel;

namespace TradeFoundry.Mcp;

public sealed class TradeFilterInput
{
    [Description("Inclusive local journal date in YYYY-MM-DD format.")]
    public string? StartDate { get; init; }

    [Description("Inclusive local journal date in YYYY-MM-DD format.")]
    public string? EndDate { get; init; }

    [Description("Date field used by the range: exit (default) or entry.")]
    public string TimeBasis { get; init; } = "exit";
    public string? Symbol { get; init; }
    public string? Account { get; init; }
    public string? Direction { get; init; }
    public string? Status { get; init; }

    [Description("winner, loser, breakeven, or all.")]
    public string Outcome { get; init; } = "all";
    public decimal? MinimumNetPnl { get; init; }
    public decimal? MaximumNetPnl { get; init; }
    public decimal? MinimumRMultiple { get; init; }
    public decimal? MaximumRMultiple { get; init; }

    [Description("Optional text matched against symbol, instrument, account, direction, status, and source note.")]
    public string? Search { get; init; }
}

public sealed class BrokerFeeProfileInput
{
    [Description("User-managed broker or plan name, such as AMP Futures or a negotiated plan.")]
    public string Name { get; init; } = string.Empty;

    [Description("Instrument root such as MES, or * for a profile that applies to all instruments.")]
    public string Instrument { get; init; } = "*";

    [Description("Optional note about the source, plan, or assumptions.")]
    public string? Notes { get; init; }

    [Description("Commission in the journal currency per contract side. Divide round-turn quotes by two.")]
    public decimal? CommissionPerContractSide { get; init; }

    [Description("Exchange fee in the journal currency per contract side.")]
    public decimal? ExchangePerContractSide { get; init; }

    [Description("NFA fee in the journal currency per contract side.")]
    public decimal? NfaPerContractSide { get; init; }

    [Description("Clearing fee in the journal currency per contract side.")]
    public decimal? ClearingPerContractSide { get; init; }

    [Description("Optional monthly platform charge in the journal currency.")]
    public decimal? PlatformMonthly { get; init; }

    [Description("Optional monthly market-data charge in the journal currency.")]
    public decimal? DataMonthly { get; init; }

    [Description("Optional other monthly charge in the journal currency.")]
    public decimal? OtherMonthly { get; init; }
}

public sealed class McpBrokerFeeProfileItem
{
    public string ProfileId { get; init; } = string.Empty;
    public string JournalId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Instrument { get; init; } = "*";
    public string Notes { get; init; } = string.Empty;
    public decimal? CommissionPerContractSide { get; init; }
    public decimal? ExchangePerContractSide { get; init; }
    public decimal? NfaPerContractSide { get; init; }
    public decimal? ClearingPerContractSide { get; init; }
    public decimal? PlatformMonthly { get; init; }
    public decimal? DataMonthly { get; init; }
    public decimal? OtherMonthly { get; init; }
    public int Revision { get; init; }
    public string CreatedUtc { get; init; } = string.Empty;
    public string UpdatedUtc { get; init; } = string.Empty;
}

public sealed class McpBrokerFeeProfileListResponse
{
    public string SchemaVersion { get; init; } = "1";
    public string DataBoundary { get; init; } = McpResponseDefaults.DataBoundary;
    public string JournalId { get; init; } = string.Empty;
    public string Currency { get; init; } = "USD";
    public string? Instrument { get; init; }
    public string AppPath { get; init; } = string.Empty;
    public IReadOnlyList<McpBrokerFeeProfileItem> Profiles { get; init; } = Array.Empty<McpBrokerFeeProfileItem>();
}

public sealed class McpBrokerFeeProfileMutationResponse
{
    public string SchemaVersion { get; init; } = "1";
    public string DataBoundary { get; init; } = McpResponseDefaults.DataBoundary;
    public string JournalId { get; init; } = string.Empty;
    public bool Saved { get; init; }
    public bool Conflict { get; init; }
    public bool NotFound { get; init; }
    public string Message { get; init; } = string.Empty;
    public McpBrokerFeeProfileItem? Profile { get; init; }
}

public sealed record AppliedTradeFilters(
    string? StartDate,
    string? EndDate,
    string TimeBasis,
    string? Symbol,
    string? Account,
    string? Direction,
    string? Status,
    string Outcome,
    decimal? MinimumNetPnl,
    decimal? MaximumNetPnl,
    decimal? MinimumRMultiple,
    decimal? MaximumRMultiple,
    string? Search);

public sealed record McpJournalItem(
    string JournalId,
    string Name,
    string ExecutionContext,
    string Labels,
    string TimeZone,
    string Currency,
    string? FirstTradeDate,
    string? LastTradeDate,
    int TradeCount,
    bool HasOrderEvents,
    bool HasBars,
    bool HasBenchmark,
    string AppPath);

public sealed class McpJournalListResponse
{
    public string SchemaVersion { get; init; } = "1";
    public string DataBoundary { get; init; } = McpResponseDefaults.DataBoundary;
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public IReadOnlyList<string> DataGaps { get; init; } = Array.Empty<string>();
    public IReadOnlyList<McpJournalItem> Journals { get; init; } = Array.Empty<McpJournalItem>();
}

public sealed record McpPerformanceMetrics(
    int ClosedTrades,
    int OpenTrades,
    int Winners,
    int Losers,
    int Breakeven,
    decimal GrossPnl,
    decimal NetPnl,
    decimal ExchangeFees,
    decimal NfaFees,
    decimal ClearingFees,
    decimal Fees,
    decimal GrossPoints,
    decimal WinRatePercent,
    decimal? ProfitFactor,
    decimal? Expectancy,
    decimal? AverageWinner,
    decimal? AverageLoser,
    decimal MaximumDrawdown,
    decimal? AverageRMultiple,
    decimal? AverageMaePoints,
    decimal? AverageMfePoints,
    double? AverageDurationSeconds);

public sealed record McpRecentDay(string Date, int ClosedTrades, decimal NetPnl);

public sealed class McpJournalOverviewResponse
{
    public string SchemaVersion { get; init; } = "1";
    public string DataBoundary { get; init; } = McpResponseDefaults.DataBoundary;
    public string JournalId { get; init; } = string.Empty;
    public string JournalTimeZone { get; init; } = "UTC";
    public string Currency { get; init; } = "USD";
    public AppliedTradeFilters AppliedFilters { get; init; } = McpResponseDefaults.EmptyFilters;
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public IReadOnlyList<string> DataGaps { get; init; } = Array.Empty<string>();
    public string AppPath { get; init; } = string.Empty;
    public string? CoverageStartDate { get; init; }
    public string? CoverageEndDate { get; init; }
    public McpPerformanceMetrics Metrics { get; init; } = McpResponseDefaults.EmptyMetrics;
    public IReadOnlyList<McpRecentDay> RecentDays { get; init; } = Array.Empty<McpRecentDay>();
}

public sealed record McpTradeSummary(
    string ReviewKey,
    string Symbol,
    string Instrument,
    string Account,
    string Direction,
    string Status,
    string EntryUtc,
    string EntryLocal,
    string? ExitUtc,
    string? ExitLocal,
    decimal EntryPrice,
    decimal? ExitPrice,
    int Quantity,
    int ClosedQuantity,
    decimal GrossPnl,
    decimal ExchangeFees,
    decimal NfaFees,
    decimal ClearingFees,
    decimal Fees,
    decimal NetPnl,
    decimal? RMultiple,
    string SourceType,
    string AppPath);

public sealed class McpTradeSearchResponse
{
    public string SchemaVersion { get; init; } = "1";
    public string DataBoundary { get; init; } = McpResponseDefaults.DataBoundary;
    public string JournalId { get; init; } = string.Empty;
    public string JournalTimeZone { get; init; } = "UTC";
    public string Currency { get; init; } = "USD";
    public AppliedTradeFilters AppliedFilters { get; init; } = McpResponseDefaults.EmptyFilters;
    public string Sort { get; init; } = "entry_desc";
    public int TotalMatches { get; init; }
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public IReadOnlyList<string> DataGaps { get; init; } = Array.Empty<string>();
    public string AppPath { get; init; } = string.Empty;
    public IReadOnlyList<McpTradeSummary> Trades { get; init; } = Array.Empty<McpTradeSummary>();
}

public sealed record McpOrderEvidence(
    string EventUtc,
    string EventLocal,
    string OrderType,
    string OrderStatus,
    string Side,
    string OpenClose,
    decimal? Price,
    decimal? FillPrice,
    int? Quantity,
    int? FilledQuantity,
    bool? IsAutomated);

public sealed record McpFillEvidence(string EventUtc, string EventLocal, string Side, int Quantity, decimal Price, decimal Fees, string SourceType);

public sealed record McpTradeEvidence(
    decimal? InitialStopPrice,
    decimal? InitialTargetPrice,
    decimal? InitialRiskPoints,
    decimal? InitialRiskCurrency,
    decimal? MaePoints,
    decimal? MfePoints,
    decimal? EntryOrderPrice,
    decimal? ExitOrderPrice,
    decimal? EntryChasePoints,
    decimal? ExitChasePoints,
    string ExitType,
    string GroupingPolicy,
    string TradingApplication,
    string SourceType,
    string SourceNote,
    string SourceNoteTrust,
    IReadOnlyList<McpFillEvidence> Fills,
    bool FillsTruncated,
    IReadOnlyList<McpOrderEvidence> NearbyOrderEvents,
    bool OrderEventsTruncated);

public sealed class McpTradeDetailResponse
{
    public string SchemaVersion { get; init; } = "1";
    public string DataBoundary { get; init; } = McpResponseDefaults.DataBoundary;
    public string JournalId { get; init; } = string.Empty;
    public string JournalTimeZone { get; init; } = "UTC";
    public string Currency { get; init; } = "USD";
    public AppliedTradeFilters AppliedFilters { get; init; } = McpResponseDefaults.EmptyFilters;
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public IReadOnlyList<string> DataGaps { get; init; } = Array.Empty<string>();
    public string AppPath { get; init; } = string.Empty;
    public McpTradeSummary Trade { get; init; } = null!;
    public McpTradeEvidence Evidence { get; init; } = null!;
}

public sealed record McpBarPoint(string EventUtc, string EventLocal, decimal Open, decimal High, decimal Low, decimal Close, long? Volume);

public sealed class McpTradePriceContextResponse
{
    public string SchemaVersion { get; init; } = "1";
    public string DataBoundary { get; init; } = McpResponseDefaults.DataBoundary;
    public string JournalId { get; init; } = string.Empty;
    public string JournalTimeZone { get; init; } = "UTC";
    public string Currency { get; init; } = "USD";
    public string ReviewKey { get; init; } = string.Empty;
    public string RequestedInterval { get; init; } = string.Empty;
    public string ResolvedInterval { get; init; } = string.Empty;
    public int MinutesBefore { get; init; }
    public int MinutesAfter { get; init; }
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public IReadOnlyList<string> DataGaps { get; init; } = Array.Empty<string>();
    public string AppPath { get; init; } = string.Empty;
    public string EntryUtc { get; init; } = string.Empty;
    public string? ExitUtc { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal? ExitPrice { get; init; }
    public IReadOnlyList<McpBarPoint> Bars { get; init; } = Array.Empty<McpBarPoint>();
}

public sealed class McpTradingDayResponse
{
    public string SchemaVersion { get; init; } = "1";
    public string DataBoundary { get; init; } = McpResponseDefaults.DataBoundary;
    public string JournalId { get; init; } = string.Empty;
    public string JournalTimeZone { get; init; } = "UTC";
    public string Currency { get; init; } = "USD";
    public string Date { get; init; } = string.Empty;
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public IReadOnlyList<string> DataGaps { get; init; } = Array.Empty<string>();
    public string AppPath { get; init; } = string.Empty;
    public McpPerformanceMetrics RealizedMetrics { get; init; } = McpResponseDefaults.EmptyMetrics;
    public IReadOnlyList<string> Observations { get; init; } = Array.Empty<string>();
    public IReadOnlyList<McpTradeSummary> OpenedTrades { get; init; } = Array.Empty<McpTradeSummary>();
    public IReadOnlyList<McpTradeSummary> ClosedTrades { get; init; } = Array.Empty<McpTradeSummary>();
}

public sealed record McpCohortRow(string Key, int SampleSize, McpPerformanceMetrics Metrics, IReadOnlyList<string> Warnings);

public sealed class McpTradeAnalysisResponse
{
    public string SchemaVersion { get; init; } = "1";
    public string DataBoundary { get; init; } = McpResponseDefaults.DataBoundary;
    public string JournalId { get; init; } = string.Empty;
    public string JournalTimeZone { get; init; } = "UTC";
    public string Currency { get; init; } = "USD";
    public AppliedTradeFilters AppliedFilters { get; init; } = McpResponseDefaults.EmptyFilters;
    public string GroupBy { get; init; } = string.Empty;
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public IReadOnlyList<string> DataGaps { get; init; } = Array.Empty<string>();
    public string AppPath { get; init; } = string.Empty;
    public McpPerformanceMetrics Overall { get; init; } = McpResponseDefaults.EmptyMetrics;
    public IReadOnlyList<McpCohortRow> Cohorts { get; init; } = Array.Empty<McpCohortRow>();
}

public sealed record McpQualityCount(string Key, int Count, string Meaning);

public sealed class McpDataQualityResponse
{
    public string SchemaVersion { get; init; } = "1";
    public string DataBoundary { get; init; } = McpResponseDefaults.DataBoundary;
    public string JournalId { get; init; } = string.Empty;
    public string JournalTimeZone { get; init; } = "UTC";
    public string Currency { get; init; } = "USD";
    public AppliedTradeFilters AppliedFilters { get; init; } = McpResponseDefaults.EmptyFilters;
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public IReadOnlyList<string> DataGaps { get; init; } = Array.Empty<string>();
    public string AppPath { get; init; } = string.Empty;
    public int TradeCount { get; init; }
    public int OrderEventCount { get; init; }
    public int BenchmarkPointCount { get; init; }
    public string? BenchmarkFirstDate { get; init; }
    public string? BenchmarkLastDate { get; init; }
    public int TradesNeedingEvidenceCount { get; init; }
    public IReadOnlyList<string> ReviewKeysNeedingEvidence { get; init; } = Array.Empty<string>();
    public IReadOnlyList<McpQualityCount> Checks { get; init; } = Array.Empty<McpQualityCount>();
}

internal static class McpResponseDefaults
{
    public const string DataBoundary = "Historical journal evidence only. Win/loss, profit factor, and expectancy use gross P&L; net P&L includes fees. No live market data, signals, or order placement. Broker fee profile tools may mutate only explicit user-managed fee profiles; imported journal evidence remains immutable.";
    public static readonly AppliedTradeFilters EmptyFilters = new(null, null, "exit", null, null, null, null, "all", null, null, null, null, null);
    public static readonly McpPerformanceMetrics EmptyMetrics = new(0, 0, 0, 0, 0, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, null, null, null, null, 0m, null, null, null, null);
}

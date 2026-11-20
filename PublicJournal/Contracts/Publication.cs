using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeFoundry.Publishing;

// Deliberately independent of internal journal, account, import, and review models.
public sealed record PublicationSnapshot
{
    public int SchemaVersion { get; init; } = 6;
    public Guid SnapshotId { get; init; } = Guid.NewGuid();
    public Guid JournalId { get; init; }
    public DateTimeOffset GeneratedUtc { get; init; }
    public DateOnly AsOfDate { get; init; }
    public string GoogleTagScript { get; init; } = "";
    public string Title { get; init; } = "";
    public string Introduction { get; init; } = "";
    public List<PublicationNode> IntroductionContent { get; init; } = [];
    public string Currency { get; init; } = "USD";
    public string TimeZone { get; init; } = "UTC";
    public PublicationDashboard Dashboard { get; init; } = new();
    public List<PublicationDay> Days { get; init; } = [];
    public List<PublicationMonth> Months { get; init; } = [];
    public List<PublicationEquityPoint> Equity { get; init; } = [];
    public List<PublicationImage> Images { get; init; } = [];
    /// <summary>Schema 5: benchmark series and comparison statistics, when a benchmark is imported.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicationBenchmark? Benchmark { get; init; }
    /// <summary>Schema 5: optional tear-sheet indicators chosen in the publishing settings.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PublicationMetricGroup>? TearSheet { get; init; }
}

public sealed record PublicationBenchmark
{
    public string Symbol { get; init; } = "";
    /// <summary>Balance the strategy's percentage line is measured against.</summary>
    public decimal? StartingBalance { get; init; }
    public decimal? StrategyReturnPercent { get; init; }
    public decimal? BenchmarkReturnPercent { get; init; }
    public decimal? AlphaPercent { get; init; }
    public decimal? AnnualizedAlphaPercent { get; init; }
    public decimal? Beta { get; init; }
    public decimal? Correlation { get; init; }
    public List<PublicationBenchmarkPoint> Points { get; init; } = [];
}
public sealed record PublicationBenchmarkPoint(DateOnly Date, decimal Close);
public sealed record PublicationMetricGroup(string Title, string Description, List<PublicationMetric> Metrics);
public sealed record PublicationMetric(string Label, string Value, string Note, string Tone = "neutral");

public sealed record PublicationDashboard
{
    public string? Alpha { get; init; }
    public string? AnnualizedAlpha { get; init; }
    public decimal? Balance { get; init; }
    public string BalanceNote { get; init; } = "";
    public decimal LifetimeNetPnl { get; init; }
    public decimal CurrentMonthNetPnl { get; init; }
    public int CompletedTrades { get; init; }
    public decimal? WinRate { get; init; }
    public decimal? ProfitFactor { get; init; }
}

public sealed record PublicationDay
{
    public DateOnly Date { get; init; }
    public decimal NetPnl { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public int Breakevens { get; init; }
    /// <summary>
    /// Schema 6: the day's estimated low in running net P&amp;L, combining results closed before each entry
    /// with that trade's maximum adverse excursion. Null when no trade that day recorded MAE.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? IntradayLow { get; init; }
    public List<PublicationTrade> Trades { get; init; } = [];
    public List<PublicationNode> Content { get; init; } = [];
}

public sealed record PublicationTrade(string Symbol, string Direction, int Quantity, decimal NetPnl)
{
    // Schema 5: exact execution times and prices; null in older snapshots.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? EntryUtc { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ExitUtc { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? EntryPrice { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? ExitPrice { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicationTradeSetupInfo? Setup { get; init; }
    public List<PublicationNode> JournalContent { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicationTradeChart? Chart { get; init; }
}

public sealed record PublicationTradeSetupInfo(string Label, List<PublicationSetup> Definitions);
public sealed record PublicationSetup(string Name, int Version, string Role, List<PublicationSetupCriterion> Criteria);
public sealed record PublicationSetupCriterion(string Name, string Type, string Stage, string State);

public sealed record PublicationTradeChart
{
    public string Interval { get; init; } = "5m";
    public List<PublicationChartBar> Bars { get; init; } = [];
    public PublicationChartExecution Entry { get; init; } = new(0, 0);
    public PublicationChartExecution Exit { get; init; } = new(0, 0);
}
public sealed record PublicationChartBar(long Time, decimal Open, decimal High, decimal Low, decimal Close, long Volume);
public sealed record PublicationChartExecution(long Time, decimal Price);
public sealed record PublicationMonth(DateOnly Month, decimal NetPnl, int TradeCount)
{
    /// <summary>Schema 5: account balance just before the month, for monthly percentage returns.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? OpeningBalance { get; init; }
}
public sealed record PublicationEquityPoint(DateOnly Date, decimal CumulativePnl);
public sealed record PublicationImage(string Id, string ContentType, byte[] Content);

public sealed record PublicationNode
{
    public string Type { get; init; } = "paragraph";
    public string Text { get; init; } = "";
    public int Format { get; init; }
    public string Alignment { get; init; } = "";
    public string Url { get; init; } = "";
    public string ImageId { get; init; } = "";
    public string Alt { get; init; } = "";
    public int Width { get; init; }
    public int ListStart { get; init; } = 1;
    public List<PublicationNode> Children { get; init; } = [];
}

public sealed record PublicationUpload(long ExpectedRevision, PublicationSnapshot Snapshot);
public sealed record PublicationStatus(long Revision, Guid? BoundJournalId, Guid? SnapshotId, DateTimeOffset? PublishedUtc);
public sealed record PublicationDelete(long ExpectedRevision, Guid JournalId, bool ResetBinding = false);

public static class PublicationJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 48
    };
    public const int MaxUploadBytes = 128 * 1024 * 1024;
    public const int MaxImageBytes = 10 * 1024 * 1024;
}


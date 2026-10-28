using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeFoundry.Publishing;

// Deliberately independent of internal journal, account, import, and review models.
public sealed record PublicationSnapshot
{
    public int SchemaVersion { get; init; } = 2;
    public Guid SnapshotId { get; init; } = Guid.NewGuid();
    public Guid JournalId { get; init; }
    public DateTimeOffset GeneratedUtc { get; init; }
    public DateOnly AsOfDate { get; init; }
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
}

public sealed record PublicationDashboard
{
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
    public List<PublicationTrade> Trades { get; init; } = [];
    public List<PublicationNode> Content { get; init; } = [];
}

public sealed record PublicationTrade(string Symbol, string Direction, int Quantity, decimal NetPnl)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicationTradeChart? Chart { get; init; }
}

public sealed record PublicationTradeChart
{
    public string Interval { get; init; } = "5m";
    public List<PublicationChartBar> Bars { get; init; } = [];
    public PublicationChartExecution Entry { get; init; } = new(0, 0);
    public PublicationChartExecution Exit { get; init; } = new(0, 0);
}
public sealed record PublicationChartBar(long Time, decimal Open, decimal High, decimal Low, decimal Close, long Volume);
public sealed record PublicationChartExecution(long Time, decimal Price);
public sealed record PublicationMonth(DateOnly Month, decimal NetPnl, int TradeCount);
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

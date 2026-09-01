using System.Globalization;

namespace TradeFoundry.Core;

public static class TradeFoundryConstants
{
    public const string OwnerUserId = "owner";
    public const string SierraFills = "Sierra Chart fills";
    public const string TradingViewAccount = "TradingView account history";
    public const string TradingViewStrategy = "TradingView Strategy Tester";
    public const string OhlcvBars = "OHLCV bars";
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
    public string TimeZone { get; init; } = "UTC";
    public string Currency { get; init; } = "USD";
    public string GroupingPolicy { get; init; } = "flat_to_flat";
    public DateTimeOffset CreatedUtc { get; init; }
}

public sealed class ImportBatch
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public string FileName { get; init; } = string.Empty;
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
    public DateTimeOffset EventUtc { get; init; }
    public string SourceTimeText { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string Account { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public decimal Price { get; init; }
    public string OpenClose { get; init; } = string.Empty;
    public decimal? High { get; init; }
    public decimal? Low { get; init; }
    public string Note { get; init; } = string.Empty;
    public int? PositionQuantity { get; init; }
    public string OrderId { get; init; } = string.Empty;
    public string ServiceOrderId { get; init; } = string.Empty;
    public decimal Fees { get; init; }
    public int RowNumber { get; init; }
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
    public decimal Fees { get; init; }
    public decimal NetPnl { get; init; }
    public decimal? MaePoints { get; init; }
    public decimal? MfePoints { get; init; }
    public string Status { get; init; } = "closed";
    public string Note { get; init; } = string.Empty;
    public TimeSpan? Duration => ExitUtc.HasValue ? ExitUtc.Value - EntryUtc : null;
    public bool IsWinner => NetPnl > 0m;
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
}

public sealed class JournalSnapshot
{
    public Journal Journal { get; init; } = new();
    public IReadOnlyList<Trade> Trades { get; init; } = Array.Empty<Trade>();
    public IReadOnlyList<ImportBatch> Imports { get; init; } = Array.Empty<ImportBatch>();
    public IReadOnlyList<string> Symbols { get; init; } = Array.Empty<string>();
    public IReadOnlyList<DailyPnl> DailyPnl { get; init; } = Array.Empty<DailyPnl>();
    public IReadOnlyList<Trade> ClosedTrades => Trades.Where(x => x.Status.Equals("closed", StringComparison.OrdinalIgnoreCase)).ToArray();
    public IReadOnlyList<Trade> OpenTrades => Trades.Where(x => !x.Status.Equals("closed", StringComparison.OrdinalIgnoreCase)).ToArray();
    public decimal NetPnl => Trades.Sum(x => x.NetPnl);
    public decimal GrossPnl => Trades.Sum(x => x.GrossPnl);
    public decimal Points => Trades.Sum(x => x.GrossPoints);
    public decimal Fees => Trades.Sum(x => x.Fees);
    public int TradeCount => ClosedTrades.Count;
    public int WinningTrades => ClosedTrades.Count(x => x.NetPnl > 0m);
    public int LosingTrades => ClosedTrades.Count(x => x.NetPnl < 0m);
    public decimal WinRate => TradeCount == 0 ? 0m : (decimal)WinningTrades / TradeCount * 100m;
    public decimal ProfitFactor
    {
        get
        {
            var wins = ClosedTrades.Where(x => x.NetPnl > 0m).Sum(x => x.NetPnl);
            var losses = Math.Abs(ClosedTrades.Where(x => x.NetPnl < 0m).Sum(x => x.NetPnl));
            return losses == 0m ? (wins > 0m ? decimal.MaxValue : 0m) : wins / losses;
        }
    }
}

public sealed class DailyPnl
{
    public DateOnly Date { get; init; }
    public decimal NetPnl { get; init; }
    public int TradeCount { get; init; }
}

public sealed class ImportResult
{
    public ImportBatch Batch { get; init; } = new();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class FillDraft
{
    public string SourceType { get; init; } = string.Empty;
    public string SourceKey { get; init; } = string.Empty;
    public DateTimeOffset EventUtc { get; init; }
    public string SourceTimeText { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string Account { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public decimal Price { get; init; }
    public string OpenClose { get; init; } = string.Empty;
    public decimal? High { get; init; }
    public decimal? Low { get; init; }
    public string Note { get; init; } = string.Empty;
    public int? PositionQuantity { get; init; }
    public string OrderId { get; init; } = string.Empty;
    public string ServiceOrderId { get; init; } = string.Empty;
    public decimal Fees { get; init; }
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
    public decimal Fees { get; init; }
    public decimal NetPnl { get; init; }
    public string Note { get; init; } = string.Empty;
}

public sealed class ParsedRecord
{
    public string SourceType { get; init; } = string.Empty;
    public string SourceKey { get; init; } = string.Empty;
    public int RowNumber { get; init; }
    public string PayloadJson { get; init; } = "{}";
    public FillDraft? Fill { get; init; }
    public BarDraft? Bar { get; init; }
}

public sealed class ParsedImport
{
    public string SourceType { get; init; } = string.Empty;
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

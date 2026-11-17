using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class OrderExecutionSummaryTests
{
    private const string NewYork = "America/New_York";

    [Fact]
    public void Slim_counts_match_counts_from_full_order_events()
    {
        var directory = NewDirectory();
        try
        {
            var (database, journal) = CreateFixture(directory);
            var service = new OrderExecutionSummaryService(database);

            var counts = service.Get(journal.Id, JournalFilter.None.Scope(journal.TimeZone), database.GetAllTrades(journal.Id));

            Assert.Equal(OrderExecutionCounts.From(database.GetOrderEvents(journal.Id)), counts);
            // Order A is modified then filled inside the trade; order B is canceled weeks earlier.
            Assert.Equal(new OrderExecutionCounts(TotalOrders: 2, Filled: 1, Canceled: 1, Modified: 1, PartiallyFilled: 0, TotalModifications: 1), counts);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public void Filtered_view_counts_only_orders_inside_its_trades()
    {
        var directory = NewDirectory();
        try
        {
            var (database, journal) = CreateFixture(directory);
            var service = new OrderExecutionSummaryService(database);
            var scope = JournalFilter.Parse("custom", "2026-11-02", "2026-11-02", null).Scope(journal.TimeZone);
            var trades = scope.Apply(database.GetAllTrades(journal.Id));

            var counts = service.Get(journal.Id, scope, trades);

            Assert.Equal(1, counts.TotalOrders);
            Assert.Equal(1, counts.Filled);
            Assert.Equal(0, counts.Canceled);
            // The unfiltered view is cached separately and still sees both orders.
            Assert.Equal(2, service.Get(journal.Id, JournalFilter.None.Scope(journal.TimeZone), database.GetAllTrades(journal.Id)).TotalOrders);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public void Importing_or_removing_order_events_refreshes_cached_counts()
    {
        var directory = NewDirectory();
        try
        {
            var (database, journal) = CreateFixture(directory);
            var service = new OrderExecutionSummaryService(database);
            var whole = JournalFilter.None.Scope(journal.TimeZone);
            Assert.Equal(2, service.Get(journal.Id, whole, database.GetAllTrades(journal.Id)).TotalOrders);

            var parsed = NewImport();
            parsed.Records.Add(OrderRecord("order-c-1", "C", "Canceled", Utc(11, 3, 15), 1));
            var batch = database.CommitImport(journal.Id, "more-orders.txt", parsed, "flat_to_flat", "source").Batch;
            Assert.Equal(3, service.Get(journal.Id, whole, database.GetAllTrades(journal.Id)).TotalOrders);

            database.RemoveImportBatch(batch.Id);
            Assert.Equal(2, service.Get(journal.Id, whole, database.GetAllTrades(journal.Id)).TotalOrders);
        }
        finally { DeleteDirectory(directory); }
    }

    private static (TradeFoundryDb Database, Journal Journal) CreateFixture(string directory)
    {
        var database = new TradeFoundryDb(Options.Create(new StorageOptions { DataDirectory = directory, DatabaseFileName = "journal.db" }));
        database.CreateOwner("Owner", "test-hash");
        var journal = database.CreateJournal("Live", "live", string.Empty, NewYork, "USD", "flat_to_flat");
        var parsed = NewImport();
        parsed.Records.Add(FillRecord("fill-1", "Buy", 5000m, Utc(11, 2, 15), 1));
        parsed.Records.Add(FillRecord("fill-2", "Sell", 5002m, Utc(11, 2, 16), 2));
        parsed.Records.Add(OrderRecord("order-a-1", "A", "Open", Utc(11, 2, 15, 10), 3));
        parsed.Records.Add(OrderRecord("order-a-2", "A", "Modify Order", Utc(11, 2, 15, 20), 4));
        parsed.Records.Add(OrderRecord("order-a-3", "A", "Filled", Utc(11, 2, 15, 30), 5));
        parsed.Records.Add(OrderRecord("order-b-1", "B", "Open", Utc(10, 20, 15), 6));
        parsed.Records.Add(OrderRecord("order-b-2", "B", "Canceled", Utc(10, 20, 15, 5), 7));
        database.CommitImport(journal.Id, "fixture.txt", parsed, "flat_to_flat", "source");
        return (database, database.GetJournal(journal.Id)!);
    }

    private static ParsedImport NewImport() => new() { SourceType = TradeFoundryConstants.SierraFills, SourceApplication = TradeFoundryConstants.SierraChart };

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    private static ParsedRecord FillRecord(string sourceKey, string side, decimal price, DateTimeOffset timestamp, int row) => new()
    {
        SourceType = TradeFoundryConstants.SierraFills,
        SourceKey = sourceKey,
        RowNumber = row,
        PayloadJson = "{}",
        Fill = new FillDraft
        {
            SourceType = TradeFoundryConstants.SierraFills, SourceKey = sourceKey, EventUtc = timestamp,
            SourceTimeText = timestamp.ToString("O"), Symbol = "MESZ26", Instrument = "MES", Account = "SIM",
            Side = side, Quantity = 1, Price = price, Fees = 0m, RowNumber = row, PointValue = 5m, TickSize = .25m
        }
    };

    private static ParsedRecord OrderRecord(string sourceKey, string orderId, string status, DateTimeOffset timestamp, int row) => new()
    {
        SourceType = TradeFoundryConstants.SierraFills,
        SourceKey = sourceKey,
        RowNumber = row,
        PayloadJson = "{}",
        OrderEvent = new OrderEventDraft
        {
            SourceType = TradeFoundryConstants.SierraFills, SourceKey = sourceKey, EventUtc = timestamp,
            SourceTimeText = timestamp.ToString("O"), Symbol = "MESZ26", Instrument = "MES", Account = "SIM",
            InternalOrderId = orderId, OrderStatus = status, Side = "Buy", Quantity = 1, RowNumber = row
        }
    };

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "TradeFoundry.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string directory)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

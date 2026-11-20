using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

/// <summary>
/// Covers the NinjaTrader Trade Performance "Trades ($)" adapter: detection, currency
/// validation, normalization, fees, native excursions, deduplication, and persistence.
/// </summary>
public sealed class NinjaTraderImportTests
{
    private const string Header = "Trade number,Instrument,Account,Strategy,Market pos.,Qty,Entry price,Exit price,Entry time,Exit time,Entry name,Exit name,Profit,Cum. net profit,Commission,Clearing Fee,Exchange Fee,IP Fee,NFA Fee,MAE,MFE,ETD,Bars,";

    // Anonymized fixture: long/short, quantity 2, every fee component, an identical
    // paired row, parentheses negatives, empty optional metadata/excursions, and
    // three malformed rows (zero quantity, unknown position, P&L that cannot reconcile).
    private static readonly string[] FixtureRows =
    [
        "1,MES 06-23,SimA,5pt ATM,Short,1,4169.25,4158.25,5/23/2023 2:16:59 PM,5/23/2023 2:26:28 PM,Entry,Stop1,$55.00,$55.00,$0.00,$0.00,$0.00,$0.00,$0.00,$2.50,$81.25,$26.25,0,",
        "2,MNQ 12-23,SimB,NQ - No Trail,Long,2,15000.00,14990.50,11/15/2023 1:12:10 PM,11/15/2023 1:22:05 PM,Entry,Close,($39.60),$15.40,$1.00,$0.20,$0.30,$0.04,$0.06,$40.00,$6.00,$45.60,0,",
        "3,MNQ 12-23,SimB,NQ - No Trail,Short,1,15872.75,15861.75,11/16/2023 1:37:58 PM,11/16/2023 1:39:59 PM,Entry,Target1,$20.78,$36.18,$1.22,$0.00,$0.00,$0.00,$0.00,$3.00,$25.00,$4.22,0,",
        "4,MNQ 12-23,SimB,NQ - No Trail,Short,1,15872.75,15861.75,11/16/2023 1:37:58 PM,11/16/2023 1:39:59 PM,Entry,Target1,$20.78,$56.96,$1.22,$0.00,$0.00,$0.00,$0.00,$3.00,$25.00,$4.22,0,",
        "5,MNQ 03-24,SimB,,Long,1,17000.00,17010.00,1/5/2024 10:00:00 AM,1/5/2024 10:05:00 AM,,Entry,$20.00,$76.96,$0.00,$0.00,$0.00,$0.00,$0.00,,,,0,",
        "6,MNQ 03-24,SimB,NQ - No Trail,Long,0,17000.00,17010.00,1/5/2024 11:00:00 AM,1/5/2024 11:05:00 AM,Entry,Stop1,$0.00,$76.96,$0.00,$0.00,$0.00,$0.00,$0.00,,,,0,",
        "7,MNQ 03-24,SimB,NQ - No Trail,Flat,1,17000.00,17010.00,1/5/2024 12:00:00 PM,1/5/2024 12:05:00 PM,Entry,Stop1,$0.00,$76.96,$0.00,$0.00,$0.00,$0.00,$0.00,,,,0,",
        "8,ZZZ 06-23,SimB,NQ - No Trail,Long,1,100.00,101.00,1/5/2024 1:00:00 PM,1/5/2024 1:05:00 PM,Entry,Target1,$50.00,$126.96,$0.00,$0.00,$0.00,$0.00,$0.00,,,,0,"
    ];

    private static string Fixture(params int[] rowNumbers) =>
        Header + "\n" + string.Join("\n", (rowNumbers.Length == 0 ? Enumerable.Range(1, FixtureRows.Length) : rowNumbers).Select(n => FixtureRows[n - 1])) + "\n";

    private static ParsedImport ParseFixture(string text, InstrumentConfiguration? configuration = null, string timeZone = "America/New_York") =>
        new ImportService(null!).Parse(text, "auto", "source", timeZone, configuration: configuration);

    // Fixture rows are numbered by their NinjaTrader trade number, which the adapter keeps as Sequence.
    private static ImportedTradeDraft TradeAt(ParsedImport parsed, int tradeNumber) =>
        parsed.Records.Select(record => record.Trade).Single(trade => trade?.Sequence == tradeNumber)!;

    [Fact]
    public void DetectsNinjaTraderTradesBeforeTradingViewStrategy()
    {
        var parsed = ParseFixture(Fixture());

        Assert.Equal(TradeFoundryConstants.NinjaTraderTrades, parsed.SourceType);
        Assert.Equal(TradeFoundryConstants.NinjaTrader, parsed.SourceApplication);
        Assert.Equal(TradeFoundryConstants.SourceDefinedGrouping, parsed.GroupingPolicyOverride);
        Assert.Empty(parsed.Trades);
        Assert.True(ImportService.IsSupportedTradeExport(Fixture()));

        // The generic TradingView Strategy Tester signature still routes correctly.
        var strategy = new ImportService(null!).Parse("Trade #,Symbol,Type,Entry Price,Exit Price,Entry Time,Exit Time,Quantity,Net P&L\n1,MES,Entry Long,6875.00,,2025-11-03T10:50:27Z,,1,\n1,MES,Exit Long,,6878.25,,2025-11-03T11:18:35Z,1,15.75\n");
        Assert.Equal(TradeFoundryConstants.TradingViewStrategy, strategy.SourceType);
    }

    [Fact]
    public void NormalizesFieldsTimezoneAndNativeExcursions()
    {
        var parsed = ParseFixture(Fixture());

        Assert.Equal(8, parsed.Records.Count);
        Assert.Equal(5, parsed.Records.Count(record => record.Trade is not null));

        var shortTrade = TradeAt(parsed, 1);
        Assert.Equal("MES 06-23", shortTrade.Symbol);
        Assert.Equal("MES", shortTrade.Instrument);
        Assert.Equal("SimA", shortTrade.Account);
        Assert.Equal("Short", shortTrade.Direction);
        Assert.Equal(new DateTimeOffset(2023, 5, 23, 18, 16, 59, TimeSpan.Zero), shortTrade.EntryUtc);
        Assert.Equal(new DateTimeOffset(2023, 5, 23, 18, 26, 28, TimeSpan.Zero), shortTrade.ExitUtc);
        Assert.Equal(11m, shortTrade.GrossPoints);
        Assert.Equal(55m, shortTrade.GrossPnl);
        Assert.Equal(55m, shortTrade.NetPnl);
        Assert.Equal(0.5m, shortTrade.MaePoints);
        Assert.Equal(16.25m, shortTrade.MfePoints);
        Assert.Equal("stop", shortTrade.ExitType);
        Assert.Equal("5pt ATM", shortTrade.Strategy);
        Assert.Equal("Entry", shortTrade.EntryName);
        Assert.Equal("Stop1", shortTrade.ExitName);
        Assert.Equal(1, shortTrade.Sequence);

        // Quantity 2 on MNQ ($2/point): excursions divide by quantity × point value.
        var longTrade = TradeAt(parsed, 2);
        Assert.Equal(-19m, longTrade.GrossPoints);
        Assert.Equal(-38m, longTrade.GrossPnl);
        Assert.Equal(1.60m, longTrade.Fees);
        Assert.Equal(-39.60m, longTrade.NetPnl);
        Assert.Equal(0m, longTrade.ExchangeFees + longTrade.NfaFees + longTrade.ClearingFees);
        Assert.Equal(10m, longTrade.MaePoints);
        Assert.Equal(1.5m, longTrade.MfePoints);
        Assert.Equal("close", longTrade.ExitType);

        Assert.Equal("target", TradeAt(parsed, 3).ExitType);

        var sparse = TradeAt(parsed, 5);
        Assert.Null(sparse.MaePoints);
        Assert.Null(sparse.MfePoints);
        Assert.Equal(string.Empty, sparse.Strategy);
        Assert.Equal(string.Empty, sparse.EntryName);
        Assert.Equal(string.Empty, sparse.ExitType);
    }

    [Fact]
    public void RetainsMalformedRowsWithActionableWarnings()
    {
        var parsed = ParseFixture(Fixture());

        Assert.Null(parsed.Records.Single(record => record.RowNumber == 7).Trade);
        Assert.Null(parsed.Records.Single(record => record.RowNumber == 8).Trade);
        Assert.Null(parsed.Records.Single(record => record.RowNumber == 9).Trade);
        Assert.Equal(3, parsed.Warnings.Count);
        Assert.DoesNotContain(parsed.Warnings, warning => warning.Contains("reconciliation", StringComparison.Ordinal));

        // A filtered or re-sorted export breaks the running Cum. net profit; that is advisory only.
        var resorted = ParseFixture(Fixture(2, 1));
        Assert.Equal(2, resorted.Records.Count(record => record.Trade is not null));
        Assert.Contains(resorted.Warnings, warning => warning.Contains("Cum. net profit", StringComparison.Ordinal));
        Assert.Contains(parsed.Warnings, warning => warning.Contains("row 7", StringComparison.Ordinal) && warning.Contains("Qty", StringComparison.Ordinal));
        Assert.Contains(parsed.Warnings, warning => warning.Contains("row 8", StringComparison.Ordinal) && warning.Contains("Market pos.", StringComparison.Ordinal));
        Assert.Contains(parsed.Warnings, warning => warning.Contains("row 9", StringComparison.Ordinal) && warning.Contains("NinjaTrader symbol mapping", StringComparison.Ordinal));
        Assert.All(parsed.Warnings, warning => Assert.Contains("raw row was retained", warning, StringComparison.Ordinal));
    }

    [Fact]
    public void IdenticalPairedRowsGetDistinctStableIdentities()
    {
        var first = ParseFixture(Fixture());
        var second = ParseFixture(Fixture());
        var keys = first.Records.Where(record => record.Trade is not null).Select(record => record.SourceKey).ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.NotEqual(TradeAt(first, 3).SourceKey, TradeAt(first, 4).SourceKey);
        Assert.Equal(keys, second.Records.Where(record => record.Trade is not null).Select(record => record.SourceKey).ToArray());
        Assert.All(first.Records.Where(record => record.Trade is not null), record => Assert.Equal(record.SourceKey, record.Trade!.SourceKey));

        // Trade number and Cum. net profit are report-relative and excluded from identity.
        var renumbered = Fixture(1).Replace("\n1,MES 06-23", "\n41,MES 06-23", StringComparison.Ordinal).Replace("$55.00,$55.00", "$55.00,$999.00", StringComparison.Ordinal);
        Assert.Equal(TradeAt(first, 1).SourceKey, ParseFixture(renumbered).Records.Single().SourceKey);

        // A different source timezone must not change identity.
        Assert.Equal(TradeAt(first, 1).SourceKey, TradeAt(ParseFixture(Fixture(), timeZone: "UTC"), 1).SourceKey);
    }

    [Fact]
    public void RejectsNonCurrencyDisplayAndExecutionsExports()
    {
        var service = new ImportService(null!);
        var points = Header + "\n1,MES 06-23,SimA,5pt ATM,Short,1,4169.25,4158.25,5/23/2023 2:16:59 PM,5/23/2023 2:26:28 PM,Entry,Stop1,11.00,11.00,0.00,0.00,0.00,0.00,0.00,0.50,16.25,5.25,0,\n";
        var currency = Assert.Throws<FormatException>(() => service.Parse(points));
        Assert.Contains("Currency", currency.Message, StringComparison.Ordinal);

        var executions = "Instrument,Action,Quantity,Price,Time,ID,E/X,Position,Order ID,Name,Commission,Rate,Account,Connection,\nMES 06-23,Sell,1,4169.25,5/23/2023 2:16:59 PM,abc,Entry,1 S,def,Entry,$0.00,1,SimA,My NinjaTrader,\n";
        Assert.Contains("Executions", Assert.Throws<FormatException>(() => service.Parse(executions)).Message, StringComparison.Ordinal);
        Assert.Contains("Executions", Assert.Throws<FormatException>(() => service.Parse(executions, "ninjatrader")).Message, StringComparison.Ordinal);
        Assert.False(ImportService.IsSupportedTradeExport(executions));

        var tradingViewStrategy = "Trade #,Symbol,Type,Entry Price,Exit Price,Entry Time,Exit Time,Quantity,Net P&L\n1,MES,Entry Long,6875.00,,2025-11-03T10:50:27Z,,1,\n";
        Assert.Throws<FormatException>(() => service.Parse(tradingViewStrategy, "ninjatrader"));
    }

    [Fact]
    public void ReconciliationAllowsOneCentAndRejectsMore()
    {
        var withinCent = Fixture(1).Replace("$55.00,$55.00", "$55.01,$55.01", StringComparison.Ordinal);
        // ETD is adjusted with Profit so only the P&L reconciliation fails.
        var beyondCent = Fixture(1).Replace("$55.00,$55.00", "$55.02,$55.02", StringComparison.Ordinal).Replace("$26.25", "$26.23", StringComparison.Ordinal);

        Assert.NotNull(ParseFixture(withinCent).Records.Single().Trade);
        var rejected = ParseFixture(beyondCent);
        Assert.Null(rejected.Records.Single().Trade);
        Assert.Single(rejected.Warnings);
    }

    [Fact]
    public void FeePrecedenceIsSourceMappingThenInstrumentThenReported()
    {
        var mnq = new InstrumentDefinition { Code = "MNQ", PointValue = 2m, TickSize = 0.25m };

        // Reported costs are used when nothing is configured.
        Assert.Equal(1.60m, TradeAt(ParseFixture(Fixture(2), new InstrumentConfiguration([mnq], [])), 2).Fees);

        // An instrument default commission (per contract per side) replaces reported costs.
        var withDefault = new InstrumentConfiguration([new InstrumentDefinition { Code = "MNQ", PointValue = 2m, TickSize = 0.25m, DefaultCommission = 0.50m }], []);
        var defaultTrade = TradeAt(ParseFixture(Fixture(2), withDefault), 2);
        Assert.Equal(2.00m, defaultTrade.Fees);
        Assert.Equal(-40.00m, defaultTrade.NetPnl);

        // A NinjaTrader mapping override beats the instrument default; Sierra mappings do not apply.
        var mappings = new[]
        {
            new InstrumentMapping { ApplicationKey = TradeFoundryConstants.SierraChart, MatchRegex = "MNQ", InstrumentCode = "MNQ", CommissionOverride = 9m, Position = 0 },
            new InstrumentMapping { ApplicationKey = TradeFoundryConstants.NinjaTrader, MatchRegex = "^MNQ \\d{2}-\\d{2}$", InstrumentCode = "MNQ", CommissionOverride = 0.25m, Position = 0 }
        };
        var mapped = TradeAt(ParseFixture(Fixture(2), new InstrumentConfiguration([new InstrumentDefinition { Code = "MNQ", PointValue = 2m, TickSize = 0.25m, DefaultCommission = 0.50m }], mappings)), 2);
        Assert.Equal(1.00m, mapped.Fees);
        Assert.Equal(-39.00m, mapped.NetPnl);

        // An instrument fee breakdown populates the component columns.
        var breakdown = new InstrumentConfiguration([new InstrumentDefinition { Code = "MNQ", PointValue = 2m, TickSize = 0.25m, ExchangeFeePerContract = 0.10m, NfaFeePerContract = 0.02m, ClearingFeePerContract = 0.05m }], []);
        var componentTrade = TradeAt(ParseFixture(Fixture(2), breakdown), 2);
        Assert.Equal(0.40m, componentTrade.ExchangeFees);
        Assert.Equal(0.08m, componentTrade.NfaFees);
        Assert.Equal(0.20m, componentTrade.ClearingFees);
        Assert.Equal(0.68m, componentTrade.Fees);

        // A mapping can supply the point value for a contract the catalog does not know.
        var zzz = new InstrumentConfiguration([new InstrumentDefinition { Code = "ZZZ", PointValue = 50m, TickSize = 0.01m }], []);
        var zzzTrade = TradeAt(ParseFixture(Fixture(8), zzz), 8);
        Assert.Equal(50m, zzzTrade.NetPnl);
    }

    [Fact]
    public void ExtractsRootFromNinjaTraderContractNames()
    {
        Assert.Equal("MES", InstrumentCatalog.ExtractRoot("MES 06-23"));
        Assert.Equal("MNQ", InstrumentCatalog.ExtractRoot("MNQ 12-23"));
        Assert.Equal("M2K", InstrumentCatalog.ExtractRoot("M2K 03-24"));
        Assert.Equal("MES", InstrumentCatalog.ExtractRoot("MESZ25_FUT_CME"));
    }

    [Fact]
    public void PersistsAtomicallyDeduplicatesAndUndoesWithoutTouchingFillTrades()
    {
        WithDatabase((database, service, journal) =>
        {
            // An unrelated fill-derived trade must survive NinjaTrader imports and undo.
            service.ImportText(journal.Id, "tv-account.csv", "Timestamp,Symbol,Side,Quantity,Price,Order ID,Status,Fees\n2025-11-03T10:50:27Z,MESZ25_FUT_CME,Buy,1,6875.00,acct-001,Filled,0.50\n2025-11-03T11:18:35Z,MESZ25_FUT_CME,Sell,1,6878.25,acct-002,Filled,0.50\n", "auto", journal.GroupingPolicy, "source");
            var fillTrade = Assert.Single(database.GetAllTrades(journal.Id));

            var first = service.ImportText(journal.Id, "nt-trades.csv", Fixture(), "auto", journal.GroupingPolicy, "source");
            Assert.Equal(8, first.Batch.TotalRows);
            Assert.Equal(8, first.Batch.NewRows);
            Assert.Equal(0, first.Batch.DuplicateRows);
            Assert.Equal(TradeFoundryConstants.NinjaTrader, first.Batch.SourceApplication);
            Assert.Equal(3, first.Warnings.Count);
            Assert.Contains("source-defined pairing", first.Batch.Message, StringComparison.Ordinal);

            var ninjaTrades = database.GetAllTrades(journal.Id).Where(trade => trade.SourceType == TradeFoundryConstants.NinjaTraderTrades).ToArray();
            Assert.Equal(5, ninjaTrades.Length);
            Assert.All(ninjaTrades, trade => Assert.Equal(TradeFoundryConstants.SourceDefinedGrouping, trade.GroupingPolicy));
            Assert.Equal("flat_to_flat", database.GetJournal(journal.Id)!.GroupingPolicy);
            Assert.Equal(8, CountRaw(database, journal.Id, TradeFoundryConstants.NinjaTraderTrades));

            var persisted = ninjaTrades.Single(trade => trade.Quantity == 2);
            Assert.Equal(10m, persisted.MaePoints);
            Assert.Equal(1.5m, persisted.MfePoints);
            Assert.Equal("NQ - No Trail", persisted.Strategy);
            Assert.Equal("Entry", persisted.EntryName);
            Assert.Equal("Close", persisted.ExitName);
            Assert.Equal(2, persisted.Sequence);
            Assert.Equal(1.60m, persisted.Fees);
            Assert.Equal(-39.60m, persisted.NetPnl);
            Assert.True(persisted.IsSourceDefined);

            // Ledger search reaches strategy and signal names.
            Assert.Equal(2, database.GetTrades(new TradeQuery { JournalId = journal.Id, Page = 1, PageSize = 50, Search = "Target1" }).TotalCount);
            Assert.Equal(3, database.GetTrades(new TradeQuery { JournalId = journal.Id, Page = 1, PageSize = 50, Search = "No Trail" }).TotalCount);
            Assert.Equal((5, 6), database.CountSourceDefinedTrades(journal.Id));

            var reviewKeys = ninjaTrades.Select(trade => trade.ReviewKey).OrderBy(key => key, StringComparer.Ordinal).ToArray();

            // Re-import and an overlapping export add nothing.
            var again = service.ImportText(journal.Id, "nt-trades.csv", Fixture(), "auto", journal.GroupingPolicy, "source");
            Assert.Equal(0, again.Batch.NewRows);
            Assert.Equal(8, again.Batch.DuplicateRows);
            var overlap = service.ImportText(journal.Id, "nt-overlap.csv", Fixture(3, 5), "auto", journal.GroupingPolicy, "source");
            Assert.Equal(0, overlap.Batch.NewRows);
            Assert.Equal(2, overlap.Batch.DuplicateRows);
            Assert.Equal(6, database.GetAllTrades(journal.Id).Count);

            // Undo removes only the NinjaTrader batch; reimport restores the same review keys.
            database.RemoveImportBatch(again.Batch.Id);
            database.RemoveImportBatch(overlap.Batch.Id);
            database.RemoveImportBatch(first.Batch.Id);
            var remaining = Assert.Single(database.GetAllTrades(journal.Id));
            Assert.Equal(fillTrade.ReviewKey, remaining.ReviewKey);
            Assert.Equal(0, CountRaw(database, journal.Id, TradeFoundryConstants.NinjaTraderTrades));

            var restored = service.ImportText(journal.Id, "nt-trades.csv", Fixture(), "auto", journal.GroupingPolicy, "source");
            Assert.Equal(8, restored.Batch.NewRows);
            Assert.Equal(reviewKeys, database.GetAllTrades(journal.Id).Where(trade => trade.SourceType == TradeFoundryConstants.NinjaTraderTrades)
                .Select(trade => trade.ReviewKey).OrderBy(key => key, StringComparer.Ordinal).ToArray());
            Assert.Contains(database.GetAllTrades(journal.Id), trade => trade.ReviewKey == fillTrade.ReviewKey);
        });
    }

    [Fact]
    public void WatchedImportsRejectExecutionsWithInstructions()
    {
        WithDatabase((database, service, journal) =>
        {
            var executions = "Instrument,Action,Quantity,Price,Time,ID,E/X,Position,Order ID,Name,Commission,Rate,Account,Connection,\nMES 06-23,Sell,1,4169.25,5/23/2023 2:16:59 PM,abc,Entry,1 S,def,Entry,$0.00,1,SimA,My NinjaTrader,\n";
            var error = Assert.Throws<FormatException>(() => service.ImportWatchedText(journal.Id, Path.Combine(Path.GetTempPath(), "executions.csv"), executions, "hash-1"));
            Assert.Contains("Trades grid", error.Message, StringComparison.Ordinal);
            Assert.Throws<FormatException>(() => service.ImportText(journal.Id, "executions.csv", executions, "auto", journal.GroupingPolicy, "source"));

            var watched = service.ImportWatchedText(journal.Id, Path.Combine(Path.GetTempPath(), "nt-trades.csv"), Fixture(1, 2), "hash-2");
            Assert.Equal(2, watched.Batch.NewRows);
            Assert.All(database.GetAllTrades(journal.Id), trade => Assert.Equal(TradeFoundryConstants.SourceDefinedGrouping, trade.GroupingPolicy));
        });
    }

    [Fact]
    public void SuppliedNinjaTraderExportReconcilesToSourceTotals()
    {
        var path = FindSample("NinjaTrader Trades Export.csv");
        WithDatabase((database, service, journal) =>
        {
            var text = File.ReadAllText(path);
            var result = service.ImportText(journal.Id, "NinjaTrader Trades Export.csv", text, "auto", journal.GroupingPolicy, "source");
            Assert.Equal(205, result.Batch.TotalRows);
            Assert.Equal(205, result.Batch.NewRows);
            Assert.Empty(result.Warnings);

            var trades = database.GetAllTrades(journal.Id);
            Assert.Equal(205, trades.Count);
            Assert.Equal(205, CountRaw(database, journal.Id, TradeFoundryConstants.NinjaTraderTrades));
            Assert.Equal(94, trades.Count(trade => trade.NetPnl > 0m));
            Assert.Equal(111, trades.Count(trade => trade.NetPnl < 0m));
            Assert.Equal(4, trades.Select(trade => trade.Symbol).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(2, trades.Select(trade => trade.Account).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(-1335.00m, trades.Sum(trade => trade.GrossPnl));
            Assert.Equal(214.72m, trades.Sum(trade => trade.Fees));
            Assert.Equal(-1549.72m, trades.Sum(trade => trade.NetPnl));
            Assert.All(trades, trade => Assert.True(trade.MaePoints.HasValue && trade.MfePoints.HasValue));

            var overview = database.GetOverview(journal.Id);
            // Overview totals are aggregated in SQLite as REAL; compare at cents.
            Assert.Equal(-1549.72m, Math.Round(overview.NetPnl, 2));

            var again = service.ImportText(journal.Id, "NinjaTrader Trades Export.csv", text, "auto", journal.GroupingPolicy, "source");
            Assert.Equal(0, again.Batch.NewRows);
            Assert.Equal(205, again.Batch.DuplicateRows);
        });
    }

    private static void WithDatabase(Action<TradeFoundryDb, ImportService, Journal> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tradefoundry-ninjatrader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var database = new TradeFoundryDb(Options.Create(new StorageOptions { DataDirectory = directory, DatabaseFileName = "journal.db" }));
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "America/New_York", "USD", "flat_to_flat");
            test(database, new ImportService(database), journal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static int CountRaw(TradeFoundryDb database, Guid journalId, string sourceType)
    {
        using var connection = new SqliteConnection($"Data Source={database.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM raw_records WHERE journal_id = $journal AND source_type = $source";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$source", sourceType);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string FindSample(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Samples", fileName);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Samples/{fileName} was not found above {AppContext.BaseDirectory}.");
    }
}

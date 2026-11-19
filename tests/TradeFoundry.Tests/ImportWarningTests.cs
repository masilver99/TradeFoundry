using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class ImportWarningTests
{
    private const string SierraHeader = "DateTime\tActivity Type\tTransDateTime\tSymbol\tOrder Action Source\tTrade Account\tNote\tAccount Balance";

    [Fact]
    public void SierraCashBalanceAdjustmentUsesReconciledBalanceFromOrderActionSource()
    {
        var text = string.Join('\n',
            SierraHeader,
            "2026-09-09  03:22:55.454539\tAccount Balance\t00:00:00.000000\t\tCash Balance update | Cash balance adjustment | Account: 166902 | Balance: 2620.72 | EODAccountValueUsingOpenProfitLossBasedOnSettlement: 2620.72 | AccountValueCaptureTime: 2026-09-08  21:55:00 UTC | BalanceFromFirmImportFile: 2620.84 | Change: 0.12 | New NLV: 2620.84 | RecalculateDailyLossLimit: true\t\t\t",
            "2026-09-10  03:22:55.327000\tAccount Balance\t00:00:00.000000\t\tTeton CME Routing historical balance (Cash Balance update | Cash balance adjustment | Account: 166902 | Balance: 2636.69 | BalanceFromFirmImportFile: 2637.29 | Change: 0.60\t\t\t",
            "2026-09-10  13:11:48.170107\tAccount Balance\t00:00:00.000000\t\tCash Balance update | Trade Fee: 0.52USD. Qty: 1. MESU26_FUT_CME\t166902 [AMP LIVE]\t\t2604.78");

        var parsed = new ImportService(null!).Parse(text);

        Assert.Empty(parsed.Warnings);
        var balances = parsed.Records.Select(record => record.AccountBalance).ToArray();
        Assert.All(balances, Assert.NotNull);
        Assert.Equal(2620.84m, balances[0]!.Balance);
        Assert.Equal("166902", balances[0]!.Account);
        Assert.Equal(2637.29m, balances[1]!.Balance);
        Assert.Equal(2604.78m, balances[2]!.Balance);
        Assert.Equal("166902 [AMP LIVE]", balances[2]!.Account);
    }

    [Fact]
    public void SierraAccountBalanceRowWithoutAnyBalanceStillWarns()
    {
        var text = string.Join('\n',
            SierraHeader,
            "2026-09-09  03:22:55.454539\tAccount Balance\t00:00:00.000000\t\tCash Balance update | Something else\t\t\t");

        var parsed = new ImportService(null!).Parse(text);

        Assert.Single(parsed.Warnings);
        Assert.Null(parsed.Records.Single().AccountBalance);
    }

    [Fact]
    public void AutoDetectRejectsUnrecognizedFilesInsteadOfImportingThemAsTradingView()
    {
        var root = Path.Combine(Path.GetTempPath(), "TradeFoundry.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new TradeFoundryDb(Options.Create(new StorageOptions { DataDirectory = Path.Combine(root, "data"), DatabaseFileName = "journal.db" }));
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var imports = new ImportService(database);
            const string statistics = "Trade Statistics,Num Fills Filtered,First Fill,Last Fill,Daily Date\nAll Symbols,Filtered: 0,2026-09-08  09:11:47,2026-11-03  15:20:13,2026-11-03\nAll Accounts,,,,\n";

            Assert.Throws<FormatException>(() => imports.ImportText(journal.Id, "TradeActivityLog_full.txt", statistics, "auto", "flat_to_flat", "source"));
            Assert.Equal(0, database.GetImports(new ImportQuery { JournalId = journal.Id, Page = 1, PageSize = 25 }).TotalCount);

            var ninjaTrader = "Instrument,Action,Quantity,Price,Time,ID,Account\nMES 12-26,Buy,1,6000.25,2026-09-01 10:00:00,1,Sim101\n";
            var result = imports.ImportText(journal.Id, "executions.csv", ninjaTrader, "auto", "flat_to_flat", "source");
            Assert.Equal(1, database.GetImports(new ImportQuery { JournalId = journal.Id, Page = 1, PageSize = 25 }).TotalCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

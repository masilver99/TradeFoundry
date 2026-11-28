using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;
using TradeFoundry.Services.Backup;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class PatternDiscoveryTests
{
    private static readonly DateTimeOffset Entry = new(2026, 1, 5, 15, 15, 0, TimeSpan.Zero);

    [Fact]
    public void EntryAtrUsesOnlyCompletedContiguousBars()
    {
        var bars = Enumerable.Range(-30, 31).Select(offset => At(Entry.AddMinutes(offset),
            offset == 0 ? 999m : 101m, 99m, 100m)).ToDictionary(bar => bar.EventUtc);
        var row = Observe(Entry.AddSeconds(20));

        PatternEvidence.ReadPriceFeatures(row, bars, 1);

        Assert.Equal(2m, row.Features[PatternFeature.AtrPoints]);
        Assert.Equal(1m, row.Features[PatternFeature.CandleRange]);
        Assert.Equal(0m, row.Features[PatternFeature.MovePoints]);
        bars.Remove(Entry.AddMinutes(-8));
        row = Observe(Entry);
        PatternEvidence.ReadPriceFeatures(row, bars, 1);
        Assert.True(row.HasBar);
        Assert.False(row.Features.ContainsKey(PatternFeature.AtrPoints));
    }

    [Fact]
    public void ManagementUsesWorkingPricesAndRequiresAnOpenPositionAtEachLandmark()
    {
        var bars = Enumerable.Range(1, 5).Select(offset => At(Entry.AddMinutes(offset), 104m, 98m, 102m))
            .ToDictionary(bar => bar.EventUtc);
        var entry = new TradeFoundryDb.PatternEntry("entry", Entry, 100m, 1, true, Entry.AddMinutes(7));
        var events = new[]
        {
            Stop(Entry, 98m, "Open"),
            Stop(Entry.AddMinutes(1), 100m, "Pending Modify"),
            Stop(Entry.AddMinutes(2), 99m, "Open"),
            Stop(Entry.AddMinutes(4), 100m, "Open"),
            Stop(Entry.AddMinutes(6), 102m, "Open")
        };
        var row = Observe(Entry);
        row.Features[PatternFeature.AtrPoints] = 2m;

        PatternEvidence.ReadManagement(row, entry, events, bars, 1);

        Assert.Equal(1m, row.Features[PatternFeature.StopAtr]);
        Assert.Equal(0m, row.Features[PatternFeature.BreakEven]);
        Assert.Equal(1m, row.Features[PatternFeature.Trailing]);
        Assert.Equal(1, row.TrailingProgress);

        var earlyExit = Observe(Entry);
        earlyExit.Features[PatternFeature.AtrPoints] = 2m;
        PatternEvidence.ReadManagement(earlyExit, entry with { FirstExitUtc = Entry.AddMinutes(2) }, events, bars, 1);
        Assert.True(earlyExit.HasWorkingStop);
        Assert.False(earlyExit.Features.ContainsKey(PatternFeature.BreakEven));
        Assert.False(earlyExit.Features.ContainsKey(PatternFeature.Trailing));
    }

    [Fact]
    public void RechecksLaterOutcomesAndDowngradesAPreviousSignalAfterAReversal()
    {
        var first = PatternDiscovery.Scan("MES", History(reverseLater: false), null, CancellationToken.None);
        var signal = Assert.Single(first.Findings,
            finding => finding.Key.StartsWith("CandleRange:") && finding.Criteria.Count == 1);
        Assert.Equal(1, signal.Direction);
        Assert.False(signal.Weakened);
        Assert.True(signal.Validation.MatchedLift > 0);

        var reversed = PatternDiscovery.Scan("MES", History(reverseLater: true), first, CancellationToken.None);
        var updated = Assert.Single(reversed.Findings, finding => finding.Key == signal.Key);
        Assert.True(updated.Weakened);
        Assert.True(updated.Validation.MatchedLift < 0);
        var fresh = PatternDiscovery.Scan("MES", History(reverseLater: true), null, CancellationToken.None);
        Assert.DoesNotContain(fresh.Findings, finding => finding.Key == signal.Key);
    }

    [Fact]
    public async Task NewSharedBarsRefreshAPersistedScanWithoutSubstitutingInstruments()
    {
        var root = Path.Combine(Path.GetTempPath(), "TradeFoundry.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = Options.Create(new StorageOptions { DataDirectory = root });
            var database = new TradeFoundryDb(options);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Trading", "live", "", "America/New_York", "USD", "flat_to_flat");
            var barJournal = database.CreateJournal("Shared bars", "paper", "", "UTC", "USD", "flat_to_flat");
            ImportFills(database, journal.Id);
            ImportBar(database, barJournal.Id, "MES");
            var worker = new PatternDiscoveryWorker(database, new AppActivityGate(), TimeProvider.System,
                NullLogger<PatternDiscoveryWorker>.Instance);
            await worker.CheckAsync(CancellationToken.None);

            var (snapshot, current) = worker.Read(journal.Id);
            Assert.True(current);
            Assert.NotNull(snapshot);
            Assert.Equal(0, Assert.Single(snapshot.Instruments, report => report.Instrument == "ES").BarCount);
            var mes = Assert.Single(snapshot.Instruments, report => report.Instrument == "MES");
            Assert.Equal(1, mes.BarCount);
            Assert.Equal(1, mes.StopCount);
            Assert.Equal(1, mes.Management.BreakEvenReached);
            Assert.Equal(1, mes.Management.TrailingAdjusted);

            ImportBar(database, barJournal.Id, "ES");
            Assert.False(worker.Read(journal.Id).IsCurrent);
            await worker.CheckAsync(CancellationToken.None);

            var reopened = new PatternDiscoveryWorker(new TradeFoundryDb(options), new AppActivityGate(), TimeProvider.System,
                NullLogger<PatternDiscoveryWorker>.Instance);
            var (updated, refreshed) = reopened.Read(journal.Id);
            Assert.True(refreshed);
            Assert.Equal(1, Assert.Single(updated!.Instruments, report => report.Instrument == "ES").BarCount);
            await reopened.CheckAsync(CancellationToken.None);
            Assert.Equal(updated.ScannedUtc, reopened.Read(journal.Id).Snapshot!.ScannedUtc);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static List<PatternObservation> History(bool reverseLater)
    {
        var rows = new List<PatternObservation>();
        for (var day = 0; day < 75; day++)
        {
            for (var trade = 0; trade < 4; trade++)
            {
                var time = Entry.AddDays(day).AddMinutes(trade * 5);
                var matches = trade % 2 == 0;
                var wins = matches ? !reverseLater || day < 45 : reverseLater && day >= 45;
                var row = Observe(time, wins ? 20m : -10m);
                row.Features[PatternFeature.CandleRange] = matches ? .5m : 1.1m;
                rows.Add(row);
            }
        }
        return rows;
    }

    private static PatternObservation Observe(DateTimeOffset time, decimal net = 5m) =>
        new(new Trade
        {
            EntryUtc = time, ExitUtc = time.AddMinutes(1), EntryPrice = 100m, Direction = "Long",
            ClosedQuantity = 1, PointValue = 5m, NetPnl = net, ReviewKey = Guid.NewGuid().ToString("N")
        }, DateOnly.FromDateTime(time.DateTime), DateOnly.FromDateTime(time.DateTime), 2);

    private static Bar At(DateTimeOffset time, decimal high, decimal low, decimal close) =>
        new() { EventUtc = time, High = high, Low = low, Open = close, Close = close };

    private static OrderEvent Stop(DateTimeOffset time, decimal price, string status) =>
        new() { Id = Guid.NewGuid(), InternalOrderId = "stop", ParentOrderId = "entry", EventUtc = time,
            OrderType = "Stop", OrderStatus = status, Side = "Sell", OpenClose = "Close", Quantity = 1, Price = price };

    private static void ImportFills(TradeFoundryDb database, Guid journal)
    {
        var batch = new ParsedImport { SourceType = TradeFoundryConstants.SierraFills, SourceApplication = TradeFoundryConstants.SierraChart };
        foreach (var instrument in new[] { "ES", "MES" })
        {
            AddFill(instrument, "Buy", Entry, 5000m);
            AddFill(instrument, "Sell", Entry.AddMinutes(7), 5001m);
        }
        AddStop(0, 4998m);
        AddStop(1, 4999m);
        AddStop(2, 5000m);
        database.CommitImport(journal, "sierra.txt", batch, "flat_to_flat", "source");

        void AddFill(string instrument, string side, DateTimeOffset time, decimal price)
        {
            var key = $"{instrument}-{side}";
            var row = batch.Records.Count + 1;
            batch.Records.Add(new ParsedRecord
            {
                SourceType = batch.SourceType, SourceKey = key, RowNumber = row, PayloadJson = "{}",
                Fill = new FillDraft { SourceType = batch.SourceType, SourceKey = key, RowNumber = row,
                    Account = "SIM", Symbol = $"{instrument}H26", Instrument = instrument, Side = side, Quantity = 1,
                    OrderId = key, EventUtc = time, Price = price, PointValue = instrument == "ES" ? 50m : 5m, TickSize = .25m }
            });
        }

        void AddStop(int minute, decimal price)
        {
            var key = $"MES-stop-{minute}";
            var row = batch.Records.Count + 1;
            batch.Records.Add(new ParsedRecord
            {
                SourceType = batch.SourceType, SourceKey = key, RowNumber = row, PayloadJson = "{}",
                OrderEvent = new OrderEventDraft {
                    SourceType = batch.SourceType, SourceKey = key, RowNumber = row, EventUtc = Entry.AddMinutes(minute),
                    Account = "SIM", Symbol = "MESH26", Instrument = "MES", InternalOrderId = "stop",
                    ParentOrderId = "MES-Buy", OrderType = "Stop", OrderStatus = "Open", Side = "Sell", OpenClose = "Close",
                    Price = price, Quantity = 1
                }
            });
        }
    }

    private static void ImportBar(TradeFoundryDb database, Guid journal, string instrument)
    {
        var batch = new ParsedImport { SourceType = TradeFoundryConstants.OhlcvBars, SourceApplication = TradeFoundryConstants.Ohlcv };
        for (var minute = -15; minute <= 4; minute++)
        {
            batch.Records.Add(new ParsedRecord
            {
                SourceType = batch.SourceType, SourceKey = $"{instrument}-{minute}", RowNumber = minute + 16, PayloadJson = "{}",
                Bar = new BarDraft { Symbol = instrument, Interval = "1m", EventUtc = Entry.AddMinutes(minute),
                    Open = 5000m, High = 5001m, Low = 4999m, Close = 5000m, Volume = 100 }
            });
        }
        database.CommitImport(journal, $"{instrument}.csv", batch, "flat_to_flat", "1m", "UTC");
    }
}

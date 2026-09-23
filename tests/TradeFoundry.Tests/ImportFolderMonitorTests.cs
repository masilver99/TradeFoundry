using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class ImportFolderMonitorTests
{
    [Fact]
    public void TradeFileRecognitionExcludesMarketDataAndBenchmarks()
    {
        Assert.True(ImportService.IsSupportedTradeExport("Activity Type,DateTime,Symbol,BuySell,Quantity,Fill Price"));
        Assert.True(ImportService.IsSupportedTradeExport("timestamp,symbol,side,quantity,price"));
        Assert.True(ImportService.IsSupportedTradeExport("Trade Number,Symbol,Net PnL,Entry Price"));
        Assert.False(ImportService.IsSupportedTradeExport("Date,Open,High,Low,Close,Volume"));
        Assert.False(ImportService.IsSupportedTradeExport("Date,Adj Close,Total Return"));
        Assert.False(ImportService.IsSupportedTradeExport("not a trade export"));
        Assert.False(ImportService.IsSupportedTradeExport(string.Empty));
    }

    [Fact]
    public async Task StartupImportIsJournalScopedAndUnchangedFileIsNotImportedAgain()
    {
        var root = NewDirectory();
        ImportFolderMonitor? monitor = null;
        try
        {
            var database = CreateDatabase(Path.Combine(root, "data"));
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var otherJournal = database.CreateJournal("Replay", "replay", string.Empty, "UTC", "USD", "flat_to_flat");
            var folder = Path.Combine(root, "incoming");
            Directory.CreateDirectory(folder);
            var filePath = Path.Combine(folder, "fills.csv");
            await File.WriteAllTextAsync(filePath, AccountCsv("2026-09-01T10:00:00Z", "5000"));

            Assert.True(database.SaveImportWatchDirectory(journal.Id, folder, out var conflict));
            Assert.Null(conflict);
            Assert.False(database.SaveImportWatchDirectory(otherJournal.Id, folder + Path.DirectorySeparatorChar, out conflict));
            Assert.Equal(journal.Name, conflict);

            monitor = NewMonitor(database);
            await monitor.StartAsync(CancellationToken.None);
            var completed = await WaitForStatusAsync(database, journal.Id, filePath, "completed");
            Assert.NotNull(completed.ImportBatchId);
            Assert.Equal(1, Imports(database, journal.Id).TotalCount);
            Assert.Equal(0, Imports(database, otherJournal.Id).TotalCount);

            await monitor.StopAsync(CancellationToken.None);
            monitor.Dispose();
            monitor = null;

            monitor = NewMonitor(database);
            await monitor.StartAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            Assert.Equal(1, Imports(database, journal.Id).TotalCount);
            Assert.Equal(completed.ImportBatchId, database.GetWatchedImportFileStatus(journal.Id, filePath)?.ImportBatchId);

            await File.WriteAllTextAsync(filePath, AccountCsv("2026-09-01T10:01:00Z", "5001"));
            var changed = await WaitForStatusAsync(database, journal.Id, filePath, "completed", completed.ContentHash);
            Assert.NotEqual(completed.ImportBatchId, changed.ImportBatchId);
            Assert.Equal(2, Imports(database, journal.Id).TotalCount);
        }
        finally
        {
            if (monitor is not null)
            {
                await monitor.StopAsync(CancellationToken.None);
                monitor.Dispose();
            }
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SlowCopyWaitsForWriterAndFailedFileRetriesAfterContentsChange()
    {
        var root = NewDirectory();
        ImportFolderMonitor? monitor = null;
        try
        {
            var database = CreateDatabase(Path.Combine(root, "data"));
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var folder = Path.Combine(root, "incoming");
            Directory.CreateDirectory(folder);
            Assert.True(database.SaveImportWatchDirectory(journal.Id, folder, out _));

            monitor = NewMonitor(database);
            await monitor.StartAsync(CancellationToken.None);

            var slowFile = Path.Combine(folder, "slow.csv");
            await using (var writer = new FileStream(slowFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                var header = System.Text.Encoding.UTF8.GetBytes("timestamp,symbol,side,quantity,price\n");
                await writer.WriteAsync(header);
                await writer.FlushAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(1300));
                var row = System.Text.Encoding.UTF8.GetBytes("2026-09-02T10:00:00Z,MESZ26,Buy,1,5000\n");
                await writer.WriteAsync(row);
                await writer.FlushAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(1300));
            }

            await WaitForStatusAsync(database, journal.Id, slowFile, "completed");
            Assert.Equal(1, Imports(database, journal.Id).TotalCount);

            var unsupportedFile = Path.Combine(folder, "market.csv");
            await File.WriteAllTextAsync(unsupportedFile, "Date,Open,High,Low,Close,Volume\n2026-09-02,1,2,1,2,3\n");
            var failed = await WaitForStatusAsync(database, journal.Id, unsupportedFile, "failed");
            Assert.Contains("not a recognized trade export", failed.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, Imports(database, journal.Id).TotalCount);

            await File.WriteAllTextAsync(unsupportedFile, AccountCsv("2026-09-02T10:01:00Z", "5002"));
            await WaitForStatusAsync(database, journal.Id, unsupportedFile, "completed", failed.ContentHash);
            Assert.Equal(2, Imports(database, journal.Id).TotalCount);

            Assert.True(database.SaveImportWatchDirectory(journal.Id, string.Empty, out _));
            monitor.NotifyConfigurationChanged();
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            var afterDisable = Path.Combine(folder, "after-disable.csv");
            await File.WriteAllTextAsync(afterDisable, AccountCsv("2026-09-02T10:02:00Z", "5003"));
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            Assert.Equal(2, Imports(database, journal.Id).TotalCount);
            Assert.Equal(string.Empty, database.GetJournal(journal.Id)?.ImportWatchDirectory);
        }
        finally
        {
            if (monitor is not null)
            {
                await monitor.StopAsync(CancellationToken.None);
                monitor.Dispose();
            }
            DeleteDirectory(root);
        }
    }

    private static ImportFolderMonitor NewMonitor(TradeFoundryDb database) =>
        new(database, new ImportService(database), NullLogger<ImportFolderMonitor>.Instance);

    private static TradeFoundryDb CreateDatabase(string directory) =>
        new(Options.Create(new StorageOptions { DataDirectory = directory, DatabaseFileName = "journal.db" }));

    private static PagedResult<ImportBatch> Imports(TradeFoundryDb database, Guid journalId) =>
        database.GetImports(new ImportQuery { JournalId = journalId, Page = 1, PageSize = 25 });

    private static async Task<WatchedImportFileStatus> WaitForStatusAsync(
        TradeFoundryDb database,
        Guid journalId,
        string filePath,
        string expectedStatus,
        string? previousHash = null)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < timeout)
        {
            var status = database.GetWatchedImportFileStatus(journalId, filePath);
            if (status?.Status == expectedStatus && (previousHash is null || status.ContentHash != previousHash))
                return status;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting for {Path.GetFileName(filePath)} to reach status '{expectedStatus}'.");
    }

    private static string AccountCsv(string timestamp, string price) =>
        $"timestamp,symbol,side,quantity,price\n{timestamp},MESZ26,Buy,1,{price}\n";

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

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

            var notifications = new ImportNotificationFeed();
            monitor = NewMonitor(database, notifications);
            await monitor.StartAsync(CancellationToken.None);
            var completed = await WaitForStatusAsync(database, journal.Id, filePath, "completed");
            Assert.NotNull(completed.ImportBatchId);
            var notice = Assert.Single(notifications.Since(0));
            Assert.Equal(journal.Id, notice.JournalId);
            Assert.Equal("success", notice.Severity);
            Assert.Contains("fills.csv", notice.Message);
            Assert.Contains(journal.Name, notice.Message);
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

    [Fact]
    public async Task WatchedFileImportsOnChangeOnlyAddsNewRowsAndStopsWhenDisabled()
    {
        var root = NewDirectory();
        ImportFolderMonitor? monitor = null;
        try
        {
            var database = CreateDatabase(Path.Combine(root, "data"));
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var folder = Path.Combine(root, "sierra");
            Directory.CreateDirectory(folder);
            var filePath = Path.Combine(folder, "activity.csv");
            var sibling = Path.Combine(folder, "other.csv");
            await File.WriteAllTextAsync(filePath, AccountCsv("2026-09-03T10:00:00Z", "5000"));
            await File.WriteAllTextAsync(sibling, AccountCsv("2026-09-03T09:00:00Z", "4990"));

            Assert.True(database.SaveImportWatchFile(journal.Id, filePath, enabled: true));
            var saved = database.GetJournal(journal.Id)!;
            Assert.True(saved.ImportWatchFileEnabled);
            Assert.Equal(Path.GetFullPath(filePath), saved.ImportWatchFile);

            monitor = NewMonitor(database);
            await monitor.StartAsync(CancellationToken.None);
            var first = await WaitForStatusAsync(database, journal.Id, filePath, "completed");
            Assert.Equal(1, Imports(database, journal.Id).TotalCount);
            Assert.Null(database.GetWatchedImportFileStatus(journal.Id, sibling));

            await File.AppendAllTextAsync(filePath, "2026-09-03T10:05:00Z,MESZ26,Sell,1,5004\n");
            var second = await WaitForStatusAsync(database, journal.Id, filePath, "completed", first.ContentHash);
            var batches = Imports(database, journal.Id);
            Assert.Equal(2, batches.TotalCount);
            var latest = batches.Items.Single(batch => batch.Id == second.ImportBatchId);
            Assert.Equal(1, latest.NewRows);
            Assert.Equal(1, latest.DuplicateRows);

            Assert.True(database.SaveImportWatchFile(journal.Id, filePath, enabled: false));
            Assert.Equal(Path.GetFullPath(filePath), database.GetJournal(journal.Id)?.ImportWatchFile);
            monitor.NotifyConfigurationChanged();
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            await File.AppendAllTextAsync(filePath, "2026-09-03T10:10:00Z,MESZ26,Buy,1,5006\n");
            await Task.Delay(TimeSpan.FromSeconds(2.5));
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
    public async Task WatchedFileHeldOpenByWriterImportsOnlyCompleteLines()
    {
        var root = NewDirectory();
        ImportFolderMonitor? monitor = null;
        FileStream? writer = null;
        try
        {
            var database = CreateDatabase(Path.Combine(root, "data"));
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var filePath = Path.Combine(root, "live-log.csv");

            writer = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            await writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(
                "timestamp,symbol,side,quantity,price\n2026-09-04T10:00:00Z,MESZ26,Buy,1,5000\n2026-09-04T10:01:00Z,MESZ26,Sell,1,50"));
            await writer.FlushAsync();

            Assert.True(database.SaveImportWatchFile(journal.Id, filePath, enabled: true));
            monitor = NewMonitor(database);
            await monitor.StartAsync(CancellationToken.None);

            var first = await WaitForStatusAsync(database, journal.Id, filePath, "completed");
            var firstBatch = Imports(database, journal.Id).Items.Single();
            Assert.Equal(1, firstBatch.NewRows);

            await writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes("04.25\n"));
            await writer.FlushAsync();
            var second = await WaitForStatusAsync(database, journal.Id, filePath, "completed", first.ContentHash);
            var secondBatch = Imports(database, journal.Id).Items.Single(batch => batch.Id == second.ImportBatchId);
            Assert.Equal(1, secondBatch.NewRows);
            Assert.Equal(1, secondBatch.DuplicateRows);
        }
        finally
        {
            if (writer is not null) await writer.DisposeAsync();
            if (monitor is not null)
            {
                await monitor.StopAsync(CancellationToken.None);
                monitor.Dispose();
            }
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ImportNotificationFeedReturnsNewerItemsAndKeepsOnlyRecentOnes()
    {
        var feed = new ImportNotificationFeed();
        var journalId = Guid.NewGuid();
        for (var index = 1; index <= 60; index++) feed.Add(journalId, "success", $"Import {index}");

        Assert.Equal(60, feed.LatestSequence);
        Assert.Equal(50, feed.Since(0).Count);
        Assert.Equal("Import 11", feed.Since(0)[0].Message);
        Assert.Equal(new[] { "Import 59", "Import 60" }, feed.Since(58).Select(item => item.Message));
        Assert.Empty(feed.Since(60));
    }

    [Fact]
    public void ImportDescriptionCountsNewAndDuplicateRows()
    {
        Assert.Equal("Automatically imported 3 new rows from fills.csv into Live (1 duplicate skipped).",
            ImportFolderMonitor.DescribeImport("Live", Path.Combine("x", "fills.csv"), new ImportBatch { NewRows = 3, DuplicateRows = 1 }));
        Assert.Equal("Automatically imported fills.csv into Live: no new rows (2 duplicates skipped).",
            ImportFolderMonitor.DescribeImport("Live", "fills.csv", new ImportBatch { NewRows = 0, DuplicateRows = 2 }));
    }

    private static ImportFolderMonitor NewMonitor(TradeFoundryDb database, ImportNotificationFeed? notifications = null) =>
        new(database, new ImportService(database), NullLogger<ImportFolderMonitor>.Instance, notifications);

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

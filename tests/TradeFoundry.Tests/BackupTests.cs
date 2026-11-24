using System.IO.Compression;
using System.Text;
using ImageMagick;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;
using TradeFoundry.Services.Backup;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class BackupTests : IDisposable
{
    private const string Secret = "publishing-token-that-must-not-leave-this-machine";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tradefoundry-backup-tests", Guid.NewGuid().ToString("N"));
    private string DataDirectory => Path.Combine(_root, "data");
    private string BackupFolder => Path.Combine(_root, "backups");

    [Fact]
    public async Task GateHoldsNewWorkDuringMaintenanceAndStartsMaintenanceOnlyWhenIdle()
    {
        var gate = new AppActivityGate();
        Assert.True(gate.TryEnter(out var request));

        var maintenance = gate.TryEnterMaintenanceAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(maintenance.IsCompleted);
        Assert.False(gate.TryEnter(out _));
        var held = gate.EnterAsync(longRunning: false, CancellationToken.None).AsTask();

        request.Dispose();
        var window = await maintenance;
        Assert.NotNull(window);
        Assert.True(gate.InMaintenance);
        await Task.Delay(50);
        Assert.False(held.IsCompleted);

        window!.Dispose();
        using var lease = await held.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(gate.InMaintenance);
        Assert.Equal(1, gate.ActiveCount);
    }

    [Fact]
    public async Task MaintenanceBacksOutWhenWorkDoesNotFinishInTime()
    {
        var gate = new AppActivityGate();
        Assert.True(gate.TryEnter(out var request));

        var window = await gate.TryEnterMaintenanceAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Null(window);
        Assert.False(gate.InMaintenance);
        Assert.True(gate.TryEnter(out var other));
        other.Dispose();
        request.Dispose();
    }

    [Fact]
    public async Task WaitingForLongWorkDoesNotBlockOtherWork()
    {
        var gate = new AppActivityGate();
        Assert.True(gate.TryEnter(out var import, longRunning: true));

        var waiting = gate.WaitForLongWorkAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(gate.TryEnter(out var request));
        request.Dispose();
        Assert.False(waiting.IsCompleted);
        Assert.False(await gate.WaitForLongWorkAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None));

        import.Dispose();
        Assert.True(await waiting);
    }

    [Theory]
    [InlineData("GET", "navigate", "text/html")]
    [InlineData("GET", "cors", "application/json")]
    [InlineData("HEAD", "navigate", "")]
    public async Task ReadsDuringMaintenanceGetServiceUnavailableWithoutRunningThePage(string method, string fetchMode, string expectedType)
    {
        var gate = new AppActivityGate();
        using var window = await gate.TryEnterMaintenanceAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var ran = false;
        var middleware = new MaintenanceMiddleware(_ => { ran = true; return Task.CompletedTask; }, gate);
        var context = NewContext(method, "/journal/x/overview", fetchMode);

        await middleware.InvokeAsync(context);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("5", context.Response.Headers.RetryAfter.ToString());
        if (expectedType.Length > 0) Assert.StartsWith(expectedType, context.Response.ContentType);
    }

    [Fact]
    public async Task WritesDuringMaintenanceAreHeldAndThenRunNormally()
    {
        var gate = new AppActivityGate();
        var window = await gate.TryEnterMaintenanceAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var ran = false;
        var middleware = new MaintenanceMiddleware(context => { ran = true; context.Response.StatusCode = 200; return Task.CompletedTask; }, gate);
        var context = NewContext("POST", "/journal/x/review?handler=Autosave", "cors");

        var request = middleware.InvokeAsync(context);
        await Task.Delay(100);
        Assert.False(request.IsCompleted);
        Assert.False(ran);

        window!.Dispose();
        await request.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ran);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(0, gate.ActiveCount);
    }

    [Fact]
    public async Task HealthAndBackupStatusAnswerDuringMaintenance()
    {
        var gate = new AppActivityGate();
        using var window = await gate.TryEnterMaintenanceAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var calls = 0;
        var middleware = new MaintenanceMiddleware(_ => { calls++; return Task.CompletedTask; }, gate);

        await middleware.InvokeAsync(NewContext("GET", "/health", "cors"));
        await middleware.InvokeAsync(NewContext("GET", MaintenanceMiddleware.StatusPath, "cors"));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task BackupContainsTheDatabaseAndDataFilesWithoutSecretsAndIsListed()
    {
        var (database, journal) = CreateJournal();
        var image = await new DailyJournalImageService(database).SaveAsync(journal.Id, new DateOnly(2026, 11, 20), new MemoryStream(Png()), "chart.png", "image/png", Png().Length);
        Directory.CreateDirectory(Path.Combine(DataDirectory, DataDirectoryLayout.MigrationBackups));
        File.WriteAllText(Path.Combine(DataDirectory, DataDirectoryLayout.MigrationBackups, "tags-migration-old.db"), "excluded");
        Directory.CreateDirectory(Path.Combine(DataDirectory, "keys"));
        File.WriteAllText(Path.Combine(DataDirectory, "keys", "key.xml"), "excluded");
        Execute(database, $"INSERT INTO journal_publishing (journal_id, settings_json, protected_token) VALUES ('{journal.Id:D}', '{{}}', '{Secret}')");
        var service = CreateService(database, out var gate);

        Assert.True(service.TryStart(BackupTrigger.Manual, out var runId, out _));
        await service.WaitAsync();

        var run = Assert.Single(database.GetBackupRuns(5));
        Assert.Equal(runId, run.Id);
        Assert.Equal(BackupRunStatus.Completed, run.Status);
        Assert.NotNull(run.MaintenanceMilliseconds);
        Assert.False(gate.InMaintenance);
        Assert.False(service.Progress.Running);

        var listing = Assert.Single(service.ListBackups());
        Assert.True(listing.FromThisInstallation);
        Assert.Equal(1, listing.Sidecar.Counts["journals"]);
        Assert.Equal(1, listing.Sidecar.Counts["images"]);
        Assert.Equal(TradeFoundryDb.SchemaVersion, listing.Sidecar.SchemaVersion);
        Assert.Equal(new FileInfo(listing.ArchivePath).Length, listing.Sidecar.Size);
        Assert.Empty(Directory.GetFiles(BackupFolder, "*" + BackupService.PartialSuffix));
        Assert.False(Directory.Exists(Path.Combine(DataDirectory, BackupService.StagingDirectoryName, runId.ToString("N"))));

        var (manifest, sha256) = BackupArchive.Verify(listing.ArchivePath, null, CancellationToken.None);
        Assert.Equal(listing.Sidecar.Sha256, sha256);
        var paths = manifest.Entries.Select(entry => entry.Path).ToList();
        Assert.Contains("database/journal.db", paths);
        Assert.Contains($"files/{DataDirectoryLayout.DailyJournalImages}/{journal.Id:D}/{image.StorageKey}", paths);
        Assert.DoesNotContain(paths, path => path.Contains("tags-migration", StringComparison.Ordinal) || path.Contains("key.xml", StringComparison.Ordinal));

        var extracted = Path.Combine(_root, "extracted");
        ZipFile.ExtractToDirectory(listing.ArchivePath, extracted);
        var copy = Path.Combine(extracted, "database", "journal.db");
        Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(File.ReadAllBytes(copy)), StringComparison.Ordinal);
        using (var connection = new SqliteConnection($"Data Source={copy};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM journals";
            Assert.Equal("Live", command.ExecuteScalar());
        }

        Assert.Equal(Secret, Scalar(database, "SELECT protected_token FROM journal_publishing"));
    }

    [Fact]
    public async Task AMissingImageIsAWarningNotAFailure()
    {
        var (database, journal) = CreateJournal();
        var image = await new DailyJournalImageService(database).SaveAsync(journal.Id, new DateOnly(2026, 11, 20), new MemoryStream(Png()), "chart.png", "image/png", Png().Length);
        File.Delete(Path.Combine(DataDirectory, DataDirectoryLayout.DailyJournalImages, journal.Id.ToString("D"), image.StorageKey));
        var service = CreateService(database, out _);

        Assert.True(service.TryStart(BackupTrigger.Manual, out _, out _));
        await service.WaitAsync();

        var run = Assert.Single(database.GetBackupRuns(5));
        Assert.Equal(BackupRunStatus.CompletedWithWarnings, run.Status);
        Assert.Contains(image.StorageKey, Assert.Single(run.Warnings), StringComparison.Ordinal);
        Assert.Single(service.ListBackups());
    }

    [Fact]
    public async Task ABusySiteFailsTheBackupAndLeavesTheSiteOpen()
    {
        var (database, _) = CreateJournal();
        var service = CreateService(database, out var gate, drainTimeout: TimeSpan.FromMilliseconds(100));
        Assert.True(gate.TryEnter(out var stuckRequest));

        Assert.True(service.TryStart(BackupTrigger.Manual, out _, out _));
        await service.WaitAsync();
        stuckRequest.Dispose();

        var run = Assert.Single(database.GetBackupRuns(5));
        Assert.Equal(BackupRunStatus.Failed, run.Status);
        Assert.Contains("busy", run.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(gate.InMaintenance);
        Assert.Empty(service.ListBackups());
    }

    [Fact]
    public async Task ACancelledBackupLeavesNothingBehind()
    {
        var (database, _) = CreateJournal();
        var service = CreateService(database, out var gate);
        Assert.True(gate.TryEnter(out var import, longRunning: true));

        Assert.True(service.TryStart(BackupTrigger.Manual, out _, out _));
        Assert.False(service.TryStart(BackupTrigger.Manual, out _, out var message));
        Assert.Contains("already running", message, StringComparison.Ordinal);
        Assert.True(service.Cancel());
        await service.WaitAsync();
        import.Dispose();

        Assert.Equal(BackupRunStatus.Cancelled, Assert.Single(database.GetBackupRuns(5)).Status);
        Assert.False(gate.InMaintenance);
        Assert.Empty(service.ListBackups());
        Assert.Empty(Directory.Exists(BackupFolder) ? Directory.GetFiles(BackupFolder) : []);
    }

    [Fact]
    public void BackupFolderMustBeAbsoluteAndOutsideTheDataDirectory()
    {
        var (database, _) = CreateJournal();
        var service = CreateService(database, out _);

        Assert.NotNull(service.ValidateFolder("relative\\folder"));
        Assert.NotNull(service.ValidateFolder(DataDirectory));
        Assert.NotNull(service.ValidateFolder(Path.Combine(DataDirectory, "backups-inside")));
        Assert.Null(service.ValidateFolder(BackupFolder));
        Assert.Equal(DataDirectory + "-backups", new BackupService(database, new AppActivityGate(), Options.Create(new BackupOptions()), NullLogger<BackupService>.Instance).DefaultFolder);
    }

    [Fact]
    public async Task StartupMarksInterruptedRunsAndRemovesLeftovers()
    {
        var (database, _) = CreateJournal();
        database.InsertBackupRun(new BackupRun(Guid.NewGuid(), BackupTrigger.Manual, BackupRunStatus.Running, DateTimeOffset.UtcNow, null, null, string.Empty, null, string.Empty, []));
        var staging = Path.Combine(DataDirectory, BackupService.StagingDirectoryName, "old");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(BackupFolder);
        var partial = Path.Combine(BackupFolder, "tradefoundry-x.tfbackup.partial");
        File.WriteAllText(partial, "partial");
        var service = CreateService(database, out _);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(BackupRunStatus.Interrupted, Assert.Single(database.GetBackupRuns(5)).Status);
        Assert.False(Directory.Exists(staging));
        Assert.False(File.Exists(partial));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (TradeFoundryDb Database, Journal Journal) CreateJournal()
    {
        var database = new TradeFoundryDb(Options.Create(new StorageOptions { DataDirectory = DataDirectory, DatabaseFileName = "journal.db" }));
        database.CreateOwner("Owner", "test-hash");
        return (database, database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat"));
    }

    private BackupService CreateService(TradeFoundryDb database, out AppActivityGate gate, TimeSpan? drainTimeout = null)
    {
        gate = new AppActivityGate();
        database.SetBackupFolder(BackupFolder);
        return new BackupService(database, gate, Options.Create(new BackupOptions()), NullLogger<BackupService>.Instance)
        {
            DrainTimeoutOverride = drainTimeout
        };
    }

    private static DefaultHttpContext NewContext(string method, string path, string fetchMode)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        var query = path.IndexOf('?');
        context.Request.Path = query < 0 ? path : path[..query];
        context.Request.Headers["Sec-Fetch-Mode"] = fetchMode;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static byte[] Png()
    {
        using var image = new MagickImage(MagickColors.SteelBlue, 4, 4);
        return image.ToByteArray(MagickFormat.Png);
    }

    private static void Execute(TradeFoundryDb database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(TradeFoundryDb database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

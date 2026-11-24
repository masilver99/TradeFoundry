using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImageMagick;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;
using TradeFoundry.Services.Backup;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class RestoreTests : IDisposable
{
    private const string SourcePassword = "source-password";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tradefoundry-restore-tests", Guid.NewGuid().ToString("N"));
    private string BackupFolder => Path.Combine(_root, "backups");

    [Fact]
    public async Task ABackupRoundTripsIntoACleanInstallationWithTheDocumentedChanges()
    {
        var source = await CreateSourceWithBackupAsync();
        var target = Installation("target");
        target.Database.CreateOwner("Target owner", PasswordService.Hash("target-password"));
        var restore = Restore(target, desktop: true, out var lifetime);

        Assert.Equal(RestoreAccess.Allowed, restore.CheckAccess(authenticated: true, code: null));
        var listing = Assert.Single(restore.ListBackups(BackupFolder));
        Assert.True(restore.TryStageFromFolder(BackupFolder, listing.Sidecar.Archive, out _));
        var job = await WaitForStagingAsync(restore);
        Assert.Equal(RestoreJob.Ready, job.State);
        Assert.Equal(["Live"], job.Preview!.Journals);
        Assert.Equal("Source owner", job.Preview.OwnerName);
        Assert.True(target.Database.IsClean());

        Assert.Equal("That is not the password of the backup's owner account.", restore.Commit("target-password"));
        Assert.Null(restore.Commit(SourcePassword));
        Assert.True(PendingDataOperation.IsPending(target.Directory));
        Assert.True(SpinWait.SpinUntil(() => lifetime.Stopped, TimeSpan.FromSeconds(5)));

        Assert.True(PendingDataOperation.Apply(target.Directory, "journal.db", NullLogger.Instance));
        Assert.False(PendingDataOperation.IsPending(target.Directory));
        var restored = Open(target.Directory);
        var journal = Assert.Single(restored.GetJournals());
        Assert.Equal("Live", journal.Name);
        Assert.Equal("Source owner", restored.GetOwner()?.DisplayName);
        Assert.True(PasswordService.Verify(SourcePassword, restored.GetOwnerPasswordHash()!));
        Assert.Equal(Hash(source.ImagePath), Hash(Path.Combine(target.Directory, DataDirectoryLayout.DailyJournalImages, journal.Id.ToString("D"), Path.GetFileName(source.ImagePath))));

        Assert.True(restored.AreIntegrationsPaused());
        Assert.NotNull(restored.GetSessionEpoch());
        Assert.NotNull(restored.GetRestoredFrom());
        Assert.Null(restored.GetBackupFolder());
        Assert.NotEqual(source.InstallationId, restored.GetOrCreateInstallationId());
        Assert.Equal(1L, Scalar(target.Directory, "SELECT COUNT(*) FROM mcp_access_tokens WHERE revoked_utc IS NOT NULL"));
        Assert.Equal(string.Empty, Scalar(target.Directory, "SELECT protected_token FROM journal_publishing"));
        Assert.Equal(BackupRunStatus.Completed, Assert.Single(restored.GetBackupRuns(5)).Status);

        var aside = Assert.Single(Directory.GetDirectories(Path.Combine(target.Directory, DataDirectoryLayout.SetAside)));
        Assert.EndsWith("-before-restore", aside, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(aside, "journal.db")));
        Assert.False(Directory.Exists(Path.Combine(target.Directory, DataDirectoryLayout.RestoreStaging, job.Id.ToString("N"))));
    }

    [Fact]
    public async Task AnInstallationWithJournalsIsNeverRestoredInto()
    {
        await CreateSourceWithBackupAsync();
        var target = Installation("target");
        target.Database.CreateOwner("Target owner", PasswordService.Hash("target-password"));
        var restore = Restore(target, desktop: true, out _);
        Assert.True(restore.TryStageFromFolder(BackupFolder, Assert.Single(restore.ListBackups(BackupFolder)).Sidecar.Archive, out _));
        Assert.Equal(RestoreJob.Ready, (await WaitForStagingAsync(restore)).State);

        target.Database.CreateJournal("Created meanwhile", "live", string.Empty, "UTC", "USD", "flat_to_flat");

        Assert.Equal(RestoreAccess.NotClean, restore.CheckAccess(authenticated: true, code: null));
        Assert.Contains("no longer clean", restore.Commit(SourcePassword), StringComparison.Ordinal);
        Assert.False(PendingDataOperation.IsPending(target.Directory));
    }

    [Fact]
    public void RestoreBeforeSetupNeedsTheLoggedCodeExceptOnTheDesktop()
    {
        var target = Installation("target");
        var server = Restore(target, desktop: false, out _);
        Assert.True(server.RequiresCode);
        server.EnsureCode();
        Assert.Equal(RestoreAccess.NeedsCode, server.CheckAccess(authenticated: false, code: null));
        Assert.Equal(RestoreAccess.NeedsCode, server.CheckAccess(authenticated: false, code: "WRONG-CODE"));

        Assert.Equal(RestoreAccess.Allowed, Restore(target, desktop: true, out _).CheckAccess(authenticated: false, code: null));

        target.Database.CreateOwner("Owner", PasswordService.Hash(""));
        Assert.Equal(RestoreAccess.NeedsSignIn, server.CheckAccess(authenticated: false, code: null));
        Assert.Equal(RestoreAccess.Allowed, server.CheckAccess(authenticated: true, code: null));
    }

    [Fact]
    public async Task StartCleanKeepsTheOwnerAndPutBackRestoresEverything()
    {
        var source = await CreateSourceWithBackupAsync();
        var restore = Restore(source.Installation, desktop: true, out _);
        var ownerBefore = source.Installation.Database.GetStartCleanOwner();

        restore.StartClean();
        SqliteConnection.ClearAllPools();
        Assert.True(PendingDataOperation.Apply(source.Installation.Directory, "journal.db", NullLogger.Instance));

        var clean = Open(source.Installation.Directory);
        Assert.True(clean.IsClean());
        var ownerAfter = clean.GetStartCleanOwner();
        Assert.Equal(ownerBefore.Id, ownerAfter.Id);
        Assert.Equal(ownerBefore.PasswordHash, ownerAfter.PasswordHash);
        Assert.Equal(source.InstallationId, clean.GetOrCreateInstallationId());
        Assert.Equal(BackupFolder, clean.GetBackupFolder());
        Assert.False(Directory.Exists(Path.Combine(source.Installation.Directory, DataDirectoryLayout.DailyJournalImages)));

        var afterClean = Restore(new Install(source.Installation.Directory, clean), desktop: true, out _);
        var copy = Assert.Single(afterClean.ListSetAside());
        Assert.Equal(1, copy.Journals);

        Assert.Null(afterClean.PutBack(copy.Name));
        SqliteConnection.ClearAllPools();
        Assert.True(PendingDataOperation.Apply(source.Installation.Directory, "journal.db", NullLogger.Instance));

        var back = Open(source.Installation.Directory);
        Assert.Equal("Live", Assert.Single(back.GetJournals()).Name);
        Assert.Equal(source.ImageHash, Hash(source.ImagePath));
        Assert.Empty(Restore(new Install(source.Installation.Directory, back), desktop: true, out _).ListSetAside());
    }

    [Fact]
    public void PutBackIsRefusedOnceTheInstallationHasJournals()
    {
        var installation = Installation("source");
        installation.Database.CreateOwner("Owner", PasswordService.Hash(""));
        installation.Database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
        var restore = Restore(installation, desktop: true, out _);

        Assert.Contains("no journals", restore.PutBack("20260101T000000Z"), StringComparison.Ordinal);
        Assert.False(PendingDataOperation.IsPending(installation.Directory));
    }

    [Theory]
    [InlineData(PendingOperation.MovingAside, false)]
    [InlineData(PendingOperation.MovingAside, true)]
    [InlineData(PendingOperation.MovingIn, true)]
    public async Task AnInterruptedRestoreResumesAtTheNextStart(string phase, bool databaseAlreadyMoved)
    {
        await CreateSourceWithBackupAsync();
        var target = Installation("target");
        target.Database.CreateOwner("Target owner", PasswordService.Hash(""));
        var restore = Restore(target, desktop: true, out _);
        Assert.True(restore.TryStageFromFolder(BackupFolder, Assert.Single(restore.ListBackups(BackupFolder)).Sidecar.Archive, out _));
        var job = await WaitForStagingAsync(restore);
        Assert.Null(restore.Commit(SourcePassword));
        SqliteConnection.ClearAllPools();

        // Simulate a crash part-way: the marker is in the given phase and some moves already happened.
        var marker = JsonSerializer.Deserialize<PendingOperation>(File.ReadAllText(PendingDataOperation.MarkerPath(target.Directory)), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        PendingDataOperation.Write(target.Directory, marker with { Phase = phase });
        var aside = Path.Combine(target.Directory, DataDirectoryLayout.SetAside, marker.AsideName);
        if (databaseAlreadyMoved)
        {
            Directory.CreateDirectory(aside);
            File.Move(Path.Combine(target.Directory, "journal.db"), Path.Combine(aside, "journal.db"));
            if (phase == PendingOperation.MovingIn)
                File.Move(Path.Combine(target.Directory, DataDirectoryLayout.RestoreStaging, job.Id.ToString("N"), "journal.db"), Path.Combine(target.Directory, "journal.db"));
        }

        Assert.True(PendingDataOperation.Apply(target.Directory, "journal.db", NullLogger.Instance));
        Assert.Equal("Live", Assert.Single(Open(target.Directory).GetJournals()).Name);
        Assert.True(File.Exists(Path.Combine(aside, "journal.db")));
    }

    [Fact]
    public void AFailedRestorePutsThePreviousDataBack()
    {
        var target = Installation("target");
        target.Database.CreateOwner("Target owner", PasswordService.Hash(""));
        SqliteConnection.ClearAllPools();
        PendingDataOperation.Write(target.Directory, new PendingOperation(
            PendingOperation.Restore, PendingOperation.MovingAside, DateTimeOffset.UtcNow, "20260101T000000Z-before-restore", StagingId: "missing"));

        Assert.False(PendingDataOperation.Apply(target.Directory, "journal.db", NullLogger.Instance));

        Assert.False(PendingDataOperation.IsPending(target.Directory));
        Assert.Contains("previous data was put back", PendingDataOperation.LastError, StringComparison.Ordinal);
        Assert.Equal("Target owner", Open(target.Directory).GetOwner()?.DisplayName);
    }

    [Theory]
    [InlineData("files/daily-journal-images/../../evil.txt", "unsafe file name")]
    [InlineData("files/keys/key.xml", "does not restore")]
    [InlineData("files/daily-journal-images/a/..", "unsafe file name")]
    [InlineData("other/readme.txt", "does not restore")]
    public void HostilePathsAreRefused(string path, string expected)
    {
        var archive = WriteArchive(extra: [(path, "x"u8.ToArray())]);

        var error = Assert.Throws<InvalidDataException>(() => BackupRestoreReader.Extract(archive, Path.Combine(_root, "staging"), null, CancellationToken.None));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DamagedAndUnlistedEntriesAreRefused()
    {
        var tampered = WriteArchive(extra: [("files/image-settings/a.json", "{}"u8.ToArray())], tamper: "files/image-settings/a.json");
        Assert.Contains("damaged", Assert.Throws<InvalidDataException>(() => BackupRestoreReader.Extract(tampered, Path.Combine(_root, "s1"), null, CancellationToken.None)).Message, StringComparison.Ordinal);

        var unlisted = WriteArchive(unlisted: ("files/image-settings/b.json", "{}"u8.ToArray()));
        Assert.Contains("does not list", Assert.Throws<InvalidDataException>(() => BackupRestoreReader.Extract(unlisted, Path.Combine(_root, "s2"), null, CancellationToken.None)).Message, StringComparison.Ordinal);

        var collision = WriteArchive(extra: [("files/image-settings/a.json", "{}"u8.ToArray()), ("files/image-settings/A.json", "{}"u8.ToArray())]);
        Assert.Contains("ignoring case", Assert.Throws<InvalidDataException>(() => BackupRestoreReader.Extract(collision, Path.Combine(_root, "s3"), null, CancellationToken.None)).Message, StringComparison.Ordinal);

        var newer = WriteArchive(schemaVersion: TradeFoundryDb.SchemaVersion + 1);
        Assert.Contains("newer version", Assert.Throws<InvalidDataException>(() => BackupRestoreReader.Extract(newer, Path.Combine(_root, "s4"), null, CancellationToken.None)).Message, StringComparison.Ordinal);

        var truncated = Path.Combine(_root, "truncated.tfbackup");
        var bytes = File.ReadAllBytes(WriteArchive());
        File.WriteAllBytes(truncated, bytes[..(bytes.Length / 2)]);
        Assert.ThrowsAny<InvalidDataException>(() => BackupRestoreReader.Extract(truncated, Path.Combine(_root, "s5"), null, CancellationToken.None));
    }

    [Fact]
    public void ADatabaseWithTriggersIsRefusedBeforeTheAppTouchesIt()
    {
        var installation = Installation("hostile");
        installation.Database.CreateOwner("Owner", PasswordService.Hash(""));
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Data Source={installation.Database.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER evil AFTER INSERT ON journals BEGIN DELETE FROM app_users; END";
            command.ExecuteNonQuery();
        }

        var error = Assert.Throws<InvalidDataException>(() => BackupRestoreReader.CheckDatabase(installation.Database.DatabasePath));
        Assert.Contains("trigger", error.Message, StringComparison.Ordinal);
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

    private sealed record Install(string Directory, TradeFoundryDb Database);

    private sealed record Source(Install Installation, string ImagePath, string ImageHash, Guid InstallationId);

    private async Task<Source> CreateSourceWithBackupAsync()
    {
        var source = Installation("source");
        source.Database.CreateOwner("Source owner", PasswordService.Hash(SourcePassword));
        var journal = source.Database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
        var png = Png();
        var image = await new DailyJournalImageService(source.Database).SaveAsync(journal.Id, new DateOnly(2026, 11, 20), new MemoryStream(png), "chart.png", "image/png", png.Length);
        new McpTokenService(source.Database).Create("Client", [journal.Id], false);
        Execute(source.Directory, $"INSERT INTO journal_publishing (journal_id, settings_json, protected_token) VALUES ('{journal.Id:D}', '{{}}', 'secret')");
        Execute(source.Directory, $"UPDATE journals SET import_watch_directory = 'C:\\Trading\\Exports' WHERE id = '{journal.Id:D}'");
        source.Database.SetBackupFolder(BackupFolder);
        var backups = new BackupService(source.Database, new AppActivityGate(), Options.Create(new BackupOptions()), NullLogger<BackupService>.Instance);
        Assert.True(backups.TryStart(BackupTrigger.Manual, out _, out _));
        await backups.WaitAsync();
        Assert.Equal(BackupRunStatus.Completed, source.Database.GetBackupRuns(1)[0].Status);
        var imagePath = Path.Combine(source.Directory, DataDirectoryLayout.DailyJournalImages, journal.Id.ToString("D"), image.StorageKey);
        return new Source(source, imagePath, Hash(imagePath), source.Database.GetOrCreateInstallationId());
    }

    private Install Installation(string name)
    {
        var directory = Path.Combine(_root, name);
        return new Install(directory, Open(directory));
    }

    private static TradeFoundryDb Open(string directory) =>
        new(Options.Create(new StorageOptions { DataDirectory = directory, DatabaseFileName = "journal.db" }));

    private static RestoreService Restore(Install installation, bool desktop, out FakeLifetime lifetime)
    {
        lifetime = new FakeLifetime();
        var backups = new BackupService(installation.Database, new AppActivityGate(), Options.Create(new BackupOptions()), NullLogger<BackupService>.Instance);
        return new RestoreService(installation.Database, backups, new HostRuntimeInfo(desktop), new HostRestart(), lifetime, NullLogger<RestoreService>.Instance);
    }

    private static async Task<RestoreJob> WaitForStagingAsync(RestoreService restore)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (restore.Job is { State: RestoreJob.Staging })
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Staging did not finish.");
            await Task.Delay(50);
        }

        var job = restore.Job!;
        Assert.True(job.State == RestoreJob.Ready, job.Message);
        return job;
    }

    private string WriteArchive(
        IReadOnlyList<(string Path, byte[] Content)>? extra = null,
        string? tamper = null,
        (string Path, byte[] Content)? unlisted = null,
        int? schemaVersion = null)
    {
        var path = Path.Combine(_root, $"crafted-{Guid.NewGuid():N}.tfbackup");
        Directory.CreateDirectory(_root);
        var entries = new List<(string Path, byte[] Content)> { ("database/journal.db", "not really a database"u8.ToArray()) };
        entries.AddRange(extra ?? []);
        var manifestEntries = entries.Select(entry => new BackupManifestEntry(entry.Path, entry.Content.Length, Convert.ToHexStringLower(SHA256.HashData(entry.Content)))).ToList();
        var manifest = new BackupManifest(BackupManifest.FormatName, 1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, BackupTrigger.Manual, "test", "test",
            schemaVersion ?? TradeFoundryDb.SchemaVersion, false, "database/journal.db", new Dictionary<string, long>(), [], manifestEntries);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            using var stream = zip.CreateEntry(entryPath).Open();
            var written = entryPath == tamper ? content.Select(value => (byte)(value ^ 0xFF)).ToArray() : content;
            stream.Write(written);
        }

        if (unlisted is { } item)
        {
            using var stream = zip.CreateEntry(item.Path).Open();
            stream.Write(item.Content);
        }

        using (var stream = zip.CreateEntry(BackupArchive.ManifestEntry).Open())
            JsonSerializer.Serialize(stream, manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return path;
    }

    private static byte[] Png()
    {
        using var image = new MagickImage(MagickColors.SteelBlue, 4, 4);
        return image.ToByteArray(MagickFormat.Png);
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static void Execute(string directory, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "journal.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(string directory, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "journal.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public bool Stopped { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopped = true;
    }
}

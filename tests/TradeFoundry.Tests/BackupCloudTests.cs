using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
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

public sealed class BackupCloudTests : IDisposable
{
    private const string OwnerPassword = "source-password";
    private const string Passphrase = "correct horse battery staple";
    private const string PublishingToken = "pt_0123456789abcdefghijklmnopqrstuvwxyz";
    private static readonly B2Connection Connection = new("https://s3.us-west-002.backblazeb2.com", "tradefoundry-backup", "keyid", "secret");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tradefoundry-cloud-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeCloud _cloud = new();
    private string BackupFolder => Path.Combine(_root, "backups");

    [Fact]
    public async Task AnEncryptedBackupCarriesPublishingTokensAndRestoresOnlyWithItsPassphrase()
    {
        var source = await CreateSourceAsync(encrypted: true, cloud: false);
        var listing = Assert.Single(source.Backups.ListBackups());
        Assert.True(listing.Sidecar.Encrypted);
        Assert.True(BackupEncryption.IsEncrypted(listing.ArchivePath));
        Assert.Throws<InvalidDataException>(() => ZipFile.OpenRead(listing.ArchivePath).Dispose());

        // Inside: tokens in secrets.json, and no protected token, passphrase or cloud key in the database.
        using (var decrypted = BackupEncryption.OpenDecrypted(listing.ArchivePath, Passphrase))
        using (var zip = new ZipArchive(decrypted, ZipArchiveMode.Read))
        {
            using (var secrets = zip.GetEntry(BackupArchive.SecretsEntry)!.Open())
                Assert.Equal(PublishingToken, Assert.Single(JsonSerializer.Deserialize<BackupSecretsFile>(secrets, BackupArchive.Json)!.PublishingTokens).Token);
            var copy = Path.Combine(_root, "inspect.db");
            zip.GetEntry("database/journal.db")!.ExtractToFile(copy);
            Assert.Equal(0L, Scalar(copy, $"SELECT COUNT(*) FROM app_settings WHERE settings_key IN ('{string.Join("','", TradeFoundryDb.MachineOnlyBackupSettings)}')"));
            Assert.Equal(string.Empty, Scalar(copy, "SELECT protected_token FROM journal_publishing"));
        }

        Assert.Throws<BackupPassphraseException>(() => BackupEncryption.OpenDecrypted(listing.ArchivePath, "not the passphrase").Dispose());

        // A different machine: its own data protection keys, so the token must be protected again there.
        var target = Installation("target");
        var targetProtection = new EphemeralDataProtectionProvider();
        var restore = Restore(target, targetProtection, out var lifetime);
        Assert.True(restore.TryStageFromFolder(BackupFolder, listing.Sidecar.Archive, out _));
        Assert.Equal(RestoreJob.NeedsPassphrase, (await WaitAsync(restore)).State);
        Assert.True(restore.Unlock("not the passphrase", out _));
        var refused = await WaitAsync(restore);
        Assert.Equal(RestoreJob.NeedsPassphrase, refused.State);
        Assert.Equal("That passphrase does not open this backup.", refused.Message);
        Assert.True(restore.Unlock(Passphrase, out _));
        var ready = await WaitAsync(restore);
        Assert.True(ready.State == RestoreJob.Ready, ready.Message);
        Assert.Equal(1, ready.Preview!.PublishingTokens);

        Assert.Null(restore.Commit(OwnerPassword));
        Assert.True(SpinWait.SpinUntil(() => lifetime.Stopped, TimeSpan.FromSeconds(5)));
        Assert.True(PendingDataOperation.Apply(target.Directory, "journal.db", NullLogger.Instance));
        var restored = Open(target.Directory);
        var stored = (string)Scalar(Path.Combine(target.Directory, "journal.db"), "SELECT protected_token FROM journal_publishing")!;
        Assert.Equal(PublishingToken, targetProtection.CreateProtector(PublicationWorkflow.TokenPurpose).Unprotect(stored));
        Assert.Null(restored.GetProtectedBackupPassphrase());
        Assert.Null(restored.GetBackupCloudSettings());
    }

    [Fact]
    public async Task ACloudBackupIsUploadedArchiveFirstAndRetentionOnlyHidesBackups()
    {
        var source = await CreateSourceAsync(encrypted: true, cloud: true);
        var run = source.Database.GetBackupRuns(1)[0];
        Assert.Equal(BackupCloudStatus.Uploaded, run.CloudStatus);
        var prefix = CloudBackups.InstallationPrefix(source.Database.GetOrCreateInstallationId());
        var archiveKey = prefix + run.ArchiveName;
        Assert.Equal([archiveKey, CloudBackups.SidecarKey(archiveKey)], _cloud.PutOrder);
        Assert.Equal([archiveKey], source.Database.GetUnprunedCloudUploads());
        Assert.Equal(1, _cloud.AbortCalls);

        // Ten older scheduled backups, one a day; keeping 3 daily hides the other 7, sidecar first. The manual one is never a candidate.
        var installationId = source.Database.GetOrCreateInstallationId();
        for (var day = 1; day <= 10; day++) _cloud.Seed(installationId, DateTimeOffset.UtcNow.AddDays(-day), BackupTrigger.Scheduled);
        var schedule = BackupSchedule.Default with { KeepDaily = 3, KeepWeekly = 0, KeepMonthly = 0, TimeZoneId = "UTC" };
        var state = await source.Backups.RefreshCloudAsync(schedule, CancellationToken.None);
        Assert.Null(state.Error);
        Assert.Equal(4, state.Backups.Count);
        Assert.Contains(state.Backups, item => item.ArchivePath == archiveKey);
        Assert.Equal(14, _cloud.HideOrder.Count);
        Assert.EndsWith(".json", _cloud.HideOrder[0], StringComparison.Ordinal);
        Assert.Equal(0, _cloud.EraseCalls);
        Assert.Empty(source.Database.GetMissingCloudBackups());

        var listed = await CloudBackups.ListAsync(_cloud, installationId, includeHidden: true, CancellationToken.None);
        Assert.Equal(11, listed.Count);
        Assert.Equal(7, listed.Count(item => item.Hidden));
    }

    [Fact]
    public async Task ABackupThatDisappearsIsReportedAndCanStillBeRestoredWhileHidden()
    {
        var source = await CreateSourceAsync(encrypted: true, cloud: true);
        var run = source.Database.GetBackupRuns(1)[0];
        var archiveKey = CloudBackups.InstallationPrefix(source.Database.GetOrCreateInstallationId()) + run.ArchiveName;

        // Someone hides it with the key; TradeFoundry did not prune it.
        await _cloud.HideAsync(CloudBackups.SidecarKey(archiveKey), CancellationToken.None);
        await _cloud.HideAsync(archiveKey, CancellationToken.None);
        await source.Backups.RefreshCloudAsync(null, CancellationToken.None);
        Assert.Equal([archiveKey], source.Database.GetMissingCloudBackups());

        var target = Installation("target");
        var restore = Restore(target, new EphemeralDataProtectionProvider(), out _);
        Assert.Empty(await restore.ListCloudBackupsAsync(Connection, includeHidden: false, CancellationToken.None));
        var hidden = Assert.Single(await restore.ListCloudBackupsAsync(Connection, includeHidden: true, CancellationToken.None));
        Assert.True(hidden.Hidden);
        Assert.NotNull(hidden.VersionId);

        Assert.True(restore.TryStageFromCloud(hidden.ArchivePath, hidden.VersionId, out var message), message);
        Assert.Equal(RestoreJob.NeedsPassphrase, (await WaitAsync(restore)).State);
        Assert.True(restore.Unlock(Passphrase, out _));
        var ready = await WaitAsync(restore);
        Assert.True(ready.State == RestoreJob.Ready, ready.Message);
        Assert.Equal(["Live"], ready.Preview!.Journals);
    }

    [Fact]
    public async Task AFailedUploadKeepsTheBackupAndCanBeRetriedWithoutANewSnapshot()
    {
        _cloud.FailPuts = true;
        var source = await CreateSourceAsync(encrypted: true, cloud: true);
        var run = source.Database.GetBackupRuns(1)[0];
        Assert.Equal(BackupRunStatus.Completed, run.Status);
        Assert.Equal(BackupCloudStatus.Failed, run.CloudStatus);
        Assert.Contains("failed", run.CloudMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Single(source.Backups.ListBackups());
        Assert.Empty(_cloud.ListKeys());

        _cloud.FailPuts = false;
        Assert.True(source.Backups.TryStartUpload(run.Id, out var message), message);
        await source.Backups.WaitAsync();
        var retried = source.Database.GetBackupRun(run.Id)!;
        Assert.Equal(BackupCloudStatus.Uploaded, retried.CloudStatus);
        Assert.Equal(run.MaintenanceMilliseconds, retried.MaintenanceMilliseconds);
        Assert.Equal(2, _cloud.ListKeys().Count);
    }

    [Fact]
    public async Task TheSchedulerRetriesAFailedUploadAndClearsItsWarning()
    {
        _cloud.FailPuts = true;
        var source = await CreateSourceAsync(encrypted: true, cloud: true);
        source.Database.SetBackupSchedule(BackupSchedule.Default with { Enabled = false });
        var clock = new FakeClock { Now = DateTimeOffset.UtcNow };
        var scheduler = new BackupScheduler(source.Database, source.Backups, clock, NullLogger<BackupScheduler>.Instance);

        await scheduler.CheckAsync(CancellationToken.None);
        Assert.StartsWith("backup-upload-failed:", scheduler.Warning?.Key, StringComparison.Ordinal);
        Assert.Empty(_cloud.ListKeys());

        _cloud.FailPuts = false;
        clock.Now += TimeSpan.FromMinutes(6);
        await scheduler.CheckAsync(CancellationToken.None);
        Assert.Equal(BackupCloudStatus.Uploaded, source.Database.GetBackupRuns(1)[0].CloudStatus);
        Assert.Null(scheduler.Warning);
        Assert.Single(source.Database.GetBackupRuns(10));
    }

    [Fact]
    public async Task ADamagedEncryptedBackupIsRefused()
    {
        var source = await CreateSourceAsync(encrypted: true, cloud: false);
        var listing = Assert.Single(source.Backups.ListBackups());
        using (var file = new FileStream(listing.ArchivePath, FileMode.Open)) file.SetLength(file.Length - 1000);

        var restore = Restore(Installation("target"), new EphemeralDataProtectionProvider(), out _);
        Assert.True(restore.TryStageFromFolder(BackupFolder, listing.Sidecar.Archive, out _));
        Assert.Equal(RestoreJob.NeedsPassphrase, (await WaitAsync(restore)).State);
        Assert.True(restore.Unlock(Passphrase, out _));
        var failed = await WaitAsync(restore);
        Assert.Equal(RestoreJob.Failed, failed.State);
        Assert.False(Directory.Exists(Path.Combine(_root, "target", DataDirectoryLayout.RestoreStaging, failed.Id.ToString("N"))));
    }

    [Fact]
    public async Task CloudBackupWithoutEncryptionIsRefusedBeforeTheSitePauses()
    {
        var source = await CreateSourceAsync(encrypted: false, cloud: true, expectSuccess: false);
        var run = source.Database.GetBackupRuns(1)[0];
        Assert.Equal(BackupRunStatus.Failed, run.Status);
        Assert.Null(run.MaintenanceMilliseconds);
        Assert.Contains("encrypted", run.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheConnectionTestExpectsTheKeyToBeRefusedPermanentDeletes()
    {
        var installation = Guid.NewGuid();
        var safe = await CloudBackups.TestAsync(_cloud, installation, CancellationToken.None);
        Assert.True(safe.Works);
        Assert.False(safe.CanErase);
        Assert.Empty(await _cloud.ListAsync(CloudBackups.Root, includeHidden: false, CancellationToken.None));

        _cloud.AllowErase = true;
        var unsafeKey = await CloudBackups.TestAsync(_cloud, installation, CancellationToken.None);
        Assert.True(unsafeKey.Works);
        Assert.True(unsafeKey.CanErase);
        Assert.Contains("permanently delete", unsafeKey.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://s3.us-west-002.backblazeb2.com", "https://s3.us-west-002.backblazeb2.com")]
    [InlineData("s3.eu-central-003.backblazeb2.com/", "https://s3.eu-central-003.backblazeb2.com")]
    [InlineData("HTTPS://S3.US-EAST-005.BACKBLAZEB2.COM", "https://s3.us-east-005.backblazeb2.com")]
    [InlineData("http://s3.us-west-002.backblazeb2.com", null)]
    [InlineData("https://s3.us-west-002.backblazeb2.com.evil.example", null)]
    [InlineData("https://s3.amazonaws.com", null)]
    [InlineData("https://s3.us-west-002.backblazeb2.com/bucket", null)]
    [InlineData("https://s3.us-west-002.backblazeb2.com:8443", null)]
    public void OnlyHttpsBackblazeEndpointsAreAccepted(string input, string? expected) =>
        Assert.Equal(expected, B2Cloud.NormalizeEndpoint(input));

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

    private sealed record Source(TradeFoundryDb Database, BackupService Backups);

    private async Task<Source> CreateSourceAsync(bool encrypted, bool cloud, bool expectSuccess = true)
    {
        var source = Installation("source");
        source.Database.CreateOwner("Source owner", PasswordService.Hash(OwnerPassword));
        var journal = source.Database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
        var protection = new EphemeralDataProtectionProvider();
        var token = protection.CreateProtector(PublicationWorkflow.TokenPurpose).Protect(PublishingToken);
        Execute(Path.Combine(source.Directory, "journal.db"), $"INSERT INTO journal_publishing (journal_id, settings_json, protected_token) VALUES ('{journal.Id:D}', '{{}}', '{token}')");
        source.Database.SetBackupFolder(BackupFolder);
        var secrets = new BackupSecrets(source.Database, protection);
        if (encrypted) secrets.SetPassphrase(Passphrase);
        if (cloud) secrets.SetCloudConnection(Connection);
        var backups = new BackupService(source.Database, new AppActivityGate(), Options.Create(new BackupOptions()), NullLogger<BackupService>.Instance, secrets, new FakeCloudFactory(_cloud));
        Assert.True(backups.TryStart(BackupTrigger.Manual, out _, out _));
        await backups.WaitAsync();
        var run = source.Database.GetBackupRuns(1)[0];
        if (expectSuccess) Assert.True(run.Status == BackupRunStatus.Completed, run.Message);
        return new Source(source.Database, backups);
    }

    private Install Installation(string name)
    {
        var directory = Path.Combine(_root, name);
        return new Install(directory, Open(directory));
    }

    private static TradeFoundryDb Open(string directory) =>
        new(Options.Create(new StorageOptions { DataDirectory = directory, DatabaseFileName = "journal.db" }));

    private RestoreService Restore(Install installation, IDataProtectionProvider protection, out FakeLifetime lifetime)
    {
        lifetime = new FakeLifetime();
        var secrets = new BackupSecrets(installation.Database, protection);
        var backups = new BackupService(installation.Database, new AppActivityGate(), Options.Create(new BackupOptions()), NullLogger<BackupService>.Instance, secrets, new FakeCloudFactory(_cloud));
        return new RestoreService(installation.Database, backups, new HostRuntimeInfo(true), new HostRestart(), lifetime, NullLogger<RestoreService>.Instance, secrets);
    }

    private static async Task<RestoreJob> WaitAsync(RestoreService restore)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (restore.Job is { State: RestoreJob.Staging })
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Staging did not finish.");
            await Task.Delay(50);
        }

        return restore.Job!;
    }

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
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

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeCloudFactory(FakeCloud cloud) : IBackupCloudFactory
    {
        public IBackupCloud Create(B2Connection connection) => cloud;
    }

    /// <summary>A versioned bucket that behaves like B2 for a key without deleteFiles.</summary>
    private sealed class FakeCloud : IBackupCloud
    {
        private readonly Dictionary<string, List<(string VersionId, byte[]? Content, long Order)>> _versions = new(StringComparer.Ordinal);
        private long _order;
        public bool FailPuts { get; set; }
        public bool AllowErase { get; set; }
        public List<string> PutOrder { get; } = [];
        public List<string> HideOrder { get; } = [];
        public int EraseCalls { get; private set; }
        public int AbortCalls { get; private set; }

        public List<string> ListKeys() => _versions.Where(pair => pair.Value[^1].Content is not null).Select(pair => pair.Key).ToList();

        public void Seed(Guid installationId, DateTimeOffset created, string trigger)
        {
            var name = $"tradefoundry-{installationId.ToString("N")[..8]}-{created.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}-{Guid.NewGuid().ToString("N")[..8]}";
            var key = CloudBackups.ArchiveKey(installationId, name + BackupArchive.Extension);
            Add(key, [1, 2, 3]);
            var sidecar = new BackupSidecar(BackupManifest.FormatName, 1, Guid.NewGuid(), installationId, created, trigger, "test", TradeFoundryDb.SchemaVersion, true,
                name + BackupArchive.Extension, 3, "00", new Dictionary<string, long>(), []);
            Add(CloudBackups.SidecarKey(key), JsonSerializer.SerializeToUtf8Bytes(sidecar, BackupArchive.Json));
        }

        private void Add(string key, byte[]? content)
        {
            if (!_versions.TryGetValue(key, out var list)) _versions[key] = list = [];
            list.Add((Guid.NewGuid().ToString("N"), content, ++_order));
        }

        public Task PutFileAsync(string key, string path, IProgress<double>? progress, CancellationToken cancellationToken) =>
            PutBytesAsync(key, File.ReadAllBytes(path), cancellationToken);

        public Task PutBytesAsync(string key, byte[] content, CancellationToken cancellationToken)
        {
            if (FailPuts) throw new HttpRequestException("network down");
            Add(key, content);
            PutOrder.Add(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CloudObject>> ListAsync(string prefix, bool includeHidden, CancellationToken cancellationToken)
        {
            var result = new List<CloudObject>();
            foreach (var (key, list) in _versions.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var newestFile = list.LastOrDefault(version => version.Content is not null);
                if (newestFile.Content is null) continue;
                var hidden = list[^1].Content is null;
                if (hidden && !includeHidden) continue;
                result.Add(new CloudObject(key, newestFile.Content.Length, includeHidden ? newestFile.VersionId : null, DateTimeOffset.UtcNow, hidden));
            }

            return Task.FromResult<IReadOnlyList<CloudObject>>(result);
        }

        private byte[] Get(string key, string? versionId)
        {
            var list = _versions[key];
            if (versionId is not null) return list.Single(version => version.VersionId == versionId).Content ?? throw new InvalidOperationException("marker");
            return list[^1].Content ?? throw new InvalidOperationException("hidden");
        }

        public Task<byte[]> ReadBytesAsync(string key, string? versionId, long maxBytes, CancellationToken cancellationToken) => Task.FromResult(Get(key, versionId));

        public async Task DownloadAsync(string key, string? versionId, string path, IProgress<double>? progress, CancellationToken cancellationToken) =>
            await File.WriteAllBytesAsync(path, Get(key, versionId), cancellationToken);

        public Task HideAsync(string key, CancellationToken cancellationToken)
        {
            Add(key, null);
            HideOrder.Add(key);
            return Task.CompletedTask;
        }

        public Task<bool> TryDeleteVersionAsync(string key, string versionId, CancellationToken cancellationToken)
        {
            EraseCalls++;
            if (!AllowErase) return Task.FromResult(false);
            _versions[key].RemoveAll(version => version.VersionId == versionId);
            return Task.FromResult(true);
        }

        public Task AbortUnfinishedAsync(string prefix, CancellationToken cancellationToken)
        {
            AbortCalls++;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services.Backup;

public sealed record RestorePreview(BackupManifest Manifest, IReadOnlyList<string> Journals, string OwnerName, IReadOnlyList<string> Warnings, int PublishingTokens = 0);

public sealed record RestoreJob(Guid Id, string State, string Stage, double? Fraction, string? Message, RestorePreview? Preview)
{
    public const string Staging = "staging";
    public const string NeedsPassphrase = "needs-passphrase";
    public const string Ready = "ready";
    public const string Failed = "failed";
    public const string Committing = "committing";
}

/// <summary>A copy moved aside by start clean, offered back while the installation is still clean.</summary>
public sealed record SetAsideCopy(string Name, DateTimeOffset MovedUtc, long Journals, long Trades, long Bytes);

public enum RestoreAccess
{
    Allowed,
    NotClean,
    NeedsSignIn,
    NeedsCode
}

/// <summary>
/// Restores one backup into a clean installation. The backup is unpacked, checked and migrated in
/// staging while the site keeps running; only after the owner confirms with the backup's password is a
/// pending operation written and the host restarted, so nothing live changes until then.
/// </summary>
public sealed class RestoreService : IHostedService
{
    private const int MaxCodeAttempts = 10;
    private readonly TradeFoundryDb _database;
    private readonly BackupService _backups;
    private readonly HostRuntimeInfo _runtime;
    private readonly HostRestart _restart;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<RestoreService> _logger;
    private readonly object _sync = new();
    private RestoreJob? _job;
    private CancellationTokenSource? _jobCancellation;
    private (string ArchivePath, bool Owned)? _source;
    private B2Connection? _cloudConnection;
    private IReadOnlyList<BackupListing> _cloudListings = [];
    private readonly BackupSecrets? _secrets;
    private string? _code;
    private int _codeFailures;

    public RestoreService(TradeFoundryDb database, BackupService backups, HostRuntimeInfo runtime, HostRestart restart, IHostApplicationLifetime lifetime, ILogger<RestoreService> logger,
        BackupSecrets? secrets = null)
    {
        _secrets = secrets;
        _database = database;
        _backups = backups;
        _runtime = runtime;
        _restart = restart;
        _lifetime = lifetime;
        _logger = logger;
    }

    public string DataDirectory => Path.GetDirectoryName(_database.DatabasePath)!;
    private string StagingRoot => Path.Combine(DataDirectory, DataDirectoryLayout.RestoreStaging);
    private string SetAsideRoot => Path.Combine(DataDirectory, DataDirectoryLayout.SetAside);

    public RestoreJob? Job
    {
        get { lock (_sync) return _job; }
    }

    public string? LastOperationError => PendingDataOperation.LastError;

    /// <summary>Before setup on a server, a one-time code from the server log proves the visitor runs the server.</summary>
    public bool RequiresCode => !_runtime.IsDesktop && !_database.HasOwner();

    public RestoreAccess CheckAccess(bool authenticated, string? code)
    {
        if (!_database.IsClean()) return RestoreAccess.NotClean;
        if (_database.HasOwner()) return authenticated ? RestoreAccess.Allowed : RestoreAccess.NeedsSignIn;
        if (_runtime.IsDesktop) return RestoreAccess.Allowed;
        return CheckCode(code) ? RestoreAccess.Allowed : RestoreAccess.NeedsCode;
    }

    /// <summary>Creates the one-time code on first need and writes it to the server log.</summary>
    public void EnsureCode()
    {
        lock (_sync)
        {
            if (_code is not null || !RequiresCode) return;
            _code = NewCode();
            _logger.LogWarning("To restore a backup before setup, enter this one-time code on the restore page: {RestoreCode}", _code);
        }
    }

    public IReadOnlyList<BackupListing> ListBackups(string folder) => _backups.ListBackups(folder);

    /// <summary>Starts unpacking a backup from a folder this server can read.</summary>
    public bool TryStageFromFolder(string folder, string archiveName, out string message)
    {
        if (archiveName != Path.GetFileName(archiveName) || !archiveName.EndsWith(BackupArchive.Extension, StringComparison.OrdinalIgnoreCase))
        {
            message = "Choose a backup from the list.";
            return false;
        }

        var path = Path.Combine(Path.GetFullPath(folder), archiveName);
        if (!File.Exists(path))
        {
            message = "That backup is no longer in the folder.";
            return false;
        }

        return TryStart(path, null, out message);
    }

    /// <summary>Saves an uploaded backup into staging and starts unpacking it.</summary>
    public async Task<(bool Started, string Message)> StageUploadAsync(Stream body, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var directory = Path.Combine(StagingRoot, id.ToString("N"));
        Directory.CreateDirectory(directory);
        var upload = Path.Combine(directory, "upload" + BackupArchive.Extension);
        try
        {
            await using (var output = new FileStream(upload, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                await body.CopyToAsync(output, 1024 * 1024, cancellationToken);
            }
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }

        return TryStart(upload, id, out var message) ? (true, message) : (false, message);
    }

    /// <summary>The bucket last listed for restore, without its key, so the page can show which one.</summary>
    public string? CloudBucket { get { lock (_sync) return _cloudConnection?.Bucket; } }

    /// <summary>
    /// Lists backups in a B2 bucket, from every installation. The key is kept in memory only, for
    /// downloading the chosen backup, and is never stored.
    /// </summary>
    public async Task<IReadOnlyList<BackupListing>> ListCloudBackupsAsync(B2Connection connection, bool includeHidden, CancellationToken cancellationToken)
    {
        using var cloud = _backups.CloudFactory.Create(connection);
        var listings = await CloudBackups.ListAsync(cloud, null, includeHidden, cancellationToken);
        lock (_sync)
        {
            _cloudConnection = connection;
            _cloudListings = listings;
        }

        return listings;
    }

    /// <summary>Downloads a listed cloud backup into staging, checks its hash against the sidecar, then unpacks it like an upload.</summary>
    public bool TryStageFromCloud(string archiveKey, string? versionId, out string message)
    {
        lock (_sync)
        {
            var listing = _cloudListings.FirstOrDefault(item => item.ArchivePath == archiveKey && item.VersionId == versionId);
            if (_cloudConnection is not { } connection || listing is null)
            {
                message = "List the bucket's backups again, then choose one.";
                return false;
            }

            if (_job is { State: RestoreJob.Staging or RestoreJob.Committing })
            {
                message = "A backup is already being prepared.";
                return false;
            }

            if (_job is { } previous) TryDeleteDirectory(Path.Combine(StagingRoot, previous.Id.ToString("N")));
            var id = Guid.NewGuid();
            _jobCancellation?.Dispose();
            _jobCancellation = new CancellationTokenSource();
            _job = new RestoreJob(id, RestoreJob.Staging, "Downloading from Backblaze B2", 0, null, null);
            _source = null;
            var token = _jobCancellation.Token;
            _ = Task.Run(() => DownloadAndStageAsync(id, connection, listing, token), CancellationToken.None);
            message = "Downloading the backup.";
            return true;
        }
    }

    /// <summary>Continues a restore that stopped to ask for the backup's passphrase.</summary>
    public bool Unlock(string passphrase, out string message)
    {
        lock (_sync)
        {
            if (_job is not { State: RestoreJob.NeedsPassphrase } job || _source is not { } source)
            {
                message = "There is no encrypted backup waiting for its passphrase.";
                return false;
            }

            if (string.IsNullOrEmpty(passphrase))
            {
                message = "Enter the backup's passphrase.";
                return false;
            }

            _jobCancellation?.Dispose();
            _jobCancellation = new CancellationTokenSource();
            _job = job with { State = RestoreJob.Staging, Stage = "Opening the encrypted backup", Fraction = null, Message = null };
            var token = _jobCancellation.Token;
            _ = Task.Run(() => StageAsync(job.Id, source.ArchivePath, source.Owned, passphrase, token), CancellationToken.None);
            message = "Checking the backup.";
            return true;
        }
    }

    /// <summary>Drops a staged or failed restore.</summary>
    public void Discard()
    {
        lock (_sync)
        {
            if (_job?.State == RestoreJob.Committing) return;
            _jobCancellation?.Cancel();
            _job = null;
            _source = null;
        }

        TryDeleteDirectory(StagingRoot);
    }

    /// <summary>
    /// Confirms the staged restore with the backup owner's password, records it as pending and restarts.
    /// Returns an error for the owner, or null when the restart is under way.
    /// </summary>
    public string? Commit(string password)
    {
        lock (_sync)
        {
            if (_job is not { State: RestoreJob.Ready, Preview: { } preview } job) return "There is no checked backup ready to restore.";
            if (!_database.IsClean()) return "This installation is no longer clean, so the backup was not restored.";

            var staging = Path.Combine(StagingRoot, job.Id.ToString("N"));
            var databasePath = Path.Combine(staging, BackupRestoreReader.StagedDatabaseName);
            using (var connection = OpenStaged(databasePath, SqliteOpenMode.ReadWrite))
            {
                using var hash = connection.CreateCommand();
                hash.CommandText = "SELECT password_hash FROM app_users ORDER BY created_utc LIMIT 1";
                if (hash.ExecuteScalar() is not string stored || !PasswordService.Verify(password ?? string.Empty, stored))
                    return "That is not the password of the backup's owner account.";
                TradeFoundryDb.PrepareRestoredDatabase(connection, preview.Manifest, DateTimeOffset.UtcNow, ProtectCarriedTokens(staging));
            }

            TryDeleteFile(Path.Combine(staging, BackupRestoreReader.StagedSecretsName));
            lock (_sync) _cloudConnection = null;

            PendingDataOperation.Write(DataDirectory, new PendingOperation(
                PendingOperation.Restore, PendingOperation.MovingAside, DateTimeOffset.UtcNow,
                PendingDataOperation.NewAsideName("-before-restore"), StagingId: job.Id.ToString("N")));
            _job = job with { State = RestoreJob.Committing, Stage = "Restarting", Fraction = null };
            _logger.LogWarning("Restoring backup {BackupId} from {Created}; restarting.", preview.Manifest.BackupId, preview.Manifest.CreatedUtc);
        }

        RestartSoon();
        return null;
    }

    /// <summary>
    /// Moves the current data aside and restarts into a fresh database that keeps only the owner account.
    /// </summary>
    public void StartClean()
    {
        var owner = _database.GetStartCleanOwner();
        PendingDataOperation.Write(DataDirectory, new PendingOperation(
            PendingOperation.StartClean, PendingOperation.MovingAside, DateTimeOffset.UtcNow,
            PendingDataOperation.NewAsideName(), Owner: owner));
        _logger.LogWarning("Moving the current data aside and starting clean; restarting.");
        RestartSoon();
    }

    /// <summary>Puts a set-aside copy back, replacing the clean installation. Returns an error or null.</summary>
    public string? PutBack(string name)
    {
        if (!_database.IsClean()) return "Put back is only possible while this installation has no journals.";
        if (ListSetAside().All(copy => copy.Name != name)) return "That set-aside copy no longer exists.";
        PendingDataOperation.Write(DataDirectory, new PendingOperation(
            PendingOperation.PutBack, PendingOperation.MovingAside, DateTimeOffset.UtcNow,
            PendingDataOperation.NewAsideName("-replaced"), SetAsideName: name));
        _logger.LogWarning("Putting back set-aside data {Name}; restarting.", name);
        RestartSoon();
        return null;
    }

    /// <summary>Set-aside copies that hold at least one journal, newest first.</summary>
    public IReadOnlyList<SetAsideCopy> ListSetAside()
    {
        if (!Directory.Exists(SetAsideRoot)) return [];
        var databaseFile = Path.GetFileName(_database.DatabasePath);
        var copies = new List<SetAsideCopy>();
        foreach (var directory in new DirectoryInfo(SetAsideRoot).EnumerateDirectories())
        {
            var database = Path.Combine(directory.FullName, databaseFile);
            if (!File.Exists(database)) continue;
            try
            {
                using var connection = OpenStaged(database, SqliteOpenMode.ReadOnly);
                var journals = Count(connection, "SELECT COUNT(*) FROM journals");
                if (journals == 0) continue;
                var trades = Count(connection, "SELECT COUNT(*) FROM trades");
                var bytes = directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
                var moved = DateTimeOffset.TryParseExact(directory.Name[..Math.Min(16, directory.Name.Length)], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed
                    : new DateTimeOffset(directory.CreationTimeUtc, TimeSpan.Zero);
                copies.Add(new SetAsideCopy(directory.Name, moved, journals, trades, bytes));
            }
            catch (SqliteException ex)
            {
                _logger.LogWarning(ex, "Could not read set-aside copy {Name}.", directory.Name);
            }
        }

        return copies.OrderByDescending(copy => copy.MovedUtc).ToList();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Staging only survives when a restore is pending; anything else is from an abandoned attempt.
        if (!PendingDataOperation.IsPending(DataDirectory)) TryDeleteDirectory(StagingRoot);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_sync) _jobCancellation?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>Publishing tokens from the backup's secrets.json, protected again with this machine's keys.</summary>
    private Dictionary<Guid, string> ProtectCarriedTokens(string staging)
    {
        var path = Path.Combine(staging, BackupRestoreReader.StagedSecretsName);
        var tokens = new Dictionary<Guid, string>();
        if (_secrets is null || !File.Exists(path)) return tokens;
        foreach (var token in ReadSecrets(path)?.PublishingTokens ?? [])
        {
            if (token.Token.Length is >= 32 and <= 4096 && token.Token.All(c => c is >= '!' and <= '~'))
                tokens[token.JournalId] = _secrets.ProtectPublishingToken(token.Token);
        }

        return tokens;
    }

    private static BackupSecretsFile? ReadSecrets(string path)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<BackupSecretsFile>(File.ReadAllText(path), BackupArchive.Json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private async Task DownloadAndStageAsync(Guid id, B2Connection connection, BackupListing listing, CancellationToken cancellationToken)
    {
        var staging = Path.Combine(StagingRoot, id.ToString("N"));
        var download = Path.Combine(staging, "download" + BackupArchive.Extension);
        try
        {
            Directory.CreateDirectory(staging);
            using (var cloud = _backups.CloudFactory.Create(connection))
            {
                await cloud.DownloadAsync(listing.ArchivePath, listing.VersionId, download, new StageProgress(this, id, "Downloading from Backblaze B2"), cancellationToken);
            }

            Report(id, "Checking the download", null);
            string hash;
            await using (var file = new FileStream(download, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            {
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
            }

            if (new FileInfo(download).Length != listing.Sidecar.Size || !string.Equals(hash, listing.Sidecar.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded backup does not match the size and hash recorded when it was made.");
        }
        catch (Exception ex)
        {
            var cancelled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            if (!cancelled && ex is not InvalidDataException) _logger.LogError(ex, "Could not download backup {Key}.", listing.ArchivePath);
            TryDeleteDirectory(staging);
            lock (_sync)
            {
                if (_job?.Id == id)
                    _job = cancelled ? null : _job with { State = RestoreJob.Failed, Stage = string.Empty, Fraction = null, Message = ex is InvalidDataException ? ex.Message : $"The backup could not be downloaded: {B2Cloud.Describe(ex)}" };
            }

            return;
        }

        await StageAsync(id, download, deleteArchive: true, null, cancellationToken);
    }

    private bool TryStart(string archivePath, Guid? uploadId, out string message)
    {
        lock (_sync)
        {
            if (_job is { State: RestoreJob.Staging or RestoreJob.Committing })
            {
                message = "A backup is already being prepared.";
                if (uploadId is { } discard) TryDeleteDirectory(Path.Combine(StagingRoot, discard.ToString("N")));
                return false;
            }

            if (_job is { } previous && previous.Id != uploadId) TryDeleteDirectory(Path.Combine(StagingRoot, previous.Id.ToString("N")));
            var id = uploadId ?? Guid.NewGuid();
            _jobCancellation?.Dispose();
            _jobCancellation = new CancellationTokenSource();
            _job = new RestoreJob(id, RestoreJob.Staging, "Checking the backup", null, null, null);
            _source = null;
            var token = _jobCancellation.Token;
            _ = Task.Run(() => StageAsync(id, archivePath, uploadId is not null, null, token), CancellationToken.None);
            message = "Checking the backup.";
            return true;
        }
    }

    private Task StageAsync(Guid id, string archivePath, bool deleteArchive, string? passphrase, CancellationToken cancellationToken)
    {
        var staging = Path.Combine(StagingRoot, id.ToString("N"));
        var unpacked = deleteArchive ? Path.Combine(staging, "unpacked") : staging;
        try
        {
            BackupManifest manifest;
            if (BackupEncryption.IsEncrypted(archivePath))
            {
                // Ask for the passphrase and keep the archive; nothing has been unpacked yet.
                Stream decrypted;
                try
                {
                    if (passphrase is null) throw new BackupPassphraseException();
                    Report(id, "Opening the encrypted backup", null);
                    decrypted = BackupEncryption.OpenDecrypted(archivePath, passphrase);
                }
                catch (BackupPassphraseException)
                {
                    lock (_sync)
                    {
                        if (_job?.Id == id)
                        {
                            _source = (archivePath, deleteArchive);
                            _job = _job with { State = RestoreJob.NeedsPassphrase, Stage = string.Empty, Fraction = null, Message = passphrase is null ? null : "That passphrase does not open this backup." };
                        }
                    }

                    return Task.CompletedTask;
                }

                using (decrypted)
                {
                    Report(id, "Decrypting, checking and unpacking the backup", 0);
                    manifest = BackupRestoreReader.Extract(decrypted, unpacked, new StageProgress(this, id, "Decrypting, checking and unpacking the backup"), cancellationToken);
                }
            }
            else
            {
                Report(id, "Checking and unpacking the backup", 0);
                manifest = BackupRestoreReader.Extract(archivePath, unpacked, new StageProgress(this, id, "Checking and unpacking the backup"), cancellationToken);
            }

            if (deleteArchive)
            {
                // Uploads are unpacked beside the upload; move the result up and drop the upload.
                File.Delete(archivePath);
                foreach (var entry in Directory.EnumerateFileSystemEntries(unpacked))
                {
                    var target = Path.Combine(staging, Path.GetFileName(entry));
                    if (Directory.Exists(entry)) Directory.Move(entry, target);
                    else File.Move(entry, target);
                }

                Directory.Delete(unpacked);
            }

            var databasePath = Path.Combine(staging, BackupRestoreReader.StagedDatabaseName);
            Report(id, "Checking the database", null);
            var warnings = BackupRestoreReader.CheckDatabase(databasePath).Concat(manifest.Warnings).ToList();
            cancellationToken.ThrowIfCancellationRequested();

            // Upgrade the staged copy with the normal initializer, so a failed upgrade leaves this
            // installation untouched. Then leave it as a single file with no WAL.
            Report(id, "Upgrading the backup to this version", null);
            _ = new TradeFoundryDb(Options.Create(new StorageOptions { DataDirectory = staging, DatabaseFileName = BackupRestoreReader.StagedDatabaseName }));
            SqliteConnection.ClearAllPools();
            string ownerName;
            var journals = new List<string>();
            using (var connection = OpenStaged(databasePath, SqliteOpenMode.ReadWrite))
            {
                using (var checkpoint = connection.CreateCommand())
                {
                    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode = DELETE;";
                    checkpoint.ExecuteNonQuery();
                }

                using (var owner = connection.CreateCommand())
                {
                    owner.CommandText = "SELECT display_name FROM app_users ORDER BY created_utc LIMIT 1";
                    ownerName = owner.ExecuteScalar() as string ?? "Owner";
                }

                using var names = connection.CreateCommand();
                names.CommandText = "SELECT name, archived FROM journals ORDER BY archived, created_utc";
                using var reader = names.ExecuteReader();
                while (reader.Read()) journals.Add(reader.GetInt64(1) == 0 ? reader.GetString(0) : $"{reader.GetString(0)} (archived)");
            }

            var secretsPath = Path.Combine(staging, BackupRestoreReader.StagedSecretsName);
            var carriedTokens = File.Exists(secretsPath) ? ReadSecrets(secretsPath)?.PublishingTokens.Count ?? 0 : 0;
            lock (_sync)
            {
                if (_job?.Id == id)
                    _job = _job with { State = RestoreJob.Ready, Stage = string.Empty, Fraction = null, Message = null, Preview = new RestorePreview(manifest, journals, ownerName, warnings, carriedTokens) };
            }
        }
        catch (Exception ex)
        {
            var cancelled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            if (!cancelled && ex is not InvalidDataException) _logger.LogError(ex, "Could not prepare backup {Archive} for restore.", archivePath);
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(staging);
            lock (_sync)
            {
                if (_job?.Id == id)
                    _job = cancelled ? null : _job with { State = RestoreJob.Failed, Stage = string.Empty, Fraction = null, Message = ex is InvalidDataException ? ex.Message : $"The backup could not be prepared: {ex.Message}" };
            }
        }

        return Task.CompletedTask;
    }

    private void RestartSoon() => _ = Task.Run(async () =>
    {
        // Let the response that confirmed the request reach the browser first.
        await Task.Delay(TimeSpan.FromMilliseconds(750));
        _restart.Request(_lifetime);
    });

    private void Report(Guid id, string stage, double? fraction)
    {
        lock (_sync)
        {
            if (_job?.Id == id && _job.State == RestoreJob.Staging) _job = _job with { Stage = stage, Fraction = fraction };
        }
    }

    private bool CheckCode(string? code)
    {
        lock (_sync)
        {
            if (_code is null || string.IsNullOrWhiteSpace(code)) return false;
            var normalized = code.Trim().ToUpperInvariant();
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(normalized), Encoding.UTF8.GetBytes(_code))) return true;
            if (++_codeFailures >= MaxCodeAttempts)
            {
                _code = NewCode();
                _codeFailures = 0;
                _logger.LogWarning("Too many wrong restore codes. The new one-time code is: {RestoreCode}", _code);
            }

            return false;
        }
    }

    private static string NewCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var characters = RandomNumberGenerator.GetItems<char>(alphabet, 8);
        return $"{new string(characters, 0, 4)}-{new string(characters, 4, 4)}";
    }

    private static SqliteConnection OpenStaged(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class StageProgress(RestoreService service, Guid id, string stage) : IProgress<double>
    {
        public void Report(double value) => service.Report(id, stage, value);
    }
}

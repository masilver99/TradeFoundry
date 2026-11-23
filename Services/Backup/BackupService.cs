using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services.Backup;

public sealed class BackupOptions
{
    /// <summary>Folder used until the owner chooses one. Empty means "&lt;data directory&gt;-backups" beside the data directory.</summary>
    public string DefaultFolder { get; set; } = string.Empty;
}

/// <summary>What the Backups page and <c>/backup-status</c> show. Kept in memory, so it can be read during maintenance.</summary>
public sealed record BackupProgress(
    bool Running,
    Guid? RunId,
    string Stage,
    double? Fraction,
    bool Maintenance,
    DateTimeOffset? StartedUtc,
    string? LastStatus,
    string? LastMessage);

/// <summary>
/// Runs one backup at a time: waits for long work, pauses the site only while the database and data
/// files are copied, then compresses, verifies and publishes the archive with the site open again.
/// </summary>
public sealed class BackupService : IHostedService
{
    public const string StagingDirectoryName = ".backup-staging";
    public const string PartialSuffix = ".partial";
    public const string ArchivePrefix = "tradefoundry-";

    internal static readonly TimeSpan LongWorkTimeout = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Tests shorten the drain wait.</summary>
    internal TimeSpan? DrainTimeoutOverride { get; init; }

    private readonly TradeFoundryDb _database;
    private readonly AppActivityGate _gate;
    private readonly BackupOptions _options;
    private readonly ILogger<BackupService> _logger;
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private Task _running = Task.CompletedTask;
    private BackupProgress _progress = new(false, null, string.Empty, null, false, null, null, null);

    public BackupService(TradeFoundryDb database, AppActivityGate gate, IOptions<BackupOptions> options, ILogger<BackupService> logger)
    {
        _database = database;
        _gate = gate;
        _options = options.Value;
        _logger = logger;
    }

    public string DataDirectory => Path.GetDirectoryName(_database.DatabasePath)!;

    public string DefaultFolder => string.IsNullOrWhiteSpace(_options.DefaultFolder)
        ? DataDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "-backups"
        : Path.GetFullPath(_options.DefaultFolder);

    public string Folder => _database.GetBackupFolder() ?? DefaultFolder;

    public BackupProgress Progress
    {
        get { lock (_sync) return _progress with { Maintenance = _gate.InMaintenance }; }
    }

    public static string AppVersion { get; } =
        typeof(BackupService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(BackupService).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>Starts a backup in the background. Returns false with a reason when one cannot start.</summary>
    public bool TryStart(string trigger, out Guid runId, out string message)
    {
        lock (_sync)
        {
            if (_progress.Running)
            {
                runId = _progress.RunId ?? Guid.Empty;
                message = "A backup is already running.";
                return false;
            }

            runId = Guid.NewGuid();
            var run = new BackupRun(runId, trigger, BackupRunStatus.Running, DateTimeOffset.UtcNow, null, null, string.Empty, null, string.Empty, []);
            _database.InsertBackupRun(run);
            _cancellation = new CancellationTokenSource();
            _progress = new BackupProgress(true, runId, "Starting", null, false, run.StartedUtc, _progress.LastStatus, _progress.LastMessage);
            var token = _cancellation.Token;
            _running = Task.Run(() => RunAsync(run, token), CancellationToken.None);
            message = "Backup started.";
            return true;
        }
    }

    public bool Cancel()
    {
        lock (_sync)
        {
            if (!_progress.Running || _cancellation is null) return false;
            _cancellation.Cancel();
            return true;
        }
    }

    /// <summary>Waits for the running backup, if any, to finish. For tests and shutdown.</summary>
    public Task WaitAsync() { lock (_sync) return _running; }

    public IReadOnlyList<BackupListing> ListBackups()
    {
        var folder = Folder;
        if (!Directory.Exists(folder)) return [];
        var installationId = _database.GetOrCreateInstallationId();
        var listings = new List<BackupListing>();
        foreach (var sidecarPath in Directory.EnumerateFiles(folder, ArchivePrefix + "*.json"))
        {
            try
            {
                var sidecar = JsonSerializer.Deserialize<BackupSidecar>(File.ReadAllText(sidecarPath), BackupArchive.Json);
                if (sidecar is null || sidecar.Format != BackupManifest.FormatName) continue;
                if (sidecar.Archive != Path.GetFileName(sidecar.Archive)) continue;
                var archivePath = Path.Combine(folder, sidecar.Archive);
                if (!File.Exists(archivePath)) continue;
                listings.Add(new BackupListing(sidecar, archivePath, sidecar.InstallationId == installationId));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(ex, "Skipped unreadable backup sidecar {Sidecar}.", sidecarPath);
            }
        }

        return listings.OrderByDescending(listing => listing.Sidecar.CreatedUtc).ToList();
    }

    /// <summary>Returns why <paramref name="folder"/> cannot hold backups, or null when it can.</summary>
    public string? ValidateFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder.Trim()))
            return "Enter a full folder path, such as D:\\TradeFoundry Backups.";

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim()));
        var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DataDirectory));
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(full, data, comparison) || full.StartsWith(data + Path.DirectorySeparatorChar, comparison))
            return "Choose a folder outside the data directory. A backup inside it would be lost with the data it protects.";

        try
        {
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, $".tradefoundry-write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, [1]);
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return $"TradeFoundry cannot write to that folder: {ex.Message}";
        }
    }

    /// <summary>True when the backup folder is on the same drive as the journal, so one disk failure loses both.</summary>
    public bool FolderSharesDriveWithData()
    {
        try
        {
            return string.Equals(Path.GetPathRoot(Path.GetFullPath(Folder)), Path.GetPathRoot(DataDirectory), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var interrupted = _database.FailInterruptedBackupRuns();
            if (interrupted > 0) _logger.LogWarning("{Count} backup run(s) were interrupted by the last shutdown.", interrupted);
            TryDeleteDirectory(Path.Combine(DataDirectory, StagingDirectoryName));
            var folder = Folder;
            if (Directory.Exists(folder))
            {
                foreach (var partial in Directory.EnumerateFiles(folder, ArchivePrefix + "*" + PartialSuffix))
                    TryDeleteFile(partial);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not clean up after an earlier backup.");
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Cancel();
        try
        {
            await WaitAsync().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
    }

    private async Task RunAsync(BackupRun run, CancellationToken cancellationToken)
    {
        var staging = Path.Combine(DataDirectory, StagingDirectoryName, run.Id.ToString("N"));
        var partialFiles = new List<string>();
        var warnings = new List<string>();
        long? maintenanceMilliseconds = null;
        try
        {
            var folder = Folder;
            if (ValidateFolder(folder) is { } folderProblem) throw new BackupFailedException(folderProblem);

            var databaseBytes = new FileInfo(_database.DatabasePath).Length;
            var fileBytes = DataDirectoryLayout.UserContentDirectories
                .Select(directory => Path.Combine(DataDirectory, directory))
                .Where(Directory.Exists)
                .Sum(directory => EnumerateDataFiles(directory).Sum(file => file.Length));
            EnsureFreeSpace(DataDirectory, (long)(databaseBytes * 1.3) + fileBytes, "the data drive");
            EnsureFreeSpace(folder, databaseBytes / 3 + fileBytes, "the backup folder's drive");

            Report("Waiting for imports to finish", null);
            if (!await _gate.WaitForLongWorkAsync(LongWorkTimeout, cancellationToken))
                throw new BackupFailedException("An import is still running. Try again when it has finished.");

            Directory.CreateDirectory(staging);
            var snapshotPath = Path.Combine(staging, "journal.db");
            var filesRoot = Path.Combine(staging, "files");

            Report("Pausing the site", null);
            var window = await _gate.TryEnterMaintenanceAsync(DrainTimeoutOverride ?? DrainTimeout, cancellationToken)
                ?? throw new BackupFailedException("TradeFoundry was busy saving or importing and could not pause. Try again in a minute.");
            var clock = Stopwatch.StartNew();
            using (window)
            {
                Report("Copying the database", 0);
                await Task.Run(() => SqliteSnapshot.Copy(_database.DatabasePath, snapshotPath, new StageProgress(this, "Copying the database"), cancellationToken), cancellationToken);
                Report("Copying images and receipts", null);
                await Task.Run(() => CopyDataFiles(filesRoot, cancellationToken), cancellationToken);
            }

            maintenanceMilliseconds = clock.ElapsedMilliseconds;
            _logger.LogInformation("Backup {RunId} paused the site for {Milliseconds} ms.", run.Id, maintenanceMilliseconds);

            Report("Checking the copy", null);
            var details = await Task.Run(() => SqliteSnapshot.Prepare(snapshotPath), cancellationToken);
            var copied = Directory.Exists(filesRoot)
                ? Directory.EnumerateFiles(filesRoot, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(filesRoot, path).Replace('\\', '/'))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            var missing = details.ReferencedFiles.Where(file => !copied.Contains(file)).ToList();
            if (missing.Count > 0)
            {
                warnings.Add(missing.Count == 1
                    ? $"1 image or receipt was already missing from the data directory: {missing[0]}"
                    : $"{missing.Count} images or receipts were already missing from the data directory, such as {missing[0]}");
            }

            var installationId = _database.GetOrCreateInstallationId();
            var name = $"{ArchivePrefix}{installationId.ToString("N")[..8]}-{run.StartedUtc.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}-{run.Id.ToString("N")[..8]}";
            var archiveName = name + BackupArchive.Extension;
            var archivePath = Path.Combine(folder, archiveName);
            var archivePartial = archivePath + PartialSuffix;
            partialFiles.Add(archivePartial);

            var manifest = new BackupManifest(
                BackupManifest.FormatName,
                BackupManifest.CurrentFormatVersion,
                run.Id,
                installationId,
                run.StartedUtc,
                run.Trigger,
                AppVersion,
                Platform(),
                details.SchemaVersion,
                Encrypted: false,
                BackupArchive.DatabaseFolder + Path.GetFileName(_database.DatabasePath),
                details.Counts,
                warnings,
                []);

            Report("Compressing", 0);
            manifest = await Task.Run(() => BackupArchive.Write(archivePartial, snapshotPath, Path.GetFileName(_database.DatabasePath), filesRoot, manifest, new StageProgress(this, "Compressing"), cancellationToken), cancellationToken);
            TryDeleteDirectory(staging);

            Report("Verifying", 0);
            var (_, sha256) = await Task.Run(() => BackupArchive.Verify(archivePartial, new StageProgress(this, "Verifying"), cancellationToken), cancellationToken);
            var size = new FileInfo(archivePartial).Length;

            Report("Finishing", null);
            File.Move(archivePartial, archivePath);
            partialFiles.Remove(archivePartial);
            partialFiles.Add(archivePath);

            var sidecar = new BackupSidecar(
                manifest.Format, manifest.FormatVersion, manifest.BackupId, manifest.InstallationId, manifest.CreatedUtc,
                manifest.Trigger, manifest.AppVersion, manifest.SchemaVersion, manifest.Encrypted, archiveName, size, sha256,
                manifest.Counts, manifest.Warnings);
            var sidecarPath = Path.Combine(folder, name + ".json");
            var sidecarPartial = sidecarPath + PartialSuffix;
            partialFiles.Add(sidecarPartial);
            await File.WriteAllTextAsync(sidecarPartial, JsonSerializer.Serialize(sidecar, BackupArchive.Json), CancellationToken.None);
            File.Move(sidecarPartial, sidecarPath);
            partialFiles.Clear();

            var status = warnings.Count > 0 ? BackupRunStatus.CompletedWithWarnings : BackupRunStatus.Completed;
            Finish(run with { Status = status, ArchiveName = archiveName, ArchiveSize = size, Message = $"Saved to {folder}." }, maintenanceMilliseconds, warnings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Finish(run with { Status = BackupRunStatus.Cancelled, Message = "The backup was cancelled." }, maintenanceMilliseconds, warnings);
        }
        catch (BackupFailedException ex)
        {
            Finish(run with { Status = BackupRunStatus.Failed, Message = ex.Message }, maintenanceMilliseconds, warnings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup {RunId} failed.", run.Id);
            var message = ex is IOException io && IsDiskFull(io) ? "The disk is full. Free some space and try again." : $"The backup failed: {ex.Message}";
            Finish(run with { Status = BackupRunStatus.Failed, Message = message }, maintenanceMilliseconds, warnings);
        }
        finally
        {
            foreach (var path in partialFiles) TryDeleteFile(path);
            TryDeleteDirectory(staging);
        }
    }

    private void Finish(BackupRun run, long? maintenanceMilliseconds, IReadOnlyList<string> warnings)
    {
        var finished = run with { FinishedUtc = DateTimeOffset.UtcNow, MaintenanceMilliseconds = maintenanceMilliseconds, Warnings = warnings };
        try
        {
            _database.UpdateBackupRun(finished);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record the result of backup {RunId}.", run.Id);
        }

        lock (_sync)
        {
            _progress = new BackupProgress(false, run.Id, string.Empty, null, false, null, finished.Status, finished.Message);
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private void CopyDataFiles(string filesRoot, CancellationToken cancellationToken)
    {
        foreach (var directory in DataDirectoryLayout.UserContentDirectories)
        {
            var source = Path.Combine(DataDirectory, directory);
            if (!Directory.Exists(source)) continue;
            foreach (var file in EnumerateDataFiles(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(filesRoot, directory, Path.GetRelativePath(source, file.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                file.CopyTo(target);
            }
        }
    }

    // Links are skipped: a backup holds what the stores wrote, never something a link points to.
    private static IEnumerable<FileInfo> EnumerateDataFiles(string directory) =>
        new DirectoryInfo(directory).EnumerateFiles("*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        });

    private static void EnsureFreeSpace(string path, long requiredBytes, string where)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return;
            var available = new DriveInfo(root).AvailableFreeSpace;
            if (available < requiredBytes)
                throw new BackupFailedException($"There is not enough free space on {where}: about {FormatBytes(requiredBytes)} is needed and {FormatBytes(available)} is free.");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Unknown free space is not a reason to refuse; a full disk is still caught when writing.
        }
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024d):0.#} MB",
        _ => $"{bytes / (1024d * 1024d * 1024d):0.##} GB"
    };

    private static bool IsDiskFull(IOException exception) =>
        (exception.HResult & 0xFFFF) is 0x70 or 0x27 || exception.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase);

    private static string Platform() =>
        $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture})" +
        (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ? " in a container" : string.Empty);

    private void Report(string stage, double? fraction)
    {
        lock (_sync)
        {
            if (_progress.Running) _progress = _progress with { Stage = stage, Fraction = fraction };
        }
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

    private sealed class StageProgress(BackupService service, string stage) : IProgress<double>
    {
        public void Report(double value) => service.Report(stage, value);
    }
}

/// <summary>A backup that stopped for a reason the owner can act on; its message is shown as is.</summary>
public sealed class BackupFailedException(string message) : Exception(message);

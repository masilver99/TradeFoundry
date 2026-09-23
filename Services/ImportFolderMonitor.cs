using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services;

public sealed class ImportFolderMonitor : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StableInterval = TimeSpan.FromSeconds(1);
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".csv", ".tsv", ".txt" };

    private readonly TradeFoundryDb _database;
    private readonly ImportService _imports;
    private readonly ILogger<ImportFolderMonitor> _logger;
    private readonly Channel<MonitorWork> _work = Channel.CreateUnbounded<MonitorWork>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Dictionary<Guid, WatchBinding> _watchers = new();

    public ImportFolderMonitor(TradeFoundryDb database, ImportService imports, ILogger<ImportFolderMonitor> logger)
    {
        _database = database;
        _imports = imports;
        _logger = logger;
    }

    public void NotifyConfigurationChanged() => _work.Writer.TryWrite(MonitorWork.Reconcile);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = QueuePeriodicScansAsync(stoppingToken);
        await _work.Writer.WriteAsync(MonitorWork.Reconcile, stoppingToken);

        try
        {
            await foreach (var work in _work.Reader.ReadAllAsync(stoppingToken))
            {
                if (work.ReconcileAll)
                    await ReconcileAndScanAsync(stoppingToken);
                else if (work.JournalId.HasValue && work.Path is not null)
                    await ProcessFileAsync(work.JournalId.Value, work.Path, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();
        }
    }

    private async Task QueuePeriodicScansAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ScanInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await _work.Writer.WriteAsync(MonitorWork.Reconcile, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task ReconcileAndScanAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Journal> journals;
        try
        {
            journals = _database.GetJournals();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load import watch folders.");
            return;
        }

        var configured = journals
            .Where(journal => !string.IsNullOrWhiteSpace(journal.ImportWatchDirectory))
            .ToDictionary(journal => journal.Id, journal => (Journal: journal, Path: NormalizeDirectory(journal.ImportWatchDirectory)));

        foreach (var existing in _watchers.ToArray())
        {
            if (!configured.TryGetValue(existing.Key, out var target) ||
                !PathEquals(existing.Value.DirectoryPath, target.Path) ||
                !Directory.Exists(target.Path))
            {
                existing.Value.Dispose();
                _watchers.Remove(existing.Key);
            }
        }

        foreach (var target in configured.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(target.Path))
            {
                _logger.LogWarning("Import watch directory {Directory} for journal {JournalId} does not exist.", target.Path, target.Journal.Id);
                continue;
            }

            if (!_watchers.ContainsKey(target.Journal.Id))
            {
                try
                {
                    var watcher = new FileSystemWatcher(target.Path)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        EnableRaisingEvents = false
                    };
                    FileSystemEventHandler changed = (_, args) => QueueFile(target.Journal.Id, args.FullPath);
                    RenamedEventHandler renamed = (_, args) => QueueFile(target.Journal.Id, args.FullPath);
                    ErrorEventHandler error = (_, args) =>
                    {
                        _logger.LogWarning(args.GetException(), "Import folder watcher overflowed for {Directory}; a full scan was queued.", target.Path);
                        NotifyConfigurationChanged();
                    };
                    watcher.Created += changed;
                    watcher.Changed += changed;
                    watcher.Renamed += renamed;
                    watcher.Error += error;
                    watcher.EnableRaisingEvents = true;
                    _watchers[target.Journal.Id] = new WatchBinding(target.Path, watcher);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    _logger.LogError(ex, "Could not watch import folder {Directory} for journal {JournalId}.", target.Path, target.Journal.Id);
                    continue;
                }
            }

            try
            {
                foreach (var filePath in Directory.EnumerateFiles(target.Path)
                    .Where(IsSupportedExtension)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    QueueFile(target.Journal.Id, filePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not scan import folder {Directory} for journal {JournalId}.", target.Path, target.Journal.Id);
            }
        }

        await Task.CompletedTask;
    }

    private void QueueFile(Guid journalId, string filePath)
    {
        if (IsSupportedExtension(filePath))
            _work.Writer.TryWrite(new MonitorWork(false, journalId, filePath));
    }

    private async Task ProcessFileAsync(Guid journalId, string filePath, CancellationToken cancellationToken)
    {
        var journal = _database.GetJournal(journalId);
        if (journal is null || string.IsNullOrWhiteSpace(journal.ImportWatchDirectory)) return;

        var fullPath = Path.GetFullPath(filePath);
        if (!IsDirectChild(journal.ImportWatchDirectory, fullPath) || !IsSupportedExtension(fullPath)) return;

        try
        {
            var bytes = await ReadStableFileAsync(fullPath, cancellationToken);
            if (bytes is null) return;

            var contentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var previous = _database.GetWatchedImportFileStatus(journalId, fullPath);
            if (previous is not null && previous.ContentHash.Equals(contentHash, StringComparison.Ordinal)) return;

            var request = new WatchedImportRequest { JournalId = journalId, FilePath = fullPath, ContentHash = contentHash };
            try
            {
                var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
                var result = _imports.ImportWatchedText(journalId, fullPath, text, contentHash);
                _logger.LogInformation("Automatically imported {NewRows} rows from {FilePath} into journal {JournalId} (batch {BatchId}).",
                    result.Batch.NewRows, fullPath, journalId, result.Batch.Id);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                _database.RecordWatchedImportFailure(request, ex.Message);
                _logger.LogWarning(ex, "Automatic import failed for {FilePath} in journal {JournalId}.", fullPath, journalId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Import file {FilePath} is not ready to read yet.", fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure while processing watched import file {FilePath}.", fullPath);
        }
    }

    private static async Task<byte[]?> ReadStableFileAsync(string path, CancellationToken cancellationToken)
    {
        long? previousLength = null;
        DateTime? previousWriteTimeUtc = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = new FileInfo(path);
            if (!before.Exists) return null;

            var length = before.Length;
            var writeTimeUtc = before.LastWriteTimeUtc;
            if (previousLength == length && previousWriteTimeUtc == writeTimeUtc)
            {
                try
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 64 * 1024, useAsync: true);
                    using var memory = new MemoryStream(length > 0 && length <= int.MaxValue ? (int)length : 0);
                    await stream.CopyToAsync(memory, cancellationToken);
                    var after = new FileInfo(path);
                    if (after.Exists && after.Length == length && after.LastWriteTimeUtc == writeTimeUtc && memory.Length == length)
                        return memory.ToArray();
                }
                catch (FileNotFoundException)
                {
                    return null;
                }
                catch (IOException)
                {
                    // A writer still has the file open or the file changed while it was read.
                }
            }

            previousLength = length;
            previousWriteTimeUtc = writeTimeUtc;
            await Task.Delay(StableInterval, cancellationToken);
        }
        return null;
    }

    private static bool IsSupportedExtension(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    private static string NormalizeDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(fullPath);
        return fullPath.Length > (root?.Length ?? 0) ? Path.TrimEndingDirectorySeparator(fullPath) : fullPath;
    }

    private static bool PathEquals(string left, string right) => string.Equals(
        NormalizeDirectory(left), NormalizeDirectory(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsDirectChild(string directory, string filePath) => PathEquals(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty, directory);

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _work.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private sealed record WatchBinding(string DirectoryPath, FileSystemWatcher Watcher) : IDisposable
    {
        public void Dispose() => Watcher.Dispose();
    }

    private sealed record MonitorWork(bool ReconcileAll, Guid? JournalId = null, string? Path = null)
    {
        public static MonitorWork Reconcile { get; } = new(true);
    }
}

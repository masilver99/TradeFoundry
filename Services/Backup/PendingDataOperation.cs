using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services.Backup;

/// <summary>A restore, start clean or put back, recorded on disk and applied at the next start.</summary>
public sealed record PendingOperation(
    string Kind,
    string Phase,
    DateTimeOffset CreatedUtc,
    string AsideName,
    string? StagingId = null,
    string? SetAsideName = null,
    StartCleanOwner? Owner = null)
{
    public const string Restore = "restore";
    public const string StartClean = "start-clean";
    public const string PutBack = "put-back";
    public const string MovingAside = "moving-aside";
    public const string MovingIn = "moving-in";
    public const string Creating = "creating";
}

/// <summary>
/// Applies a <see cref="PendingOperation"/> while the host is down and nothing has the database open.
/// </summary>
/// <remarks>
/// Entries are moved one by one, never the data directory itself, because in Docker it is a mount
/// point. Moves stay on the data volume, so they are renames that need no extra space. Each phase is
/// recorded in the marker before the next begins and every move is skipped when already done, so a
/// crash at any point resumes at the next start. If a phase fails outright, what was moved is put back.
/// </remarks>
public static class PendingDataOperation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The last failure, kept for the life of the process so the restore page can show it after the restart.</summary>
    public static string? LastError { get; private set; }

    public static string MarkerPath(string dataDirectory) => Path.Combine(dataDirectory, DataDirectoryLayout.PendingOperationFile);

    public static bool IsPending(string dataDirectory) => File.Exists(MarkerPath(dataDirectory));

    public static string NewAsideName(string suffix = "") =>
        DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture) + suffix;

    public static void Write(string dataDirectory, PendingOperation operation)
    {
        var path = MarkerPath(dataDirectory);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, operation, Json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Applies the pending operation, if any. Returns true when one was applied successfully.</summary>
    public static bool Apply(string dataDirectory, string databaseFileName, ILogger logger)
    {
        var markerPath = MarkerPath(dataDirectory);
        if (!File.Exists(markerPath)) return false;

        PendingOperation operation;
        try
        {
            operation = JsonSerializer.Deserialize<PendingOperation>(File.ReadAllText(markerPath), Json)
                ?? throw new InvalidDataException("The pending operation marker is empty.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            logger.LogError(ex, "Ignored an unreadable pending data operation; nothing was moved.");
            File.Move(markerPath, markerPath + ".unreadable", overwrite: true);
            return false;
        }

        SqliteConnection.ClearAllPools();
        var layout = new Layout(dataDirectory, databaseFileName, operation);
        logger.LogWarning("Applying pending {Kind} (phase {Phase}).", operation.Kind, operation.Phase);
        try
        {
            switch (operation.Kind)
            {
                case PendingOperation.Restore:
                    ApplyRestore(layout, operation);
                    break;
                case PendingOperation.StartClean:
                    ApplyStartClean(layout, operation);
                    break;
                case PendingOperation.PutBack:
                    ApplyPutBack(layout, operation);
                    break;
                default:
                    throw new InvalidDataException($"Unknown pending operation {operation.Kind}.");
            }

            File.Delete(markerPath);
            TryDeleteEmptyDirectory(layout.Aside);
            LastError = null;
            logger.LogWarning("Finished {Kind}.", operation.Kind);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The pending {Kind} failed; putting the previous data back.", operation.Kind);
            LastError = $"The {Describe(operation.Kind)} could not be finished, so the previous data was put back: {ex.Message}";
            try
            {
                RollBack(layout, operation);
                File.Delete(markerPath);
            }
            catch (Exception rollbackError)
            {
                // Leave the marker so the next start tries again rather than opening half-moved data.
                logger.LogCritical(rollbackError, "Could not put the previous data back. The marker was kept at {Marker}.", markerPath);
                throw;
            }

            return false;
        }
    }

    private static void ApplyRestore(Layout layout, PendingOperation operation)
    {
        var staging = layout.Staging ?? throw new InvalidDataException("The restore has no staged backup.");
        if (operation.Phase == PendingOperation.MovingAside)
        {
            if (!File.Exists(Path.Combine(staging, BackupRestoreReader.StagedDatabaseName)) && !File.Exists(layout.Database))
                throw new InvalidDataException("The staged backup is missing.");
            foreach (var entry in layout.RestoreEntries) Move(layout.Live(entry), layout.AsideEntry(entry));
            operation = Advance(layout, operation, PendingOperation.MovingIn);
        }

        foreach (var (from, to) in layout.StagedMoves(staging)) Move(from, to);
        if (!File.Exists(layout.Database)) throw new InvalidDataException("The restored database is not in place.");
        TryDeleteDirectory(staging);
    }

    private static void ApplyStartClean(Layout layout, PendingOperation operation)
    {
        var owner = operation.Owner ?? throw new InvalidDataException("Start clean has no owner account to keep.");
        if (operation.Phase == PendingOperation.MovingAside)
        {
            foreach (var entry in layout.DataEntries) Move(layout.Live(entry), layout.AsideEntry(entry));
            operation = Advance(layout, operation, PendingOperation.Creating);
        }

        // A crash while creating leaves a partial fresh database; it holds nothing, so start again.
        foreach (var entry in layout.DatabaseEntries) TryDeleteFile(layout.Live(entry));
        var database = new TradeFoundryDb(Options.Create(new StorageOptions { DataDirectory = layout.DataDirectory, DatabaseFileName = layout.DatabaseFileName }));
        database.SeedFreshDatabase(owner);
        SqliteConnection.ClearAllPools();
    }

    private static void ApplyPutBack(Layout layout, PendingOperation operation)
    {
        var source = layout.SetAsideSource ?? throw new InvalidDataException("Put back has no set-aside copy.");
        if (operation.Phase == PendingOperation.MovingAside)
        {
            if (!File.Exists(Path.Combine(source, layout.DatabaseFileName)))
                throw new InvalidDataException("The set-aside copy has no database.");
            foreach (var entry in layout.DataEntries) Move(layout.Live(entry), layout.AsideEntry(entry));
            operation = Advance(layout, operation, PendingOperation.MovingIn);
        }

        foreach (var entry in layout.DataEntries) Move(Path.Combine(source, entry), layout.Live(entry));
        TryDeleteEmptyDirectory(source);
    }

    /// <summary>Moves whatever was moved in back to where it came from, then moves the set-aside data back.</summary>
    private static void RollBack(Layout layout, PendingOperation operation)
    {
        SqliteConnection.ClearAllPools();
        switch (operation.Kind)
        {
            case PendingOperation.Restore when layout.Staging is { } staging:
                foreach (var (from, to) in layout.StagedMoves(staging)) MoveIfFree(to, from);
                foreach (var entry in layout.RestoreEntries) MoveIfFree(layout.AsideEntry(entry), layout.Live(entry));
                break;
            case PendingOperation.StartClean:
                foreach (var entry in layout.DatabaseEntries) TryDeleteFile(layout.Live(entry));
                foreach (var entry in layout.DataEntries) MoveIfFree(layout.AsideEntry(entry), layout.Live(entry));
                break;
            case PendingOperation.PutBack when layout.SetAsideSource is { } source:
                foreach (var entry in layout.DataEntries) MoveIfFree(layout.Live(entry), Path.Combine(source, entry));
                foreach (var entry in layout.DataEntries) MoveIfFree(layout.AsideEntry(entry), layout.Live(entry));
                break;
        }

        TryDeleteEmptyDirectory(layout.Aside);
    }

    private static PendingOperation Advance(Layout layout, PendingOperation operation, string phase)
    {
        var next = operation with { Phase = phase };
        Write(layout.DataDirectory, next);
        return next;
    }

    /// <summary>Moves a file or directory unless that already happened. Both existing means something else wrote there.</summary>
    private static void Move(string from, string to)
    {
        var fromExists = File.Exists(from) || Directory.Exists(from);
        if (!fromExists) return;
        if (File.Exists(to) || Directory.Exists(to))
            throw new IOException($"Cannot move {from} to {to}: something is already there.");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    /// <summary>Rollback moves skip anything whose destination is taken, so they work from any phase.</summary>
    private static void MoveIfFree(string from, string to)
    {
        if (File.Exists(to) || Directory.Exists(to)) return;
        Move(from, to);
    }

    private static string Describe(string kind) => kind switch
    {
        PendingOperation.Restore => "restore",
        PendingOperation.StartClean => "start clean",
        PendingOperation.PutBack => "put back",
        _ => kind
    };

    private static void TryDeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (path is not null && Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string? path)
    {
        try
        {
            if (path is not null && Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class Layout(string dataDirectory, string databaseFileName, PendingOperation operation)
    {
        public string DataDirectory { get; } = Path.GetFullPath(dataDirectory);
        public string DatabaseFileName { get; } = databaseFileName;
        public string Database => Live(DatabaseFileName);
        public string Aside => Path.Combine(DataDirectory, DataDirectoryLayout.SetAside, operation.AsideName);

        public string? Staging => operation.StagingId is { } id
            ? Path.Combine(DataDirectory, DataDirectoryLayout.RestoreStaging, SafeName(id))
            : null;

        public string? SetAsideSource => operation.SetAsideName is { } name
            ? Path.Combine(DataDirectory, DataDirectoryLayout.SetAside, SafeName(name))
            : null;

        public IReadOnlyList<string> DatabaseEntries =>
            [DatabaseFileName, DatabaseFileName + "-wal", DatabaseFileName + "-shm", DatabaseFileName + "-journal"];

        /// <summary>Everything that makes up a journal: the database and the allowlisted directories.</summary>
        public IReadOnlyList<string> DataEntries => [.. DatabaseEntries, .. DataDirectoryLayout.UserContentDirectories];

        /// <summary>A restore also replaces migration safety copies, which belong to the restored database.</summary>
        public IReadOnlyList<string> RestoreEntries => [.. DataEntries, DataDirectoryLayout.MigrationBackups];

        public string Live(string entry) => Path.Combine(DataDirectory, entry);

        public string AsideEntry(string entry) => Path.Combine(Aside, entry);

        public IEnumerable<(string From, string To)> StagedMoves(string staging)
        {
            yield return (Path.Combine(staging, BackupRestoreReader.StagedDatabaseName), Database);
            foreach (var directory in DataDirectoryLayout.UserContentDirectories)
                yield return (Path.Combine(staging, BackupRestoreReader.StagedFilesFolder, directory), Live(directory));
            yield return (Path.Combine(staging, DataDirectoryLayout.MigrationBackups), Live(DataDirectoryLayout.MigrationBackups));
        }

        private static string SafeName(string name) =>
            name.Length > 0 && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or 'T' or 'Z')
                ? name
                : throw new InvalidDataException("The pending operation names an invalid folder.");
    }
}

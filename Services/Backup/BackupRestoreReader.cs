using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services.Backup;

/// <summary>
/// Unpacks a backup into staging, trusting nothing in it: every path is checked against the allowlist,
/// every entry must match the manifest's size and hash, and the database is checked before the app
/// ever runs its own code against it.
/// </summary>
public static partial class BackupRestoreReader
{
    private const int MaxEntries = 500_000;
    private const int MaxDepth = 4;
    private const long MaxManifestBytes = 64L * 1024 * 1024;
    public const string StagedDatabaseName = "journal.db";
    public const string StagedFilesFolder = "files";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex SafeSegment();

    /// <summary>Reads only the manifest, for a quick look before unpacking.</summary>
    public static BackupManifest ReadManifest(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        return ReadManifest(zip);
    }

    /// <summary>
    /// Validates <paramref name="archivePath"/> and unpacks it into <paramref name="stagingDirectory"/>
    /// as <c>journal.db</c> and <c>files/&lt;directory&gt;/…</c>. Throws <see cref="InvalidDataException"/>
    /// with a message for the owner when anything is wrong; the caller removes the staging directory.
    /// </summary>
    public static BackupManifest Extract(string archivePath, string stagingDirectory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var manifest = ReadManifest(zip);
        if (zip.Entries.Count > MaxEntries) throw new InvalidDataException("The backup contains too many files.");

        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        var seenIgnoringCase = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var databaseEntries = 0;
        foreach (var entry in manifest.Entries)
        {
            if (entry.Size < 0 || entry.Sha256 is not { Length: 64 } || !entry.Sha256.All(char.IsAsciiHexDigitLower))
                throw new InvalidDataException($"The manifest entry for {entry.Path} is malformed.");
            if (!seenIgnoringCase.Add(entry.Path))
                throw new InvalidDataException($"The backup lists {entry.Path} more than once, ignoring case.");
            targets[entry.Path] = TargetFor(entry.Path, manifest, stagingDirectory, ref databaseEntries);
        }

        if (databaseEntries != 1) throw new InvalidDataException("The backup does not contain exactly one database.");

        var zipNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            if (!zipNames.Add(entry.FullName)) throw new InvalidDataException($"The backup contains {entry.FullName} more than once.");
            if (entry.FullName == BackupArchive.ManifestEntry) continue;
            if (!targets.ContainsKey(entry.FullName)) throw new InvalidDataException($"The backup contains {entry.FullName}, which its manifest does not list.");
            // Unix file type bits: a symbolic link is never restored.
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException($"The backup stores {entry.FullName} as a link.");
        }

        var totalBytes = Math.Max(1, manifest.Entries.Sum(entry => entry.Size));
        EnsureFreeSpace(stagingDirectory, (long)(totalBytes * 1.1));
        Directory.CreateDirectory(stagingDirectory);
        var buffer = new byte[1024 * 1024];
        long doneBytes = 0;
        foreach (var item in manifest.Entries)
        {
            var entry = zip.GetEntry(item.Path) ?? throw new InvalidDataException($"The backup is missing {item.Path}.");
            if (entry.Length != item.Size) throw new InvalidDataException($"{item.Path} in the backup does not match its manifest.");
            var target = targets[item.Path];
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            using (var input = entry.Open())
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    length += read;
                    if (length > item.Size) throw new InvalidDataException($"{item.Path} in the backup is larger than its manifest says.");
                    hash.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                    doneBytes += read;
                    progress?.Report((double)doneBytes / totalBytes);
                }
            }

            if (length != item.Size || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), item.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"{item.Path} in the backup is damaged: its contents do not match the manifest.");
        }

        return manifest;
    }

    /// <summary>
    /// Checks the staged database before any app code runs against it. Opens it with
    /// <c>trusted_schema = OFF</c>, refuses triggers and views (TradeFoundry defines none), and runs
    /// <c>quick_check</c>. Returns warnings that do not block a restore.
    /// </summary>
    public static IReadOnlyList<string> CheckDatabase(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA trusted_schema = OFF");

        using (var objects = connection.CreateCommand())
        {
            objects.CommandText = "SELECT type, name FROM sqlite_master WHERE type IN ('trigger', 'view') LIMIT 1";
            using var reader = objects.ExecuteReader();
            if (reader.Read())
                throw new InvalidDataException($"The backup's database contains a {reader.GetString(0)} ({reader.GetString(1)}), which TradeFoundry never creates. It was not restored.");
        }

        var check = Convert.ToString(Scalar(connection, "PRAGMA quick_check(1)"));
        if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The backup's database is damaged: {check}");

        if (Scalar(connection, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'journals'") is null ||
            Scalar(connection, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'app_users'") is null ||
            Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM app_users")) == 0)
        {
            throw new InvalidDataException("The backup's database has no TradeFoundry journal or owner account.");
        }

        var warnings = new List<string>();
        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check";
            using var reader = foreignKeys.ExecuteReader();
            var count = 0;
            while (reader.Read() && count < 1000) count++;
            if (count > 0) warnings.Add($"The backup's database has {(count >= 1000 ? "1,000 or more" : count.ToString("N0"))} rows that refer to missing rows. They were restored as they were.");
        }

        return warnings;
    }

    private static BackupManifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(BackupArchive.ManifestEntry) ?? throw new InvalidDataException("This file is not a TradeFoundry backup: it has no manifest.");
        if (entry.Length > MaxManifestBytes) throw new InvalidDataException("The backup manifest is too large.");
        BackupManifest? manifest;
        try
        {
            using var stream = entry.Open();
            manifest = JsonSerializer.Deserialize<BackupManifest>(stream, BackupArchive.Json);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The backup manifest cannot be read.");
        }

        if (manifest is null || manifest.Format != BackupManifest.FormatName || manifest.Entries is null || manifest.Counts is null || manifest.Warnings is null)
            throw new InvalidDataException("This file is not a TradeFoundry backup.");
        if (manifest.FormatVersion > BackupManifest.CurrentFormatVersion || manifest.SchemaVersion > TradeFoundryDb.SchemaVersion)
            throw new InvalidDataException($"This backup was made by a newer version of TradeFoundry ({manifest.AppVersion}). Install that version or newer to restore it.");
        if (manifest.Encrypted)
            throw new InvalidDataException("This backup is encrypted, which this version of TradeFoundry cannot read yet.");
        return manifest;
    }

    private static string TargetFor(string path, BackupManifest manifest, string stagingDirectory, ref int databaseEntries)
    {
        var segments = path.Split('/');
        if (segments.Any(segment => !SafeSegment().IsMatch(segment)))
            throw new InvalidDataException($"The backup contains an unsafe file name: {path}");

        if (segments is ["database", var databaseFile] && path == manifest.DatabaseEntry)
        {
            databaseEntries++;
            _ = databaseFile;
            return Path.Combine(stagingDirectory, StagedDatabaseName);
        }

        if (segments.Length < 3 || segments.Length > MaxDepth + 2 || segments[0] != "files" || !DataDirectoryLayout.UserContentDirectories.Contains(segments[1]))
            throw new InvalidDataException($"The backup contains a file TradeFoundry does not restore: {path}");

        var root = Path.GetFullPath(Path.Combine(stagingDirectory, StagedFilesFolder)) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine([root, .. segments[1..]]));
        if (!target.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException($"The backup contains an unsafe file name: {path}");
        return target;
    }

    private static void EnsureFreeSpace(string path, long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return;
            var available = new DriveInfo(root).AvailableFreeSpace;
            if (available < requiredBytes)
                throw new InvalidDataException($"There is not enough free space to restore this backup: about {BackupService.FormatBytes(requiredBytes)} is needed and {BackupService.FormatBytes(available)} is free.");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

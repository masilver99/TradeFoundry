using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TradeFoundry.Core;

namespace TradeFoundry.Services.Backup;

/// <summary>
/// Reads and writes <c>.tfbackup</c> archives: ZIP64 files holding the database copy, the data files
/// and a manifest with the size and SHA-256 of every entry.
/// </summary>
public static class BackupArchive
{
    public const string Extension = ".tfbackup";
    public const string ManifestEntry = "manifest.json";
    public const string DatabaseFolder = "database/";
    public const string FilesFolder = "files/";
    /// <summary>Publishing tokens, unprotected. Only ever written into encrypted backups.</summary>
    public const string SecretsEntry = "secrets.json";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly HashSet<string> StoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".webp", ".png", ".jpg", ".jpeg", ".gif", ".pdf", ".zip"
    };

    /// <summary>
    /// Writes an archive from a staged snapshot and staged files, then returns the manifest it wrote.
    /// <paramref name="filesRoot"/> holds one folder per allowlisted data directory.
    /// </summary>
    public static BackupManifest Write(
        string archivePath,
        string snapshotPath,
        string databaseFileName,
        string filesRoot,
        BackupManifest manifest,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        string? secretsPath = null)
    {
        var sources = new List<(string EntryName, string Path, long Length)>
        {
            (DatabaseFolder + databaseFileName, snapshotPath, new FileInfo(snapshotPath).Length)
        };
        if (secretsPath is not null) sources.Add((SecretsEntry, secretsPath, new FileInfo(secretsPath).Length));
        if (Directory.Exists(filesRoot))
        {
            foreach (var path in Directory.EnumerateFiles(filesRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(filesRoot, path).Replace('\\', '/');
                sources.Add((FilesFolder + relative, path, new FileInfo(path).Length));
            }
        }

        var totalBytes = Math.Max(1, sources.Sum(source => source.Length));
        long doneBytes = 0;
        var entries = new List<BackupManifestEntry>(sources.Count);
        var buffer = new byte[1024 * 1024];

        using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024))
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var source in sources)
                {
                    var level = StoredExtensions.Contains(Path.GetExtension(source.Path)) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                    var entry = zip.CreateEntry(source.EntryName, level);
                    entry.LastWriteTime = File.GetLastWriteTimeUtc(source.Path);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long length = 0;
                    using (var input = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                    using (var target = entry.Open())
                    {
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            hash.AppendData(buffer, 0, read);
                            target.Write(buffer, 0, read);
                            length += read;
                            doneBytes += read;
                            progress?.Report((double)doneBytes / totalBytes);
                        }
                    }

                    entries.Add(new BackupManifestEntry(source.EntryName, length, Convert.ToHexStringLower(hash.GetHashAndReset())));
                }

                manifest = manifest with { Entries = entries };
                var manifestEntry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                using var manifestStream = manifestEntry.Open();
                JsonSerializer.Serialize(manifestStream, manifest, Json);
            }

            output.Flush(flushToDisk: true);
        }

        return manifest;
    }

    /// <summary>
    /// Reads an archive back in full: every manifest entry must be present with its recorded size and
    /// hash, and nothing else may be present. Returns the manifest and the SHA-256 of the archive file.
    /// </summary>
    public static (BackupManifest Manifest, string Sha256) Verify(string archivePath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string archiveHash;
        using (var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
        {
            archiveHash = Convert.ToHexStringLower(SHA256.HashData(file));
        }

        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        return (Verify(stream, progress, cancellationToken), archiveHash);
    }

    /// <summary>Checks every entry of an archive read from a seekable stream, such as a decrypted backup.</summary>
    public static BackupManifest Verify(Stream archive, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        var manifestEntry = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException("The backup has no manifest.");
        BackupManifest manifest;
        using (var stream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<BackupManifest>(stream, Json) ?? throw new InvalidDataException("The backup manifest is empty.");
        }

        if (manifest.Format != BackupManifest.FormatName)
            throw new InvalidDataException("This file is not a TradeFoundry backup.");

        var expected = manifest.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var extra = zip.Entries.Select(entry => entry.FullName).Where(name => name != ManifestEntry && !expected.ContainsKey(name)).ToList();
        if (extra.Count > 0) throw new InvalidDataException($"The backup contains files its manifest does not list, such as {extra[0]}.");

        var totalBytes = Math.Max(1, manifest.Entries.Sum(entry => entry.Size));
        long doneBytes = 0;
        foreach (var item in manifest.Entries)
        {
            var entry = zip.GetEntry(item.Path) ?? throw new InvalidDataException($"The backup is missing {item.Path}.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            using (var stream = entry.Open())
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, read);
                    length += read;
                    doneBytes += read;
                    if (length > item.Size) break;
                    progress?.Report((double)doneBytes / totalBytes);
                }
            }

            if (length != item.Size || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), item.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"{item.Path} in the backup does not match its manifest.");
        }

        return manifest;
    }
}

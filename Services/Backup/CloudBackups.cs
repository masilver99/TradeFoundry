using System.Text;
using System.Text.Json;
using TradeFoundry.Core;

namespace TradeFoundry.Services.Backup;

/// <summary>
/// How backups are laid out in a bucket: <c>tradefoundry/&lt;installation&gt;/&lt;archive&gt;</c> with its
/// sidecar beside it, uploaded last. Names are never reused, and pruning only hides, so the bucket's
/// lifecycle rule decides when a pruned backup is finally erased.
/// </summary>
public static class CloudBackups
{
    public const string Root = "tradefoundry/";
    private const long MaxSidecarBytes = 1024 * 1024;

    public static string InstallationPrefix(Guid installationId) => $"{Root}{installationId:N}/";

    public static string ArchiveKey(Guid installationId, string archiveName) => InstallationPrefix(installationId) + archiveName;

    public static string SidecarKey(string archiveKey) => archiveKey[..^BackupArchive.Extension.Length] + ".json";

    /// <summary>Uploads an archive, then its sidecar. Unfinished uploads from earlier crashes are cancelled first.</summary>
    public static async Task<string> UploadAsync(IBackupCloud cloud, Guid installationId, string archivePath, BackupSidecar sidecar, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var archiveKey = ArchiveKey(installationId, sidecar.Archive);
        await cloud.AbortUnfinishedAsync(InstallationPrefix(installationId), cancellationToken);
        await cloud.PutFileAsync(archiveKey, archivePath, progress, cancellationToken);
        await cloud.PutBytesAsync(SidecarKey(archiveKey), JsonSerializer.SerializeToUtf8Bytes(sidecar, BackupArchive.Json), cancellationToken);
        return archiveKey;
    }

    /// <summary>
    /// Backups in the bucket, listed from their sidecars, newest first. With <paramref name="includeHidden"/>,
    /// pruned or deleted backups still in the recovery window are listed too, marked hidden.
    /// </summary>
    public static async Task<IReadOnlyList<BackupListing>> ListAsync(IBackupCloud cloud, Guid? thisInstallation, bool includeHidden, CancellationToken cancellationToken)
    {
        var objects = await cloud.ListAsync(Root, includeHidden, cancellationToken);
        var archives = objects.Where(item => item.Key.EndsWith(BackupArchive.Extension, StringComparison.Ordinal)).ToDictionary(item => item.Key, StringComparer.Ordinal);
        var listings = new List<BackupListing>();
        foreach (var sidecarObject in objects.Where(item => item.Key.EndsWith(".json", StringComparison.Ordinal)))
        {
            var archiveKey = sidecarObject.Key[..^".json".Length] + BackupArchive.Extension;
            if (!archives.TryGetValue(archiveKey, out var archive)) continue;
            BackupSidecar? sidecar;
            try
            {
                var bytes = await cloud.ReadBytesAsync(sidecarObject.Key, sidecarObject.VersionId, MaxSidecarBytes, cancellationToken);
                sidecar = JsonSerializer.Deserialize<BackupSidecar>(bytes, BackupArchive.Json);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                continue;
            }

            if (sidecar is null || sidecar.Format != BackupManifest.FormatName || !archiveKey.EndsWith("/" + sidecar.Archive, StringComparison.Ordinal)) continue;
            // The installation is the folder the object sits in, not what the sidecar claims.
            var fromThis = thisInstallation is { } id && archiveKey.StartsWith(InstallationPrefix(id), StringComparison.Ordinal);
            listings.Add(new BackupListing(sidecar, archiveKey, fromThis, archive.VersionId, archive.Hidden || sidecarObject.Hidden));
        }

        return listings.OrderByDescending(listing => listing.Sidecar.CreatedUtc).ToList();
    }

    /// <summary>Hides backups chosen by retention, sidecar first, so an interrupted prune never lists a backup without its archive.</summary>
    public static async Task<IReadOnlyList<string>> PruneAsync(IBackupCloud cloud, IReadOnlyList<BackupListing> listings, CancellationToken cancellationToken)
    {
        var pruned = new List<string>();
        foreach (var listing in listings.Where(listing => !listing.Hidden))
        {
            await cloud.HideAsync(SidecarKey(listing.ArchivePath), cancellationToken);
            await cloud.HideAsync(listing.ArchivePath, cancellationToken);
            pruned.Add(listing.ArchivePath);
        }

        return pruned;
    }

    /// <summary>
    /// Proves the key can do what backups need and cannot do what a thief would want: puts, lists, reads
    /// and hides a probe, then tries to erase it for good. Being refused is the expected result.
    /// </summary>
    public static async Task<CloudTestResult> TestAsync(IBackupCloud cloud, Guid installationId, CancellationToken cancellationToken)
    {
        var key = $"{InstallationPrefix(installationId)}connection-test-{Guid.NewGuid():N}.txt";
        var content = Encoding.UTF8.GetBytes($"TradeFoundry connection test {DateTimeOffset.UtcNow:O}");
        await cloud.PutBytesAsync(key, content, cancellationToken);
        var listed = await cloud.ListAsync(InstallationPrefix(installationId), includeHidden: false, cancellationToken);
        if (listed.All(item => item.Key != key)) return new CloudTestResult(false, false, "The test file was uploaded but did not appear in the bucket listing.");
        var read = await cloud.ReadBytesAsync(key, null, 4096, cancellationToken);
        if (!read.AsSpan().SequenceEqual(content)) return new CloudTestResult(false, false, "The test file read back differently from what was uploaded.");
        await cloud.HideAsync(key, cancellationToken);
        var versions = await cloud.ListAsync(InstallationPrefix(installationId), includeHidden: true, cancellationToken);
        var hidden = versions.FirstOrDefault(item => item.Key == key);
        if (hidden is not { Hidden: true, VersionId: { } versionId })
            return new CloudTestResult(false, false, "The test file could not be hidden, so old backups could not be pruned.");
        var erased = await cloud.TryDeleteVersionAsync(key, versionId, cancellationToken);
        return erased
            ? new CloudTestResult(true, true, "The connection works, but this key can permanently delete files. If this computer were compromised, every backup could be erased at once. Create a key without deleteFiles, as described on this page.")
            : new CloudTestResult(true, false, "The connection works. Uploading, listing, reading and pruning succeeded, and the key cannot permanently delete backups.");
    }
}

public sealed record CloudTestResult(bool Works, bool CanErase, string Message);

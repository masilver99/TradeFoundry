namespace TradeFoundry.Core;

public static class BackupRunStatus
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string CompletedWithWarnings = "completed_with_warnings";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Interrupted = "interrupted";
}

public static class BackupTrigger
{
    public const string Manual = "manual";
    public const string Scheduled = "scheduled";
}

public sealed record BackupRun(
    Guid Id,
    string Trigger,
    string Status,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    long? MaintenanceMilliseconds,
    string ArchiveName,
    long? ArchiveSize,
    string Message,
    IReadOnlyList<string> Warnings);

/// <summary>One file inside a backup archive, with the size and hash recorded at backup time.</summary>
public sealed record BackupManifestEntry(string Path, long Size, string Sha256);

/// <summary>
/// Written into every archive as <c>manifest.json</c>. Restore trusts nothing in an archive that the
/// manifest does not list with a matching size and hash.
/// </summary>
public sealed record BackupManifest(
    string Format,
    int FormatVersion,
    Guid BackupId,
    Guid InstallationId,
    DateTimeOffset CreatedUtc,
    string Trigger,
    string AppVersion,
    string Platform,
    int SchemaVersion,
    bool Encrypted,
    string DatabaseEntry,
    IReadOnlyDictionary<string, long> Counts,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<BackupManifestEntry> Entries)
{
    public const string FormatName = "tradefoundry-backup";
    public const int CurrentFormatVersion = 1;
}

/// <summary>
/// Written beside each archive as <c>&lt;archive&gt;.json</c>, last, after the archive has been read back
/// and verified. Backups are listed from sidecars; an archive without one is incomplete.
/// </summary>
public sealed record BackupSidecar(
    string Format,
    int FormatVersion,
    Guid BackupId,
    Guid InstallationId,
    DateTimeOffset CreatedUtc,
    string Trigger,
    string AppVersion,
    int SchemaVersion,
    bool Encrypted,
    string Archive,
    long Size,
    string Sha256,
    IReadOnlyDictionary<string, long> Counts,
    IReadOnlyList<string> Warnings);

/// <summary>Where a restored installation came from, shown on the post-restore checklist.</summary>
public sealed record RestoredFrom(Guid BackupId, DateTimeOffset BackupCreatedUtc, string AppVersion, DateTimeOffset RestoredUtc);

/// <summary>The owner account and settings carried from a database into the fresh one "start clean" creates.</summary>
public sealed record StartCleanOwner(string Id, string DisplayName, string PasswordHash, string CreatedUtc, IReadOnlyDictionary<string, string> Settings);

/// <summary>A backup found in a destination, as listed from its sidecar.</summary>
public sealed record BackupListing(BackupSidecar Sidecar, string ArchivePath, bool FromThisInstallation);

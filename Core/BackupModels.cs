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
    IReadOnlyList<string> Warnings,
    string CloudStatus = "",
    string CloudMessage = "");

/// <summary>Where a run's archive stands in Backblaze B2. Empty when cloud backup was off for that run.</summary>
public static class BackupCloudStatus
{
    public const string None = "";
    public const string Uploading = "uploading";
    public const string Uploaded = "uploaded";
    public const string Failed = "failed";
}

/// <summary>
/// A Backblaze B2 bucket reached through its S3-compatible API. The application key is held only in
/// memory; what is stored is <see cref="BackupCloudSettings"/>.
/// </summary>
public sealed record B2Connection(string Endpoint, string Bucket, string KeyId, string ApplicationKey);

/// <summary>Stored cloud destination. The application key is protected with Data Protection.</summary>
public sealed record BackupCloudSettings(string Endpoint, string Bucket, string KeyId, string ProtectedApplicationKey);

/// <summary>Publishing tokens carried in an encrypted backup as <c>secrets.json</c>, unprotected.</summary>
public sealed record BackupSecretsFile(IReadOnlyList<BackupPublishingToken> PublishingTokens);

public sealed record BackupPublishingToken(Guid JournalId, string Token);

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

public static class BackupFrequency
{
    public const string Daily = "daily";
    public const string Weekly = "weekly";
}

/// <summary>
/// When scheduled backups run and how many are kept. Retention keeps the newest backup of each of the
/// last <see cref="KeepDaily"/> days, <see cref="KeepWeekly"/> ISO weeks and <see cref="KeepMonthly"/>
/// months, counted in <see cref="TimeZoneId"/>.
/// </summary>
public sealed record BackupSchedule(
    bool Enabled,
    string Frequency,
    TimeOnly Time,
    DayOfWeek Day,
    string TimeZoneId,
    int KeepDaily,
    int KeepWeekly,
    int KeepMonthly)
{
    public static BackupSchedule Default { get; } = new(true, BackupFrequency.Daily, new TimeOnly(2, 0), DayOfWeek.Sunday, LocalTimeZoneId(), 7, 4, 3);

    public TimeSpan Interval => Frequency == BackupFrequency.Weekly ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1);

    /// <summary>The zone the schedule uses, falling back to UTC when the stored id is unknown here.</summary>
    public TimeZoneInfo ResolveTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>The machine's zone as an IANA id where possible, so it reads the same in Docker and on Windows.</summary>
    public static string LocalTimeZoneId()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId) return local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : local.Id;
    }
}

/// <summary>Where a restored installation came from, shown on the post-restore checklist.</summary>
public sealed record RestoredFrom(Guid BackupId, DateTimeOffset BackupCreatedUtc, string AppVersion, DateTimeOffset RestoredUtc);

/// <summary>The owner account and settings carried from a database into the fresh one "start clean" creates.</summary>
public sealed record StartCleanOwner(string Id, string DisplayName, string PasswordHash, string CreatedUtc, IReadOnlyDictionary<string, string> Settings);

/// <summary>
/// A backup found in a destination, as listed from its sidecar. For a folder, <see cref="ArchivePath"/>
/// is a file path; for B2 it is the object key, with <see cref="VersionId"/> set and <see cref="Hidden"/>
/// true when it was pruned or deleted but is still within the bucket's recovery window.
/// </summary>
public sealed record BackupListing(BackupSidecar Sidecar, string ArchivePath, bool FromThisInstallation, string? VersionId = null, bool Hidden = false);

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;
using TradeFoundry.Services.Backup;

namespace TradeFoundry.Pages.Settings;

[Authorize]
public class BackupsModel : PageModel
{
    private const int HistoryLength = 15;
    private readonly TradeFoundryDb _database;
    private readonly BackupService _backups;
    private readonly RestoreService _restore;
    private readonly ImportFolderMonitor _monitor;

    private readonly BackupScheduler _scheduler;
    private readonly BackupSecrets _secrets;

    public BackupsModel(TradeFoundryDb database, BackupService backups, RestoreService restore, ImportFolderMonitor monitor, BackupScheduler scheduler, BackupSecrets secrets)
    {
        _database = database;
        _backups = backups;
        _restore = restore;
        _monitor = monitor;
        _scheduler = scheduler;
        _secrets = secrets;
    }

    public bool EncryptionEnabled { get; private set; }
    public BackupCloudSettings? Cloud { get; private set; }
    public CloudListingState? CloudListing { get; private set; }
    public bool OwnerHasPassword { get; private set; }

    public IActionResult OnPostEncryption(string? passphrase, string? confirmPassphrase, bool savedElsewhere)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        if (!PasswordMatches()) return Fail("The password is incorrect. Encryption was not changed.");
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < BackupEncryption.MinimumPassphraseLength)
            return Fail($"Use a passphrase of at least {BackupEncryption.MinimumPassphraseLength} characters. A few unrelated words work well.");
        if (passphrase != confirmPassphrase) return Fail("The two passphrases do not match. Encryption was not changed.");
        if (!savedElsewhere) return Fail("Confirm that the passphrase is saved somewhere other than this computer. Without it, encrypted backups cannot be opened.");
        var changed = _secrets.EncryptionEnabled;
        _secrets.SetPassphrase(passphrase);
        Flash(changed
            ? "Passphrase changed. New backups use it; earlier backups still need the passphrase they were made with."
            : "Backups are now encrypted. Earlier backups stay unencrypted until retention removes them.", "success");
        return Redirect(PagePath);
    }

    public IActionResult OnPostDisableEncryption()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        if (!PasswordMatches()) return Fail("The password is incorrect. Encryption was not changed.");
        if (_secrets.CloudSettings is not null) return Fail("Turn off Backblaze B2 first. Cloud backups are always encrypted.");
        _secrets.SetPassphrase(null);
        Flash("Encryption is off. New backups are not encrypted and do not include publishing tokens.", "success");
        return Redirect(PagePath);
    }

    public async Task<IActionResult> OnPostCloud(string? endpoint, string? bucket, string? keyId, string? applicationKey, CancellationToken cancellationToken)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        if (!PasswordMatches()) return Fail("The password is incorrect. Backblaze B2 was not changed.");
        if (string.IsNullOrEmpty(CurrentPassword)) return Fail("Set an owner password in Settings > Access before backing up to the cloud. Without one, anyone who can open this site could send your journal to another bucket.");
        if (!_secrets.EncryptionEnabled) return Fail("Turn on encryption first. Cloud backups are always encrypted.");
        var normalized = B2Cloud.NormalizeEndpoint(endpoint);
        if (normalized is null) return Fail("Enter the bucket's S3 endpoint, such as https://s3.us-west-002.backblazeb2.com. It is shown on the bucket's page in Backblaze.");
        bucket = bucket?.Trim();
        if (!B2Cloud.IsValidBucket(bucket)) return Fail("Enter the bucket name exactly as Backblaze shows it.");
        keyId = keyId?.Trim();
        if (string.IsNullOrEmpty(keyId)) return Fail("Enter the application key ID.");
        var secret = applicationKey?.Trim();
        if (string.IsNullOrEmpty(secret))
        {
            // The key is write-only on this page: leaving it empty keeps the saved one for the same key ID.
            var current = SafeCloudConnection();
            if (current is null || current.KeyId != keyId) return Fail("Enter the application key.");
            secret = current.ApplicationKey;
        }

        var connection = new B2Connection(normalized, bucket!, keyId, secret);
        var result = await TestAsync(connection, cancellationToken);
        if (!result.Works) return Fail($"Backblaze B2 was not saved. {result.Message}");
        _secrets.SetCloudConnection(connection);
        _backups.ForgetCloudListing();
        Flash($"Backups are now uploaded to {bucket} after each backup. {result.Message}", result.CanErase ? "error" : "success");
        return Redirect(PagePath);
    }

    public async Task<IActionResult> OnPostTestCloud(CancellationToken cancellationToken)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        if (SafeCloudConnection() is not { } connection) return Fail("Backblaze B2 is not set up, or its saved key cannot be read. Enter it again.");
        var result = await TestAsync(connection, cancellationToken);
        Flash(result.Message, result.Works && !result.CanErase ? "success" : "error");
        return Redirect(PagePath);
    }

    public IActionResult OnPostDisableCloud()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        if (!PasswordMatches()) return Fail("The password is incorrect. Backblaze B2 was not changed.");
        _secrets.SetCloudConnection(null);
        _database.SetMissingCloudBackups([]);
        _backups.ForgetCloudListing();
        Flash("Backups are no longer uploaded. Backups already in the bucket were left there.", "success");
        return Redirect(PagePath);
    }

    public async Task<IActionResult> OnPostRefreshCloud(CancellationToken cancellationToken)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var state = await _backups.RefreshCloudAsync(null, cancellationToken);
        if (state.Error is not null) Flash(state.Error, "error");
        return Redirect(PagePath + "#cloud");
    }

    public IActionResult OnPostRetryUpload(Guid runId)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var started = _backups.TryStartUpload(runId, out var message);
        Flash(message, started ? "success" : "error");
        return Redirect(PagePath);
    }

    private async Task<CloudTestResult> TestAsync(B2Connection connection, CancellationToken cancellationToken)
    {
        try
        {
            using var cloud = _backups.CloudFactory.Create(connection);
            return await CloudBackups.TestAsync(cloud, _database.GetOrCreateInstallationId(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CloudTestResult(false, false, B2Cloud.Describe(ex));
        }
    }

    private B2Connection? SafeCloudConnection()
    {
        try
        {
            return _secrets.GetCloudConnection();
        }
        catch (BackupFailedException)
        {
            return null;
        }
    }

    private bool PasswordMatches() => _database.GetOwnerPasswordHash() is { } hash && PasswordService.Verify(CurrentPassword, hash);

    private IActionResult Fail(string message)
    {
        Flash(message, "error");
        return Redirect(PagePath);
    }

    public BackupSchedule Schedule { get; private set; } = BackupSchedule.Default;
    public DateTimeOffset? NextRun => _scheduler.NextRun;
    public BackupWarning? Warning => _scheduler.Warning;

    /// <summary>IANA zone ids offered in the time zone field.</summary>
    public static IReadOnlyList<string> TimeZoneIds { get; } = TimeZoneInfo.GetSystemTimeZones()
        .Select(zone => zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null)
        .OfType<string>()
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToList();

    public IActionResult OnPostSchedule(bool enabled, string? frequency, string? time, DayOfWeek day, string? timeZone, int keepDaily, int keepWeekly, int keepMonthly)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        if (!TimeOnly.TryParse(time, System.Globalization.CultureInfo.InvariantCulture, out var at))
        {
            Flash("Enter the time as hours and minutes, such as 02:00.", "error");
            return Redirect(PagePath);
        }

        var zoneId = string.IsNullOrWhiteSpace(timeZone) ? BackupSchedule.LocalTimeZoneId() : timeZone.Trim();
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            Flash($"{zoneId} is not a time zone this machine knows. Use an id such as Europe/London or America/New_York.", "error");
            return Redirect(PagePath);
        }

        if (keepDaily is < 1 or > 90 || keepWeekly is < 0 or > 52 || keepMonthly is < 0 or > 60)
        {
            Flash("Keep between 1 and 90 daily, 0 and 52 weekly, and 0 and 60 monthly backups.", "error");
            return Redirect(PagePath);
        }

        _database.SetBackupSchedule(new BackupSchedule(
            enabled,
            frequency == BackupFrequency.Weekly ? BackupFrequency.Weekly : BackupFrequency.Daily,
            new TimeOnly(at.Hour, at.Minute),
            day,
            zoneId,
            keepDaily,
            keepWeekly,
            keepMonthly));
        Flash(enabled ? "Backup schedule saved." : "Scheduled backups are off. Manual backups still work.", "success");
        return Redirect(PagePath);
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty] public string? NewFolder { get; set; }
    [BindProperty] public string CurrentPassword { get; set; } = string.Empty;

    public Journal? Journal { get; private set; }
    public BackupProgress Progress { get; private set; } = null!;
    public string Folder { get; private set; } = string.Empty;
    public string DefaultFolder => _backups.DefaultFolder;
    public bool UsesDefaultFolder { get; private set; }
    public bool FolderSharesDrive { get; private set; }
    public IReadOnlyList<BackupListing> Backups { get; private set; } = [];
    public IReadOnlyList<BackupRun> Runs { get; private set; } = [];
    public string? FlashMessage { get; private set; }
    public string? FlashKind { get; private set; }
    public RestoredFrom? RestoredFrom { get; private set; }
    public bool IntegrationsPaused { get; private set; }
    public IReadOnlyList<Journal> WatchedJournals { get; private set; } = [];
    public bool Restarting { get; private set; }
    public const string StartCleanPhrase = "start clean";
    private string PagePath => $"/journal/{JournalId:D}/settings/backups";

    public IActionResult OnGet()
    {
        if (!Load()) return NotFound();
        FlashMessage = TempData["FlashMessage"] as string;
        FlashKind = TempData["FlashKind"] as string ?? "success";
        return Page();
    }

    public IActionResult OnPostBackUpNow()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var started = _backups.TryStart(BackupTrigger.Manual, out _, out var message);
        Flash(started ? "Backup started. The site pauses briefly while the journal is copied." : message, started ? "success" : "error");
        return Redirect(PagePath);
    }

    public IActionResult OnPostCancel()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        Flash(_backups.Cancel() ? "Cancelling the backup." : "No backup is running.", "success");
        return Redirect(PagePath);
    }

    public IActionResult OnPostFolder()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var hash = _database.GetOwnerPasswordHash();
        if (hash is null || !PasswordService.Verify(CurrentPassword, hash))
        {
            Flash("The password is incorrect. The backup folder was not changed.", "error");
            return Redirect(PagePath);
        }

        if (string.IsNullOrWhiteSpace(NewFolder))
        {
            _database.SetBackupFolder(null);
            Flash($"Backups will be saved to the default folder, {_backups.DefaultFolder}.", "success");
            return Redirect(PagePath);
        }

        if (_backups.ValidateFolder(NewFolder) is { } problem)
        {
            Flash(problem, "error");
            return Redirect(PagePath);
        }

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(NewFolder.Trim()));
        _database.SetBackupFolder(folder);
        Flash($"Backups will be saved to {folder}. Existing backups were not moved.", "success");
        return Redirect(PagePath);
    }

    public IActionResult OnPostStartClean(string? confirmation)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var hash = _database.GetOwnerPasswordHash();
        if (hash is null || !PasswordService.Verify(CurrentPassword, hash))
        {
            Flash("The password is incorrect. Nothing was moved.", "error");
            return Redirect(PagePath);
        }

        if (!string.Equals(confirmation?.Trim(), StartCleanPhrase, StringComparison.OrdinalIgnoreCase))
        {
            Flash($"Type \"{StartCleanPhrase}\" to confirm. Nothing was moved.", "error");
            return Redirect(PagePath);
        }

        if (_backups.Progress.Running)
        {
            Flash("Wait for the running backup to finish first.", "error");
            return Redirect(PagePath);
        }

        _restore.StartClean();
        Load();
        Restarting = true;
        return Page();
    }

    public IActionResult OnPostResumeIntegrations()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        _database.ResumeIntegrations();
        _monitor.NotifyConfigurationChanged();
        Flash("Watched imports resumed.", "success");
        return Redirect(PagePath);
    }

    public IActionResult OnPostDismissRestore()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        _database.DismissRestoredFrom();
        return Redirect(PagePath);
    }

    public static string FormatBytes(long bytes) => BackupService.FormatBytes(bytes);

    public static string StatusLabel(string status) => status switch
    {
        BackupRunStatus.Running => "Running",
        BackupRunStatus.Completed => "Completed",
        BackupRunStatus.CompletedWithWarnings => "Completed with warnings",
        BackupRunStatus.Failed => "Failed",
        BackupRunStatus.Cancelled => "Cancelled",
        BackupRunStatus.Interrupted => "Interrupted",
        _ => status
    };

    public static string StatusClass(string status) => status switch
    {
        BackupRunStatus.Completed => "text-success",
        BackupRunStatus.CompletedWithWarnings => "text-warning",
        BackupRunStatus.Failed or BackupRunStatus.Interrupted => "text-danger",
        _ => "text-secondary"
    };

    private bool Load()
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return false;
        Progress = _backups.Progress;
        Folder = _backups.Folder;
        UsesDefaultFolder = _database.GetBackupFolder() is null;
        FolderSharesDrive = _backups.FolderSharesDriveWithData();
        Backups = _backups.ListBackups();
        Runs = _database.GetBackupRuns(HistoryLength);
        IntegrationsPaused = _database.AreIntegrationsPaused();
        Schedule = _database.GetBackupSchedule();
        EncryptionEnabled = _secrets.EncryptionEnabled;
        Cloud = _secrets.CloudSettings;
        CloudListing = Cloud is null ? null : _backups.CloudListing;
        OwnerHasPassword = _database.GetOwnerPasswordHash() is { } ownerHash && !PasswordService.Verify(string.Empty, ownerHash);
        RestoredFrom = null;
        if (_database.GetRestoredFrom() is { } restored)
        {
            try
            {
                RestoredFrom = System.Text.Json.JsonSerializer.Deserialize<RestoredFrom>(restored);
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        WatchedJournals = _database.GetJournals()
            .Where(journal => !string.IsNullOrWhiteSpace(journal.ImportWatchDirectory) || journal.ImportWatchFileEnabled)
            .ToList();
        return true;
    }

    private void Flash(string message, string kind)
    {
        TempData["FlashMessage"] = message;
        TempData["FlashKind"] = kind;
    }
}

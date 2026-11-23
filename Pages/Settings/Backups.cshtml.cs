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

    public BackupsModel(TradeFoundryDb database, BackupService backups, RestoreService restore, ImportFolderMonitor monitor)
    {
        _database = database;
        _backups = backups;
        _restore = restore;
        _monitor = monitor;
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

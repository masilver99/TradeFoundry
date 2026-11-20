using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages.Settings;

[Authorize]
public class SourcesModel : PageModel
{
    private readonly TradeFoundryDb _database;
    private readonly ImportFolderMonitor _importFolderMonitor;

    public SourcesModel(TradeFoundryDb database, ImportFolderMonitor importFolderMonitor)
    {
        _database = database;
        _importFolderMonitor = importFolderMonitor;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty] public string ImportWatchDirectory { get; set; } = string.Empty;
    public Journal? Journal { get; private set; }
    public IReadOnlyList<InstrumentDefinition> Instruments { get; private set; } = Array.Empty<InstrumentDefinition>();
    /// <summary>Source applications whose imported symbols can be mapped to instruments, in display order.</summary>
    public static IReadOnlyList<(string Key, string Label, string Example)> MappableApplications { get; } =
    [
        (TradeFoundryConstants.SierraChart, "Sierra Chart", "(?<!\\w)MES"),
        (TradeFoundryConstants.NinjaTrader, "NinjaTrader", "^MES \\d{2}-\\d{2}$")
    ];

    public IReadOnlyDictionary<string, IReadOnlyList<InstrumentMapping>> MappingsByApplication { get; private set; } = new Dictionary<string, IReadOnlyList<InstrumentMapping>>();
    public IReadOnlyList<WatchedImportFileStatus> WatchedImportFiles { get; private set; } = Array.Empty<WatchedImportFileStatus>();
    public string? FlashMessage { get; private set; }
    public string? FlashKind { get; private set; }

    public IActionResult OnGet()
    {
        Load();
        if (Journal is null) return NotFound();
        FlashMessage = TempData["FlashMessage"] as string;
        FlashKind = TempData["FlashKind"] as string ?? "success";
        return Page();
    }

    public IActionResult OnPostSaveMapping(string applicationKey, Guid? mappingId, string matchRegex, string instrumentCode, decimal? commissionOverride, int position)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        if (!TryGetApplication(applicationKey, out var application)) return BadRequest();
        var regexIsValid = InstrumentConfiguration.IsValidMatchRegex(matchRegex ?? string.Empty);
        var saved = regexIsValid && _database.SaveInstrumentMapping(mappingId, application.Key, matchRegex ?? string.Empty, instrumentCode ?? string.Empty, commissionOverride, position);
        TempData["FlashMessage"] = saved
            ? $"{application.Label} mapping saved."
            : regexIsValid
                ? "The mapping could not be saved. Check the regex, instrument, commission, and duplicate patterns."
                : "The mapping regex is invalid.";
        TempData["FlashKind"] = saved ? "success" : "error";
        return Redirect($"/journal/{JournalId:D}/settings/sources");
    }

    public IActionResult OnPostDeleteMapping(string applicationKey, Guid mappingId)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        if (!TryGetApplication(applicationKey, out var application)) return BadRequest();
        var deleted = _database.DeleteInstrumentMapping(mappingId);
        TempData["FlashMessage"] = deleted ? $"{application.Label} mapping removed." : $"The {application.Label} mapping could not be removed.";
        TempData["FlashKind"] = deleted ? "success" : "error";
        return Redirect($"/journal/{JournalId:D}/settings/sources");
    }

    private static bool TryGetApplication(string? key, out (string Key, string Label, string Example) application)
    {
        application = MappableApplications.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return application.Key is not null;
    }

    public IActionResult OnPostSaveImportWatchDirectory(bool clearImportWatchDirectory = false)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();

        var directory = clearImportWatchDirectory ? string.Empty : (ImportWatchDirectory ?? string.Empty).Trim();
        if (directory.Length > 0)
        {
            if (!Path.IsPathFullyQualified(directory))
            {
                TempData["FlashMessage"] = "Enter an absolute folder path on the TradeFoundry server.";
                TempData["FlashKind"] = "error";
                return Redirect($"/journal/{JournalId:D}/settings/sources");
            }

            try
            {
                directory = Path.GetFullPath(directory);
                if (!Directory.Exists(directory))
                    throw new DirectoryNotFoundException();
                _ = Directory.EnumerateFileSystemEntries(directory).Take(1).ToArray();
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                TempData["FlashMessage"] = "The folder does not exist or the TradeFoundry server cannot read it.";
                TempData["FlashKind"] = "error";
                return Redirect($"/journal/{JournalId:D}/settings/sources");
            }
        }

        try
        {
            if (!_database.SaveImportWatchDirectory(JournalId, directory, out var conflictJournalName))
            {
                TempData["FlashMessage"] = conflictJournalName is null
                    ? "The watched folder could not be saved."
                    : $"That folder is already assigned to the {conflictJournalName} journal.";
                TempData["FlashKind"] = "error";
                return Redirect($"/journal/{JournalId:D}/settings/sources");
            }
        }
        catch (ArgumentException)
        {
            TempData["FlashMessage"] = "Enter a valid absolute folder path.";
            TempData["FlashKind"] = "error";
            return Redirect($"/journal/{JournalId:D}/settings/sources");
        }

        _importFolderMonitor.NotifyConfigurationChanged();
        TempData["FlashMessage"] = directory.Length == 0
            ? "Automatic imports disabled. Previous import history was kept."
            : "Watched folder saved. Existing trade export files will be scanned and imported.";
        TempData["FlashKind"] = "success";
        return Redirect($"/journal/{JournalId:D}/settings/sources");
    }

    private void Load()
    {
        Journal = _database.GetJournal(JournalId);
        ImportWatchDirectory = Journal?.ImportWatchDirectory ?? string.Empty;
        Instruments = _database.GetInstruments();
        MappingsByApplication = MappableApplications.ToDictionary(item => item.Key, item => _database.GetInstrumentMappings(item.Key), StringComparer.OrdinalIgnoreCase);
        WatchedImportFiles = Journal is null ? Array.Empty<WatchedImportFileStatus>() : _database.GetWatchedImportFileStatuses(JournalId);
    }
}

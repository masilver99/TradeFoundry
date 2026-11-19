using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
[RequestSizeLimit(256L * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 256L * 1024 * 1024)]
public class ImportsModel : PageModel
{
    private readonly TradeFoundryDb _database;
    private readonly ImportService _imports;
    private readonly ImportFolderMonitor _importMonitor;

    public ImportsModel(TradeFoundryDb database, ImportService imports, ImportFolderMonitor importMonitor)
    {
        _database = database;
        _imports = imports;
        _importMonitor = importMonitor;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? CalendarStart { get; set; }
    [BindProperty(SupportsGet = true)] public string CalendarSymbol { get; set; } = string.Empty;
    [BindProperty] public IFormFile? ImportFile { get; set; }
    [BindProperty] public IFormFile? BarImportFile { get; set; }
    [BindProperty] public string BarText { get; set; } = string.Empty;
    [BindProperty] public string BarSymbol { get; set; } = string.Empty;
    [BindProperty] public string BarImportInterval { get; set; } = BarIntervals.Auto;
    [BindProperty] public string BarImportTimeZone { get; set; } = TimeZoneCatalog.Auto;
    [BindProperty] public string ImportTimeZone { get; set; } = TimeZoneCatalog.Auto;
    [BindProperty] public string RequestedType { get; set; } = "auto";
    [BindProperty] public string GroupingPolicy { get; set; } = "flat_to_flat";
    [BindProperty] public string BarInterval { get; set; } = "source";
    [BindProperty] public string BenchmarkSymbol { get; set; } = "^GSPC";
    [BindProperty] public string WatchFilePath { get; set; } = string.Empty;
    [BindProperty] public bool WatchFileEnabled { get; set; }

    public Journal? Journal { get; private set; }
    public PagedResult<ImportBatch> Results { get; private set; } = new();
    public OhlcCalendar Calendar { get; private set; } = new();
    public IReadOnlyList<string> OhlcSymbols { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<TimeZoneOption> BarImportTimeZones { get; private set; } = Array.Empty<TimeZoneOption>();
    public IReadOnlyList<TimeZoneOption> ImportTimeZones { get; private set; } = Array.Empty<TimeZoneOption>();
    public WatchedImportFileStatus? WatchFileStatus { get; private set; }
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

    public async Task<IActionResult> OnPostImportAsync(CancellationToken cancellationToken)
    {
        var journal = _database.GetJournal(JournalId);
        if (journal is null) return NotFound();
        if (ImportFile is null || ImportFile.Length == 0)
        {
            TempData["FlashMessage"] = "Choose a CSV or TSV file first.";
            TempData["FlashKind"] = "error";
            return Redirect($"/journal/{JournalId:D}/imports");
        }

        var importTimeZone = string.IsNullOrWhiteSpace(ImportTimeZone) ? TimeZoneCatalog.Auto : ImportTimeZone;
        importTimeZone = TimeZoneCatalog.CanonicalId(importTimeZone);
        if (!TimeZoneCatalog.IsAuto(importTimeZone) && !TimeZoneCatalog.TryResolve(importTimeZone, out _))
        {
            TempData["FlashMessage"] = "Choose a supported source timezone for the trade timestamps.";
            TempData["FlashKind"] = "error";
            return Redirect($"/journal/{JournalId:D}/imports");
        }

        try
        {
            var result = await _imports.ImportAsync(JournalId, Path.GetFileName(ImportFile.FileName), ImportFile.OpenReadStream(), RequestedType, GroupingPolicy, BarInterval, cancellationToken, BenchmarkSymbol, timeZone: importTimeZone);
            var warningSuffix = result.Warnings.Count == 0 ? string.Empty : $" {result.Warnings.Count} warning(s) were retained with the import.";
            TempData["FlashMessage"] = $"Imported {result.Batch.NewRows:N0} new row(s); ignored {result.Batch.DuplicateRows:N0} duplicate(s). Source timestamps interpreted as {result.ResolvedTimeZone}.{warningSuffix}";
            TempData["FlashKind"] = result.Warnings.Count == 0 ? "success" : "warning";
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            TempData["FlashMessage"] = $"Import stopped: {ex.Message}";
            TempData["FlashKind"] = "error";
        }
        return Redirect($"/journal/{JournalId:D}/imports");
    }

    public async Task<IActionResult> OnPostImportBarsAsync(CancellationToken cancellationToken)
    {
        var journal = _database.GetJournal(JournalId);
        if (journal is null) return NotFound();
        var importTimeZone = string.IsNullOrWhiteSpace(BarImportTimeZone) ? TimeZoneCatalog.Auto : BarImportTimeZone;
        importTimeZone = TimeZoneCatalog.CanonicalId(importTimeZone);
        if (!TimeZoneCatalog.IsAuto(importTimeZone) && !TimeZoneCatalog.TryResolve(importTimeZone, out _))
        {
            TempData["FlashMessage"] = "Choose a supported source timezone for the OHLC timestamps.";
            TempData["FlashKind"] = "error";
            return Redirect($"/journal/{JournalId:D}/imports");
        }
        if (string.IsNullOrWhiteSpace(BarSymbol))
        {
            TempData["FlashMessage"] = "Enter the CME symbol/root for these bars, such as MES.";
            TempData["FlashKind"] = "error";
            return Redirect($"/journal/{JournalId:D}/imports");
        }
        var interval = BarIntervals.IsAuto(BarImportInterval)
            ? BarIntervals.Auto
            : BarIntervals.TryNormalize(BarImportInterval, out var normalizedInterval, allowSource: false)
                ? normalizedInterval
                : string.Empty;
        if (interval.Length == 0)
        {
            TempData["FlashMessage"] = "Bar interval must be at least 1 minute, such as 1m, 5m, or 1h, or leave it on auto-detect.";
            TempData["FlashKind"] = "error";
            return Redirect($"/journal/{JournalId:D}/imports");
        }
        if ((BarImportFile is null || BarImportFile.Length == 0) && string.IsNullOrWhiteSpace(BarText))
        {
            TempData["FlashMessage"] = "Choose a Sierra bar file or paste the OHLC rows first.";
            TempData["FlashKind"] = "error";
            return Redirect($"/journal/{JournalId:D}/imports");
        }

        try
        {
            ImportResult result;
            if (BarImportFile is not null && BarImportFile.Length > 0)
            {
                await using var stream = BarImportFile.OpenReadStream();
                result = await _imports.ImportAsync(JournalId, Path.GetFileName(BarImportFile.FileName), stream, "sierra-bars", GroupingPolicy, interval, cancellationToken, barSymbol: BarSymbol, timeZone: importTimeZone);
            }
            else
            {
                result = _imports.ImportText(JournalId, "pasted-sierra-bars.txt", BarText, "sierra-bars", GroupingPolicy, interval, barSymbol: BarSymbol, timeZone: importTimeZone);
            }

            var warningSuffix = result.Warnings.Count == 0 ? string.Empty : $" {result.Warnings.Count} warning(s) were retained with the import.";
            var intervalSuffix = result.ResolvedBarInterval is null ? string.Empty : $" at {BarIntervals.Label(result.ResolvedBarInterval)}";
            var symbol = InstrumentCatalog.ExtractRoot(BarSymbol);
            TempData["FlashMessage"] = $"Imported {result.Batch.NewRows:N0} source row(s) for {symbol}{intervalSuffix}; ignored {result.Batch.DuplicateRows:N0} duplicate(s). Source timestamps interpreted as {result.ResolvedTimeZone}.{warningSuffix}";
            TempData["FlashKind"] = result.Warnings.Count == 0 ? "success" : "warning";
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            TempData["FlashMessage"] = $"Bar import stopped: {ex.Message}";
            TempData["FlashKind"] = "error";
        }
        return Redirect($"/journal/{JournalId:D}/imports");
    }

    public IActionResult OnPostSaveWatchFile()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();

        var path = (WatchFilePath ?? string.Empty).Trim().Trim('"');
        var fileExists = false;
        if (path.Length == 0)
        {
            if (WatchFileEnabled) return WatchFileError("Enter the full path of the file to monitor.");
        }
        else
        {
            if (!Path.IsPathFullyQualified(path)) return WatchFileError("Enter an absolute file path on the TradeFoundry server, such as C:\\SierraChart\\Data\\TradeActivity.txt.");
            try
            {
                path = Path.GetFullPath(path);
                if (!ImportFileExtensions.Contains(Path.GetExtension(path)))
                    return WatchFileError("The monitored file must be a CSV, TSV, or TXT export.");
                if (Directory.Exists(path))
                    return WatchFileError("That path is a folder. Use Settings → Import sources to watch a whole folder.");
                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                    return WatchFileError("The folder containing that file does not exist or the TradeFoundry server cannot read it.");
                fileExists = System.IO.File.Exists(path);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return WatchFileError("Enter a valid absolute file path.");
            }
        }

        _database.SaveImportWatchFile(JournalId, path, WatchFileEnabled);
        _importMonitor.NotifyConfigurationChanged();

        TempData["FlashMessage"] = (path.Length, WatchFileEnabled, fileExists) switch
        {
            (0, _, _) => "Automatic file import cleared. Previous import history was kept.",
            (_, false, _) => "Automatic file import is off. The file path was saved and previous import history was kept.",
            (_, true, true) => $"Monitoring {Path.GetFileName(path)}. New records are imported now and whenever the file changes; duplicate rows are ignored.",
            _ => $"Monitoring {Path.GetFileName(path)}. It does not exist yet and will be imported when it is created."
        };
        TempData["FlashKind"] = "success";
        return Redirect($"/journal/{JournalId:D}/imports");
    }

    private static readonly HashSet<string> ImportFileExtensions = new(StringComparer.OrdinalIgnoreCase) { ".csv", ".tsv", ".txt" };

    private IActionResult WatchFileError(string message)
    {
        TempData["FlashMessage"] = message;
        TempData["FlashKind"] = "error";
        return Redirect($"/journal/{JournalId:D}/imports");
    }

    public IActionResult OnPostClearOhlc()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();

        var cleared = _database.ClearOhlcData();
        TempData["FlashMessage"] = cleared.BarCount == 0 && cleared.ImportBatchCount == 0
            ? "The shared OHLC data store was already empty. Trades and other imports were not changed."
            : $"Cleared {cleared.BarCount:N0} shared OHLC bar(s) across {cleared.SeriesCount:N0} series and removed {cleared.ImportBatchCount:N0} OHLC import record(s). Trades and other imports were not changed.";
        TempData["FlashKind"] = "success";
        return Redirect($"/journal/{JournalId:D}/imports");
    }

    public IActionResult OnPostUndoImport(Guid batchId)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        if (!_database.HasImportBatch(JournalId, batchId)) return NotFound();
        _database.RemoveImportBatch(batchId);
        TempData["FlashMessage"] = "The import was removed and derived trades were rebuilt. Shared market bars remain available to other journals.";
        TempData["FlashKind"] = "success";
        return Redirect($"/journal/{JournalId:D}/imports");
    }

    private void Load()
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return;
        Results = _database.GetImports(new ImportQuery { JournalId = JournalId, Page = PageNumber, PageSize = 25, Search = Search ?? string.Empty });
        GroupingPolicy = Journal.GroupingPolicy;
        WatchFilePath = Journal.ImportWatchFile;
        WatchFileEnabled = Journal.ImportWatchFileEnabled;
        WatchFileStatus = string.IsNullOrWhiteSpace(Journal.ImportWatchFile) ? null : _database.GetWatchedImportFileStatus(JournalId, Journal.ImportWatchFile);
        BarImportTimeZone = TimeZoneCatalog.Auto;
        BarImportTimeZones = TimeZoneCatalog.ForImportSelection(Journal.TimeZone);
        ImportTimeZone = TimeZoneCatalog.Auto;
        ImportTimeZones = TimeZoneCatalog.ForImportSelection(Journal.TimeZone);

        OhlcSymbols = _database.GetOhlcSymbols();
        CalendarSymbol = string.IsNullOrWhiteSpace(CalendarSymbol)
            ? OhlcSymbols.FirstOrDefault() ?? string.Empty
            : InstrumentCatalog.ExtractRoot(CalendarSymbol);
        if (!string.IsNullOrWhiteSpace(CalendarSymbol) && !OhlcSymbols.Any(x => x.Equals(CalendarSymbol, StringComparison.OrdinalIgnoreCase)))
            CalendarSymbol = OhlcSymbols.FirstOrDefault() ?? string.Empty;

        var startMonth = CalendarStart.HasValue
            ? new DateOnly(CalendarStart.Value.Year, CalendarStart.Value.Month, 1)
            : new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);
        CalendarStart = startMonth;
        Calendar = _database.GetOhlcCalendar(startMonth, CalendarSymbol);
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
public class ImportsModel : PageModel
{
    private readonly TradeFoundryDb _database;
    private readonly ImportService _imports;

    public ImportsModel(TradeFoundryDb database, ImportService imports)
    {
        _database = database;
        _imports = imports;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public string Search { get; set; } = string.Empty;
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

    public Journal? Journal { get; private set; }
    public PagedResult<ImportBatch> Results { get; private set; } = new();
    public OhlcCalendar Calendar { get; private set; } = new();
    public IReadOnlyList<string> OhlcSymbols { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<TimeZoneOption> BarImportTimeZones { get; private set; } = Array.Empty<TimeZoneOption>();
    public IReadOnlyList<TimeZoneOption> ImportTimeZones { get; private set; } = Array.Empty<TimeZoneOption>();
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
        Results = _database.GetImports(new ImportQuery { JournalId = JournalId, Page = PageNumber, PageSize = 25, Search = Search });
        GroupingPolicy = Journal.GroupingPolicy;
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

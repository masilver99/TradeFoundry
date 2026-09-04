using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
public class IndexModel : PageModel
{
    private readonly TradeFoundryDb _database;
    private readonly ImportService _imports;

    public IndexModel(TradeFoundryDb database, ImportService imports)
    {
        _database = database;
        _imports = imports;
    }

    [BindProperty(SupportsGet = true)]
    public Guid? JournalId { get; set; }

    public IReadOnlyList<Journal> Journals { get; private set; } = Array.Empty<Journal>();
    public JournalOverview? Overview { get; private set; }
    public string? FlashMessage { get; private set; }
    public string? FlashKind { get; private set; }
    public string EquityChart => Overview is null ? string.Empty : ChartRenderer.Equity(Overview.Equity);
    public string DailyChart => Overview is null ? string.Empty : ChartRenderer.Daily(Overview.DailyPnl);

    [BindProperty] public string NewJournalName { get; set; } = string.Empty;
    [BindProperty] public string NewExecutionContext { get; set; } = "live";
    [BindProperty] public string NewLabels { get; set; } = string.Empty;
    [BindProperty] public string NewTimeZone { get; set; } = "UTC";
    [BindProperty] public string NewCurrency { get; set; } = "USD";
    [BindProperty] public string NewGroupingPolicy { get; set; } = "flat_to_flat";
    [BindProperty] public decimal? NewStartingEquity { get; set; }
    [BindProperty] public IFormFile? ImportFile { get; set; }
    [BindProperty] public string RequestedType { get; set; } = "auto";
    [BindProperty] public string GroupingPolicy { get; set; } = "flat_to_flat";
    [BindProperty] public string BarInterval { get; set; } = "source";

    public IActionResult OnGet()
    {
        if (!_database.HasOwner()) return RedirectToPage("/Setup");
        Load();
        FlashMessage = TempData["FlashMessage"] as string;
        FlashKind = TempData["FlashKind"] as string ?? "success";
        return Page();
    }

    public IActionResult OnPostCreateJournal()
    {
        var journal = _database.CreateJournal(NewJournalName, NewExecutionContext, NewLabels, NewTimeZone, NewCurrency, NewGroupingPolicy, NewStartingEquity);
        TempData["FlashMessage"] = $"Created {journal.Name}.";
        TempData["FlashKind"] = "success";
        return Redirect($"/journal/{journal.Id:D}/overview");
    }

    public async Task<IActionResult> OnPostImportAsync(Guid journalId, CancellationToken cancellationToken)
    {
        if (ImportFile is null || ImportFile.Length == 0)
        {
            TempData["FlashMessage"] = "Choose a CSV or TSV file first.";
            TempData["FlashKind"] = "error";
            return RedirectToPage(new { journalId });
        }
        if (_database.GetJournal(journalId) is null) return NotFound();
        try
        {
            var result = await _imports.ImportAsync(journalId, Path.GetFileName(ImportFile.FileName), ImportFile.OpenReadStream(), RequestedType, GroupingPolicy, BarInterval, cancellationToken);
            var warningSuffix = result.Warnings.Count == 0 ? string.Empty : $" {result.Warnings.Count} warning(s) were retained with the import.";
            TempData["FlashMessage"] = $"Imported {result.Batch.NewRows:N0} new row(s); ignored {result.Batch.DuplicateRows:N0} duplicate(s).{warningSuffix}";
            TempData["FlashKind"] = result.Warnings.Count == 0 ? "success" : "warning";
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            TempData["FlashMessage"] = $"Import stopped: {ex.Message}";
            TempData["FlashKind"] = "error";
        }
        return Redirect($"/journal/{journalId:D}/overview");
    }

    public IActionResult OnPostUndoImport(Guid journalId, Guid batchId)
    {
        if (_database.GetJournal(journalId) is null) return NotFound();
        if (!_database.HasImportBatch(journalId, batchId)) return NotFound();
        _database.RemoveImportBatch(batchId);
        TempData["FlashMessage"] = "The import was removed and derived trades were rebuilt.";
        TempData["FlashKind"] = "success";
        return Redirect($"/journal/{journalId:D}/overview");
    }

    private void Load()
    {
        Journals = _database.GetJournals();
        var selected = JournalId.HasValue && Journals.Any(x => x.Id == JournalId.Value) ? JournalId.Value : Journals.FirstOrDefault()?.Id;
        if (selected.HasValue)
        {
            JournalId = selected.Value;
            Overview = _database.GetOverview(selected.Value);
            GroupingPolicy = Overview.Journal.GroupingPolicy;
        }
    }
}

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
    [BindProperty] public IFormFile? ImportFile { get; set; }
    [BindProperty] public string RequestedType { get; set; } = "auto";
    [BindProperty] public string GroupingPolicy { get; set; } = "flat_to_flat";
    [BindProperty] public string BarInterval { get; set; } = "source";
    [BindProperty] public string BenchmarkSymbol { get; set; } = "SPY";

    public Journal? Journal { get; private set; }
    public PagedResult<ImportBatch> Results { get; private set; } = new();
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
        if (_database.GetJournal(JournalId) is null) return NotFound();
        if (ImportFile is null || ImportFile.Length == 0)
        {
            TempData["FlashMessage"] = "Choose a CSV or TSV file first.";
            TempData["FlashKind"] = "error";
            return Redirect($"/journal/{JournalId:D}/imports");
        }

        try
        {
            var result = await _imports.ImportAsync(JournalId, Path.GetFileName(ImportFile.FileName), ImportFile.OpenReadStream(), RequestedType, GroupingPolicy, BarInterval, cancellationToken, BenchmarkSymbol);
            var warningSuffix = result.Warnings.Count == 0 ? string.Empty : $" {result.Warnings.Count} warning(s) were retained with the import.";
            TempData["FlashMessage"] = $"Imported {result.Batch.NewRows:N0} new row(s); ignored {result.Batch.DuplicateRows:N0} duplicate(s).{warningSuffix}";
            TempData["FlashKind"] = result.Warnings.Count == 0 ? "success" : "warning";
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            TempData["FlashMessage"] = $"Import stopped: {ex.Message}";
            TempData["FlashKind"] = "error";
        }
        return Redirect($"/journal/{JournalId:D}/imports");
    }

    public IActionResult OnPostUndoImport(Guid batchId)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        if (!_database.HasImportBatch(JournalId, batchId)) return NotFound();
        _database.RemoveImportBatch(batchId);
        TempData["FlashMessage"] = "The import was removed and derived trades were rebuilt.";
        TempData["FlashKind"] = "success";
        return Redirect($"/journal/{JournalId:D}/imports");
    }

    private void Load()
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return;
        Results = _database.GetImports(new ImportQuery { JournalId = JournalId, Page = PageNumber, PageSize = 25, Search = Search });
        GroupingPolicy = Journal.GroupingPolicy;
    }
}

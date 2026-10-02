using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
[ValidateAntiForgeryToken]
[RequestSizeLimit(12 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 12 * 1024 * 1024)]
public sealed class ExpensesModel(TradeFoundryDb database, ExpenseService service) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Start { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? End { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? Category { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? Edit { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty] public ExpenseDraft Input { get; set; } = new();
    [BindProperty] public IFormFile? Receipt { get; set; }
    public Journal? Journal { get; private set; }
    public Expense? Editing { get; private set; }
    public IReadOnlyList<ExpenseCategory> Categories { get; private set; } = [];
    public IReadOnlyList<Expense> Matches { get; private set; } = [];
    public IReadOnlyList<Expense> Rows { get; private set; } = [];
    public IReadOnlyList<ExpenseReceipt> Receipts { get; private set; } = [];
    public int PageCount => Math.Max(1, (Matches.Count + 24) / 25);
    public bool CategoryDialogOpen { get; private set; }
    public bool SaveConflict { get; private set; }
    public string BaseUrl => $"/journal/{JournalId:D}/expenses";
    public string FilterUrl(int page = 1) => $"{BaseUrl}?start={Start:yyyy-MM-dd}&end={End:yyyy-MM-dd}&category={Category}&pageNumber={page}";
    public string ReceiptUrl(ExpenseReceipt receipt, bool download = false) => $"{BaseUrl}?handler=Receipt&expenseId={receipt.ExpenseId:D}&receiptId={receipt.Id:D}&download={download}";

    private bool Load()
    {
        Input ??= new();
        Journal = database.GetJournal(JournalId);
        if (Journal is null) return false;
        var today = DateOnly.FromDateTime(TimeZoneCatalog.Convert(DateTimeOffset.UtcNow, Journal.TimeZone).DateTime);
        Start ??= new DateOnly(today.Year, 1, 1);
        End ??= new DateOnly(today.Year, 12, 31);
        Categories = database.GetExpenseCategories(JournalId);
        if (Start > End) ModelState.AddModelError("Start", "Start date must be on or before end date.");
        if (Category.HasValue && !Categories.Any(c => c.Id == Category)) ModelState.AddModelError("Category", "Choose a category from this journal.");
        Matches = database.GetExpenses(JournalId, Start.Value, End.Value, Category);
        PageNumber = Math.Clamp(PageNumber, 1, PageCount);
        Rows = Matches.Skip((PageNumber - 1) * 25).Take(25).ToArray();
        Editing = Edit.HasValue ? database.GetExpense(JournalId, Edit.Value) : null;
        if (Editing is not null) Receipts = database.GetExpenseReceipts(JournalId, Editing.Id);
        return true;
    }

    public IActionResult OnGet()
    {
        if (!Load()) return NotFound();
        if (Edit.HasValue && Editing is null) return NotFound();
        if (Editing is not null) Input = Editing.Details with { };
        else Input.Date = DateOnly.FromDateTime(TimeZoneCatalog.Convert(DateTimeOffset.UtcNow, Journal!.TimeZone).DateTime);
        return Page();
    }

    public IActionResult OnPostSave(Guid? expenseId, int expectedRevision)
    {
        Edit = expenseId;
        if (!Load()) return NotFound();
        if (expenseId.HasValue && Editing is null) return NotFound();
        if (!ModelState.IsValid) return Page();
        try
        {
            var saved = expenseId.HasValue ? database.UpdateExpense(JournalId, expenseId.Value, expectedRevision, Input) : database.CreateExpense(JournalId, Input);
            TempData["ExpenseMessage"] = expenseId.HasValue ? "Expense updated." : "Expense added. You can attach receipts below.";
            return Redirect($"{FilterUrl(PageNumber)}&edit={saved.Id:D}#expense-form");
        }
        catch (InvalidOperationException e) { SaveConflict = e is ExpenseConflictException; ModelState.AddModelError(string.Empty, e.Message); return Page(); }
    }

    public IActionResult OnPostDelete(Guid expenseId, int expectedRevision) => Run(() => database.DeleteExpense(JournalId, expenseId, expectedRevision), "Expense deleted.");

    public IActionResult OnPostCreateCategory(string name) => Run(() => database.CreateExpenseCategory(JournalId, name), "Category added.", categories: true);
    public IActionResult OnPostUpdateCategory(Guid categoryId, int expectedRevision, string name, bool active) => Run(() => database.UpdateExpenseCategory(JournalId, categoryId, expectedRevision, name, active), "Category updated.", categories: true);

    public async Task<IActionResult> OnPostAttachAsync(Guid expenseId, int expectedRevision, CancellationToken cancellationToken)
    {
        Edit = expenseId;
        if (!Load()) return NotFound();
        if (Editing is null) return NotFound();
        Input = Editing.Details with { };
        try
        {
            if (Receipt is null) throw new InvalidOperationException("Choose a receipt file.");
            await using var stream = Receipt.OpenReadStream();
            await service.AttachAsync(JournalId, expenseId, expectedRevision, stream, Receipt.FileName, Receipt.ContentType, Receipt.Length, cancellationToken);
            TempData["ExpenseMessage"] = "Receipt attached.";
            return Redirect($"{FilterUrl(PageNumber)}&edit={expenseId:D}#expense-receipts");
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ModelState.AddModelError(string.Empty, e is InvalidOperationException ? e.Message : "The receipt could not be stored. Your expense is saved; try the upload again.");
            Load();
            return Page();
        }
    }

    public IActionResult OnPostRemoveReceipt(Guid expenseId, Guid receiptId, int expectedRevision)
    {
        Edit = expenseId;
        return Run(() => service.RemoveReceipt(JournalId, expenseId, receiptId, expectedRevision), "Receipt removed.");
    }

    private IActionResult Run(Action action, string message, bool categories = false)
    {
        if (!Load()) return NotFound();
        try
        {
            action();
            TempData["ExpenseMessage"] = message;
            return Redirect($"{FilterUrl(PageNumber)}{(Edit.HasValue ? $"&edit={Edit:D}" : "")}{(categories ? "&categories=true" : "")}");
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ModelState.AddModelError(string.Empty, e is InvalidOperationException ? e.Message : "Receipt metadata was removed, but its file could not be cleaned up.");
            Load();
            if (Editing is not null) Input = Editing.Details with { };
            CategoryDialogOpen = categories;
            return Page();
        }
    }

    public IActionResult OnGetReceipt(Guid expenseId, Guid receiptId, bool download = false)
    {
        var receipt = database.GetExpenseReceipts(JournalId, expenseId).FirstOrDefault(r => r.Id == receiptId);
        var path = service.GetReceiptPath(JournalId, expenseId, receiptId);
        if (receipt is null || path is null) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (download || receipt.ContentType == "application/pdf") return PhysicalFile(path, receipt.ContentType, receipt.OriginalFileName);
        return PhysicalFile(path, receipt.ContentType);
    }

    public IActionResult OnGetExport()
    {
        if (!Load()) return NotFound();
        if (!ModelState.IsValid) return BadRequest("Choose a valid date range and category.");
        return File(ExpenseService.ExportCsv(Matches), "text/csv; charset=utf-8", $"expenses-{Start:yyyyMMdd}-{End:yyyyMMdd}.csv");
    }
}

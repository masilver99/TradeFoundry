using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
[ValidateAntiForgeryToken]
public sealed class AccountModel : PageModel
{
    private const int MaxNoteLength = 500;
    private readonly TradeFoundryDb _database;
    private readonly AccountLedgerService _accounts;

    public AccountModel(TradeFoundryDb database, AccountLedgerService accounts)
    {
        _database = database;
        _accounts = accounts;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true, Name = "edit")] public Guid? EditId { get; set; }
    [BindProperty] public AccountTransactionInput Input { get; set; } = new();

    public Journal? Journal { get; private set; }
    public AccountLedgerPage Ledger { get; private set; } = new();
    public AccountTransaction? EditingTransaction { get; private set; }
    public string? FlashMessage { get; private set; }
    public string? FlashKind { get; private set; }

    public string Money(decimal value) => value.ToString("C2", CultureInfo.CurrentCulture);

    public string SignedMoney(decimal value) => value >= 0m ? $"+{Money(value)}" : Money(value);

    public string Percent(decimal value) => $"{value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)}%";

    public string LocalDateTime(DateTimeOffset value) => TimeZoneCatalog.Format(value, Journal?.TimeZone, "MMM d, yyyy · HH:mm");

    public IActionResult OnGet()
    {
        PageNumber = QueryPage();
        Load();
        if (Journal is null) return NotFound();

        FlashMessage = TempData["FlashMessage"] as string;
        FlashKind = TempData["FlashKind"] as string ?? "success";
        if (EditingTransaction is not null)
            PopulateInput(EditingTransaction);
        else if (Input.EffectiveLocal == default)
            Input.EffectiveLocal = LocalNow();

        return Page();
    }

    public IActionResult OnPostCreate()
    {
        var journal = _database.GetJournal(JournalId);
        if (journal is null) return NotFound();
        Input ??= new();

        if (!ValidateInput())
        {
            Load();
            return Page();
        }

        _database.CreateAccountTransaction(JournalId, new AccountTransactionDraft
        {
            Type = Input.Type,
            EffectiveUtc = TimeZoneCatalog.FromLocal(Input.EffectiveLocal, journal.TimeZone),
            Amount = Input.Amount,
            Note = Input.Note?.Trim() ?? string.Empty
        });
        TempData["FlashMessage"] = "Account entry added.";
        TempData["FlashKind"] = "success";
        return Redirect($"/journal/{JournalId:D}/account");
    }

    public IActionResult OnPostUpdate(Guid transactionId, int expectedRevision)
    {
        var journal = _database.GetJournal(JournalId);
        if (journal is null) return NotFound();
        Input ??= new();

        if (!ValidateInput())
        {
            EditId = transactionId;
            Load();
            return Page();
        }

        var result = _database.UpdateAccountTransaction(JournalId, transactionId, expectedRevision, new AccountTransactionDraft
        {
            Type = Input.Type,
            EffectiveUtc = TimeZoneCatalog.FromLocal(Input.EffectiveLocal, journal.TimeZone),
            Amount = Input.Amount,
            Note = Input.Note?.Trim() ?? string.Empty
        });
        if (result.Conflict)
        {
            EditId = transactionId;
            Load();
            ModelState.AddModelError(string.Empty, "This account entry changed in another window. Review the current values and save again.");
            return Page();
        }
        if (result.NotFound)
        {
            TempData["FlashMessage"] = "That account entry no longer exists.";
            TempData["FlashKind"] = "error";
            return Redirect($"/journal/{JournalId:D}/account");
        }

        TempData["FlashMessage"] = "Account entry updated.";
        TempData["FlashKind"] = "success";
        return Redirect($"/journal/{JournalId:D}/account");
    }

    public IActionResult OnPostDelete(Guid transactionId, int expectedRevision)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();

        var result = _database.DeleteAccountTransaction(JournalId, transactionId, expectedRevision);
        if (result.Conflict)
        {
            EditId = transactionId;
            Load();
            ModelState.AddModelError(string.Empty, "This account entry changed in another window. Refresh and try the delete again.");
            return Page();
        }
        if (result.NotFound)
        {
            TempData["FlashMessage"] = "That account entry no longer exists.";
            TempData["FlashKind"] = "error";
            return Redirect($"/journal/{JournalId:D}/account");
        }

        TempData["FlashMessage"] = "Account entry deleted.";
        TempData["FlashKind"] = "success";
        return Redirect($"/journal/{JournalId:D}/account");
    }

    private bool ValidateInput()
    {
        if (Input.Type is not AccountTransactionType.Deposit and not AccountTransactionType.Withdrawal)
            ModelState.AddModelError("Input.Type", "Choose a deposit or withdrawal.");
        if (Input.Amount <= 0m)
            ModelState.AddModelError("Input.Amount", "Enter an amount greater than zero.");
        if (Input.EffectiveLocal == default)
            ModelState.AddModelError("Input.EffectiveLocal", "Choose the effective date and time.");
        if ((Input.Note?.Length ?? 0) > MaxNoteLength)
            ModelState.AddModelError("Input.Note", $"Keep the note under {MaxNoteLength:N0} characters.");
        return ModelState.IsValid;
    }

    private void Load()
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return;

        Ledger = _accounts.GetLedger(JournalId, PageNumber, AccountLedgerService.DefaultPageSize);
        EditingTransaction = EditId.HasValue
            ? _database.GetAccountTransaction(JournalId, EditId.Value)
            : null;
    }

    private void PopulateInput(AccountTransaction transaction)
    {
        Input = new AccountTransactionInput
        {
            Type = transaction.Type,
            Amount = transaction.Amount,
            EffectiveLocal = TimeZoneCatalog.Convert(transaction.EffectiveUtc, Journal!.TimeZone).DateTime,
            Note = transaction.Note
        };
    }

    private DateTime LocalNow() => TimeZoneCatalog.Convert(DateTimeOffset.UtcNow, Journal!.TimeZone).DateTime;

    private int QueryPage() => int.TryParse(Request.Query["page"], out var page) ? Math.Max(1, page) : 1;
}

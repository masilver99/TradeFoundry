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
    /// <summary>The <see cref="AccountLedgerShow"/> row filter, kept across paging, edits, and saves.</summary>
    [BindProperty(SupportsGet = true, Name = "show")] public string? Show { get; set; }
    [BindProperty] public AccountTransactionInput Input { get; set; } = new();

    public Journal? Journal { get; private set; }
    public AccountLedgerPage Ledger { get; private set; } = new();
    /// <summary>The journal filter; only its dates apply to account activity.</summary>
    public JournalFilterScope Filter { get; private set; } = JournalFilter.None.Scope(null);
    public bool IsDateFiltered => Filter.HasDateBounds;
    public string FilterSuffix => Filter.Filter.ToQueryString() is { Length: > 0 } query ? "&" + query : string.Empty;
    public bool IsShowFiltered => Ledger.Show != AccountLedgerShow.All;
    public string ShowLabel => AccountLedgerShow.Options.FirstOrDefault(option => option.Value == Ledger.Show).Label ?? "All activity";

    /// <summary>The account page URL keeping the row filter; <paramref name="query"/> adds more parameters.</summary>
    public string AccountUrl(string? query = null)
    {
        var parts = new List<string>();
        var show = AccountLedgerShow.Normalize(Show);
        if (show.Length > 0) parts.Add($"show={Uri.EscapeDataString(show)}");
        if (!string.IsNullOrEmpty(query)) parts.Add(query);
        return parts.Count == 0 ? $"/journal/{JournalId:D}/account" : $"/journal/{JournalId:D}/account?{string.Join("&", parts)}";
    }

    public string PageUrl(int page) => AccountUrl($"page={page}{FilterSuffix}") + "#account-ledger";
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
        return Redirect(AccountUrl());
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
            return Redirect(AccountUrl());
        }

        TempData["FlashMessage"] = "Account entry updated.";
        TempData["FlashKind"] = "success";
        return Redirect(AccountUrl());
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
            return Redirect(AccountUrl());
        }

        TempData["FlashMessage"] = "Account entry deleted.";
        TempData["FlashKind"] = "success";
        return Redirect(AccountUrl());
    }

    private bool ValidateInput()
    {
        if (Input.Type is not AccountTransactionType.Deposit and not AccountTransactionType.Withdrawal and not AccountTransactionType.Correction)
            ModelState.AddModelError("Input.Type", "Choose a deposit, withdrawal, or correction.");
        if (Input.Type == AccountTransactionType.Correction)
        {
            if (Input.Amount == 0m)
                ModelState.AddModelError("Input.Amount", "Enter a non-zero correction; use a negative amount to lower the balance.");
        }
        else if (Input.Amount <= 0m)
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

        Filter = JournalFilterRequest.Resolve(HttpContext, Journal, datesOnly: true);
        Ledger = _accounts.GetLedger(JournalId, PageNumber, AccountLedgerService.DefaultPageSize, Filter.StartUtc, Filter.EndUtcExclusive, Show);
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

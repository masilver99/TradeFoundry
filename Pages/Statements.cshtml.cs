using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services.Statements;

namespace TradeFoundry.Pages;

[Authorize]
[ValidateAntiForgeryToken]
[RequestSizeLimit(MaxUploadLength)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxUploadLength)]
public sealed class StatementsModel(TradeFoundryDb database, StatementReconciliationService service, ILogger<StatementsModel> logger) : PageModel
{
    /// <summary>Up to <see cref="MaxFilesPerImport"/> statements of 10 MB each would be 1 GB; real statements are well under 1 MB.</summary>
    private const long MaxUploadLength = 200L * 1024 * 1024;
    public const int MaxFilesPerImport = 100;

    public sealed record ImportMessage(string FileName, string Outcome, string Message);

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    /// <summary>"differences" (default) lists only days that need attention; "all" lists every reconciled day.</summary>
    [BindProperty(SupportsGet = true)] public string? Show { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Day { get; set; }

    public ReconciliationReport Report { get; private set; } = new();
    public IReadOnlyList<ReconciliationDay> Rows { get; private set; } = [];
    public IReadOnlyList<ImportMessage> ImportMessages { get; private set; } = [];
    public bool ShowAll => string.Equals(Show, "all", StringComparison.OrdinalIgnoreCase);
    public string BaseUrl => $"/journal/{JournalId:D}/statements";
    public string ListUrl => ShowAll ? $"{BaseUrl}?show=all" : BaseUrl;
    public string ReviewUrl(DateOnly date) => $"/journal/{JournalId:D}/review?date={date:yyyy-MM-dd}";
    public string DayUrl(DateOnly date) => $"{BaseUrl}?{(ShowAll ? "show=all&" : string.Empty)}day={date:yyyy-MM-dd}#day-{date:yyyy-MM-dd}";

    private bool Load()
    {
        if (database.GetJournal(JournalId) is null) return false;
        Report = service.BuildReport(JournalId);
        Rows = ShowAll ? Report.Days : Report.Days.Where(d => d.Status != ReconciliationStatus.Matched).ToArray();
        if (TempData["StatementImport"] is string json)
        {
            try { ImportMessages = JsonSerializer.Deserialize<ImportMessage[]>(json) ?? []; }
            catch (JsonException) { ImportMessages = []; }
        }
        return true;
    }

    public IActionResult OnGet() => Load() ? Page() : NotFound();

    public async Task<IActionResult> OnPostImportAsync(List<IFormFile> files, CancellationToken cancellationToken)
    {
        if (database.GetJournal(JournalId) is null) return NotFound();
        var messages = new List<ImportMessage>();
        if (files.Count == 0) messages.Add(new(string.Empty, "failed", "Choose one or more statement PDFs."));
        if (files.Count > MaxFilesPerImport) messages.Add(new(string.Empty, "failed", $"Only the first {MaxFilesPerImport} files were imported; import the rest in another batch."));
        foreach (var file in files.Take(MaxFilesPerImport))
        {
            try
            {
                await using var stream = file.OpenReadStream();
                var outcome = await service.ImportAsync(JournalId, stream, file.FileName, file.Length, cancellationToken);
                messages.Add(new(outcome.FileName, outcome.Imported ? "imported" : outcome.Duplicate ? "duplicate" : "failed", outcome.Message));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One unreadable file must not stop the rest of the batch.
                logger.LogWarning(e, "Statement import failed for one file.");
                messages.Add(new(Path.GetFileName(file.FileName), "failed", e is StatementParseException ? e.Message : "This file could not be imported."));
            }
        }
        // TempData lives in a cookie, so keep it small: list a few failures and summarize the rest.
        var stored = messages.Where(m => m.Outcome == "failed").Take(8).ToList();
        var imported = messages.Count(m => m.Outcome == "imported");
        var duplicates = messages.Count(m => m.Outcome == "duplicate");
        if (messages.Count <= 10) stored = messages;
        else stored.Insert(0, new(string.Empty, imported > 0 ? "imported" : "duplicate", $"{imported} statement(s) imported, {duplicates} already imported, {messages.Count(m => m.Outcome == "failed")} could not be read."));
        TempData["StatementImport"] = JsonSerializer.Serialize(stored);
        return Redirect(ListUrl);
    }

    public IActionResult OnPostApply(DateOnly date, decimal? expectedFees)
    {
        if (database.GetJournal(JournalId) is null) return NotFound();
        try
        {
            var changed = service.ApplyFees(JournalId, date, expectedFees);
            TempData["StatementMessage"] = $"Applied statement fees to {changed} trade(s) on {date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}.";
        }
        catch (InvalidOperationException e) { TempData["StatementError"] = e.Message; }
        return Redirect(ListUrl);
    }

    public IActionResult OnPostApplyAll()
    {
        if (database.GetJournal(JournalId) is null) return NotFound();
        try
        {
            var (days, trades) = service.ApplyAllFeeDifferences(JournalId);
            TempData["StatementMessage"] = days == 0 ? "No days had fee-only differences." : $"Applied statement fees to {trades} trade(s) across {days} day(s).";
        }
        catch (InvalidOperationException e) { TempData["StatementError"] = e.Message; }
        return Redirect(ListUrl);
    }

    public IActionResult OnPostDelete(Guid statementId)
    {
        if (database.GetJournal(JournalId) is null) return NotFound();
        if (database.DeleteBrokerStatement(JournalId, statementId)) TempData["StatementMessage"] = "Statement removed. Fees already applied to trades stay in their review history.";
        else TempData["StatementError"] = "That statement was not found.";
        return Redirect(ListUrl);
    }

    public static string StatusLabel(ReconciliationStatus status) => status switch
    {
        ReconciliationStatus.Matched => "Matched",
        ReconciliationStatus.FeesDiffer => "Fees differ",
        ReconciliationStatus.TradesDiffer => "Trades differ",
        ReconciliationStatus.MissingFromJournal => "Missing from journal",
        ReconciliationStatus.NotOnStatement => "Not on statement",
        ReconciliationStatus.StatementsDisagree => "Statements disagree",
        _ => status.ToString()
    };

    public static string StatusClass(ReconciliationStatus status) => status switch
    {
        ReconciliationStatus.Matched => "tf-recon-ok",
        ReconciliationStatus.FeesDiffer => "tf-recon-fees",
        _ => "tf-recon-bad"
    };

    public static string StatusHelp(ReconciliationDay day) => day.Status switch
    {
        ReconciliationStatus.Matched => "Contracts, realized P&L and fees agree with the statement.",
        ReconciliationStatus.FeesDiffer when day.ApplyBlocker is { } blocker => $"The trades agree; only the fees differ. The statement fees can't be applied yet: {blocker}",
        ReconciliationStatus.FeesDiffer => "The trades agree; only the fees differ. Apply the statement fees to correct them.",
        ReconciliationStatus.TradesDiffer when day.UnmatchedInstruments.Count > 0 =>
            $"The journal has {string.Join(", ", day.UnmatchedInstruments)} trades on this day, which the statement doesn't show. They may be from another account.",
        ReconciliationStatus.TradesDiffer when day.StatementContractSides != day.JournalContractSides =>
            $"The statement has {day.StatementContractSides} contract sides and the journal has {day.JournalContractSides}. A trade may be missing, duplicated, or held overnight.",
        ReconciliationStatus.TradesDiffer => "Contracts agree but realized P&L differs, so a fill price or quantity differs. Fix the trades before applying fees.",
        ReconciliationStatus.MissingFromJournal => "The statement shows trading on this day, but the journal has no closed trades. Import that day's trades.",
        ReconciliationStatus.NotOnStatement => "The journal has trades on a day the statement covers, but the statement shows no trading. These may be from another account or a simulator.",
        ReconciliationStatus.StatementsDisagree => "Two imported statements give different figures for this day, which usually means the broker corrected a trade. The monthly statement is shown.",
        _ => string.Empty
    };

    public static string Money(decimal value) => value.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
    public static string SignedMoney(decimal value) => (value > 0 ? "+" : string.Empty) + Money(value);
}

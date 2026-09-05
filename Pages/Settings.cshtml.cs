using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
public class SettingsModel : PageModel
{
    private readonly TradeFoundryDb _database;

    public SettingsModel(TradeFoundryDb database) => _database = database;

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty] public string Name { get; set; } = string.Empty;
    [BindProperty] public string ExecutionContext { get; set; } = "live";
    [BindProperty] public string Labels { get; set; } = string.Empty;
    [BindProperty] public string TimeZone { get; set; } = "UTC";
    [BindProperty] public string Currency { get; set; } = "USD";
    [BindProperty] public string GroupingPolicy { get; set; } = "flat_to_flat";
    [BindProperty] public decimal? StartingEquity { get; set; }
    [BindProperty] public string NewJournalName { get; set; } = string.Empty;
    [BindProperty] public string NewExecutionContext { get; set; } = "live";
    [BindProperty] public string NewLabels { get; set; } = string.Empty;
    [BindProperty] public string CurrentPassword { get; set; } = string.Empty;
    [BindProperty] public string NewPassword { get; set; } = string.Empty;
    [BindProperty] public string ConfirmNewPassword { get; set; } = string.Empty;

    public Journal? Journal { get; private set; }
    public IReadOnlyList<Journal> Journals { get; private set; } = Array.Empty<Journal>();
    public IReadOnlyList<InstrumentDefinition> Instruments { get; private set; } = Array.Empty<InstrumentDefinition>();
    public IReadOnlyList<InstrumentMapping> SierraChartMappings { get; private set; } = Array.Empty<InstrumentMapping>();
    public string DatabasePath => _database.DatabasePath;
    public string DatabaseSize => FormatBytes(_database.DatabaseSizeBytes);
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

    public IActionResult OnPostSave()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        if (!_database.UpdateJournal(JournalId, Name, ExecutionContext, Labels, TimeZone, Currency, GroupingPolicy, StartingEquity))
        {
            TempData["FlashMessage"] = "The journal could not be updated.";
            TempData["FlashKind"] = "error";
        }
        else
        {
            TempData["FlashMessage"] = "Journal settings saved.";
            TempData["FlashKind"] = "success";
        }
        return Redirect($"/journal/{JournalId:D}/settings");
    }

    public IActionResult OnPostCreateJournal()
    {
        var journal = _database.CreateJournal(NewJournalName, NewExecutionContext, NewLabels, "UTC", "USD", "flat_to_flat");
        TempData["FlashMessage"] = $"Created {journal.Name}.";
        TempData["FlashKind"] = "success";
        return Redirect($"/journal/{journal.Id:D}/settings");
    }

    public IActionResult OnPostChangePassword()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();

        var currentHash = _database.GetOwnerPasswordHash();
        if (currentHash is null || !PasswordService.Verify(CurrentPassword, currentHash))
        {
            TempData["FlashMessage"] = "The current password is incorrect.";
            TempData["FlashKind"] = "error";
        }
        else if (!string.Equals(NewPassword, ConfirmNewPassword, StringComparison.Ordinal))
        {
            TempData["FlashMessage"] = "The new passwords do not match.";
            TempData["FlashKind"] = "error";
        }
        else if (!_database.UpdateOwnerPasswordHash(PasswordService.Hash(NewPassword)))
        {
            TempData["FlashMessage"] = "The password could not be changed.";
            TempData["FlashKind"] = "error";
        }
        else
        {
            TempData["FlashMessage"] = "Password changed.";
            TempData["FlashKind"] = "success";
        }

        return Redirect($"/journal/{JournalId:D}/settings");
    }

    public IActionResult OnPostSaveInstrument(Guid? instrumentId, string code, decimal? defaultCommission, decimal pointValue, decimal tickSize)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var saved = _database.SaveInstrument(instrumentId, code, defaultCommission, pointValue, tickSize);
        TempData["FlashMessage"] = saved
            ? $"Instrument {InstrumentConfiguration.NormalizeCode(code)} saved."
            : "The instrument could not be saved. Check that its code is unique and its point value is positive.";
        TempData["FlashKind"] = saved ? "success" : "error";
        return Redirect($"/journal/{JournalId:D}/settings");
    }

    public IActionResult OnPostDeleteInstrument(Guid instrumentId)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var deleted = _database.DeleteInstrument(instrumentId);
        TempData["FlashMessage"] = deleted ? "Instrument removed." : "The instrument could not be removed while a source mapping still uses it.";
        TempData["FlashKind"] = deleted ? "success" : "error";
        return Redirect($"/journal/{JournalId:D}/settings");
    }

    public IActionResult OnPostSaveSierraMapping(Guid? mappingId, string matchRegex, string instrumentCode, decimal? commissionOverride, int position)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var regexIsValid = InstrumentConfiguration.IsValidMatchRegex(matchRegex ?? string.Empty);

        var saved = regexIsValid && _database.SaveInstrumentMapping(mappingId, TradeFoundryConstants.SierraChart, matchRegex ?? string.Empty, instrumentCode ?? string.Empty, commissionOverride, position);
        TempData["FlashMessage"] = saved
            ? "Sierra Chart mapping saved."
            : regexIsValid
                ? "The mapping could not be saved. Check the regex, instrument, commission, and duplicate patterns."
                : "The mapping regex is invalid.";
        TempData["FlashKind"] = saved ? "success" : "error";
        return Redirect($"/journal/{JournalId:D}/settings");
    }

    public IActionResult OnPostDeleteSierraMapping(Guid mappingId)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var deleted = _database.DeleteInstrumentMapping(mappingId);
        TempData["FlashMessage"] = deleted ? "Sierra Chart mapping removed." : "The Sierra Chart mapping could not be removed.";
        TempData["FlashKind"] = deleted ? "success" : "error";
        return Redirect($"/journal/{JournalId:D}/settings");
    }

    public IActionResult OnPostArchive()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        if (_database.GetJournals().Count <= 1)
        {
            TempData["FlashMessage"] = "Keep at least one active journal in this instance.";
            TempData["FlashKind"] = "error";
            return Redirect($"/journal/{JournalId:D}/settings");
        }
        _database.ArchiveJournal(JournalId);
        var next = _database.GetJournals().FirstOrDefault();
        return Redirect(next is null ? "/" : $"/journal/{next.Id:D}/overview");
    }

    private void Load()
    {
        Journals = _database.GetJournals();
        Instruments = _database.GetInstruments();
        SierraChartMappings = _database.GetInstrumentMappings(TradeFoundryConstants.SierraChart);
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return;
        Name = Journal.Name;
        ExecutionContext = Journal.ExecutionContext;
        Labels = Journal.Labels;
        TimeZone = Journal.TimeZone;
        Currency = Journal.Currency;
        GroupingPolicy = Journal.GroupingPolicy;
        StartingEquity = Journal.StartingEquity;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes:N0} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.#} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024d * 1024d):0.#} MB";
        return $"{bytes / (1024d * 1024d * 1024d):0.##} GB";
    }
}

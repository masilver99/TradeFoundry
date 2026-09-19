using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Pages.Settings;

[Authorize]
public class InstrumentsModel : PageModel
{
    private readonly TradeFoundryDb _database;

    public InstrumentsModel(TradeFoundryDb database)
    {
        _database = database;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    public Journal? Journal { get; private set; }
    public IReadOnlyList<InstrumentDefinition> Instruments { get; private set; } = Array.Empty<InstrumentDefinition>();
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

    public IActionResult OnPostSaveInstrument(Guid? instrumentId, string code, decimal? defaultCommission, decimal? exchangeFeePerContract, decimal? nfaFeePerContract, decimal? clearingFeePerContract, decimal pointValue, decimal tickSize)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var saved = _database.SaveInstrument(instrumentId, code, defaultCommission, exchangeFeePerContract, nfaFeePerContract, clearingFeePerContract, pointValue, tickSize);
        TempData["FlashMessage"] = saved
            ? $"Instrument {InstrumentConfiguration.NormalizeCode(code)} saved."
            : "The instrument could not be saved. Check that its code is unique and its point value is positive.";
        TempData["FlashKind"] = saved ? "success" : "error";
        return Redirect($"/journal/{JournalId:D}/settings/instruments");
    }

    public IActionResult OnPostDeleteInstrument(Guid instrumentId)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var deleted = _database.DeleteInstrument(instrumentId);
        TempData["FlashMessage"] = deleted ? "Instrument removed." : "The instrument could not be removed while a source mapping still uses it.";
        TempData["FlashKind"] = deleted ? "success" : "error";
        return Redirect($"/journal/{JournalId:D}/settings/instruments");
    }

    private void Load()
    {
        Journal = _database.GetJournal(JournalId);
        Instruments = _database.GetInstruments();
    }
}

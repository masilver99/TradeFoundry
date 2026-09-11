using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Pages.Settings;

[Authorize]
public class SourcesModel : PageModel
{
    private readonly TradeFoundryDb _database;

    public SourcesModel(TradeFoundryDb database)
    {
        _database = database;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    public Journal? Journal { get; private set; }
    public IReadOnlyList<InstrumentDefinition> Instruments { get; private set; } = Array.Empty<InstrumentDefinition>();
    public IReadOnlyList<InstrumentMapping> SierraChartMappings { get; private set; } = Array.Empty<InstrumentMapping>();
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
        return Redirect($"/journal/{JournalId:D}/settings/sources");
    }

    public IActionResult OnPostDeleteSierraMapping(Guid mappingId)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var deleted = _database.DeleteInstrumentMapping(mappingId);
        TempData["FlashMessage"] = deleted ? "Sierra Chart mapping removed." : "The Sierra Chart mapping could not be removed.";
        TempData["FlashKind"] = deleted ? "success" : "error";
        return Redirect($"/journal/{JournalId:D}/settings/sources");
    }

    private void Load()
    {
        Journal = _database.GetJournal(JournalId);
        Instruments = _database.GetInstruments();
        SierraChartMappings = _database.GetInstrumentMappings(TradeFoundryConstants.SierraChart);
    }
}

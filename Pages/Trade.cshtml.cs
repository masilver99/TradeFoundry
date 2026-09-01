using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
public class TradeModel : PageModel
{
    private readonly TradeFoundryDb _database;

    public TradeModel(TradeFoundryDb database) => _database = database;

    public Trade? Trade { get; private set; }
    public Journal? Journal { get; private set; }
    public IReadOnlyList<Bar> Bars { get; private set; } = Array.Empty<Bar>();
    public string CandleChart => Trade is null ? string.Empty : ChartRenderer.Candles(Trade, Bars);

    public IActionResult OnGet(Guid id)
    {
        Trade = _database.GetTrade(id);
        if (Trade is null) return NotFound();
        Journal = _database.GetJournal(Trade.JournalId);
        if (Journal is null) return NotFound();
        var end = (Trade.ExitUtc ?? Trade.EntryUtc).AddMinutes(30);
        Bars = _database.GetBarsForTrade(Trade.JournalId, Trade.Symbol, Trade.EntryUtc.AddMinutes(-30), end, string.Empty);
        return Page();
    }
}

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
    public ImportBatch? ImportBatch { get; private set; }
    public Journal? Journal { get; private set; }
    public IReadOnlyList<Bar> Bars { get; private set; } = Array.Empty<Bar>();
    public string SourceApplication => ImportBatch is null
        ? "Manual / source-defined"
        : TradeFoundryConstants.SourceApplicationName(ImportBatch.SourceApplication);
    public string CandleChart => Trade is null ? string.Empty : ChartRenderer.Candles(Trade, Bars);

    [BindProperty(SupportsGet = true)] public Guid? JournalId { get; set; }

    public IActionResult OnGet(Guid id, Guid? journalId = null)
    {
        JournalId = journalId ?? JournalId;
        Trade = JournalId.HasValue ? _database.GetTrade(JournalId.Value, id) : _database.GetTrade(id);
        if (Trade is null) return NotFound();
        Journal = _database.GetJournal(Trade.JournalId);
        if (Journal is null) return NotFound();
        ImportBatch = Trade.ImportBatchId.HasValue ? _database.GetImport(Trade.ImportBatchId.Value) : null;
        var end = (Trade.ExitUtc ?? Trade.EntryUtc).AddMinutes(30);
        Bars = _database.GetBarsForTrade(Trade.JournalId, Trade.Symbol, Trade.EntryUtc.AddMinutes(-30), end, string.Empty);
        return Page();
    }
}

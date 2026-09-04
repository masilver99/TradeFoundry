using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
public class IndicatorsModel : PageModel
{
    private readonly TradeFoundryDb _database;

    public IndicatorsModel(TradeFoundryDb database) => _database = database;

    [BindProperty(SupportsGet = true)]
    public Guid JournalId { get; set; }

    public Journal? Journal { get; private set; }
    public JournalOverview? Overview { get; private set; }
    public IReadOnlyList<Trade> Trades { get; private set; } = Array.Empty<Trade>();
    public IReadOnlyList<OrderEvent> OrderEvents { get; private set; } = Array.Empty<OrderEvent>();
    public IReadOnlyList<AccountBalanceEvent> AccountBalances { get; private set; } = Array.Empty<AccountBalanceEvent>();
    public IReadOnlyList<BenchmarkPoint> BenchmarkPoints { get; private set; } = Array.Empty<BenchmarkPoint>();
    public TearSheetIndicatorSet Indicators { get; private set; } = new();

    public IActionResult OnGet()
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return NotFound();

        Overview = _database.GetOverview(JournalId);
        Trades = _database.GetAllTrades(JournalId);
        OrderEvents = _database.GetOrderEvents(JournalId);
        AccountBalances = _database.GetAccountBalanceEvents(JournalId);
        BenchmarkPoints = _database.GetBenchmarkPoints(JournalId);
        Indicators = TearSheetMetrics.Build(Trades, OrderEvents, AccountBalances, BenchmarkPoints, Overview.StartingEquity, Journal.TimeZone, Journal.Currency);
        return Page();
    }
}

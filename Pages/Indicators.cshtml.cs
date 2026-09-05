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
    private readonly BenchmarkRefreshService _benchmarkRefresh;

    public IndicatorsModel(TradeFoundryDb database, BenchmarkRefreshService benchmarkRefresh)
    {
        _database = database;
        _benchmarkRefresh = benchmarkRefresh;
    }

    [BindProperty(SupportsGet = true)]
    public Guid JournalId { get; set; }

    public Journal? Journal { get; private set; }
    public JournalOverview? Overview { get; private set; }
    public IReadOnlyList<Trade> Trades { get; private set; } = Array.Empty<Trade>();
    public IReadOnlyList<OrderEvent> OrderEvents { get; private set; } = Array.Empty<OrderEvent>();
    public IReadOnlyList<AccountBalanceEvent> AccountBalances { get; private set; } = Array.Empty<AccountBalanceEvent>();
    public IReadOnlyList<BenchmarkPoint> BenchmarkPoints { get; private set; } = Array.Empty<BenchmarkPoint>();
    public TearSheetIndicatorSet Indicators { get; private set; } = new();
    public string? BenchmarkRefreshMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return NotFound();

        Overview = _database.GetOverview(JournalId);
        Trades = _database.GetAllTrades(JournalId);
        var refresh = await _benchmarkRefresh.RefreshAsync(JournalId, Trades, force: false, cancellationToken: cancellationToken);
        if (refresh.Failed) BenchmarkRefreshMessage = $"{refresh.Message} Existing cached benchmark data was kept.";
        OrderEvents = _database.GetOrderEvents(JournalId);
        AccountBalances = _database.GetAccountBalanceEvents(JournalId);
        BenchmarkPoints = _database.GetBenchmarkPoints(JournalId, _benchmarkRefresh.DefaultSymbol);
        if (BenchmarkPoints.Count == 0) BenchmarkPoints = _database.GetBenchmarkPoints(JournalId);
        Indicators = TearSheetMetrics.Build(Trades, OrderEvents, AccountBalances, BenchmarkPoints, Overview.StartingEquity, Journal.TimeZone, Journal.Currency);
        return Page();
    }
}

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
    private readonly OrderExecutionSummaryService _orderExecution;

    public IndicatorsModel(TradeFoundryDb database, BenchmarkRefreshService benchmarkRefresh, OrderExecutionSummaryService orderExecution)
    {
        _database = database;
        _benchmarkRefresh = benchmarkRefresh;
        _orderExecution = orderExecution;
    }

    [BindProperty(SupportsGet = true)]
    public Guid JournalId { get; set; }

    public Journal? Journal { get; private set; }
    public JournalOverview? Overview { get; private set; }
    public IReadOnlyList<Trade> Trades { get; private set; } = Array.Empty<Trade>();
    public OrderExecutionCounts OrderExecution { get; private set; } = OrderExecutionCounts.None;
    public IReadOnlyList<AccountBalanceEvent> AccountBalances { get; private set; } = Array.Empty<AccountBalanceEvent>();
    public IReadOnlyList<BenchmarkPoint> BenchmarkPoints { get; private set; } = Array.Empty<BenchmarkPoint>();
    public TearSheetIndicatorSet Indicators { get; private set; } = new();
    public string? BenchmarkRefreshMessage { get; private set; }
    public JournalFilterScope? Filter { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return NotFound();

        Filter = JournalFilterRequest.Resolve(HttpContext, Journal);
        Overview = _database.GetOverview(JournalId, Filter.Filter);
        var allTrades = _database.GetAllTrades(JournalId);
        Trades = Filter.Apply(allTrades);
        var refresh = await _benchmarkRefresh.RefreshAsync(JournalId, allTrades, force: false, cancellationToken: cancellationToken);
        if (refresh.Failed) BenchmarkRefreshMessage = $"{refresh.Message} Existing cached benchmark data was kept.";
        OrderExecution = _orderExecution.Get(JournalId, Filter, Trades);
        AccountBalances = Filter.IsActive ? Array.Empty<AccountBalanceEvent>() : _database.GetAccountBalanceEvents(JournalId);
        BenchmarkPoints = _database.GetBenchmarkPoints(JournalId, _benchmarkRefresh.DefaultSymbol);
        if (BenchmarkPoints.Count == 0) BenchmarkPoints = _database.GetBenchmarkPoints(JournalId);
        Indicators = TearSheetMetrics.Build(Trades, OrderExecution,AccountBalances, BenchmarkPoints, Overview.StartingEquity, Journal.TimeZone, Journal.Currency);
        return Page();
    }
}

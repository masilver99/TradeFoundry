using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Globalization;
using System.Text.Json;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
public class AnalyticsModel : PageModel
{
    private static readonly JsonSerializerOptions PnlCalendarJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TradeFoundryDb _database;
    private readonly BenchmarkRefreshService _benchmarkRefresh;
    private readonly JournalAnalysisService _analysis;

    public AnalyticsModel(TradeFoundryDb database, BenchmarkRefreshService benchmarkRefresh, JournalAnalysisService analysis)
    {
        _database = database;
        _benchmarkRefresh = benchmarkRefresh;
        _analysis = analysis;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    public Journal? Journal { get; private set; }
    public JournalOverview? Overview { get; private set; }
    public TearSheetIndicatorSet Indicators { get; private set; } = new();
    public TearSheetPeriodSummary PeriodSummary { get; private set; } = new();
    public TearSheetPeriodBreakdown PeriodBreakdown { get; private set; } = new();
    public IReadOnlyList<Trade> Trades { get; private set; } = Array.Empty<Trade>();
    public IReadOnlyList<OrderEvent> OrderEvents { get; private set; } = Array.Empty<OrderEvent>();
    public IReadOnlyList<AccountBalanceEvent> AccountBalances { get; private set; } = Array.Empty<AccountBalanceEvent>();
    public IReadOnlyList<BenchmarkPoint> BenchmarkPoints { get; private set; } = Array.Empty<BenchmarkPoint>();
    public BenchmarkSeriesStatus? BenchmarkStatus { get; private set; }
    public IReadOnlyList<PnlCalendarMonth> PnlCalendarMonths { get; private set; } = Array.Empty<PnlCalendarMonth>();
    public string BenchmarkSymbol => _benchmarkRefresh.DefaultSymbol;
    public string? BenchmarkRefreshMessage { get; private set; }
    public string BenchmarkRefreshMessageKind { get; private set; } = "warning";
    public string PnlCalendarCurrency => Journal?.Currency ?? "USD";
    public string PnlCalendarJson => JsonSerializer.Serialize(PnlCalendarMonths, PnlCalendarJsonOptions);
    public IReadOnlyList<string> DataAvailabilityWarnings { get; private set; } = Array.Empty<string>();

    public string EquityChart => ChartRenderer.TearSheetEquity(Trades, AccountBalances, Overview?.StartingEquity);
    public string FeeDragChart => ChartRenderer.TearSheetFeeDrag(Trades);
    public string ReturnsChart => ChartRenderer.TearSheetReturns(Trades, BenchmarkPoints, AccountBalances, Overview?.StartingEquity);
    public string MonteCarloChart => ChartRenderer.TearSheetMonteCarlo(Trades, Overview?.StartingEquity);
    public string DrawdownChart => ChartRenderer.TearSheetDrawdown(Trades, AccountBalances, Overview?.StartingEquity);
    public string WorstDrawdownChart => ChartRenderer.TearSheetWorstDrawdownPeriods(Trades, AccountBalances, Overview?.StartingEquity);
    public string DrawdownRecoveryChart => ChartRenderer.TearSheetDrawdownRecovery(Trades, AccountBalances, Overview?.StartingEquity);
    public string RollingAnalyticsChart => ChartRenderer.TearSheetRolling(Trades);
    public string RollingVolatilityChart => ChartRenderer.TearSheetRollingVolatility(Trades, AccountBalances, BenchmarkPoints, Overview?.StartingEquity);
    public string RollingSharpeChart => ChartRenderer.TearSheetRollingSharpe(Trades, AccountBalances, Overview?.StartingEquity);
    public string RollingSortinoChart => ChartRenderer.TearSheetRollingSortino(Trades, AccountBalances, Overview?.StartingEquity);
    public string DailyChart => ChartRenderer.TearSheetDailyPnl(Trades);
    public string MonthlyChart => ChartRenderer.MonthlyPnl(Trades, Journal?.TimeZone);
    public string AnnualReturnsChart => ChartRenderer.TearSheetAnnualReturns(Trades, AccountBalances, BenchmarkPoints, Overview?.StartingEquity);
    public string MonthlyReturnsDistributionChart => ChartRenderer.TearSheetMonthlyReturnsDistribution(Trades, AccountBalances, BenchmarkPoints, Overview?.StartingEquity);
    public string DailyActiveReturnsChart => ChartRenderer.TearSheetDailyActiveReturns(Trades, AccountBalances, BenchmarkPoints, Overview?.StartingEquity);
    public string WaterfallChart => ChartRenderer.TearSheetWaterfall(Trades);
    public string DailyDistributionChart => ChartRenderer.TearSheetDailyDistribution(Trades);
    public string PnlDistributionChart => ChartRenderer.TearSheetPnlDistribution(Trades);
    public string WinnersLosersChart => ChartRenderer.TearSheetWinLossDistribution(Trades);
    public string MfeMaeChart => ChartRenderer.TearSheetMfeMae(Trades);
    public string MaeWinnersChart => ChartRenderer.TearSheetMaeWinners(Trades);
    public string DurationProfitChart => ChartRenderer.TearSheetDurationProfit(Trades);
    public string TimeBucketChart => ChartRenderer.TearSheetTimeBucketExpectancy(Trades, Journal?.TimeZone);
    public string ExcursionPercentileChart => ChartRenderer.TearSheetExcursionPercentile(Trades);
    public string HoldingEfficiencyChart => ChartRenderer.TearSheetHoldingTimeEfficiency(Trades);
    public string ExitEfficiencyChart => ChartRenderer.TearSheetExitEfficiency(Trades);
    public string TimingChart => ChartRenderer.TearSheetTimingHeatmap(Trades, Journal?.TimeZone);
    public string PositionSizeChart => ChartRenderer.TearSheetPositionSize(Trades);
    public string ConcentrationChart => ChartRenderer.TearSheetProfitConcentration(Trades);
    public string StreakStateChart => ChartRenderer.TearSheetStreakState(Trades);
    public string WinRateChart => ChartRenderer.TearSheetWinRateOverTime(Trades);
    public string DirectionMixChart => ChartRenderer.TearSheetDirectionMix(Trades);
    public string SessionMixChart => ChartRenderer.TearSheetSessionMix(Trades, Journal?.TimeZone);
    public string OutcomeMixChart => ChartRenderer.TearSheetOutcomeMix(Trades);
    public string MonthlyHeatmapChart => ChartRenderer.TearSheetMonthlyReturnHeatmap(Trades, AccountBalances, Overview?.StartingEquity);
    public string RiskDisciplineRollingRescueChart => ChartRenderer.TearSheetRiskDisciplineRollingRescue(Trades);
    public string RiskDisciplineRollingHeatChart => ChartRenderer.TearSheetRiskDisciplineRollingHeat(Trades);
    public string RiskDisciplineRollingViolationsChart => ChartRenderer.TearSheetRiskDisciplineRollingViolations(Trades);
    public string RiskDisciplineMonthlyChart => ChartRenderer.TearSheetRiskDisciplineMonthly(Trades, Journal?.TimeZone);
    public string RMultipleChart => ChartRenderer.RMultipleDistribution(Trades);
    public string ExitTypeChart => ChartRenderer.ExitTypeAnalysis(Trades);
    public string OrderExecutionChart => ChartRenderer.OrderExecution(OrderEvents);

    public sealed record PnlCalendarDay(string Date, int Day, decimal NetPnl, int TradeCount);

    public sealed record PnlCalendarMonth(
        string Key,
        int Year,
        int Month,
        string Label,
        decimal NetPnl,
        int TradeCount,
        IReadOnlyList<PnlCalendarDay> Days);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return NotFound();

        Overview = _database.GetOverview(JournalId);
        Trades = _database.GetAllTrades(JournalId);
        var refresh = await _benchmarkRefresh.RefreshAsync(JournalId, Trades, force: false, cancellationToken: cancellationToken);
        if (refresh.Failed)
        {
            BenchmarkRefreshMessage = $"{refresh.Message} Existing cached benchmark data was kept.";
            BenchmarkRefreshMessageKind = "warning";
        }

        OrderEvents = _database.GetOrderEvents(JournalId);
        AccountBalances = _database.GetAccountBalanceEvents(JournalId);
        LoadBenchmarkPoints();
        BenchmarkStatus = _database.GetBenchmarkSeriesStatus(JournalId, BenchmarkSymbol);
        Indicators = TearSheetMetrics.Build(Trades, OrderEvents, AccountBalances, BenchmarkPoints, Overview?.StartingEquity, Journal.TimeZone, Journal.Currency);
        var periodData = TearSheetPeriods.Build(Trades, Journal.TimeZone);
        PeriodSummary = periodData.Summary;
        PeriodBreakdown = periodData.Breakdown;
        DataAvailabilityWarnings = _analysis.GetOverview(JournalId, null).DataGaps;
        BuildPnlCalendar();

        if (TempData["BenchmarkFlashMessage"] is string flashMessage && !string.IsNullOrWhiteSpace(flashMessage))
        {
            BenchmarkRefreshMessage = flashMessage;
            BenchmarkRefreshMessageKind = TempData["BenchmarkFlashKind"] as string ?? "warning";
        }
        return Page();
    }

    public async Task<IActionResult> OnPostRefreshBenchmarkAsync(CancellationToken cancellationToken)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var trades = _database.GetAllTrades(JournalId);
        var refresh = await _benchmarkRefresh.RefreshAsync(JournalId, trades, force: true, cancellationToken);
        TempData["BenchmarkFlashMessage"] = refresh.Message;
        TempData["BenchmarkFlashKind"] = refresh.Succeeded ? "success" : "warning";
        return Redirect($"/journal/{JournalId:D}/analytics#performance-benchmark");
    }

    private void LoadBenchmarkPoints()
    {
        BenchmarkPoints = _database.GetBenchmarkPoints(JournalId, BenchmarkSymbol);
        if (BenchmarkPoints.Count == 0) BenchmarkPoints = _database.GetBenchmarkPoints(JournalId);
    }

    private void BuildPnlCalendar()
    {
        if (Journal is null) return;

        var daily = DailyTradeAggregation.Build(Trades, Journal.TimeZone)
            .Where(day => day.CompletedTradeCount > 0)
            .ToDictionary(day => day.Date, day => (NetPnl: day.RealizedNetPnl, TradeCount: day.CompletedTradeCount));

        PnlCalendarMonths = daily.Keys
            .GroupBy(date => new { date.Year, date.Month })
            .OrderBy(group => group.Key.Year)
            .ThenBy(group => group.Key.Month)
            .Select(group =>
            {
                var monthStart = new DateOnly(group.Key.Year, group.Key.Month, 1);
                var days = Enumerable.Range(1, DateTime.DaysInMonth(group.Key.Year, group.Key.Month))
                    .Select(dayNumber =>
                    {
                        var date = new DateOnly(group.Key.Year, group.Key.Month, dayNumber);
                        var summary = daily.TryGetValue(date, out var value)
                            ? value
                            : (NetPnl: 0m, TradeCount: 0);
                        return new PnlCalendarDay(
                            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            dayNumber,
                            summary.NetPnl,
                            summary.TradeCount);
                    })
                    .ToArray();

                return new PnlCalendarMonth(
                    monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    group.Key.Year,
                    group.Key.Month,
                    monthStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                    days.Sum(day => day.NetPnl),
                    days.Sum(day => day.TradeCount),
                    days);
            })
            .ToArray();

    }
}

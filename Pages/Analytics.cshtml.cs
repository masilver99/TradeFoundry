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
    private static readonly JsonSerializerOptions MaeCalendarJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TradeFoundryDb _database;
    private readonly BenchmarkRefreshService _benchmarkRefresh;
    private readonly JournalAnalysisService _analysis;
    private readonly OrderExecutionSummaryService _orderExecution;

    public AnalyticsModel(TradeFoundryDb database, BenchmarkRefreshService benchmarkRefresh, JournalAnalysisService analysis, OrderExecutionSummaryService orderExecution)
    {
        _database = database;
        _benchmarkRefresh = benchmarkRefresh;
        _analysis = analysis;
        _orderExecution = orderExecution;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    public Journal? Journal { get; private set; }
    public JournalOverview? Overview { get; private set; }
    public JournalFilterScope? Filter { get; private set; }
    public TearSheetIndicatorSet Indicators { get; private set; } = new();
    public EdgePersistenceMetrics EdgePersistence { get; private set; } = new();
    public TearSheetPeriodSummary PeriodSummary { get; private set; } = new();
    public TearSheetPeriodBreakdown PeriodBreakdown { get; private set; } = new();
    public IReadOnlyList<Trade> Trades { get; private set; } = Array.Empty<Trade>();
    public OrderExecutionCounts OrderExecution { get; private set; } = OrderExecutionCounts.None;
    public IReadOnlyList<AccountBalanceEvent> AccountBalances { get; private set; } = Array.Empty<AccountBalanceEvent>();
    public IReadOnlyList<BenchmarkPoint> BenchmarkPoints { get; private set; } = Array.Empty<BenchmarkPoint>();
    public BenchmarkSeriesStatus? BenchmarkStatus { get; private set; }
    public IReadOnlyList<PnlCalendarMonth> PnlCalendarMonths { get; private set; } = Array.Empty<PnlCalendarMonth>();
    public IReadOnlyList<MaeCalendarMonth> MaeCalendarMonths { get; private set; } = Array.Empty<MaeCalendarMonth>();
    public IReadOnlyList<PnlHeatmapYear> PnlHeatmapYears { get; private set; } = Array.Empty<PnlHeatmapYear>();
    public MaeTargetSettings MaeTargets { get; private set; } = new();
    public string BenchmarkSymbol => _benchmarkRefresh.DefaultSymbol;
    public string? BenchmarkRefreshMessage { get; private set; }
    public string BenchmarkRefreshMessageKind { get; private set; } = "warning";
    public string PnlCalendarCurrency => Journal?.Currency ?? "USD";
    public string PnlCalendarJson => JsonSerializer.Serialize(PnlCalendarMonths, PnlCalendarJsonOptions);
    public string MaeCalendarJson => JsonSerializer.Serialize(MaeCalendarMonths, MaeCalendarJsonOptions);
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
    public string TradePnlRangeChart => ChartRenderer.TearSheetTradePnlRangeDistribution(Trades);
    public string MfeMaeChart => ChartRenderer.TearSheetMfeMae(Trades);
    public string MaeWinnersChart => ChartRenderer.TearSheetMaeWinners(Trades);
    public string DurationProfitChart => ChartRenderer.TearSheetDurationProfit(Trades);
    public string TimeBucketChart => ChartRenderer.TearSheetTimeBucketExpectancy(Trades, Journal?.TimeZone);
    public string ExcursionPercentileChart => ChartRenderer.TearSheetExcursionPercentile(Trades);
    public string HoldingEfficiencyChart => ChartRenderer.TearSheetHoldingTimeEfficiency(Trades);
    public string ExitEfficiencyChart => ChartRenderer.TearSheetExitEfficiency(Trades);
    public string TimingChart => ChartRenderer.TearSheetTimingHeatmap(Trades, Journal?.TimeZone);
    public string PositionSizeChart => ChartRenderer.TearSheetPositionSize(Trades);
    public string TradeSizeDistributionChart => ChartRenderer.TearSheetTradeSizeDistribution(Trades);
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
    private DailyHeatSummary? _dailyHeat;
    public DailyHeatSummary DailyHeat => _dailyHeat ??= DailyHeatMetrics.Build(Trades, Journal?.TimeZone);
    public string DailyHeatRangeChart => ChartRenderer.DailyHeatRange(DailyHeat);
    public string DailyPnlPerHeatChart => ChartRenderer.DailyPnlPerHeat(DailyHeat);
    public string EdgePersistenceRollingExpectancyChart => ChartRenderer.TearSheetEdgePersistenceRollingExpectancy(EdgePersistence);
    public string EdgePersistenceRollingQualityChart => ChartRenderer.TearSheetEdgePersistenceRollingQuality(EdgePersistence);
    public string EdgePersistenceRollingMaeChart => ChartRenderer.TearSheetEdgePersistenceRollingMae(EdgePersistence);
    public string EdgePersistenceRollingLossChart => ChartRenderer.TearSheetEdgePersistenceRollingLoss(EdgePersistence);
    public string EdgePersistenceBlocksChart => ChartRenderer.TearSheetEdgePersistenceBlocks(EdgePersistence);
    public string RMultipleChart => ChartRenderer.RMultipleDistribution(Trades);
    public string ExitTypeChart => ChartRenderer.ExitTypeAnalysis(Trades);

    public sealed record PnlCalendarDay(string Date, int Day, decimal NetPnl, int TradeCount, decimal Points, decimal? MaeCurrency, decimal? ProfitPercent, int? Rating = null, string? RatingDescription = null);

    public sealed record PnlCalendarMonth(
        string Key,
        int Year,
        int Month,
        string Label,
        decimal NetPnl,
        int TradeCount,
        IReadOnlyList<PnlCalendarDay> Days)
    {
        /// <summary>Days rated 1–2, which the calendar marks with a red corner.</summary>
        public int PoorDayCount => Days.Count(day => day.Rating is <= 2);
        /// <summary>Days rated 5, which the calendar marks with a star.</summary>
        public int ExcellentDayCount => Days.Count(day => day.Rating == DailyRating.MaxRating);
        public int RatedDayCount => Days.Count(day => day.Rating.HasValue);
        public decimal? AverageRating => RatedDayCount == 0 ? null : Math.Round((decimal)Days.Where(day => day.Rating.HasValue).Average(day => day.Rating!.Value), 1);
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return NotFound();

        Filter = JournalFilterRequest.Resolve(HttpContext, Journal);
        Overview = _database.GetOverview(JournalId, Filter.Filter);
        // Benchmark caching follows the whole journal; the page itself shows only the filtered trades.
        var allTrades = _database.GetAllTrades(JournalId);
        Trades = Filter.Apply(allTrades);
        var benchmarkRefreshes = new List<(string Symbol, BenchmarkRefreshResult Result)>();
        foreach (var symbol in BenchmarkSymbolsForTrades(Trades))
        {
            var refresh = await _benchmarkRefresh.RefreshAsync(JournalId, allTrades, symbol, force: false, cancellationToken: cancellationToken);
            benchmarkRefreshes.Add((symbol, refresh));
        }

        var benchmarkFailures = benchmarkRefreshes.Where(item => item.Result.Failed).ToArray();
        if (benchmarkFailures.Length > 0)
        {
            BenchmarkRefreshMessage = string.Join(" ", benchmarkFailures.Select(item => $"{item.Symbol}: {item.Result.Message}"));
            BenchmarkRefreshMessageKind = "warning";
        }

        OrderExecution = _orderExecution.Get(JournalId, Filter, Trades);
        // Actual balance observations belong to the whole account, so a filtered view measures its trades from the starting balance.
        AccountBalances = Filter.IsActive ? Array.Empty<AccountBalanceEvent>() : _database.GetAccountBalanceEvents(JournalId);
        LoadBenchmarkPoints();
        BenchmarkStatus = _database.GetBenchmarkSeriesStatus(JournalId, BenchmarkSymbol);
        Indicators = TearSheetMetrics.Build(Trades, OrderExecution,AccountBalances, BenchmarkPoints, Overview?.StartingEquity, Journal.TimeZone, Journal.Currency);
        EdgePersistence = EdgePersistenceCalculator.Build(Trades, Journal.TimeZone);
        var periodData = TearSheetPeriods.Build(Trades, Journal.TimeZone);
        PeriodSummary = periodData.Summary;
        PeriodBreakdown = periodData.Breakdown;
        DataAvailabilityWarnings = _analysis.GetDataGaps(JournalId, allTrades);
        BuildPnlCalendar();
        MaeTargets = _database.GetMaeTargetSettings(JournalId);
        MaeCalendarMonths = MaeCalendarBuilder.Build(Trades, Journal.TimeZone, MaeTargets, _database.GetTradeReviewAnnotations(JournalId));

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
        var refreshes = new List<(string Symbol, BenchmarkRefreshResult Result)>();
        foreach (var symbol in BenchmarkSymbolsForTrades(trades))
        {
            var refresh = await _benchmarkRefresh.RefreshAsync(JournalId, trades, symbol, force: true, cancellationToken);
            refreshes.Add((symbol, refresh));
        }

        var failures = refreshes.Where(item => item.Result.Failed).ToArray();
        TempData["BenchmarkFlashMessage"] = failures.Length > 0
            ? string.Join(" ", failures.Select(item => $"{item.Symbol}: {item.Result.Message}"))
            : string.Join(" ", refreshes.Select(item => item.Result.Message));
        TempData["BenchmarkFlashKind"] = failures.Length == 0 ? "success" : "warning";
        return Redirect($"/journal/{JournalId:D}/analytics#performance-benchmark");
    }

    private void LoadBenchmarkPoints()
    {
        BenchmarkPoints = BenchmarkSymbolsForTrades(Trades)
            .SelectMany(symbol => _database.GetBenchmarkPoints(JournalId, symbol))
            .OrderBy(point => point.Symbol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(point => point.EventUtc)
            .ToArray();
        if (BenchmarkPoints.Count == 0) BenchmarkPoints = _database.GetBenchmarkPoints(JournalId);
    }

    private IReadOnlyList<string> BenchmarkSymbolsForTrades(IEnumerable<Trade> trades)
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BenchmarkSymbol };
        foreach (var trade in trades.Where(item => item.ExitUtc.HasValue))
        {
            var instrument = string.IsNullOrWhiteSpace(trade.Instrument) ? trade.Symbol : trade.Instrument;
            var root = InstrumentCatalog.ExtractRoot(instrument);
            if (root is "NQ" or "MNQ") symbols.Add("^NDX");
            if (root is "RTY" or "M2K") symbols.Add("^RUT");
        }

        return symbols
            .OrderBy(symbol => string.Equals(symbol, BenchmarkSymbol, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(symbol => symbol, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void BuildPnlCalendar()
    {
        if (Journal is null) return;

        // Ratings belong to the day, not to the filtered trades, so they show whatever the filter.
        var ratings = _database.GetDailyRatings(JournalId);
        var startingEquity = Overview?.StartingEquity;
        var hasStartingEquity = startingEquity is > 0m;

        var daily = DailyTradeAggregation.Build(Trades, Journal.TimeZone)
            .Where(day => day.CompletedTradeCount > 0)
            .ToDictionary(day => day.Date, day =>
            {
                return (
                    NetPnl: day.RealizedNetPnl,
                    TradeCount: day.CompletedTradeCount,
                    Points: day.RealizedGrossPoints,
                    MaeCurrency: day.RealizedMaeCurrency,
                    ProfitPercent: hasStartingEquity ? day.RealizedNetPnl / startingEquity!.Value * 100m : (decimal?)null);
            });

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
                            : (NetPnl: 0m, TradeCount: 0, Points: 0m, MaeCurrency: (decimal?)null, ProfitPercent: (decimal?)null);
                        var rating = ratings.TryGetValue(date, out var dayRating) ? dayRating : null;
                        return new PnlCalendarDay(
                            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            dayNumber,
                            summary.NetPnl,
                            summary.TradeCount,
                            summary.Points,
                            summary.MaeCurrency,
                            summary.ProfitPercent,
                            rating?.Rating,
                            string.IsNullOrWhiteSpace(rating?.Description) ? null : rating.Description);
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

        BuildPnlHeatmap(daily.ToDictionary(item => item.Key, item => (item.Value.NetPnl, item.Value.TradeCount)));
    }

    private void BuildPnlHeatmap(IReadOnlyDictionary<DateOnly, (decimal NetPnl, int TradeCount)> daily)
    {
        if (daily.Count == 0) { PnlHeatmapYears = Array.Empty<PnlHeatmapYear>(); return; }
        var years = daily.Keys.Select(date => date.Year).Distinct().OrderDescending().ToArray();
        var yearDates = years.SelectMany(year => Enumerable.Range(0, DateTime.IsLeapYear(year) ? 366 : 365).Select(offset => new DateOnly(year, 1, 1).AddDays(offset)));
        // Journal notes belong to the day, like ratings, so notes-only days show whatever the filter.
        var noteDates = _database.GetDailyJournalEntryDates(JournalId, yearDates.Where(date => !daily.ContainsKey(date)));

        // The journal's first year starts at the week of its first trade instead of an empty January.
        var firstTrade = daily.Keys.Min();
        var yearStarts = years.ToDictionary(year => year, year => year == firstTrade.Year ? firstTrade : new DateOnly(year, 1, 1));
        // Columns are Sunday-to-Saturday weeks, clipped to the shown part of the year.
        var weekRanges = years.ToDictionary(year => year, year =>
        {
            var ranges = new List<(DateOnly Start, DateOnly End)>();
            var yearEnd = new DateOnly(year, 12, 31);
            var start = yearStarts[year].AddDays(-(int)yearStarts[year].DayOfWeek);
            if (start.Year < year) start = new DateOnly(year, 1, 1);
            while (start <= yearEnd)
            {
                var end = start.AddDays(6 - (int)start.DayOfWeek);
                if (end > yearEnd) end = yearEnd;
                ranges.Add((start, end));
                start = end.AddDays(1);
            }
            return ranges;
        });
        var weekTotals = weekRanges.ToDictionary(item => item.Key, item => item.Value.Select(range =>
        {
            var inWeek = daily.Where(day => day.Key >= range.Start && day.Key <= range.End).Select(day => day.Value).ToArray();
            return (range.Start, range.End, NetPnl: inWeek.Sum(day => day.NetPnl), TradeCount: inWeek.Sum(day => day.TradeCount));
        }).ToArray());

        var dayScale = HeatScale(daily.Values.Select(day => day.NetPnl));
        var weekScale = HeatScale(weekTotals.Values.SelectMany(weeks => weeks).Where(week => week.TradeCount > 0).Select(week => week.NetPnl));

        PnlHeatmapYears = years.Select(year =>
        {
            var days = new Dictionary<DateOnly, PnlHeatmapDay>();
            foreach (var (date, day) in daily.Where(item => item.Key.Year == year))
                days[date] = new PnlHeatmapDay(date, day.NetPnl, day.TradeCount, HeatClass(day.NetPnl, dayScale));
            foreach (var date in noteDates.Where(date => date.Year == year && date >= yearStarts[year])) days[date] = new PnlHeatmapDay(date, 0m, 0, "note");
            var weeks = weekTotals[year]
                .Select(week => new PnlHeatmapWeek(week.Start, week.End, week.NetPnl, week.TradeCount, week.TradeCount == 0 ? string.Empty : HeatClass(week.NetPnl, weekScale)))
                .ToArray();
            return new PnlHeatmapYear(year, weeks.Sum(week => week.NetPnl), weeks.Sum(week => week.TradeCount), days, weeks);
        }).ToArray();
    }

    // Shade by magnitude against the 90th-percentile value so a single outlier does not wash out the rest.
    private static decimal HeatScale(IEnumerable<decimal> values)
    {
        var magnitudes = values.Where(value => value != 0m).Select(Math.Abs).Order().ToArray();
        return magnitudes.Length == 0 ? 1m : magnitudes[(int)Math.Ceiling(magnitudes.Length * 0.9m) - 1];
    }

    private static string HeatClass(decimal netPnl, decimal scale)
    {
        if (netPnl == 0m) return "neutral";
        var level = Math.Clamp((int)Math.Ceiling(Math.Abs(netPnl) / scale * 4), 1, 4);
        return $"{(netPnl > 0m ? "positive" : "negative")} l{level}";
    }

    public sealed record PnlHeatmapDay(DateOnly Date, decimal NetPnl, int TradeCount, string CssClass);

    public sealed record PnlHeatmapWeek(DateOnly Start, DateOnly End, decimal NetPnl, int TradeCount, string CssClass);

    /// <summary>One year of the heat map; each entry in <see cref="Weeks"/> is one grid column.</summary>
    public sealed record PnlHeatmapYear(int Year, decimal NetPnl, int TradeCount, IReadOnlyDictionary<DateOnly, PnlHeatmapDay> Days, IReadOnlyList<PnlHeatmapWeek> Weeks);

}

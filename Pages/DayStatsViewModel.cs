using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

/// <summary>The day statistics panel shared by Trade Review and the Daily Journal.</summary>
public sealed class DayStatsViewModel
{
    public Guid JournalId { get; init; }
    public DayStatisticsReport Report { get; init; } = new();
    /// <summary>Distinguishes element ids when more than one panel could be on a page.</summary>
    public string IdPrefix { get; init; } = "day-stats";
    public bool ChartsOpen { get; init; } = true;
    /// <summary>Where the panel is shown; each place remembers its own open or closed charts.</summary>
    public string Context { get; init; } = "day";
    public bool FilterActive { get; init; }
    public string IntradayEquityChart { get; init; } = string.Empty;
    public string HeatWindowChart { get; init; } = string.Empty;
    public string TradeNumberChart { get; init; } = string.Empty;
    public string SessionChart { get; init; } = string.Empty;
    public string GreenRateChart { get; init; } = string.Empty;
    public string TradeCountChart { get; init; } = string.Empty;
    public string RatingChart { get; init; } = string.Empty;

    /// <param name="history">Every trading day in the journal filter.</param>
    /// <param name="dayTrades">The day's trades as the page shows them; completed ones feed the statistics.</param>
    public static DayStatsViewModel Build(TradeFoundryDb database, Journal journal, DateOnly date, IReadOnlyList<DailyTradeSummary> history, IEnumerable<Trade> dayTrades, int openTradeCount, bool filterActive, string idPrefix = "day-stats", string context = "day", bool chartsOpen = true)
    {
        var report = DayStatistics.Build(new DayStatisticsInput
        {
            Date = date,
            DayTrades = dayTrades.Where(trade => trade.ExitUtc.HasValue && trade.Status.Equals("closed", StringComparison.OrdinalIgnoreCase)).ToArray(),
            OpenTradeCount = openTradeCount,
            History = history,
            Ratings = database.GetDailyRatings(journal.Id),
            Limits = database.GetDailyLimitSettings(journal.Id),
            TimeZoneId = journal.TimeZone
        });
        return new DayStatsViewModel
        {
            JournalId = journal.Id,
            Report = report,
            IdPrefix = idPrefix,
            Context = context,
            ChartsOpen = chartsOpen,
            FilterActive = filterActive,
            IntradayEquityChart = ChartRenderer.DayEquity(report),
            HeatWindowChart = ChartRenderer.DayHeatWindow(report),
            TradeNumberChart = ChartRenderer.DayTradeNumber(report),
            SessionChart = ChartRenderer.DaySessionCurve(report),
            GreenRateChart = ChartRenderer.DayGreenRate(report),
            TradeCountChart = ChartRenderer.DayTradeCountScatter(report),
            RatingChart = ChartRenderer.DayRatingScatter(report)
        };
    }
}

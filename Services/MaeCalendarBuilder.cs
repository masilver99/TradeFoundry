using System.Globalization;
using TradeFoundry.Core;

namespace TradeFoundry.Services;

public sealed record MaeCalendarTrade(
    string Date,
    string EntryTime,
    string ReviewKey,
    string Instrument,
    int Quantity,
    decimal MaePerContract,
    decimal PositionMae,
    decimal TargetPerContract,
    decimal NetPnl,
    decimal? PlannedRiskCurrency,
    decimal? MaeRiskMultiple,
    string Setup);

public sealed record MaeCalendarDay(
    string Date,
    int Day,
    int TradeCount,
    int MaeObservedTradeCount,
    int TargetConfiguredTradeCount,
    int TargetEvaluatedTradeCount,
    decimal? WorstMaePerContract,
    decimal? WorstPositionMae,
    string? WorstTradeReviewKey,
    int BreachCount);

public sealed record MaeCalendarMonth(
    string Key,
    int Year,
    int Month,
    string Label,
    int TradeCount,
    int MaeObservedTradeCount,
    int TargetConfiguredTradeCount,
    int TargetEvaluatedTradeCount,
    decimal? WorstMaePerContract,
    int BreachCount,
    IReadOnlyList<MaeCalendarDay> Days,
    IReadOnlyList<MaeCalendarTrade> Breaches);

public static class MaeCalendarBuilder
{
    public static IReadOnlyList<MaeCalendarMonth> Build(
        IEnumerable<Trade> sourceTrades,
        string timeZoneId,
        MaeTargetSettings targets,
        IReadOnlyDictionary<string, TradeReviewAnnotation>? annotations = null)
    {
        annotations ??= new Dictionary<string, TradeReviewAnnotation>(StringComparer.Ordinal);
        var daily = DailyTradeAggregation.Build(sourceTrades, timeZoneId)
            .Where(day => day.CompletedTradeCount > 0)
            .ToDictionary(day => day.Date, day =>
            {
                var trades = day.CompletedTrades.Select(trade =>
                {
                    var annotation = annotations.GetValueOrDefault(trade.ReviewKey);
                    var metrics = MaeReviewMetrics.Build(trade, targets, annotation?.PlannedRiskCurrency, annotation?.PlannedRiskPoints);
                    MaeCalendarTrade? breach = metrics.IsBreach == true
                        ? new MaeCalendarTrade(
                            day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            TimeZoneCatalog.Format(trade.EntryUtc, timeZoneId, "h:mm tt"),
                            trade.ReviewKey,
                            metrics.Instrument,
                            metrics.ContractCount,
                            metrics.MaePerContract!.Value,
                            metrics.PositionMae!.Value,
                            metrics.TargetPerContract!.Value,
                            trade.NetPnl,
                            metrics.PlannedRiskCurrency,
                            metrics.MaeRiskMultiple,
                            annotation?.Setup ?? string.Empty)
                        : null;
                    return (Trade: trade, Metrics: metrics, Breach: breach);
                }).ToArray();
                var worstTrades = trades
                    .Where(item => item.Metrics.MaePerContract.HasValue)
                    .OrderByDescending(item => item.Metrics.MaePerContract)
                    .ThenBy(item => item.Trade.EntryUtc)
                    .ToArray();
                var worst = worstTrades.FirstOrDefault();
                return (
                    TradeCount: day.CompletedTradeCount,
                    MaeObservedTradeCount: trades.Count(item => item.Metrics.MaePerContract.HasValue),
                    TargetConfiguredTradeCount: trades.Count(item => item.Metrics.TargetPerContract.HasValue),
                    TargetEvaluatedTradeCount: trades.Count(item => item.Metrics.IsBreach.HasValue),
                    WorstMaePerContract: worstTrades.Length > 0 ? worst.Metrics.MaePerContract : null,
                    WorstPositionMae: worstTrades.Length > 0 ? worst.Metrics.PositionMae : null,
                    WorstTradeReviewKey: worstTrades.Length > 0 ? worst.Trade.ReviewKey : null,
                    Breaches: trades.Where(item => item.Breach is not null).Select(item => item.Breach!).ToArray());
            });

        return daily.Keys
            .GroupBy(date => new { date.Year, date.Month })
            .OrderBy(group => group.Key.Year)
            .ThenBy(group => group.Key.Month)
            .Select(group => BuildMonth(group.Key.Year, group.Key.Month, group.ToArray(), daily))
            .ToArray();
    }

    private static MaeCalendarMonth BuildMonth(
        int year,
        int month,
        DateOnly[] activeDates,
        IReadOnlyDictionary<DateOnly, (int TradeCount, int MaeObservedTradeCount, int TargetConfiguredTradeCount, int TargetEvaluatedTradeCount, decimal? WorstMaePerContract, decimal? WorstPositionMae, string? WorstTradeReviewKey, MaeCalendarTrade[] Breaches)> daily)
    {
        var monthStart = new DateOnly(year, month, 1);
        var days = Enumerable.Range(1, DateTime.DaysInMonth(year, month))
            .Select(dayNumber =>
            {
                var date = new DateOnly(year, month, dayNumber);
                if (!daily.TryGetValue(date, out var value))
                    return new MaeCalendarDay(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), dayNumber, 0, 0, 0, 0, null, null, null, 0);
                return new MaeCalendarDay(
                    date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    dayNumber,
                    value.TradeCount,
                    value.MaeObservedTradeCount,
                    value.TargetConfiguredTradeCount,
                    value.TargetEvaluatedTradeCount,
                    value.WorstMaePerContract,
                    value.WorstPositionMae,
                    value.WorstTradeReviewKey,
                    value.Breaches.Length);
            })
            .ToArray();

        var breaches = activeDates
            .SelectMany(date => daily[date].Breaches)
            .OrderByDescending(item => item.MaePerContract - item.TargetPerContract)
            .ThenByDescending(item => item.MaePerContract)
            .ThenBy(item => item.Date, StringComparer.Ordinal)
            .ThenBy(item => item.EntryTime, StringComparer.Ordinal)
            .ToArray();
        var tradeCount = activeDates.Sum(date => daily[date].TradeCount);
        var maeObserved = activeDates.Sum(date => daily[date].MaeObservedTradeCount);
        var targetConfigured = activeDates.Sum(date => daily[date].TargetConfiguredTradeCount);
        var targetEvaluated = activeDates.Sum(date => daily[date].TargetEvaluatedTradeCount);
        var worstValues = activeDates
            .Select(date => daily[date].WorstMaePerContract)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();

        return new MaeCalendarMonth(
            monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            year,
            month,
            monthStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            tradeCount,
            maeObserved,
            targetConfigured,
            targetEvaluated,
            worstValues.Length > 0 ? worstValues.Max() : null,
            breaches.Length,
            days,
            breaches);
    }
}

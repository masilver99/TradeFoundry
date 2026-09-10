using TradeFoundry.Core;

namespace TradeFoundry.Services;

public sealed record RiskDisciplineRollingPoint(
    DateTimeOffset ExitUtc,
    int WindowTradeCount,
    int RiskQualifiedTradeCount,
    int WinnerCount,
    int RiskQualifiedWinnerCount,
    decimal? Score,
    decimal? RescueRate,
    decimal? RescueProfitShare,
    decimal? WinnerHeatRatio,
    decimal? WinnerMaeP90R,
    decimal? WinnerMaeP95R,
    int? MaeViolationCount,
    decimal? MaeViolationRate)
{
    public bool HasCompleteRiskData => WindowTradeCount > 0 && RiskQualifiedTradeCount == WindowTradeCount;
    public bool HasCompleteWinnerRiskData => WinnerCount > 0 && RiskQualifiedWinnerCount == WinnerCount;
}

public sealed record RiskDisciplineMonthlyPoint(
    DateOnly Month,
    int TradeCount,
    int RiskQualifiedTradeCount,
    int WinnerCount,
    int RiskQualifiedWinnerCount,
    decimal? Score,
    decimal? RescueRate,
    decimal? RescueProfitShare,
    decimal? WinnerHeatRatio,
    decimal? WinnerMaeP90R,
    decimal? WinnerMaeP95R,
    int? MaeViolationCount,
    decimal? MaeViolationRate)
{
    public bool HasCompleteRiskData => TradeCount > 0 && RiskQualifiedTradeCount == TradeCount;
    public bool HasCompleteWinnerRiskData => WinnerCount > 0 && RiskQualifiedWinnerCount == WinnerCount;
}

public sealed class RiskDisciplineTrend
{
    public const int RollingWindow = 20;

    public IReadOnlyList<RiskDisciplineRollingPoint> Rolling { get; init; } = Array.Empty<RiskDisciplineRollingPoint>();
    public IReadOnlyList<RiskDisciplineMonthlyPoint> Monthly { get; init; } = Array.Empty<RiskDisciplineMonthlyPoint>();

    public static RiskDisciplineTrend Build(
        IEnumerable<Trade> source,
        string? timeZoneId = null,
        RiskDisciplineThresholds? thresholds = null)
    {
        var configuration = thresholds ?? TearSheetMetrics.DefaultRiskDisciplineThresholds;
        var trades = source
            .Where(trade => trade.ExitUtc.HasValue)
            .OrderBy(trade => trade.ExitUtc)
            .ThenBy(trade => trade.Sequence)
            .ToArray();
        var rolling = trades.Length < RollingWindow
            ? Array.Empty<RiskDisciplineRollingPoint>()
            : Enumerable.Range(RollingWindow - 1, trades.Length - RollingWindow + 1)
                .Select(index =>
                {
                    var window = trades[(index - RollingWindow + 1)..(index + 1)];
                    return ToRollingPoint(trades[index].ExitUtc!.Value, window, configuration);
                })
                .ToArray();
        var timeZone = TimeZoneCatalog.Resolve(timeZoneId);
        var monthly = trades
            .GroupBy(trade => MonthStart(trade.ExitUtc!.Value, timeZone))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var monthTrades = group.ToArray();
                return ToMonthlyPoint(group.Key, monthTrades, configuration);
            })
            .ToArray();

        return new RiskDisciplineTrend
        {
            Rolling = rolling,
            Monthly = monthly
        };
    }

    private static RiskDisciplineRollingPoint ToRollingPoint(
        DateTimeOffset exitUtc,
        IReadOnlyList<Trade> trades,
        RiskDisciplineThresholds thresholds)
    {
        var metrics = TearSheetMetrics.BuildRiskDiscipline(trades, thresholds);
        return new RiskDisciplineRollingPoint(
            exitUtc,
            metrics.CompletedTradeCount,
            metrics.RiskObservedTradeCount,
            metrics.WinnerCount,
            metrics.RiskObservedWinnerCount,
            metrics.Score.Value,
            metrics.RescueRate,
            metrics.RescueProfitShare,
            metrics.WinnerHeatRatio,
            metrics.WinnerMaeP90R,
            metrics.WinnerMaeP95R,
            metrics.MaeViolationCount,
            ViolationRate(metrics));
    }

    private static RiskDisciplineMonthlyPoint ToMonthlyPoint(
        DateOnly month,
        IReadOnlyList<Trade> trades,
        RiskDisciplineThresholds thresholds)
    {
        var metrics = TearSheetMetrics.BuildRiskDiscipline(trades, thresholds);
        return new RiskDisciplineMonthlyPoint(
            month,
            metrics.CompletedTradeCount,
            metrics.RiskObservedTradeCount,
            metrics.WinnerCount,
            metrics.RiskObservedWinnerCount,
            metrics.Score.Value,
            metrics.RescueRate,
            metrics.RescueProfitShare,
            metrics.WinnerHeatRatio,
            metrics.WinnerMaeP90R,
            metrics.WinnerMaeP95R,
            metrics.MaeViolationCount,
            ViolationRate(metrics));
    }

    private static decimal? ViolationRate(RiskDisciplineMetrics metrics)
        => metrics.MaeViolationCount.HasValue && metrics.RiskObservedTradeCount > 0
            ? (decimal)metrics.MaeViolationCount.Value / metrics.RiskObservedTradeCount
            : null;

    private static DateOnly MonthStart(DateTimeOffset value, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(value, timeZone);
        return new DateOnly(local.Year, local.Month, 1);
    }
}

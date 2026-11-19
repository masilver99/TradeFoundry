using TradeFoundry.Core;

namespace TradeFoundry.Services;

/// <summary>
/// One trading day's realized result next to the heat taken to earn it.
/// </summary>
/// <param name="WorstPoint">
/// The day's estimated low: the lowest running P&amp;L reached, combining realized
/// results of trades closed before each entry with that trade's position MAE.
/// Always at or below zero because the day starts flat.
/// </param>
/// <param name="TotalHeat">The sum of position MAE across the day's MAE-observed trades.</param>
/// <param name="ObservedNetPnl">Net P&amp;L of only the trades that carry MAE, so the heat ratio compares like with like.</param>
/// <param name="HasOverlap">True when positions overlapped, so the worst point is an estimate rather than a sequence.</param>
public sealed record DailyHeatDay(
    DateOnly Date,
    int TradeCount,
    int MaeObservedTradeCount,
    decimal NetPnl,
    decimal WorstPoint,
    decimal TotalHeat,
    decimal ObservedNetPnl,
    bool HasOverlap)
{
    public bool HasCompleteMae => MaeObservedTradeCount == TradeCount;

    /// <summary>How far the day recovered from its low to the close.</summary>
    public decimal Recovery => NetPnl - WorstPoint;

    /// <summary>Net P&amp;L earned per currency unit of MAE sat through; null when no heat was taken.</summary>
    public decimal? PnlPerHeat => TotalHeat > 0m ? ObservedNetPnl / TotalHeat : null;
}

public sealed record DailyHeatRollingPoint(DateOnly Date, int WindowDayCount, decimal NetPnl, decimal Heat)
{
    public decimal? PnlPerHeat => Heat > 0m ? NetPnl / Heat : null;
}

public sealed record DailyHeatSummary(IReadOnlyList<DailyHeatDay> Days, IReadOnlyList<DailyHeatRollingPoint> Rolling)
{
    public decimal TotalObservedNetPnl => Days.Sum(day => day.ObservedNetPnl);
    public decimal TotalHeat => Days.Sum(day => day.TotalHeat);
    public decimal? PnlPerHeat => TotalHeat > 0m ? TotalObservedNetPnl / TotalHeat : null;
}

public static class DailyHeatMetrics
{
    public const int RollingWindow = 20;

    private static readonly MaeTargetSettings NoTargets = new();

    public static DailyHeatSummary Build(IEnumerable<Trade> trades, string? timeZoneId)
    {
        var days = DailyTradeAggregation.Build(trades, timeZoneId ?? string.Empty)
            .Where(day => day.CompletedTradeCount > 0)
            .Select(BuildDay)
            .Where(day => day.MaeObservedTradeCount > 0)
            .ToArray();
        return new DailyHeatSummary(days, BuildRolling(days));
    }

    private static DailyHeatDay BuildDay(DailyTradeSummary summary)
    {
        // CompletedTrades arrive ordered by entry.
        var trades = summary.CompletedTrades;
        var byExit = trades.OrderBy(trade => trade.ExitUtc).ThenBy(trade => trade.Sequence).ToArray();

        var worst = 0m;
        var running = 0m;
        foreach (var trade in byExit)
        {
            running += trade.NetPnl;
            worst = Math.Min(worst, running);
        }

        var heat = 0m;
        var observedNet = 0m;
        var observed = 0;
        var overlap = false;
        DateTimeOffset? latestExit = null;
        foreach (var trade in trades)
        {
            if (latestExit.HasValue && trade.EntryUtc < latestExit.Value) overlap = true;
            if (!latestExit.HasValue || trade.ExitUtc > latestExit) latestExit = trade.ExitUtc;

            var positionMae = MaeReviewMetrics.Build(trade, NoTargets).PositionMae;
            if (!positionMae.HasValue) continue;

            observed++;
            heat += positionMae.Value;
            observedNet += trade.NetPnl;
            var realizedBefore = trades
                .Where(other => !ReferenceEquals(other, trade) && other.ExitUtc <= trade.EntryUtc)
                .Sum(other => other.NetPnl);
            worst = Math.Min(worst, realizedBefore - positionMae.Value);
        }

        return new DailyHeatDay(summary.Date, trades.Count, observed, summary.RealizedNetPnl, worst, heat, observedNet, overlap);
    }

    private static DailyHeatRollingPoint[] BuildRolling(IReadOnlyList<DailyHeatDay> days)
    {
        if (days.Count < RollingWindow) return Array.Empty<DailyHeatRollingPoint>();
        return Enumerable.Range(RollingWindow - 1, days.Count - RollingWindow + 1)
            .Select(end =>
            {
                var window = days.Skip(end - RollingWindow + 1).Take(RollingWindow).ToArray();
                return new DailyHeatRollingPoint(days[end].Date, window.Length, window.Sum(day => day.ObservedNetPnl), window.Sum(day => day.TotalHeat));
            })
            .ToArray();
    }
}

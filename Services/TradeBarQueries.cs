using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services;

/// <summary>The bar window shown around a trade: the requested (or source) interval with about 50 bars of context on each side.</summary>
public static class TradeBarQueries
{
    public static BarQueryResult GetTradeBarQuery(TradeFoundryDb database, Trade trade, string? interval)
    {
        var available = database.GetBarSeriesAvailability(trade.JournalId, trade.Symbol);
        var requestedInterval = BarIntervals.TryNormalize(interval, out var normalizedInterval, allowSource: false)
            ? normalizedInterval
            : BarIntervals.TryNormalize(trade.SourceTimeframe, out var sourceTimeframe, allowSource: false)
                ? sourceTimeframe
            : available.Where(x => x.IntervalMinutes > 0).OrderBy(x => x.IntervalMinutes).Select(x => x.Interval).FirstOrDefault()
                ?? (available.Count > 0 ? available[0].Interval : "1m");
        // Load context in bars, so a five-minute chart gets the same context as a one-minute chart.
        var intervalMinutes = BarIntervals.TryGetMinutes(requestedInterval, out var minutes) ? minutes : 1;
        var padding = TimeSpan.FromMinutes(50d * intervalMinutes);
        var end = trade.ExitUtc ?? trade.EntryUtc;
        var query = database.GetBarWindow(trade.JournalId, trade.Symbol, trade.EntryUtc - padding, end + padding, requestedInterval, available);
        if (BarIntervals.TryGetMinutes(query.ResolvedInterval, out var resolvedMinutes) && resolvedMinutes > intervalMinutes)
        {
            padding = TimeSpan.FromMinutes(50d * resolvedMinutes);
            query = database.GetBarWindow(trade.JournalId, trade.Symbol, trade.EntryUtc - padding, end + padding, requestedInterval, available);
        }
        return query;
    }
}

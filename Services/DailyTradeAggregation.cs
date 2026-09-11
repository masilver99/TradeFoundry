using TradeFoundry.Core;

namespace TradeFoundry.Services;

/// <summary>
/// The shared date boundary for the calendar, period summaries, and daily review.
/// Completed trades use their localized exit date; every other trade uses its
/// localized entry date and is kept out of realized P&amp;L.
/// </summary>
public static class DailyTradeAggregation
{
    public static IReadOnlyList<DailyTradeSummary> Build(IEnumerable<Trade> trades, string timeZoneId)
    {
        var groups = new Dictionary<DateOnly, (List<Trade> Completed, List<Trade> Open)>();
        var timeZone = TimeZoneCatalog.Resolve(timeZoneId);
        foreach (var trade in trades)
        {
            var completed = trade.ExitUtc.HasValue && trade.Status.Equals("closed", StringComparison.OrdinalIgnoreCase);
            var timestamp = completed ? trade.ExitUtc!.Value : trade.EntryUtc;
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, timeZone).DateTime);
            if (!groups.TryGetValue(date, out var group))
            {
                group = (new List<Trade>(), new List<Trade>());
                groups[date] = group;
            }

            if (completed) group.Completed.Add(trade);
            else group.Open.Add(trade);
        }

        return groups
            .OrderBy(item => item.Key)
            .Select(item => new DailyTradeSummary
            {
                Date = item.Key,
                CompletedTrades = item.Value.Completed
                    .OrderBy(trade => trade.EntryUtc)
                    .ThenBy(trade => trade.Sequence)
                    .ThenBy(trade => trade.Id)
                    .ToArray(),
                OpenTrades = item.Value.Open
                    .OrderBy(trade => trade.EntryUtc)
                    .ThenBy(trade => trade.Sequence)
                    .ThenBy(trade => trade.Id)
                    .ToArray()
            })
            .ToArray();
    }
}

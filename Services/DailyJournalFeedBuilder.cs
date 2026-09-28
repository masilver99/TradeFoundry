using TradeFoundry.Core;

namespace TradeFoundry.Services;

public static class DailyJournalFeedBuilder
{
    public static DailyJournalFeedPage Build(
        Guid journalId,
        DateOnly excludedDate,
        DateOnly? beforeDate,
        int limit,
        IReadOnlyList<DailyTradeSummary> tradeDays,
        IReadOnlyList<DailyJournalEntry> journalEntries)
    {
        var pageSize = Math.Clamp(limit, 1, 50);
        var tradeByDate = tradeDays
            .Where(day => IsInRange(day.Date, excludedDate, beforeDate))
            .ToDictionary(day => day.Date);
        var journalByDate = journalEntries
            .Where(entry => IsInRange(entry.Date, excludedDate, beforeDate)
                && !string.IsNullOrWhiteSpace(LexicalPlainText.Extract(entry.Text)))
            .ToDictionary(entry => entry.Date);

        var dates = tradeByDate.Keys
            .Concat(journalByDate.Keys)
            .Distinct()
            .OrderByDescending(date => date)
            .Take(pageSize + 1)
            .ToArray();
        var hasMore = dates.Length > pageSize;
        var days = dates
            .Take(pageSize)
            .Select(date => new DailyJournalFeedDay
            {
                Entry = journalByDate.TryGetValue(date, out var entry)
                    ? entry
                    : new DailyJournalEntry { JournalId = journalId, Date = date },
                Trading = tradeByDate.GetValueOrDefault(date)
            })
            .ToArray();

        return new DailyJournalFeedPage { Days = days, HasMore = hasMore };
    }

    private static bool IsInRange(DateOnly date, DateOnly excludedDate, DateOnly? beforeDate) =>
        date != excludedDate && (!beforeDate.HasValue || date < beforeDate.Value);
}

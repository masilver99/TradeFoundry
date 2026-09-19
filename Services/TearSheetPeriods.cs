using System.Globalization;
using TradeFoundry.Core;

namespace TradeFoundry.Services;

public sealed record TearSheetPeriodSummaryRow(
    string Id,
    string? ParentId,
    string Level,
    string Label,
    string StartDate,
    string EndDate,
    bool HasChildren,
    decimal NetPnl,
    decimal EstimatedTaxes,
    int TradeCount,
    int DayCount,
    decimal? AveragePnlPerDay,
    decimal? AveragePnlPerTrade,
    decimal? WinRate,
    decimal? ProfitFactor,
    decimal? ExchangeFees,
    decimal? NfaFees,
    decimal? ClearingFees,
    decimal? TotalFees,
    double? AverageHoldSeconds,
    decimal? BestTrade,
    decimal? WorstTrade);

public sealed class TearSheetPeriodSummary
{
    public IReadOnlyList<TearSheetPeriodSummaryRow> Rows { get; init; } = Array.Empty<TearSheetPeriodSummaryRow>();
    public int YearCount { get; init; }
    public int QuarterCount { get; init; }
    public int MonthCount { get; init; }
    public decimal DefaultTaxRate { get; init; } = .24m;
    public decimal LongTermRate { get; init; } = .15m;
    public decimal LongTermShare { get; init; } = .60m;
    public decimal ShortTermShare { get; init; } = .40m;
}

public sealed record TearSheetPeriodBreakdownRow(
    string Label,
    int TradeCount,
    decimal GrossPnl,
    decimal? WinRate,
    decimal? Expectancy);

public sealed class TearSheetPeriodBreakdown
{
    public IReadOnlyList<TearSheetPeriodBreakdownRow> ByDayOfWeek { get; init; } = Array.Empty<TearSheetPeriodBreakdownRow>();
    public IReadOnlyList<TearSheetPeriodBreakdownRow> ByEntryHour { get; init; } = Array.Empty<TearSheetPeriodBreakdownRow>();
    public IReadOnlyList<TearSheetPeriodBreakdownRow> ByWeek { get; init; } = Array.Empty<TearSheetPeriodBreakdownRow>();
    public IReadOnlyList<TearSheetPeriodBreakdownRow> ByMonth { get; init; } = Array.Empty<TearSheetPeriodBreakdownRow>();
}

public sealed class TearSheetPeriodData
{
    public TearSheetPeriodSummary Summary { get; init; } = new();
    public TearSheetPeriodBreakdown Breakdown { get; init; } = new();
}

public static class TearSheetPeriods
{
    private const decimal DefaultTaxRate = .24m;
    private const decimal LongTermRate = .15m;
    private const decimal LongTermShare = .60m;
    private const decimal ShortTermShare = .40m;
    private static readonly CultureInfo UiCulture = CultureInfo.CurrentCulture;

    public static TearSheetPeriodData Build(IEnumerable<Trade> source, string? timeZoneId)
    {
        var timeZone = TimeZoneCatalog.Resolve(timeZoneId);
        var completedTrades = DailyTradeAggregation.Build(source, timeZoneId ?? "UTC")
            .Where(day => day.CompletedTradeCount > 0)
            .SelectMany(day => day.CompletedTrades)
            .ToArray();
        var trades = completedTrades
            .OrderBy(trade => trade.ExitUtc)
            .ThenBy(trade => trade.Sequence)
            .Select(trade =>
            {
                var exitLocal = TimeZoneInfo.ConvertTime(trade.ExitUtc!.Value, timeZone);
                var entryLocal = TimeZoneInfo.ConvertTime(trade.EntryUtc, timeZone);
                return new TimedTrade(trade, exitLocal, entryLocal, DateOnly.FromDateTime(exitLocal.DateTime));
            })
            .ToArray();
        var nodes = BuildSummaryNodes(trades);
        var rows = new List<TearSheetPeriodSummaryRow>();
        foreach (var node in nodes) Flatten(node, parentId: null, rows);

        return new TearSheetPeriodData
        {
            Summary = new TearSheetPeriodSummary
            {
                Rows = rows,
                YearCount = rows.Count(row => row.Level == "year"),
                QuarterCount = rows.Count(row => row.Level == "quarter"),
                MonthCount = rows.Count(row => row.Level == "month"),
                DefaultTaxRate = DefaultTaxRate,
                LongTermRate = LongTermRate,
                LongTermShare = LongTermShare,
                ShortTermShare = ShortTermShare
            },
            Breakdown = BuildBreakdown(trades)
        };
    }

    private static IReadOnlyList<PeriodNode> BuildSummaryNodes(IReadOnlyList<TimedTrade> trades)
    {
        var years = new List<PeriodNode>();
        foreach (var yearGroup in trades.GroupBy(item => item.ExitDate.Year).OrderBy(group => group.Key))
        {
            var yearNode = new PeriodNode($"year-{yearGroup.Key}", "year", yearGroup.Key.ToString("0000", CultureInfo.InvariantCulture), yearGroup.ToArray());
            years.Add(yearNode);

            foreach (var quarterGroup in yearGroup
                .GroupBy(item => new { item.ExitDate.Year, Quarter = (item.ExitDate.Month - 1) / 3 + 1 })
                .OrderBy(group => group.Key.Quarter))
            {
                var quarterNode = new PeriodNode(
                    $"quarter-{quarterGroup.Key.Year:0000}-Q{quarterGroup.Key.Quarter}",
                    "quarter",
                    $"Q{quarterGroup.Key.Quarter} {quarterGroup.Key.Year:0000}",
                    quarterGroup.ToArray());
                yearNode.Children.Add(quarterNode);

                foreach (var monthGroup in quarterGroup
                    .GroupBy(item => new { item.ExitDate.Year, item.ExitDate.Month })
                    .OrderBy(group => group.Key.Month))
                {
                    var monthStart = new DateOnly(monthGroup.Key.Year, monthGroup.Key.Month, 1);
                    var monthNode = new PeriodNode(
                        $"month-{monthGroup.Key.Year:0000}-{monthGroup.Key.Month:00}",
                        "month",
                        monthStart.ToString("MMM yyyy", UiCulture),
                        monthGroup.ToArray());
                    quarterNode.Children.Add(monthNode);

                    foreach (var weekGroup in monthGroup
                        .GroupBy(item =>
                        {
                            var date = item.ExitDate.ToDateTime(TimeOnly.MinValue);
                            return new { IsoYear = ISOWeek.GetYear(date), Week = ISOWeek.GetWeekOfYear(date) };
                        })
                        .OrderBy(group => group.Key.IsoYear)
                        .ThenBy(group => group.Key.Week))
                    {
                        var weekTrades = weekGroup.ToArray();
                        var weekStart = weekTrades.Min(item => item.ExitDate);
                        var weekEnd = weekTrades.Max(item => item.ExitDate);
                        var weekNode = new PeriodNode(
                            $"week-{monthGroup.Key.Year:0000}-{monthGroup.Key.Month:00}-{weekGroup.Key.IsoYear:0000}-W{weekGroup.Key.Week:00}",
                            "week",
                            $"W{weekGroup.Key.Week:00} ({FormatShortDate(weekStart)} - {FormatShortDate(weekEnd)})",
                            weekTrades);
                        monthNode.Children.Add(weekNode);

                        foreach (var dayGroup in weekGroup.GroupBy(item => item.ExitDate).OrderBy(group => group.Key))
                        {
                            var day = dayGroup.Key;
                            weekNode.Children.Add(new PeriodNode(
                                $"day-{day:yyyy-MM-dd}",
                                "day",
                                day.ToString("ddd yyyy-MM-dd", UiCulture),
                                dayGroup.ToArray()));
                        }
                    }
                }
            }
        }
        return years;
    }

    private static void Flatten(PeriodNode node, string? parentId, ICollection<TearSheetPeriodSummaryRow> rows)
    {
        var stats = BuildSummaryStats(node.Trades);
        var startDate = node.Trades.Min(item => item.ExitDate);
        var endDate = node.Trades.Max(item => item.ExitDate);
        rows.Add(new TearSheetPeriodSummaryRow(
            node.Id,
            parentId,
            node.Level,
            node.Label,
            startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            node.Children.Count > 0,
            stats.NetPnl,
            EstimateTaxes(stats.NetPnl),
            stats.TradeCount,
            stats.DayCount,
            stats.AveragePnlPerDay,
            stats.AveragePnlPerTrade,
            stats.WinRate,
            stats.ProfitFactor,
            stats.ExchangeFees,
            stats.NfaFees,
            stats.ClearingFees,
            stats.TotalFees,
            stats.AverageHoldSeconds,
            stats.BestTrade,
            stats.WorstTrade));

        foreach (var child in node.Children) Flatten(child, node.Id, rows);
    }

    private static PeriodStats BuildSummaryStats(IReadOnlyList<TimedTrade> trades)
    {
        var netPnl = trades.Select(item => item.Trade.NetPnl).ToArray();
        var winners = netPnl.Where(value => value > 0m).ToArray();
        var losers = netPnl.Where(value => value < 0m).ToArray();
        var totalLoss = losers.Sum();
        var durations = trades
            .Select(item => item.Trade.Duration)
            .Where(duration => duration.HasValue && duration.Value >= TimeSpan.Zero)
            .Select(duration => duration!.Value.TotalSeconds)
            .ToArray();
        var total = netPnl.Sum();
        var tradeCount = trades.Count;
        var dayCount = trades.Select(item => item.ExitDate).Distinct().Count();

        return new PeriodStats(
            total,
            tradeCount,
            dayCount,
            dayCount == 0 ? null : total / dayCount,
            tradeCount == 0 ? null : total / tradeCount,
            tradeCount == 0 ? null : (decimal)winners.Length / tradeCount,
            losers.Length == 0 ? null : winners.Sum() / Math.Abs(totalLoss),
            trades.Count == 0 ? null : trades.Sum(item => item.Trade.ExchangeFees),
            trades.Count == 0 ? null : trades.Sum(item => item.Trade.NfaFees),
            trades.Count == 0 ? null : trades.Sum(item => item.Trade.ClearingFees),
            trades.Count == 0 ? null : trades.Sum(item => item.Trade.Fees),
            durations.Length == 0 ? null : durations.Average(),
            netPnl.Length == 0 ? null : netPnl.Max(),
            netPnl.Length == 0 ? null : netPnl.Min());
    }

    private static TearSheetPeriodBreakdown BuildBreakdown(IReadOnlyList<TimedTrade> trades)
    {
        var dayOfWeek = Enumerable.Range(1, 5)
            .Select(day =>
            {
                var dayTrades = trades.Where(item => (int)item.ExitLocal.DayOfWeek == day).ToArray();
                return BuildBreakdownRow(((DayOfWeek)day).ToString(), dayTrades);
            })
            .Where(row => row.TradeCount > 0)
            .ToArray();
        var byHour = trades
            .GroupBy(item => item.EntryLocal.Hour)
            .OrderBy(group => group.Key)
            .Select(group => BuildBreakdownRow($"{group.Key:00}:xx", group.ToArray()))
            .ToArray();
        var byWeek = trades
            .GroupBy(item => WeekKey(item.ExitDate))
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => BuildBreakdownRow(group.Key, group.ToArray()))
            .ToArray();
        var byMonth = trades
            .GroupBy(item => $"{item.ExitDate.Year:0000}-{item.ExitDate.Month:00}")
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => BuildBreakdownRow(group.Key, group.ToArray()))
            .ToArray();

        return new TearSheetPeriodBreakdown
        {
            ByDayOfWeek = dayOfWeek,
            ByEntryHour = byHour,
            ByWeek = byWeek,
            ByMonth = byMonth
        };
    }

    private static TearSheetPeriodBreakdownRow BuildBreakdownRow(string label, IReadOnlyList<TimedTrade> trades)
    {
        if (trades.Count == 0) return new TearSheetPeriodBreakdownRow(label, 0, 0m, null, null);
        var grossPnl = trades.Sum(item => item.Trade.GrossPnl);
        return new TearSheetPeriodBreakdownRow(
            label,
            trades.Count,
            grossPnl,
            (decimal)trades.Count(item => item.Trade.GrossPnl > 0m) / trades.Count,
            grossPnl / trades.Count);
    }

    private static string WeekKey(DateOnly date)
    {
        var dateTime = date.ToDateTime(TimeOnly.MinValue);
        return $"{ISOWeek.GetYear(dateTime):0000}-W{ISOWeek.GetWeekOfYear(dateTime):00}";
    }

    private static string FormatShortDate(DateOnly date) => date.ToString("MMM d", UiCulture);

    private static decimal EstimateTaxes(decimal netPnl)
    {
        var effectiveRate = LongTermShare * LongTermRate + ShortTermShare * DefaultTaxRate;
        return Math.Round(Math.Max(netPnl, 0m) * effectiveRate, 2);
    }

    private sealed record TimedTrade(Trade Trade, DateTimeOffset ExitLocal, DateTimeOffset EntryLocal, DateOnly ExitDate);

    private sealed record PeriodStats(
        decimal NetPnl,
        int TradeCount,
        int DayCount,
        decimal? AveragePnlPerDay,
        decimal? AveragePnlPerTrade,
        decimal? WinRate,
        decimal? ProfitFactor,
        decimal? ExchangeFees,
        decimal? NfaFees,
        decimal? ClearingFees,
        decimal? TotalFees,
        double? AverageHoldSeconds,
        decimal? BestTrade,
        decimal? WorstTrade);

    private sealed class PeriodNode
    {
        public PeriodNode(string id, string level, string label, IReadOnlyList<TimedTrade> trades)
        {
            Id = id;
            Level = level;
            Label = label;
            Trades = trades;
        }

        public string Id { get; }
        public string Level { get; }
        public string Label { get; }
        public IReadOnlyList<TimedTrade> Trades { get; }
        public List<PeriodNode> Children { get; } = new();
    }
}

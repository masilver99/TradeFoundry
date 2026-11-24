using System.Globalization;
using TradeFoundry.Core;

namespace TradeFoundry.Services;

/// <summary>What the day statistics are built from.</summary>
public sealed class DayStatisticsInput
{
    public DateOnly Date { get; init; }
    /// <summary>The day's completed trades, as the page shows them.</summary>
    public IReadOnlyList<Trade> DayTrades { get; init; } = Array.Empty<Trade>();
    public int OpenTradeCount { get; init; }
    /// <summary>Every trading day in the current journal filter, used for comparisons and cross-day charts.</summary>
    public IReadOnlyList<DailyTradeSummary> History { get; init; } = Array.Empty<DailyTradeSummary>();
    public IReadOnlyDictionary<DateOnly, DailyRating> Ratings { get; init; } = new Dictionary<DateOnly, DailyRating>();
    public DailyLimitSettings Limits { get; init; } = new();
    public string TimeZoneId { get; init; } = "UTC";
}

/// <summary>Headline numbers for one day's completed trades.</summary>
public sealed class DayScorecard
{
    public int TradeCount { get; init; }
    public int OpenTradeCount { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public decimal NetPnl { get; init; }
    public decimal GrossPnl { get; init; }
    public decimal Fees { get; init; }
    public decimal? WinRate { get; init; }
    /// <summary>Summed winners over summed losers; null when the day has no losing trade.</summary>
    public decimal? ProfitFactor { get; init; }
    public decimal? AverageWin { get; init; }
    public decimal? AverageLoss { get; init; }
    public decimal? LargestWin { get; init; }
    public decimal? LargestLoss { get; init; }
    /// <summary>Summed R multiple of the trades that carry one.</summary>
    public decimal? TotalR { get; init; }
    public int RCoverage { get; init; }
    /// <summary>Fees as a percentage of gross P&amp;L magnitude; null when gross is zero.</summary>
    public decimal? FeesPercentOfGross { get; init; }
    public DateTime? FirstEntryLocal { get; init; }
    public DateTime? LastExitLocal { get; init; }
    /// <summary>Time with at least one position open; overlapping positions are counted once.</summary>
    public TimeSpan TimeInMarket { get; init; }
}

/// <summary>One step of the day's realized equity: the start, then one point per exit.</summary>
public sealed record DayEquityPoint(DateTime LocalTime, decimal Cumulative, decimal? TradePnl, string Label);

/// <param name="Tone">good, bad, warn, or neutral.</param>
public sealed record DayComparison(string Key, string Label, string Value, string Detail, string Tone);

/// <param name="Tone">warn, ok, or info.</param>
public sealed record DayFlag(string Key, string Title, string Detail, string Tone);

public sealed record TradeNumberBucket(string Label, int Number, int TradeCount, decimal AveragePnl, decimal WinRate, decimal? TodayPnl);

public sealed record DayPoint(DateOnly Date, int TradeCount, decimal NetPnl, int? Rating);

public sealed record GreenDayRollingPoint(DateOnly Date, int WindowDayCount, decimal GreenRate);

public sealed class GreenDayStats
{
    public int DayCount { get; init; }
    public int GreenDays { get; init; }
    public int RedDays { get; init; }
    public decimal? GreenRate { get; init; }
    public decimal? AverageGreen { get; init; }
    /// <summary>The average losing day, as a positive amount.</summary>
    public decimal? AverageRed { get; init; }
    public decimal? PayoffRatio { get; init; }
    public decimal? WorstRed { get; init; }
    /// <summary>How many average green days the worst red day erased.</summary>
    public decimal? WorstRedInGreenDays { get; init; }
    /// <summary>Positive for consecutive green days ending on the selected day, negative for red.</summary>
    public int StreakAtDate { get; init; }
    public IReadOnlyList<GreenDayRollingPoint> Rolling { get; init; } = Array.Empty<GreenDayRollingPoint>();
}

/// <summary>Average cumulative realized P&amp;L through the session, with the selected day's curve.</summary>
public sealed class SessionCurve
{
    public int SlotMinutes { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
    public IReadOnlyList<decimal> Average { get; init; } = Array.Empty<decimal>();
    public IReadOnlyList<decimal?> Today { get; init; } = Array.Empty<decimal?>();
    public int DayCount { get; init; }
}

public sealed class DayStatisticsReport
{
    public DateOnly Date { get; init; }
    public DayScorecard Scorecard { get; init; } = new();
    public IReadOnlyList<DayEquityPoint> Equity { get; init; } = Array.Empty<DayEquityPoint>();
    /// <summary>The highest realized P&amp;L reached during the day; never below zero.</summary>
    public decimal Peak { get; init; }
    public DateTime? PeakLocalTime { get; init; }
    /// <summary>Peak minus the close: profit handed back after the day's high point.</summary>
    public decimal GiveBack { get; init; }
    /// <summary>The day's estimated low including open-trade MAE, when any trade carries MAE.</summary>
    public DailyHeatDay? Heat { get; init; }
    public int ComparisonDayCount { get; init; }
    public IReadOnlyList<DayComparison> Comparisons { get; init; } = Array.Empty<DayComparison>();
    public IReadOnlyList<DayFlag> Flags { get; init; } = Array.Empty<DayFlag>();
    public DailyLimitSettings Limits { get; init; } = new();
    public IReadOnlyList<TradeNumberBucket> TradeNumbers { get; init; } = Array.Empty<TradeNumberBucket>();
    public GreenDayStats GreenDays { get; init; } = new();
    public IReadOnlyList<DayPoint> Days { get; init; } = Array.Empty<DayPoint>();
    public SessionCurve Session { get; init; } = new();
    /// <summary>Daily P&amp;L and intraday low for the trading days around the selected day.</summary>
    public IReadOnlyList<DailyHeatDay> HeatWindow { get; init; } = Array.Empty<DailyHeatDay>();
    public bool HasTrades => Scorecard.TradeCount > 0;
}

/// <summary>
/// Builds the day report card: the day's own numbers, how they compare with the journal's
/// earlier trading days, behavior flags, and the cross-day charts with the selected day marked.
/// </summary>
public static class DayStatistics
{
    public const int MinimumComparisonDays = 5;
    public const int TradeNumberCap = 10;
    public const int GreenDayWindow = 20;
    public const int HeatWindowDays = 30;
    public const int SessionSlotMinutes = 15;

    public static DayStatisticsReport Build(DayStatisticsInput input)
    {
        var timeZone = TimeZoneCatalog.Resolve(input.TimeZoneId);
        var trades = input.DayTrades
            .Where(trade => trade.ExitUtc.HasValue)
            .OrderBy(trade => trade.EntryUtc).ThenBy(trade => trade.Sequence).ThenBy(trade => trade.Id)
            .ToArray();
        var byExit = trades.OrderBy(trade => trade.ExitUtc).ThenBy(trade => trade.Sequence).ThenBy(trade => trade.Id).ToArray();
        var history = input.History.Where(day => day.CompletedTradeCount > 0).OrderBy(day => day.Date).ToArray();
        // Comparisons use only earlier days, so a past day reads as it did then and does not shift as later days are added.
        var earlier = history.Where(day => day.Date < input.Date).ToArray();
        var others = history.Where(day => day.Date != input.Date).ToArray();
        var today = new DailyTradeSummary { Date = input.Date, CompletedTrades = trades };

        var equity = BuildEquity(trades, byExit, timeZone);
        var (peak, peakUtc) = Peak(byExit);
        var net = trades.Sum(trade => trade.NetPnl);
        var heat = trades.Length == 0 ? null : DailyHeatMetrics.BuildDay(today);
        var giveBack = GiveBackFrom(peak, net) ?? 0m;

        // The cross-day views always include the selected day, even when the page reached it outside the filter.
        var daysWithToday = trades.Length == 0
            ? history
            : others.Append(today).OrderBy(day => day.Date).ToArray();

        return new DayStatisticsReport
        {
            Date = input.Date,
            Scorecard = BuildScorecard(trades, input.OpenTradeCount, timeZone),
            Equity = equity,
            Peak = peak,
            PeakLocalTime = peakUtc.HasValue ? Local(peakUtc.Value, timeZone) : null,
            GiveBack = giveBack,
            Heat = heat is { MaeObservedTradeCount: > 0 } ? heat : null,
            ComparisonDayCount = earlier.Length,
            Comparisons = trades.Length == 0 ? Array.Empty<DayComparison>() : BuildComparisons(trades, net, peak, giveBack, earlier),
            Flags = trades.Length == 0 ? Array.Empty<DayFlag>() : BuildFlags(trades, byExit, peakUtc, peak, input.Limits, timeZone),
            Limits = input.Limits,
            TradeNumbers = BuildTradeNumbers(daysWithToday, trades),
            GreenDays = BuildGreenDays(daysWithToday, input.Date),
            Days = daysWithToday.Select(day => new DayPoint(day.Date, day.CompletedTradeCount, day.RealizedNetPnl, input.Ratings.GetValueOrDefault(day.Date)?.Rating)).ToArray(),
            Session = BuildSession(daysWithToday, input.Date, timeZone),
            HeatWindow = BuildHeatWindow(daysWithToday, input.Date)
        };
    }

    private static DayScorecard BuildScorecard(Trade[] trades, int openCount, TimeZoneInfo timeZone)
    {
        var wins = trades.Where(trade => trade.NetPnl > 0m).Select(trade => trade.NetPnl).ToArray();
        var losses = trades.Where(trade => trade.NetPnl < 0m).Select(trade => trade.NetPnl).ToArray();
        var gross = trades.Sum(trade => trade.GrossPnl);
        var fees = trades.Sum(trade => trade.Fees);
        var rValues = trades.Where(trade => trade.RMultiple.HasValue).Select(trade => trade.RMultiple!.Value).ToArray();
        return new DayScorecard
        {
            TradeCount = trades.Length,
            OpenTradeCount = openCount,
            Wins = wins.Length,
            Losses = losses.Length,
            NetPnl = trades.Sum(trade => trade.NetPnl),
            GrossPnl = gross,
            Fees = fees,
            WinRate = trades.Length > 0 ? 100m * wins.Length / trades.Length : null,
            ProfitFactor = losses.Length > 0 ? wins.Sum() / Math.Abs(losses.Sum()) : null,
            AverageWin = wins.Length > 0 ? wins.Average() : null,
            AverageLoss = losses.Length > 0 ? losses.Average() : null,
            LargestWin = wins.Length > 0 ? wins.Max() : null,
            LargestLoss = losses.Length > 0 ? losses.Min() : null,
            TotalR = rValues.Length > 0 ? rValues.Sum() : null,
            RCoverage = rValues.Length,
            FeesPercentOfGross = gross != 0m ? 100m * fees / Math.Abs(gross) : null,
            FirstEntryLocal = trades.Length > 0 ? Local(trades.Min(trade => trade.EntryUtc), timeZone) : null,
            LastExitLocal = trades.Length > 0 ? Local(trades.Max(trade => trade.ExitUtc!.Value), timeZone) : null,
            TimeInMarket = TimeInMarket(trades)
        };
    }

    private static TimeSpan TimeInMarket(IEnumerable<Trade> trades)
    {
        var total = TimeSpan.Zero;
        DateTimeOffset? start = null, end = null;
        foreach (var trade in trades.OrderBy(trade => trade.EntryUtc))
        {
            var exit = trade.ExitUtc!.Value;
            if (end is null || trade.EntryUtc > end)
            {
                if (start.HasValue) total += end!.Value - start.Value;
                start = trade.EntryUtc;
                end = exit;
            }
            else if (exit > end) end = exit;
        }
        if (start.HasValue) total += end!.Value - start.Value;
        return total < TimeSpan.Zero ? TimeSpan.Zero : total;
    }

    private static DayEquityPoint[] BuildEquity(Trade[] trades, Trade[] byExit, TimeZoneInfo timeZone)
    {
        if (trades.Length == 0) return Array.Empty<DayEquityPoint>();
        var points = new List<DayEquityPoint> { new(Local(trades[0].EntryUtc, timeZone), 0m, null, "Start") };
        var running = 0m;
        foreach (var trade in byExit)
        {
            running += trade.NetPnl;
            points.Add(new DayEquityPoint(Local(trade.ExitUtc!.Value, timeZone), running, trade.NetPnl, $"{trade.Symbol} {trade.Direction}".Trim()));
        }
        return points.ToArray();
    }

    /// <summary>The highest realized running P&amp;L and the first exit that reached it; zero and null when the day never went green.</summary>
    private static (decimal Peak, DateTimeOffset? At) Peak(IEnumerable<Trade> byExit)
    {
        var running = 0m;
        var peak = 0m;
        DateTimeOffset? at = null;
        foreach (var trade in byExit)
        {
            running += trade.NetPnl;
            if (running > peak)
            {
                peak = running;
                at = trade.ExitUtc;
            }
        }
        return (peak, at);
    }

    private static DayComparison[] BuildComparisons(Trade[] trades, decimal net, decimal peak, decimal giveBack, DailyTradeSummary[] others)
    {
        if (others.Length < MinimumComparisonDays) return Array.Empty<DayComparison>();
        var items = new List<DayComparison>();

        var netPercentile = Percentile(net, others.Select(day => day.RealizedNetPnl));
        items.Add(new DayComparison("net", "Net P&L", $"{Ordinal(netPercentile)} percentile", $"of {others.Length:N0} earlier trading days",
            netPercentile >= 80 ? "good" : netPercentile <= 20 ? "bad" : "neutral"));

        var medianCount = Median(others.Select(day => (decimal)day.CompletedTradeCount));
        if (medianCount > 0m)
        {
            var ratio = trades.Length / medianCount;
            items.Add(new DayComparison("trades", "Trade count", $"{ratio.ToString("0.0", CultureInfo.InvariantCulture)}× your median",
                $"median {medianCount.ToString("0.#", CultureInfo.InvariantCulture)} trades a day",
                ratio >= 2m ? "warn" : "neutral"));
        }

        if (net < 0m)
        {
            var red = others.Where(day => day.RealizedNetPnl < 0m).Select(day => -day.RealizedNetPnl).ToArray();
            if (red.Length > 0)
            {
                var share = Percentile(-net, red);
                items.Add(new DayComparison("size", "Loss size", $"Larger than {share:0}%", $"of your {red.Length:N0} earlier red days", share >= 80 ? "bad" : "neutral"));
            }
        }
        else if (net > 0m)
        {
            var green = others.Where(day => day.RealizedNetPnl > 0m).Select(day => day.RealizedNetPnl).ToArray();
            if (green.Length > 0)
            {
                var share = Percentile(net, green);
                items.Add(new DayComparison("size", "Win size", $"Larger than {share:0}%", $"of your {green.Length:N0} earlier green days", share >= 80 ? "good" : "neutral"));
            }
        }

        if (peak > 0m)
        {
            var otherGiveBacks = others.Select(day => GiveBack(day)).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
            if (otherGiveBacks.Length >= MinimumComparisonDays)
            {
                var share = Percentile(giveBack, otherGiveBacks);
                items.Add(new DayComparison("giveback", "Give-back", $"More than {share:0}%", $"of {otherGiveBacks.Length:N0} earlier days that went green", share >= 80 ? "warn" : "neutral"));
            }
        }

        var todayR = trades.Where(trade => trade.RMultiple.HasValue).Select(trade => trade.RMultiple!.Value).ToArray();
        if (todayR.Length > 0)
        {
            var otherR = others
                .Select(day => day.CompletedTrades.Where(trade => trade.RMultiple.HasValue).Select(trade => trade.RMultiple!.Value).ToArray())
                .Where(values => values.Length > 0)
                .Select(values => values.Sum())
                .ToArray();
            if (otherR.Length >= MinimumComparisonDays)
            {
                var percentile = Percentile(todayR.Sum(), otherR);
                items.Add(new DayComparison("r", "Total R", $"{Ordinal(percentile)} percentile", $"of {otherR.Length:N0} earlier days with R",
                    percentile >= 80 ? "good" : percentile <= 20 ? "bad" : "neutral"));
            }
        }

        return items.ToArray();
    }

    /// <summary>Give-back for a day that went green at some point; null for a day that never did.</summary>
    /// <summary>Profit handed back from the day's high, capped at the high: a day that never went green has none,
    /// and losses below flat count against the day, not against give-back.</summary>
    internal static decimal? GiveBackFrom(decimal peak, decimal net) => peak > 0m ? Math.Clamp(peak - net, 0m, peak) : null;

    private static decimal? GiveBack(DailyTradeSummary day)
    {
        var (peak, _) = Peak(day.CompletedTrades.OrderBy(trade => trade.ExitUtc).ThenBy(trade => trade.Sequence));
        return GiveBackFrom(peak, day.RealizedNetPnl);
    }

    private static DayFlag[] BuildFlags(Trade[] trades, Trade[] byExit, DateTimeOffset? peakUtc, decimal peak, DailyLimitSettings limits, TimeZoneInfo timeZone)
    {
        var flags = new List<DayFlag>();
        var window = TimeSpan.FromMinutes(Math.Max(1, limits.RevengeWindowMinutes));

        var revenge = trades.Where(trade =>
        {
            var previous = trades
                .Where(other => !ReferenceEquals(other, trade) && other.ExitUtc <= trade.EntryUtc)
                .OrderByDescending(other => other.ExitUtc)
                .FirstOrDefault();
            return previous is { NetPnl: < 0m } && trade.EntryUtc - previous.ExitUtc!.Value <= window;
        }).ToArray();
        flags.Add(revenge.Length > 0
            ? new DayFlag("revenge", $"{Plural(revenge.Length, "quick re-entry", "quick re-entries")} after a loss",
                $"Entered within {limits.RevengeWindowMinutes} min of a losing exit · {Money(revenge.Sum(trade => trade.NetPnl))} combined", "warn")
            : new DayFlag("revenge", "No quick re-entries after a loss", $"No entry within {limits.RevengeWindowMinutes} min of a losing exit", "ok"));

        if (limits.MaxDailyLoss is { } maxLoss)
        {
            var breach = FirstCrossing(byExit, running => running <= -maxLoss);
            if (breach.HasValue)
            {
                var after = trades.Where(trade => trade.EntryUtc >= breach.Value).ToArray();
                flags.Add(new DayFlag("loss-limit", "Max daily loss reached",
                    $"Hit −{Money(maxLoss)} at {Local(breach.Value, timeZone):HH:mm}" + (after.Length > 0 ? $" · then {Plural(after.Length, "more trade", "more trades")} for {Money(after.Sum(trade => trade.NetPnl))}" : " · stopped trading"),
                    after.Length > 0 ? "warn" : "info"));
            }
            else
            {
                flags.Add(new DayFlag("loss-limit", "Within max daily loss", $"Realized P&L stayed above −{Money(maxLoss)}", "ok"));
            }
        }

        if (limits.DailyProfitGoal is { } goal)
        {
            var reached = FirstCrossing(byExit, running => running >= goal);
            if (reached.HasValue)
            {
                var after = trades.Where(trade => trade.EntryUtc >= reached.Value).ToArray();
                flags.Add(new DayFlag("goal", "Daily goal reached",
                    $"Hit {Money(goal)} at {Local(reached.Value, timeZone):HH:mm}" + (after.Length > 0 ? $" · kept trading: {Plural(after.Length, "trade", "trades")} for {Money(after.Sum(trade => trade.NetPnl))}" : " · stopped trading"),
                    after.Length > 0 && after.Sum(trade => trade.NetPnl) < 0m ? "warn" : "ok"));
            }
        }

        if (peakUtc.HasValue)
        {
            var afterPeak = trades.Where(trade => trade.EntryUtc >= peakUtc.Value).ToArray();
            if (afterPeak.Length > 0)
            {
                var result = afterPeak.Sum(trade => trade.NetPnl);
                flags.Add(new DayFlag("after-peak", $"{Plural(afterPeak.Length, "trade", "trades")} after the day's high",
                    $"Peak {Money(peak)} at {Local(peakUtc.Value, timeZone):HH:mm} · trades after it made {Money(result)}", result < 0m ? "warn" : "info"));
            }
        }

        return flags.ToArray();
    }

    private static DateTimeOffset? FirstCrossing(IEnumerable<Trade> byExit, Func<decimal, bool> crossed)
    {
        var running = 0m;
        foreach (var trade in byExit)
        {
            running += trade.NetPnl;
            if (crossed(running)) return trade.ExitUtc;
        }
        return null;
    }

    private static TradeNumberBucket[] BuildTradeNumbers(IReadOnlyList<DailyTradeSummary> days, Trade[] todayTrades)
    {
        var buckets = new Dictionary<int, List<Trade>>();
        foreach (var day in days)
        {
            var ordered = day.CompletedTrades.OrderBy(trade => trade.EntryUtc).ThenBy(trade => trade.Sequence).ThenBy(trade => trade.Id).ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                var number = Math.Min(index + 1, TradeNumberCap);
                if (!buckets.TryGetValue(number, out var list)) buckets[number] = list = new List<Trade>();
                list.Add(ordered[index]);
            }
        }

        var todayByNumber = new Dictionary<int, decimal>();
        for (var index = 0; index < todayTrades.Length; index++)
        {
            var number = Math.Min(index + 1, TradeNumberCap);
            todayByNumber[number] = todayByNumber.GetValueOrDefault(number) + todayTrades[index].NetPnl;
        }

        return buckets.OrderBy(pair => pair.Key).Select(pair => new TradeNumberBucket(
            pair.Key == TradeNumberCap ? $"{TradeNumberCap}+" : pair.Key.ToString(CultureInfo.InvariantCulture),
            pair.Key,
            pair.Value.Count,
            pair.Value.Average(trade => trade.NetPnl),
            100m * pair.Value.Count(trade => trade.NetPnl > 0m) / pair.Value.Count,
            todayByNumber.TryGetValue(pair.Key, out var pnl) ? pnl : null)).ToArray();
    }

    private static GreenDayStats BuildGreenDays(IReadOnlyList<DailyTradeSummary> days, DateOnly date)
    {
        if (days.Count == 0) return new GreenDayStats();
        var nets = days.Select(day => day.RealizedNetPnl).ToArray();
        var green = nets.Where(value => value > 0m).ToArray();
        var red = nets.Where(value => value < 0m).ToArray();
        var averageGreen = green.Length > 0 ? green.Average() : (decimal?)null;
        var averageRed = red.Length > 0 ? -red.Average() : (decimal?)null;
        var worstRed = red.Length > 0 ? red.Min() : (decimal?)null;

        var rolling = new List<GreenDayRollingPoint>();
        for (var end = 0; end < days.Count; end++)
        {
            var start = Math.Max(0, end - GreenDayWindow + 1);
            var count = end - start + 1;
            if (count < MinimumComparisonDays) continue;
            var greens = 0;
            for (var index = start; index <= end; index++) if (nets[index] > 0m) greens++;
            rolling.Add(new GreenDayRollingPoint(days[end].Date, count, 100m * greens / count));
        }

        var streak = 0;
        var position = -1;
        for (var index = 0; index < days.Count; index++) if (days[index].Date <= date) position = index;
        if (position >= 0 && nets[position] != 0m)
        {
            var sign = Math.Sign(nets[position]);
            for (var index = position; index >= 0 && Math.Sign(nets[index]) == sign; index--) streak += sign;
        }

        return new GreenDayStats
        {
            DayCount = days.Count,
            GreenDays = green.Length,
            RedDays = red.Length,
            GreenRate = 100m * green.Length / days.Count,
            AverageGreen = averageGreen,
            AverageRed = averageRed,
            PayoffRatio = averageGreen.HasValue && averageRed is > 0m ? averageGreen / averageRed : null,
            WorstRed = worstRed,
            WorstRedInGreenDays = worstRed.HasValue && averageGreen is > 0m ? -worstRed / averageGreen : null,
            StreakAtDate = streak,
            Rolling = rolling
        };
    }

    private static SessionCurve BuildSession(IReadOnlyList<DailyTradeSummary> days, DateOnly date, TimeZoneInfo timeZone)
    {
        if (days.Count == 0) return new SessionCurve { SlotMinutes = SessionSlotMinutes };

        // Completed trades belong to their local exit date, so exit minutes always fall within that day.
        var exits = days.ToDictionary(
            day => day.Date,
            day => day.CompletedTrades.Select(trade => (Minute: Local(trade.ExitUtc!.Value, timeZone).TimeOfDay.TotalMinutes, trade.NetPnl)).ToArray());
        var all = exits.Values.SelectMany(values => values).ToArray();
        var first = (int)Math.Floor(all.Min(value => value.Minute) / SessionSlotMinutes) * SessionSlotMinutes;
        var start = Math.Max(0, first - SessionSlotMinutes);
        var end = Math.Min(24 * 60, (int)Math.Ceiling(all.Max(value => value.Minute) / SessionSlotMinutes) * SessionSlotMinutes + SessionSlotMinutes);
        var slots = Enumerable.Range(0, (end - start) / SessionSlotMinutes + 1).Select(step => start + step * SessionSlotMinutes).ToArray();

        decimal Value((double Minute, decimal NetPnl)[] values, int slot) => values.Where(value => value.Minute < slot).Sum(value => value.NetPnl);

        var average = slots.Select(slot => exits.Values.Sum(values => Value(values, slot)) / exits.Count).ToArray();
        var today = exits.TryGetValue(date, out var todayExits)
            ? slots.Select(slot => (decimal?)Value(todayExits, slot)).ToArray()
            : Array.Empty<decimal?>();
        return new SessionCurve
        {
            SlotMinutes = SessionSlotMinutes,
            Labels = slots.Select(slot => slot >= 24 * 60 ? "24:00" : TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(slot)).ToString("HH:mm", CultureInfo.InvariantCulture)).ToArray(),
            Average = average,
            Today = today,
            DayCount = exits.Count
        };
    }

    private static DailyHeatDay[] BuildHeatWindow(IReadOnlyList<DailyTradeSummary> days, DateOnly date)
    {
        var heatDays = days.Select(DailyHeatMetrics.BuildDay).Where(day => day.MaeObservedTradeCount > 0).ToArray();
        if (heatDays.Length == 0) return heatDays;
        var last = Array.FindLastIndex(heatDays, day => day.Date <= date);
        var end = Math.Min(heatDays.Length - 1, Math.Max(last, HeatWindowDays - 1));
        var start = Math.Max(0, end - HeatWindowDays + 1);
        return heatDays[start..(end + 1)];
    }

    /// <summary>The share of the sample below the value, counting ties as half, as 0–100.</summary>
    internal static decimal Percentile(decimal value, IEnumerable<decimal> sample)
    {
        var values = sample.ToArray();
        if (values.Length == 0) return 50m;
        var below = values.Count(other => other < value);
        var equal = values.Count(other => other == value);
        return Math.Round(100m * (below + equal / 2m) / values.Length, 0);
    }

    private static decimal Median(IEnumerable<decimal> source)
    {
        var values = source.OrderBy(value => value).ToArray();
        if (values.Length == 0) return 0m;
        var middle = values.Length / 2;
        return values.Length % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2m;
    }

    internal static string Ordinal(decimal value)
    {
        var number = (int)Math.Round(value);
        var suffix = (number % 100) is 11 or 12 or 13 ? "th" : (number % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return number.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    private static string Plural(int count, string singular, string plural) => $"{count:N0} {(count == 1 ? singular : plural)}";

    private static string Money(decimal value) => value.ToString("C2", CultureInfo.CurrentCulture);

    private static DateTime Local(DateTimeOffset utc, TimeZoneInfo timeZone) => TimeZoneInfo.ConvertTime(utc, timeZone).DateTime;
}

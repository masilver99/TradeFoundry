using System.Globalization;

namespace TradeFoundry.Services;

public static partial class ChartRenderer
{
    private const string NoMaeMessage = "Daily heat appears after a completed trade with recorded MAE.";

    /// <summary>
    /// One range bar per day: the body runs from flat to the day's net P&amp;L and the
    /// wick runs down to the day's estimated low, so the heat behind a result is visible.
    /// </summary>
    public static string DailyHeatRange(DailyHeatSummary summary) => DailyHeatRange(summary.Days, null);

    /// <param name="highlight">A day to outline, such as the day a daily page is showing.</param>
    public static string DailyHeatRange(IReadOnlyList<DailyHeatDay> days, DateOnly? highlight)
    {
        if (days.Count == 0) return Empty(NoMaeMessage);

        var dates = DayLabels(days);
        var customdata = days.Select(day => new object?[]
        {
            day.WorstPoint,
            day.Recovery,
            day.TotalHeat,
            day.TradeCount,
            DayCoverageNote(day)
        }).ToArray();
        var layout = CartesianLayout();
        layout["barmode"] = "overlay";
        SetAxis(layout, "xaxis", "exit date", category: true);
        SetAxis(layout, "yaxis", "net P&L");
        ApplyDailyTicks(layout, days);
        return Plotly("Daily P&L and intraday low", new object[]
        {
            new
            {
                type = "bar",
                x = dates,
                // The wick fills from the day's low up to the bottom of the body.
                @base = days.Select(day => day.WorstPoint).ToArray(),
                y = days.Select(day => Math.Min(0m, day.NetPnl) - day.WorstPoint).ToArray(),
                width = .18,
                name = "Intraday low",
                marker = new { color = Gold, opacity = .85 },
                hoverinfo = "skip"
            },
            new
            {
                type = "bar",
                x = dates,
                y = days.Select(day => day.NetPnl).ToArray(),
                width = .7,
                name = "Net P&L",
                marker = new
                {
                    color = days.Select(day => day.NetPnl >= 0m ? Green : Red).ToArray(),
                    opacity = days.Select(day => highlight is null || day.Date == highlight ? 1d : .55d).ToArray(),
                    line = new { color = Ink, width = days.Select(day => day.Date == highlight ? 2d : 0d).ToArray() }
                },
                customdata,
                hovertemplate = "%{x}<br>Net P&L: %{y:,.2f}<br>Intraday low: %{customdata[0]:,.2f}<br>Recovered from low: %{customdata[1]:,.2f}<br>Total heat: %{customdata[2]:,.2f}<br>%{customdata[3]} trades%{customdata[4]}<extra></extra>"
            }
        }, layout);
    }

    /// <summary>
    /// Net P&amp;L per currency unit of MAE for each day, with a rolling aggregate that
    /// divides summed P&amp;L by summed heat so small-heat days cannot dominate it.
    /// </summary>
    public static string DailyPnlPerHeat(DailyHeatSummary summary)
        => DailyHeatRatio(
            summary,
            "Net P&L per dollar of heat",
            "net P&L per $1 of MAE",
            day => day.PnlPerHeat,
            point => point.PnlPerHeat,
            day => new object?[] { day.ObservedNetPnl, day.TotalHeat, DayCoverageNote(day) },
            "%{customdata[0]:,.2f} net over %{customdata[1]:,.2f} heat%{customdata[2]}");

    /// <summary>
    /// Heat factor for each day: net P&amp;L divided by how far below flat the day went,
    /// with a rolling aggregate of summed P&amp;L over summed low depth.
    /// </summary>
    public static string DailyHeatFactor(DailyHeatSummary summary)
        => DailyHeatRatio(
            summary,
            "Heat factor",
            "net P&L ÷ intraday low",
            day => day.HeatFactor,
            point => point.HeatFactor,
            day => new object?[] { day.NetPnl, day.WorstPoint, DayCoverageNote(day) },
            "%{customdata[0]:,.2f} net from a %{customdata[1]:,.2f} low%{customdata[2]}");

    private static string DailyHeatRatio(
        DailyHeatSummary summary,
        string title,
        string axisTitle,
        Func<DailyHeatDay, decimal?> dailyValue,
        Func<DailyHeatRollingPoint, decimal?> rollingValue,
        Func<DailyHeatDay, object?[]> customdata,
        string detailTemplate)
    {
        var days = summary.Days.Where(day => dailyValue(day).HasValue).ToArray();
        if (days.Length == 0) return Empty(NoMaeMessage);

        var dates = DayLabels(days);
        var ratios = days.Select(day => dailyValue(day)!.Value).ToArray();
        var rolling = summary.Rolling.ToDictionary(point => point.Date);
        var rollingValues = days
            .Select(day => rolling.TryGetValue(day.Date, out var point) ? rollingValue(point) : null)
            .ToArray();

        var layout = CartesianLayout("x unified");
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = .98 };
        SetAxis(layout, "xaxis", "exit date", category: true);
        SetAxis(layout, "yaxis", axisTitle);
        ApplyDailyTicks(layout, days);
        if (OutlierSafeRange(ratios, rollingValues) is { } range)
            SetRange(layout, "yaxis", range);

        var traces = new List<object>
        {
            new
            {
                type = "bar",
                x = dates,
                y = ratios,
                name = "Daily",
                marker = new { color = ratios.Select(value => value >= 0m ? Green : Red).ToArray(), opacity = .7 },
                customdata = days.Select(customdata).ToArray(),
                hovertemplate = $"Daily: %{{y:.2f}}<br>{detailTemplate}<extra></extra>"
            }
        };
        if (rollingValues.Any(value => value.HasValue))
        {
            traces.Add(new
            {
                type = "scatter",
                x = dates,
                y = rollingValues,
                mode = LineMode(days.Length),
                name = $"{DailyHeatMetrics.RollingWindow}-day",
                connectgaps = false,
                line = new { color = Blue, width = 1.8 },
                marker = new { color = Blue, size = 5 },
                hovertemplate = $"{DailyHeatMetrics.RollingWindow}-day: %{{y:.2f}}<extra></extra>"
            });
        }
        return Plotly(title, traces, layout);
    }

    private static string[] DayLabels(IEnumerable<DailyHeatDay> days)
        => days.Select(day => day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();

    private static string DayCoverageNote(DailyHeatDay day)
    {
        var note = day.HasCompleteMae ? string.Empty : $"<br>MAE on {day.MaeObservedTradeCount}/{day.TradeCount} trades";
        return day.HasOverlap ? note + "<br>Overlapping positions: low is estimated" : note;
    }

    private static void ApplyDailyTicks(Dictionary<string, object?> layout, IReadOnlyList<DailyHeatDay> days)
    {
        if (layout["xaxis"] is not Dictionary<string, object?> axis) return;
        var tickIndexes = EvenlySpacedIndexes(days.Count, DailyTickLimit);
        axis["tickmode"] = "array";
        axis["tickvals"] = tickIndexes.Select(index => days[index].Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
        axis["ticktext"] = tickIndexes.Select(index => days[index].Date.ToString("MMM d", CultureInfo.InvariantCulture)).ToArray();
        axis["tickangle"] = 0;
    }

    /// <summary>
    /// A day with almost no heat produces a huge ratio that flattens every other bar.
    /// Clip the axis to the 5th–95th percentile band (plus the rolling line) when such outliers exist.
    /// </summary>
    private static decimal[]? OutlierSafeRange(IReadOnlyList<decimal> values, IEnumerable<decimal?> rolling)
    {
        if (values.Count < 10) return null;
        var sorted = values.OrderBy(value => value).ToArray();
        var rollingValues = rolling.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var low = Math.Min(0m, sorted[(int)Math.Floor((sorted.Length - 1) * .05)]);
        var high = Math.Max(0m, sorted[(int)Math.Ceiling((sorted.Length - 1) * .95)]);
        if (rollingValues.Length > 0)
        {
            low = Math.Min(low, rollingValues.Min());
            high = Math.Max(high, rollingValues.Max());
        }
        if (sorted[0] >= low && sorted[^1] <= high) return null;
        var pad = Math.Max((high - low) * .15m, .1m);
        return new[] { low - pad, high + pad };
    }
}

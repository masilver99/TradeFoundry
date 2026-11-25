using System.Globalization;

namespace TradeFoundry.Services;

/// <summary>Charts for the day statistics on the daily pages. Each cross-day chart marks the selected day.</summary>
public static partial class ChartRenderer
{
    private const string Highlight = Gold;

    /// <summary>Cumulative realized P&amp;L through the day, one step per exit, with the high point and any daily limits.</summary>
    public static string DayEquity(DayStatisticsReport report)
    {
        if (report.Equity.Count < 2) return Empty("The intraday equity curve appears after a completed trade.");

        var points = report.Equity;
        var times = points.Select(point => LocalIso(point.LocalTime)).ToArray();
        var trades = points.Skip(1).ToArray();
        var layout = CartesianLayout();
        SetAxis(layout, "xaxis", "exit time", date: true);
        SetAxis(layout, "yaxis", "realized net P&L");
        if (layout["xaxis"] is Dictionary<string, object?> xaxis) xaxis["tickformat"] = "%H:%M";

        var shapes = new List<object>();
        var annotations = new List<object>();
        void Level(decimal value, string color, string text)
        {
            shapes.Add(new { type = "line", xref = "paper", x0 = 0, x1 = 1, y0 = value, y1 = value, line = new { color, width = 1, dash = "dot" } });
            annotations.Add(new { xref = "paper", x = 1, xanchor = "right", y = value, yanchor = "bottom", text, showarrow = false, font = new { color, size = 10 } });
        }
        if (report.Peak > 0m)
            Level(report.Peak, Green, report.GiveBack > 0m ? $"high {report.Peak:N2} · gave back {report.GiveBack:N2}" : $"high {report.Peak:N2}");
        if (report.Limits.MaxDailyLoss is { } maxLoss) Level(-maxLoss, Red, "max daily loss");
        if (report.Limits.DailyProfitGoal is { } goal) Level(goal, Blue, "daily goal");
        layout["shapes"] = shapes;
        layout["annotations"] = annotations;

        return Plotly("Intraday realized equity", new object[]
        {
            new
            {
                type = "scatter",
                mode = "lines",
                x = times,
                y = points.Select(point => point.Cumulative).ToArray(),
                line = new { color = Blue, width = 2, shape = "hv" },
                fill = "tozeroy",
                fillcolor = BlueFill,
                hoverinfo = "skip"
            },
            new
            {
                type = "scatter",
                mode = "markers",
                x = trades.Select(point => LocalIso(point.LocalTime)).ToArray(),
                y = trades.Select(point => point.Cumulative).ToArray(),
                marker = new { size = 8, color = trades.Select(point => point.TradePnl >= 0m ? Green : Red).ToArray(), line = new { color = "#161b22", width = 1 } },
                customdata = trades.Select(point => new object?[] { point.Label, point.TradePnl }).ToArray(),
                hovertemplate = "%{x|%H:%M:%S}<br>%{customdata[0]}: %{customdata[1]:,.2f}<br>Day: %{y:,.2f}<extra></extra>"
            }
        }, layout);
    }

    /// <summary>
    /// One range bar per trade of the selected day, in entry order: the body runs from flat to the
    /// trade's net P&amp;L and the wick runs down to its MAE, so the heat behind each result is visible.
    /// </summary>
    public static string DayTradeHeat(DayStatisticsReport report)
    {
        var trades = report.TradeHeat;
        if (trades.Count == 0) return Empty("Trade P&L and heat appear after a completed trade.");

        var labels = trades.Select(trade => $"#{trade.Number}").ToArray();
        var layout = CartesianLayout();
        layout["barmode"] = "overlay";
        SetAxis(layout, "xaxis", "trade (entry order)", category: true);
        SetAxis(layout, "yaxis", "net P&L");
        return Plotly("Trade P&L and heat", new object[]
        {
            new
            {
                type = "bar",
                x = labels,
                // The wick fills from the trade's MAE up to the bottom of the body.
                @base = trades.Select(trade => -(trade.Mae ?? 0m)).ToArray(),
                y = trades.Select(trade => Math.Max(0m, Math.Min(0m, trade.NetPnl) + (trade.Mae ?? 0m))).ToArray(),
                width = .18,
                name = "MAE",
                marker = new { color = Gold, opacity = .85 },
                hoverinfo = "skip"
            },
            new
            {
                type = "bar",
                x = labels,
                y = trades.Select(trade => trade.NetPnl).ToArray(),
                width = .7,
                name = "Net P&L",
                marker = new { color = trades.Select(trade => trade.NetPnl >= 0m ? Green : Red).ToArray() },
                customdata = trades.Select(trade => new object?[]
                {
                    trade.Label,
                    trade.EntryLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    trade.ExitLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    trade.Mae.HasValue ? (-trade.Mae.Value).ToString("N2", CultureInfo.InvariantCulture) : "not recorded"
                }).ToArray(),
                hovertemplate = "%{x} %{customdata[0]}<br>%{customdata[1]} → %{customdata[2]}<br>Net P&L: %{y:,.2f}<br>MAE: %{customdata[3]}<extra></extra>"
            }
        }, layout);
    }

    /// <summary>Average P&amp;L and win rate for the first, second, third … trade of the day, with the selected day's trades.</summary>
    public static string DayTradeNumber(DayStatisticsReport report)
    {
        var buckets = report.TradeNumbers;
        if (buckets.Count == 0) return Empty("Trade-number results appear after a completed trade.");

        var labels = buckets.Select(bucket => bucket.Label).ToArray();
        var layout = CartesianLayout();
        layout["showlegend"] = true;
        layout["legend"] = new { orientation = "h", x = 0, y = 1.12 };
        layout["margin"] = new { l = 48, r = 48, t = 28, b = 48 };
        SetAxis(layout, "xaxis", "trade number in the day", category: true);
        SetAxis(layout, "yaxis", "average net P&L");
        var winAxis = Axis();
        winAxis["overlaying"] = "y";
        winAxis["side"] = "right";
        winAxis["showgrid"] = false;
        winAxis["zeroline"] = false;
        winAxis["range"] = new[] { 0, 100 };
        winAxis["ticksuffix"] = "%";
        layout["yaxis2"] = winAxis;

        var traces = new List<object>
        {
            new
            {
                type = "bar",
                x = labels,
                y = buckets.Select(bucket => bucket.AveragePnl).ToArray(),
                name = "Average P&L",
                marker = new { color = buckets.Select(bucket => bucket.AveragePnl >= 0m ? Green : Red).ToArray(), opacity = .7 },
                customdata = buckets.Select(bucket => bucket.TradeCount).ToArray(),
                hovertemplate = "Trade %{x}<br>Average: %{y:,.2f}<br>%{customdata} trades<extra></extra>"
            },
            new
            {
                type = "scatter",
                mode = "lines+markers",
                x = labels,
                y = buckets.Select(bucket => bucket.WinRate).ToArray(),
                yaxis = "y2",
                name = "Win rate",
                line = new { color = Blue, width = 1.8 },
                marker = new { color = Blue, size = 5 },
                hovertemplate = "Win rate: %{y:.0f}%<extra></extra>"
            }
        };
        var today = buckets.Where(bucket => bucket.TodayPnl.HasValue).ToArray();
        if (today.Length > 0)
        {
            traces.Add(new
            {
                type = "scatter",
                mode = "markers",
                x = today.Select(bucket => bucket.Label).ToArray(),
                y = today.Select(bucket => bucket.TodayPnl).ToArray(),
                name = "This day",
                marker = new { symbol = "diamond", size = 10, color = Highlight, line = new { color = "#161b22", width = 1 } },
                hovertemplate = "This day, trade %{x}: %{y:,.2f}<extra></extra>"
            });
        }
        return Plotly("P&L by trade number", traces, layout);
    }

    /// <summary>Share of green days over a trailing window, with the selected day marked.</summary>
    public static string DayGreenRate(DayStatisticsReport report)
    {
        var rolling = report.GreenDays.Rolling;
        if (rolling.Count == 0) return Empty($"The green-day rate appears after {DayStatistics.MinimumComparisonDays} trading days.");

        var dates = rolling.Select(point => point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
        var layout = CartesianLayout("x unified");
        SetAxis(layout, "xaxis", "exit date", category: true);
        SetAxis(layout, "yaxis", "green days", percent: true);
        SetRange(layout, "yaxis", new[] { 0, 100 });
        if (layout["xaxis"] is Dictionary<string, object?> axis)
        {
            var ticks = EvenlySpacedIndexes(rolling.Count, DailyTickLimit);
            axis["tickmode"] = "array";
            axis["tickvals"] = ticks.Select(index => dates[index]).ToArray();
            axis["ticktext"] = ticks.Select(index => rolling[index].Date.ToString("MMM d", CultureInfo.InvariantCulture)).ToArray();
            axis["tickangle"] = 0;
        }
        layout["shapes"] = new object[] { new { type = "line", xref = "paper", x0 = 0, x1 = 1, y0 = 50, y1 = 50, line = new { color = Zero, width = 1, dash = "dot" } } };

        var traces = new List<object>
        {
            new
            {
                type = "scatter",
                mode = LineMode(rolling.Count),
                x = dates,
                y = rolling.Select(point => point.GreenRate).ToArray(),
                line = new { color = Blue, width = 1.8 },
                marker = new { color = Blue, size = 5 },
                customdata = rolling.Select(point => point.WindowDayCount).ToArray(),
                hovertemplate = "%{y:.0f}% green over the last %{customdata} days<extra></extra>"
            }
        };
        var selected = rolling.FirstOrDefault(point => point.Date == report.Date);
        if (selected is not null)
        {
            traces.Add(new
            {
                type = "scatter",
                mode = "markers",
                x = new[] { selected.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                y = new[] { selected.GreenRate },
                marker = new { symbol = "diamond", size = 11, color = Highlight },
                hovertemplate = "This day: %{y:.0f}%<extra></extra>"
            });
        }
        return Plotly($"Green days, trailing {DayStatistics.GreenDayWindow}", traces, layout);
    }

    /// <summary>Each trading day's trade count against its net P&amp;L, with the selected day marked.</summary>
    public static string DayTradeCountScatter(DayStatisticsReport report)
    {
        var days = report.Days;
        if (days.Count < 2) return Empty("This chart appears after two trading days.");

        var layout = CartesianLayout();
        SetAxis(layout, "xaxis", "completed trades in the day", linear: true);
        SetAxis(layout, "yaxis", "net P&L");
        if (layout["xaxis"] is Dictionary<string, object?> axis) axis["rangemode"] = "tozero";
        return Plotly("Trade count vs daily P&L", DayScatterTraces(days, report.Date, day => day.TradeCount + Jitter(day.Date, .25), day => day.TradeCount.ToString(CultureInfo.InvariantCulture) + " trades"), layout);
    }

    /// <summary>Each rated day's process rating against its net P&amp;L, with the average per rating.</summary>
    public static string DayRatingScatter(DayStatisticsReport report)
    {
        var rated = report.Days.Where(day => day.Rating.HasValue).ToArray();
        if (rated.Length < 3) return Empty("This chart appears after three rated trading days. Rate days from Trade Review.");

        var layout = CartesianLayout();
        SetAxis(layout, "xaxis", "day rating", linear: true);
        SetAxis(layout, "yaxis", "net P&L");
        if (layout["xaxis"] is Dictionary<string, object?> axis)
        {
            axis["range"] = new[] { .5, 5.5 };
            axis["tickvals"] = new[] { 1, 2, 3, 4, 5 };
            axis["zeroline"] = false;
        }
        var traces = DayScatterTraces(rated, report.Date, day => day.Rating!.Value + Jitter(day.Date, .18), day => $"rated {day.Rating}/5");
        var averages = rated.GroupBy(day => day.Rating!.Value).OrderBy(group => group.Key).ToArray();
        traces.Insert(0, new
        {
            type = "scatter",
            mode = "markers",
            x = averages.Select(group => group.Key).ToArray(),
            y = averages.Select(group => group.Average(day => day.NetPnl)).ToArray(),
            marker = new { symbol = "line-ew", size = 28, line = new { color = Ink, width = 2 } },
            customdata = averages.Select(group => group.Count()).ToArray(),
            hovertemplate = "Rated %{x}: average %{y:,.2f} over %{customdata} days<extra></extra>"
        });
        return Plotly("Day rating vs daily P&L", traces, layout);
    }

    /// <summary>Average cumulative realized P&amp;L through the session across all days, with the selected day's curve.</summary>
    public static string DaySessionCurve(DayStatisticsReport report)
    {
        var session = report.Session;
        if (session.Labels.Count == 0 || session.DayCount < 2) return Empty("The session curve appears after two trading days.");

        var layout = CartesianLayout("x unified");
        layout["showlegend"] = true;
        layout["legend"] = new { orientation = "h", x = 0, y = 1.12 };
        layout["margin"] = new { l = 48, r = 16, t = 28, b = 48 };
        SetAxis(layout, "xaxis", $"local time ({session.SlotMinutes}-minute steps)", category: true);
        SetAxis(layout, "yaxis", "cumulative net P&L");
        if (layout["xaxis"] is Dictionary<string, object?> axis)
        {
            var ticks = EvenlySpacedIndexes(session.Labels.Count, DailyTickLimit);
            axis["tickmode"] = "array";
            axis["tickvals"] = ticks.Select(index => session.Labels[index]).ToArray();
            axis["tickangle"] = 0;
        }

        var traces = new List<object>
        {
            new
            {
                type = "scatter",
                mode = "lines",
                x = session.Labels,
                y = session.Average,
                name = $"Average of {session.DayCount:N0} days",
                line = new { color = Blue, width = 2 },
                fill = "tozeroy",
                fillcolor = BlueFill,
                hovertemplate = "Average: %{y:,.2f}<extra></extra>"
            }
        };
        if (session.Today.Count > 0)
        {
            traces.Add(new
            {
                type = "scatter",
                mode = "lines",
                x = session.Labels,
                y = session.Today,
                name = "This day",
                line = new { color = Highlight, width = 2, shape = "hv" },
                hovertemplate = "This day: %{y:,.2f}<extra></extra>"
            });
        }
        return Plotly("Average P&L through the session", traces, layout);
    }

    private static List<object> DayScatterTraces(IReadOnlyList<DayPoint> days, DateOnly selected, Func<DayPoint, double> x, Func<DayPoint, string> detail)
    {
        var others = days.Where(day => day.Date != selected).ToArray();
        var traces = new List<object>
        {
            new
            {
                type = "scatter",
                mode = "markers",
                x = others.Select(x).ToArray(),
                y = others.Select(day => day.NetPnl).ToArray(),
                marker = new { size = 7, color = others.Select(day => day.NetPnl >= 0m ? Green : Red).ToArray(), opacity = .6 },
                customdata = others.Select(day => new object[] { day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), detail(day) }).ToArray(),
                hovertemplate = "%{customdata[0]}<br>%{customdata[1]}<br>Net P&L: %{y:,.2f}<extra></extra>"
            }
        };
        var today = days.FirstOrDefault(day => day.Date == selected);
        if (today is not null)
        {
            traces.Add(new
            {
                type = "scatter",
                mode = "markers",
                x = new[] { x(today) },
                y = new[] { today.NetPnl },
                marker = new { symbol = "diamond", size = 13, color = Highlight, line = new { color = "#161b22", width = 1 } },
                customdata = new[] { new object[] { today.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), detail(today) } },
                hovertemplate = "This day · %{customdata[0]}<br>%{customdata[1]}<br>Net P&L: %{y:,.2f}<extra></extra>"
            });
        }
        return traces;
    }

    /// <summary>A small, stable horizontal offset so days with the same count or rating do not stack exactly.</summary>
    private static double Jitter(DateOnly date, double spread)
    {
        var hash = (uint)(date.DayNumber * 2654435761u);
        return ((hash % 1000) / 999d - .5) * spread;
    }

    private static string LocalIso(DateTime local) => local.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
}

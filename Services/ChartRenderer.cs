using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using TradeFoundry.Core;

namespace TradeFoundry.Services;

public static partial class ChartRenderer
{
    private const string Gold = "#d29922";
    private const string Blue = "#58a6ff";
    private const string Green = "#3fb950";
    private const string Red = "#f85149";
    private const string Purple = "#8b949e";
    private const string Ink = "#c9d1d9";
    private const string Muted = "#8b949e";
    private const string Grid = "#30363d";
    private const string Zero = "#30363d";

    public static string Equity(IEnumerable<EquityPoint> source)
    {
        var points = source.OrderBy(x => x.ExitUtc).ToArray();
        if (points.Length == 0) return Empty("Import a completed trade to see the equity curve.");

        var x = WithStart(points.Select(point => point.ExitUtc), points[0].ExitUtc);
        var y = new[] { 0m }.Concat(points.Select(point => point.CumulativePnl)).ToArray();
        return Line(x, y, "cumulative net P&L", Blue, "exit date");
    }

    public static string Equity(IEnumerable<DailyPnl> source)
    {
        var days = source
            .GroupBy(x => x.Date)
            .OrderBy(x => x.Key)
            .Select(x => (Date: x.Key, NetPnl: x.Sum(day => day.NetPnl)))
            .ToArray();
        if (days.Length == 0) return Empty("Import a completed trade to see the equity curve.");

        var cumulative = 0m;
        var values = days.Select(day =>
        {
            cumulative += day.NetPnl;
            return cumulative;
        }).ToArray();
        var x = new[] { days[0].Date.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
            .Concat(days.Select(day => day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            .ToArray();
        var y = new[] { 0m }.Concat(values).ToArray();
        return Line(x, y, "daily cumulative net P&L", Blue, "exit date");
    }

    public static string Equity(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source);
        if (trades.Length == 0) return Empty("Import a completed trade to see the equity curve.");

        var cumulative = 0m;
        var values = trades.Select(trade =>
        {
            cumulative += trade.NetPnl;
            return cumulative;
        }).ToArray();
        var x = WithStart(trades.Select(trade => trade.ExitUtc!.Value), trades[0].ExitUtc!.Value);
        var y = new[] { 0m }.Concat(values).ToArray();
        return Line(x, y, "cumulative net P&L", Blue, "exit date");
    }

    public static string BenchmarkComparison(IEnumerable<Trade> source, IEnumerable<BenchmarkPoint> benchmarkSource, decimal? startingEquity)
    {
        var trades = ClosedTrades(source);
        if (trades.Length == 0) return Empty("Benchmark comparison appears after the first completed trade.");
        if (!startingEquity.HasValue || startingEquity <= 0m) return Empty("Set starting equity in journal settings to compare percentage returns.");

        var benchmark = benchmarkSource
            .Where(x => x.Value > 0m)
            .GroupBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()?
            .OrderBy(x => x.EventUtc)
            .ToArray() ?? Array.Empty<BenchmarkPoint>();
        if (benchmark.Length == 0) return Empty("Import a benchmark daily series to compare percentage returns.");

        var firstTrade = trades[0].ExitUtc!.Value;
        var lastTrade = trades[^1].ExitUtc!.Value;
        var basePoint = benchmark.LastOrDefault(x => x.EventUtc <= firstTrade) ?? benchmark[0];
        var comparisonPoints = benchmark
            .Where(x => x.EventUtc >= basePoint.EventUtc && x.EventUtc <= lastTrade.AddDays(1))
            .ToArray();
        if (comparisonPoints.Length == 0) comparisonPoints = new[] { basePoint };

        var cumulative = 0m;
        var strategyValues = trades.Select(trade =>
        {
            cumulative += trade.NetPnl;
            return cumulative / startingEquity.Value * 100m;
        }).ToArray();
        var strategyX = WithStart(trades.Select(x => x.ExitUtc!.Value), firstTrade);
        var strategyY = new[] { 0m }.Concat(strategyValues).ToArray();
        var benchmarkBase = basePoint.Value;
        var benchmarkX = comparisonPoints.Select(x => x.EventUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).ToArray();
        var benchmarkY = comparisonPoints.Select(x => (x.Value / benchmarkBase - 1m) * 100m).ToArray();

        var layout = CartesianLayout("x unified");
        layout["showlegend"] = true;
        SetAxis(layout, "xaxis", "date", date: true);
        SetAxis(layout, "yaxis", "return", percent: true);
        return Plotly("Strategy versus benchmark returns", new object[]
        {
            new
            {
                type = "scatter", x = strategyX, y = strategyY, mode = "lines+markers", name = "Strategy",
                line = new { color = Blue, width = 2 }, marker = new { color = Blue, size = 5 },
                hovertemplate = "%{x|%b %-d, %Y}<br>Strategy: %{y:.2f}%<extra></extra>"
            },
            new
            {
                type = "scatter", x = benchmarkX, y = benchmarkY, mode = "lines", name = benchmark[0].Symbol,
                line = new { color = Gold, width = 1.5, dash = "dot" },
                hovertemplate = $"%{{x|%b %-d, %Y}}<br>{benchmark[0].Symbol}: %{{y:.2f}}%<extra></extra>"
            }
        }, layout);
    }

    public static string RMultipleDistribution(IEnumerable<Trade> source)
    {
        var values = ClosedTrades(source).Where(x => x.RMultiple.HasValue).Select(x => x.RMultiple!.Value).ToArray();
        if (values.Length == 0) return Empty("R-multiple data appears after importing initial stop orders.");

        var layout = CartesianLayout();
        layout["bargap"] = .08;
        layout["shapes"] = new object[] { VerticalMarker(1m, Green), VerticalMarker(2m, Gold) };
        SetAxis(layout, "xaxis", "R multiple");
        SetAxis(layout, "yaxis", "trade count");
        return Plotly("R-multiple distribution", new object[] { HistogramTrace(values, "R multiple", Blue) }, layout);
    }

    public static string ExitTypeAnalysis(IEnumerable<Trade> source)
    {
        var values = ClosedTrades(source)
            .Where(x => !string.IsNullOrWhiteSpace(x.ExitType))
            .GroupBy(x => x.ExitType, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => (
                Label: x.Key,
                Value: x.Average(t => t.NetPnl),
                Detail: $"{x.Count()} trades · {x.Count(t => t.NetPnl > 0m) * 100m / x.Count():0.#}% win rate"))
            .ToArray();
        return values.Length == 0
            ? Empty("Exit-type analysis appears after importing classified exit orders.")
            : Bar(values, "average net P&L by exit type", Blue, "average net P&L");
    }

    public static string OrderExecution(IEnumerable<OrderEvent> source)
    {
        var events = source.ToArray();
        if (events.Length == 0) return Empty("Order-execution analysis appears after importing Sierra order rows.");

        var lifecycles = events
            .GroupBy(x => $"{x.Account}\u001f{x.Symbol}\u001f{(string.IsNullOrWhiteSpace(x.InternalOrderId) ? x.SourceKey : x.InternalOrderId)}", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.OrderBy(e => e.EventUtc).ThenBy(e => e.RowNumber).ToArray())
            .ToArray();
        var total = lifecycles.Length;
        var filled = lifecycles.Count(x => x.Any(e => e.OrderStatus.Equals("Filled", StringComparison.OrdinalIgnoreCase)));
        var canceled = lifecycles.Count(x => x[^1].OrderStatus.Equals("Canceled", StringComparison.OrdinalIgnoreCase));
        var modified = lifecycles.Count(x => x.Any(e => e.OrderStatus.Contains("modify", StringComparison.OrdinalIgnoreCase)));
        var partial = lifecycles.Count(x => x.Any(e => e.OrderStatus.Contains("partial", StringComparison.OrdinalIgnoreCase) || e.OrderActionSource.Contains("partial", StringComparison.OrdinalIgnoreCase)));
        var values = new[]
        {
            (Label: "Fill rate", Value: Percent(filled, total), Detail: $"{filled} of {total} orders"),
            (Label: "Cancel rate", Value: Percent(canceled, total), Detail: $"{canceled} of {total} orders"),
            (Label: "Modify rate", Value: Percent(modified, total), Detail: $"{modified} of {total} orders"),
            (Label: "Partial-fill rate", Value: Percent(partial, total), Detail: $"{partial} of {total} orders")
        };
        var layout = CartesianLayout();
        SetAxis(layout, "xaxis", string.Empty);
        SetAxis(layout, "yaxis", "orders", percent: true);
        SetRange(layout, "yaxis", new[] { 0, 100 });
        return Plotly("Order execution rates", new object[]
        {
            new
            {
                type = "bar",
                x = values.Select(x => x.Label).ToArray(),
                y = values.Select(x => x.Value).ToArray(),
                customdata = values.Select(x => new[] { x.Detail }).ToArray(),
                marker = new { color = new[] { Green, Red, Purple, Blue } },
                hovertemplate = "%{x}<br>%{y:.1f}%<br>%{customdata[0]}<extra></extra>"
            }
        }, layout);
    }

    public static string Daily(IEnumerable<DailyPnl> daily)
    {
        var values = daily.OrderBy(x => x.Date).ToArray();
        if (values.Length == 0) return Empty("Daily bars will appear after the first closed trade.");

        var layout = CartesianLayout();
        SetAxis(layout, "xaxis", "exit date", date: true);
        SetAxis(layout, "yaxis", "net P&L");
        return Plotly("Daily net P&L", new object[]
        {
            new
            {
                type = "bar",
                x = values.Select(x => x.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray(),
                y = values.Select(x => x.NetPnl).ToArray(),
                customdata = values.Select(x => new object[] { x.TradeCount }).ToArray(),
                marker = new { color = values.Select(x => x.NetPnl >= 0m ? Green : Red).ToArray() },
                hovertemplate = "%{x}<br>%{y:,.2f}<br>%{customdata[0]} trades<extra></extra>"
            }
        }, layout);
    }

    public static string FeeDrag(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source);
        if (trades.Length == 0) return Empty("Import a completed trade to compare gross and net equity.");

        var gross = 0m;
        var net = 0m;
        var grossValues = trades.Select(trade =>
        {
            gross += trade.GrossPnl;
            return gross;
        }).ToArray();
        var netValues = trades.Select(trade =>
        {
            net += trade.NetPnl;
            return net;
        }).ToArray();
        var x = WithStart(trades.Select(trade => trade.ExitUtc!.Value), trades[0].ExitUtc!.Value);
        var layout = CartesianLayout("x unified");
        SetAxis(layout, "xaxis", "exit date", date: true);
        SetAxis(layout, "yaxis", "cumulative P&L");
        return Plotly("Gross and net cumulative P&L", new object[]
        {
            LineTrace(x, new[] { 0m }.Concat(grossValues).ToArray(), "Gross P&L", Blue),
            LineTrace(x, new[] { 0m }.Concat(netValues).ToArray(), "Net P&L", Green)
        }, layout);
    }

    public static string Drawdown(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source);
        if (trades.Length == 0) return Empty("Drawdown appears after the first completed trade.");

        var equity = 0m;
        var peak = 0m;
        var drawdown = trades.Select(trade =>
        {
            equity += trade.NetPnl;
            peak = Math.Max(peak, equity);
            return equity - peak;
        }).ToArray();
        var x = WithStart(trades.Select(trade => trade.ExitUtc!.Value), trades[0].ExitUtc!.Value);
        var layout = CartesianLayout();
        SetAxis(layout, "xaxis", "exit date", date: true);
        SetAxis(layout, "yaxis", "underwater net P&L");
        return Plotly("Underwater net P&L", new object[]
        {
            LineTrace(x, new[] { 0m }.Concat(drawdown).ToArray(), "underwater net P&L", Red)
        }, layout);
    }

    public static string MonthlyPnl(IEnumerable<Trade> source, string? timeZoneId = null)
    {
        var trades = ClosedTrades(source);
        if (trades.Length == 0) return Empty("Monthly results appear after the first completed trade.");

        var values = trades.GroupBy(x =>
            {
                var exit = timeZoneId is null ? x.ExitUtc!.Value : InZone(x.ExitUtc!.Value, timeZoneId);
                return new DateOnly(exit.Year, exit.Month, 1);
            })
            .OrderBy(x => x.Key)
            .Select(x => (
                Label: x.Key.ToString("MMM yy", CultureInfo.InvariantCulture),
                Value: x.Sum(t => t.NetPnl),
                Detail: $"{x.Key:yyyy-MM} · {x.Count()} trades"))
            .ToArray();
        return Bar(values, "monthly net P&L", Green, "net P&L");
    }

    public static string DailyDistribution(IEnumerable<Trade> source)
    {
        var daily = ClosedTrades(source)
            .GroupBy(x => DateOnly.FromDateTime(x.ExitUtc!.Value.UtcDateTime.Date))
            .Select(x => x.Sum(t => t.NetPnl))
            .ToArray();
        return daily.Length == 0
            ? Empty("Daily P&L distribution appears after the first completed trade.")
            : Histogram(daily, "daily net P&L distribution", Green);
    }

    public static string TradePnlWaterfall(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source);
        if (trades.Length == 0) return Empty("The waterfall appears after the first completed trade.");

        var layout = CartesianLayout();
        SetAxis(layout, "xaxis", "trade");
        SetAxis(layout, "yaxis", "net P&L");
        return Plotly("Trade P&L waterfall", new object[]
        {
            new
            {
                type = "waterfall",
                x = trades.Select((_, index) => $"T{index + 1}").ToArray(),
                y = trades.Select(trade => trade.NetPnl).ToArray(),
                measure = trades.Select(_ => "relative").ToArray(),
                customdata = trades.Select(trade => new[] { $"{trade.Symbol} · {Money(trade.NetPnl)}" }).ToArray(),
                connector = new { line = new { color = Grid, width = 1 } },
                increasing = new { marker = new { color = Green } },
                decreasing = new { marker = new { color = Red } },
                totals = new { marker = new { color = Gold } },
                hovertemplate = "%{x}<br>%{y:,.2f}<br>%{customdata[0]}<extra></extra>"
            }
        }, layout);
    }

    public static string TradePnlDistribution(IEnumerable<Trade> source)
    {
        var values = ClosedTrades(source).Select(x => x.NetPnl).ToArray();
        return values.Length == 0
            ? Empty("Trade P&L distribution appears after the first completed trade.")
            : Histogram(values, "trade net P&L distribution", Blue);
    }

    public static string WinnersLosersDistribution(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source);
        if (trades.Length == 0) return Empty("Winner and loser distributions appear after the first completed trade.");

        var winners = trades.Where(x => x.NetPnl > 0m).Select(x => x.NetPnl).ToArray();
        var losers = trades.Where(x => x.NetPnl < 0m).Select(x => x.NetPnl).ToArray();
        var all = winners.Concat(losers).ToArray();
        if (all.Length == 0) return Empty("No winner or loser P&L values.");

        var layout = CartesianLayout();
        layout["barmode"] = "overlay";
        layout["bargap"] = .08;
        SetAxis(layout, "xaxis", "net P&L");
        SetAxis(layout, "yaxis", "trade count");
        var traces = new List<object>();
        if (winners.Length > 0) traces.Add(HistogramTrace(winners, "winners", Green));
        if (losers.Length > 0) traces.Add(HistogramTrace(losers, "losers", Red));
        return Plotly("Winner and loser P&L distributions", traces, layout);
    }

    public static string MfeMae(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source)
            .Where(x => x.MaePoints.HasValue && x.MfePoints.HasValue)
            .ToArray();
        if (trades.Length == 0) return Empty("MFE and MAE require Sierra high/low-during-position fields.");

        var layout = CartesianLayout();
        SetAxis(layout, "xaxis", "MAE (points)");
        SetAxis(layout, "yaxis", "MFE (points)");
        return Plotly("MAE versus MFE", new object[]
        {
            new
            {
                type = "scatter",
                x = trades.Select(x => x.MaePoints!.Value).ToArray(),
                y = trades.Select(x => x.MfePoints!.Value).ToArray(),
                mode = "markers",
                marker = new
                {
                    color = trades.Select(x => x.NetPnl >= 0m ? Green : Red).ToArray(),
                    size = 8,
                    opacity = .82
                },
                customdata = trades.Select(x => new[] { $"{x.Symbol} · {x.Direction} · {Money(x.NetPnl)}" }).ToArray(),
                hovertemplate = "%{customdata[0]}<br>MAE: %{x:,.2f}<br>MFE: %{y:,.2f}<extra></extra>"
            }
        }, layout);
    }

    public static string DurationProfit(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source).Where(x => x.Duration.HasValue).ToArray();
        if (trades.Length == 0) return Empty("Duration analysis appears after the first completed trade.");

        var layout = CartesianLayout();
        SetAxis(layout, "xaxis", "duration (minutes)");
        SetAxis(layout, "yaxis", "net P&L");
        return Plotly("Time in trade versus net P&L", new object[]
        {
            new
            {
                type = "scatter",
                x = trades.Select(x => (decimal)x.Duration!.Value.TotalMinutes).ToArray(),
                y = trades.Select(x => x.NetPnl).ToArray(),
                mode = "markers",
                marker = new
                {
                    color = trades.Select(x => x.NetPnl >= 0m ? Green : Red).ToArray(),
                    size = 8,
                    opacity = .82
                },
                customdata = trades.Select(x => new[] { $"{x.Symbol} · {x.Direction} · {Money(x.NetPnl)}" }).ToArray(),
                hovertemplate = "%{customdata[0]}<br>%{x:,.1f} minutes<br>%{y:,.2f}<extra></extra>"
            }
        }, layout);
    }

    public static string HoldingTimeEfficiency(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source).Where(x => x.Duration.HasValue).ToArray();
        if (trades.Length == 0) return Empty("Holding-time efficiency appears after the first completed trade.");

        var bucketLabels = new[] { "< 1m", "1–5m", "5–15m", "15–30m", "30–60m", "60m+" };
        var values = trades.GroupBy(x => DurationBucket(x.Duration!.Value))
            .OrderBy(x => x.Key)
            .Select(x => (
                Label: bucketLabels[x.Key],
                Value: x.Average(t => t.NetPnl),
                Detail: $"{x.Count()} trades · {x.Count(t => t.NetPnl > 0m) * 100m / x.Count():0.#}% win rate"))
            .ToArray();
        return Bar(values, "expectancy by holding-time bucket", Green);
    }

    public static string TimingHeatmap(IEnumerable<Trade> source, string? timeZoneId)
    {
        var trades = ClosedTrades(source);
        if (trades.Length == 0) return Empty("Timing heatmap appears after the first completed trade.");

        var cells = trades.GroupBy(x =>
        {
            var local = InZone(x.EntryUtc, timeZoneId);
            return (Day: ((int)local.DayOfWeek + 6) % 7, Hour: local.Hour);
        }).ToDictionary(x => x.Key, x => (Average: x.Average(t => t.NetPnl), Count: x.Count()));
        var days = new[] { "Mon", "Tue", "Wed", "Thu", "Fri" };
        var hours = Enumerable.Range(0, 24).ToArray();
        var z = days.Select((_, day) => hours.Select(hour =>
            cells.TryGetValue((day, hour), out var cell) ? (decimal?)cell.Average : null).ToArray()).ToArray();
        var text = days.Select((_, day) => hours.Select(hour =>
            cells.TryGetValue((day, hour), out var cell) ? cell.Count.ToString(CultureInfo.InvariantCulture) : string.Empty).ToArray()).ToArray();

        var layout = CartesianLayout();
        layout["height"] = 390;
        layout["margin"] = new { l = 52, r = 24, t = 18, b = 54 };
        SetAxis(layout, "xaxis", "entry hour");
        SetAxis(layout, "yaxis", "entry day");
        return Plotly("P&L by entry day and hour", new object[]
        {
            new
            {
                type = "heatmap",
                x = hours.Select(hour => hour.ToString("00", CultureInfo.InvariantCulture)).ToArray(),
                y = days,
                z,
                text,
                texttemplate = "%{text}",
                textfont = new { color = Ink, size = 10 },
                colorscale = new object[]
                {
                    new object[] { 0, Red },
                    new object[] { .5, Grid },
                    new object[] { 1, Green }
                },
                zmid = 0,
                colorbar = new { title = "avg P&L", tickformat = ",.0f" },
                hovertemplate = "%{y} %{x}:00<br>Average: %{z:,.2f}<extra></extra>"
            }
        }, layout);
    }

    public static string PositionSizeSensitivity(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source).Where(x => x.Quantity > 0).ToArray();
        if (trades.Length == 0) return Empty("Position-size sensitivity appears after the first completed trade.");

        var values = trades.GroupBy(x => x.Quantity)
            .OrderBy(x => x.Key)
            .Select(x => (
                Label: x.Key.ToString(CultureInfo.InvariantCulture),
                Value: x.Average(t => t.NetPnl),
                Detail: $"{x.Count()} trades · {x.Count(t => t.NetPnl > 0m) * 100m / x.Count():0.#}% win rate"))
            .ToArray();
        return Bar(values, "expectancy by contracts traded", Blue);
    }

    public static string ProfitConcentration(IEnumerable<Trade> source)
    {
        var winners = ClosedTrades(source)
            .Where(x => x.NetPnl > 0m)
            .Select(x => x.NetPnl)
            .OrderBy(x => x)
            .ToArray();
        if (winners.Length == 0) return Empty("Profit concentration appears after the first winning trade.");

        var total = winners.Sum();
        var running = 0m;
        var x = new List<decimal> { 0m };
        var y = new List<decimal> { 0m };
        for (var i = 0; i < winners.Length; i++)
        {
            running += winners[i];
            x.Add((decimal)(i + 1) / winners.Length * 100m);
            y.Add(running / total * 100m);
        }

        var layout = CartesianLayout();
        SetAxis(layout, "xaxis", "share of winning trades", percent: true);
        SetAxis(layout, "yaxis", "share of winner profit", percent: true);
        return Plotly("Winner profit concentration", new object[]
        {
            new
            {
                type = "scatter",
                x = new[] { 0m, 100m },
                y = new[] { 0m, 100m },
                mode = "lines",
                line = new { color = Muted, dash = "dash", width = 1.5 },
                hoverinfo = "skip",
                showlegend = false
            },
            new
            {
                type = "scatter",
                x = x.ToArray(),
                y = y.ToArray(),
                mode = "lines+markers",
                line = new { color = Blue, width = 2 },
                marker = new { color = Blue, size = 5 },
                hovertemplate = "Winning trades: %{x:.1f}%<br>Profit share: %{y:.1f}%<extra></extra>",
                showlegend = false
            }
        }, layout);
    }

    public static string StreakState(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source);
        if (trades.Length < 2) return Empty("Streak-state analysis needs at least two completed trades.");

        var groups = new Dictionary<string, List<decimal>>(StringComparer.Ordinal);
        var priorResult = 0;
        var priorLength = 0;
        for (var i = 0; i < trades.Length; i++)
        {
            if (i > 0 && priorResult != 0)
            {
                var label = $"{(priorResult > 0 ? "W" : "L")}{(priorLength >= 3 ? "3+" : priorLength)}";
                if (!groups.TryGetValue(label, out var results)) groups[label] = results = new();
                results.Add(trades[i].NetPnl);
            }

            var result = Math.Sign(trades[i].NetPnl);
            if (result == 0)
            {
                priorResult = 0;
                priorLength = 0;
            }
            else if (result == priorResult)
            {
                priorLength++;
            }
            else
            {
                priorResult = result;
                priorLength = 1;
            }
        }

        var values = groups.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => (
                Label: x.Key,
                Value: x.Value.Average(),
                Detail: $"{x.Value.Count} next trades"))
            .ToArray();
        return values.Length == 0
            ? Empty("No non-neutral streak states were found.")
            : Bar(values, "next-trade expectancy after a streak", Green);
    }

    public static string WinRateOverTime(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source);
        if (trades.Length == 0) return Empty("Rolling win rate appears after the first completed trade.");

        const int window = 10;
        var values = trades.Select((_, index) =>
        {
            var start = Math.Max(0, index - window + 1);
            var slice = trades[start..(index + 1)];
            return (decimal)slice.Count(x => x.NetPnl > 0m) / slice.Length * 100m;
        }).ToArray();
        var x = trades.Select((_, index) => $"T{index + 1}").ToArray();
        var layout = CartesianLayout();
        SetAxis(layout, "xaxis", "trade");
        SetAxis(layout, "yaxis", "rolling win rate", percent: true);
        SetRange(layout, "yaxis", new[] { 0, 100 });
        return Plotly("Rolling win rate", new object[]
        {
            new
            {
                type = "scatter",
                x,
                y = values,
                mode = "lines+markers",
                line = new { color = Blue, width = 1.5 },
                marker = new { color = Blue, size = 5 },
                hovertemplate = "%{x}<br>%{y:.1f}% win rate<extra></extra>"
            }
        }, layout);
    }

    public static string TradeMix(IEnumerable<Trade> source, string? timeZoneId)
    {
        var trades = ClosedTrades(source);
        if (trades.Length == 0) return Empty("Trade mix appears after the first completed trade.");

        var layout = CartesianLayout();
        layout["height"] = 330;
        layout["showlegend"] = false;
        layout["margin"] = new { l = 12, r = 12, t = 10, b = 42 };
        layout.Remove("xaxis");
        layout.Remove("yaxis");
        layout["annotations"] = new object[]
        {
            Annotation("Direction", .17, .04),
            Annotation("Session", .50, .04),
            Annotation("Outcome", .83, .04)
        };
        return Plotly("Trade mix by direction, session, and outcome", new object[]
        {
            Pie("Direction", trades.GroupBy(x => x.Direction).ToDictionary(x => x.Key, x => x.Count()), .02, .32),
            Pie("Session", trades.GroupBy(x => SessionName(x.EntryUtc, timeZoneId)).ToDictionary(x => x.Key, x => x.Count()), .35, .65),
            Pie("Outcome", new Dictionary<string, int>
            {
                ["Wins"] = trades.Count(x => x.NetPnl > 0m),
                ["Losses"] = trades.Count(x => x.NetPnl < 0m),
                ["Breakeven"] = trades.Count(x => x.NetPnl == 0m)
            }, .68, .98)
        }, layout);
    }

    public static string Candles(Trade trade, IReadOnlyList<Bar> bars) => Candles(trade, new BarQueryResult { Bars = bars });

    public static string Candles(Trade trade, BarQueryResult query, string? timeZoneId = null)
    {
        if (query.Bars.Count == 0) return Empty("No matching OHLCV bars yet. Import a bar file for this symbol and time window.");

        var payload = CandlePayload(trade, query, timeZoneId);
        var json = JsonSerializer.Serialize(payload);
        var label = $"Candlestick chart for {trade.Symbol}, {query.ResolvedInterval}, {query.Bars.Count} bars";
        return $@"<div class=""tf-lightweight-chart-shell""><div class=""tf-lightweight-chart"" data-lightweight-chart=""{WebUtility.HtmlEncode(json)}"" role=""img"" aria-label=""{WebUtility.HtmlEncode(label)}""></div><div class=""tf-lightweight-chart-status"" data-lightweight-status role=""status"" aria-live=""polite""></div></div>";
    }

    public static object CandlePayload(Trade trade, BarQueryResult query, string? timeZoneId = null)
    {
        var orderedBars = query.Bars.OrderBy(x => x.EventUtc).ToArray();
        if (orderedBars.Length == 0) throw new ArgumentException("A candle payload requires at least one bar.", nameof(query));

        var barDuration = ResolveBarDuration(orderedBars);
        var entryBar = FindContainingBar(orderedBars, trade.EntryUtc, barDuration);
        var exitBar = trade.ExitUtc.HasValue ? FindContainingBar(orderedBars, trade.ExitUtc.Value, barDuration) : null;
        var chartPadding = TimeSpan.FromTicks(checked(barDuration.Ticks * 5));
        var chartStart = trade.EntryUtc - chartPadding;
        var chartEnd = (trade.ExitUtc ?? trade.EntryUtc) + chartPadding;
        var isLong = !trade.Direction.Equals("Short", StringComparison.OrdinalIgnoreCase);
        var entryColor = isLong ? Green : Red;
        var exitColor = trade.NetPnl > 0m ? Green : trade.NetPnl < 0m ? Red : Gold;
        var exitPrice = trade.ExitPrice ?? trade.EntryPrice;
        object? exit = trade.ExitUtc.HasValue
            ? new
            {
                time = exitBar!.EventUtc.ToUnixTimeSeconds(),
                exactTime = trade.ExitUtc.Value.ToUnixTimeSeconds(),
                price = exitPrice,
                color = exitColor,
                shape = isLong ? "arrowDown" : "arrowUp",
                barPosition = isLong ? "aboveBar" : "belowBar",
                label = "Exit"
            }
            : null;
        object? path = trade.ExitUtc.HasValue && entryBar.EventUtc != exitBar!.EventUtc
            ? new[]
            {
                new { time = entryBar.EventUtc.ToUnixTimeSeconds(), value = trade.EntryPrice },
                new { time = exitBar.EventUtc.ToUnixTimeSeconds(), value = exitPrice }
            }
            : Array.Empty<object>();
        var payload = new
        {
            symbol = trade.Symbol,
            interval = query.ResolvedInterval,
            timeZone = TimeZoneCatalog.CanonicalId(timeZoneId),
            barSeconds = Math.Max(1, (int)Math.Round(barDuration.TotalSeconds)),
            bars = orderedBars.Select(bar => new
            {
                time = bar.EventUtc.ToUnixTimeSeconds(),
                open = bar.Open,
                high = bar.High,
                low = bar.Low,
                close = bar.Close,
                volume = bar.Volume ?? 0L
            }).ToArray(),
            focus = new
            {
                from = chartStart.ToUnixTimeSeconds(),
                to = chartEnd.ToUnixTimeSeconds()
            },
            trade = new
            {
                direction = isLong ? "long" : "short",
                netPnl = trade.NetPnl,
                entry = new
                {
                    time = entryBar.EventUtc.ToUnixTimeSeconds(),
                    exactTime = trade.EntryUtc.ToUnixTimeSeconds(),
                    price = trade.EntryPrice,
                    color = entryColor,
                    shape = isLong ? "arrowUp" : "arrowDown",
                    barPosition = isLong ? "belowBar" : "aboveBar",
                    label = isLong ? "Long" : "Short"
                },
                exit,
                path
            },
            historyUrl = $"/journal/{trade.JournalId:D}/trades/{trade.Id:D}?handler=CandleBars&interval={Uri.EscapeDataString(query.ResolvedInterval)}"
        };
        return payload;
    }

    private static TimeSpan ResolveBarDuration(IReadOnlyList<Bar> bars)
    {
        if (BarIntervals.TryGetMinutes(bars[0].Interval, out var minutes))
            return TimeSpan.FromMinutes(minutes);

        var inferred = bars
            .Zip(bars.Skip(1), (left, right) => right.EventUtc - left.EventUtc)
            .Where(x => x > TimeSpan.Zero)
            .OrderBy(x => x)
            .FirstOrDefault();
        return inferred > TimeSpan.Zero ? inferred : TimeSpan.FromMinutes(1);
    }

    private static Bar FindContainingBar(IReadOnlyList<Bar> bars, DateTimeOffset instant, TimeSpan barDuration) =>
        bars.LastOrDefault(x => x.EventUtc <= instant && instant < x.EventUtc + barDuration)
        ?? bars.OrderBy(x => Math.Abs((x.EventUtc - instant).Ticks)).First();

    private static object Annotation(string text, double x, double y) => new
    {
        x,
        y,
        xref = "paper",
        yref = "paper",
        text,
        showarrow = false,
        font = new { color = Muted, family = "Segoe UI, system-ui, sans-serif", size = 11 }
    };

    private static object VerticalMarker(decimal x, string color) => new
    {
        type = "line",
        x0 = x,
        x1 = x,
        y0 = 0,
        y1 = 1,
        yref = "paper",
        line = new { color, dash = "dash", width = 1.5 }
    };

    private static object Pie(string title, IReadOnlyDictionary<string, int> values, double start, double end)
    {
        var entries = values.Where(x => x.Value > 0).ToArray();
        var palette = new[] { Gold, Blue, Green, Purple, Red };
        return new
        {
            type = "pie",
            labels = entries.Select(x => x.Key).ToArray(),
            values = entries.Select(x => x.Value).ToArray(),
            hole = .62,
            sort = false,
            textinfo = "none",
            name = title,
            domain = new { x = new[] { start, end }, y = new[] { .12, .94 } },
            marker = new { colors = entries.Select((_, index) => palette[index % palette.Length]).ToArray() },
            hovertemplate = "%{label}: %{value} trades (%{percent})<extra></extra>"
        };
    }

    private static string Bar(IReadOnlyList<(string Label, decimal Value, string Detail)> values, string label, string color, string yLabel = "average net P&L")
    {
        var layout = CartesianLayout();
        SetAxis(layout, "xaxis", string.Empty);
        SetAxis(layout, "yaxis", yLabel);
        return Plotly(label, new object[]
        {
            new
            {
                type = "bar",
                x = values.Select(x => x.Label).ToArray(),
                y = values.Select(x => x.Value).ToArray(),
                customdata = values.Select(x => new[] { x.Detail }).ToArray(),
                marker = new { color = values.Select(x => x.Value >= 0m ? color : Red).ToArray() },
                hovertemplate = "%{x}<br>%{y:,.2f}<br>%{customdata[0]}<extra></extra>"
            }
        }, layout);
    }

    private static string Histogram(IReadOnlyList<decimal> values, string label, string color)
    {
        var layout = CartesianLayout();
        layout["bargap"] = .08;
        SetAxis(layout, "xaxis", "net P&L");
        SetAxis(layout, "yaxis", "trade count");
        return Plotly(label, new object[] { HistogramTrace(values, label, color) }, layout);
    }

    private static object HistogramTrace(IReadOnlyList<decimal> values, string name, string color) => new
    {
        type = "histogram",
        x = values.ToArray(),
        name,
        nbinsx = Math.Clamp((int)Math.Ceiling(Math.Sqrt(values.Count) * 2d), 6, 18),
        marker = new { color, line = new { color, width = 1 } },
        opacity = .76,
        hovertemplate = "%{x:,.2f}<br>%{y} trades<extra></extra>"
    };

    private static decimal Percent(int numerator, int denominator) => denominator == 0 ? 0m : (decimal)numerator / denominator * 100m;

    private static object LineTrace(IReadOnlyList<string> x, IReadOnlyList<decimal> y, string name, string color) => new
    {
        type = "scatter",
        x,
        y,
        mode = "lines+markers",
        name,
        line = new { color, width = 1.5 },
        marker = new { color, size = 5 },
        hovertemplate = "%{x|%b %-d, %Y}<br>%{y:,.2f}<extra></extra>"
    };

    private static string Line(IReadOnlyList<string> x, IReadOnlyList<decimal> y, string label, string color, string xLabel)
    {
        var layout = CartesianLayout();
        SetAxis(layout, "xaxis", xLabel, date: true);
        SetAxis(layout, "yaxis", "net P&L");
        return Plotly(label, new object[] { LineTrace(x, y, label, color) }, layout);
    }

    private static Dictionary<string, object?> CartesianLayout(string? hovermode = null)
    {
        var layout = BaseLayout();
        if (hovermode is not null) layout["hovermode"] = hovermode;
        return layout;
    }

    private static Dictionary<string, object?> BaseLayout() => new(StringComparer.Ordinal)
    {
        ["paper_bgcolor"] = "#161b22",
        ["plot_bgcolor"] = "#161b22",
        ["font"] = new { family = "Segoe UI, system-ui, sans-serif", color = Ink, size = 11 },
        ["hoverlabel"] = new { bgcolor = "#1c2129", bordercolor = Grid, font = new { color = Ink, family = "Segoe UI, system-ui, sans-serif", size = 11 } },
        ["margin"] = new { l = 48, r = 16, t = 16, b = 48 },
        ["height"] = 280,
        ["showlegend"] = false,
        ["hovermode"] = "closest",
        ["xaxis"] = Axis(),
        ["yaxis"] = Axis()
    };

    private static Dictionary<string, object?> Axis() => new(StringComparer.Ordinal)
    {
        ["showgrid"] = true,
        ["gridcolor"] = Grid,
        ["zeroline"] = true,
        ["zerolinecolor"] = Zero,
        ["linecolor"] = Grid,
        ["tickfont"] = new { color = Muted, size = 11 },
        ["automargin"] = true
    };

    private static void SetAxis(Dictionary<string, object?> layout, string key, string title, bool date = false, bool percent = false)
    {
        var axis = Axis();
        if (!string.IsNullOrWhiteSpace(title))
            axis["title"] = new { text = title, font = new { color = Muted, size = 11 } };
        if (date) axis["type"] = "date";
        if (percent) axis["ticksuffix"] = "%";
        layout[key] = axis;
    }

    private static void SetRange(Dictionary<string, object?> layout, string key, object range)
    {
        if (layout[key] is Dictionary<string, object?> axis) axis["range"] = range;
    }

    private static string Plotly(string label, IEnumerable<object> traces, Dictionary<string, object?> layout, bool scrollZoom = false)
    {
        var payload = new
        {
            data = traces.ToArray(),
            layout,
            config = new
            {
                responsive = true,
                displaylogo = false,
                scrollZoom,
                modeBarButtonsToRemove = new[] { "lasso2d", "select2d" }
            }
        };
        var json = JsonSerializer.Serialize(payload);
        return $@"<div class=""tf-plotly-chart"" data-plotly-chart=""{WebUtility.HtmlEncode(json)}"" role=""img"" aria-label=""{WebUtility.HtmlEncode(label)}""></div>";
    }

    private static string Empty(string message) => $@"<div class=""chart-empty"" role=""status"">{WebUtility.HtmlEncode(message)}</div>";

    private static string[] WithStart(IEnumerable<DateTimeOffset> dates, DateTimeOffset first)
    {
        var start = first.AddMinutes(-1).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        return new[] { start }.Concat(dates.Select(date => date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))).ToArray();
    }

    private static Trade[] ClosedTrades(IEnumerable<Trade> source) => source
        .Where(x => x.ExitUtc.HasValue)
        .OrderBy(x => x.ExitUtc)
        .ThenBy(x => x.Sequence)
        .ToArray();

    private static int DurationBucket(TimeSpan duration) => duration.TotalMinutes switch
    {
        < 1 => 0,
        < 5 => 1,
        < 15 => 2,
        < 30 => 3,
        < 60 => 4,
        _ => 5
    };

    private static string SessionName(DateTimeOffset value, string? timeZoneId)
    {
        var hour = InZone(value, timeZoneId).Hour;
        return TradingSessionClassifier.Name(hour);
    }

    private static DateTimeOffset InZone(DateTimeOffset value, string? timeZoneId)
    {
        return TimeZoneCatalog.Convert(value, timeZoneId);
    }

    private static string Money(decimal value) => value.ToString("C2", CultureInfo.CurrentCulture);
}

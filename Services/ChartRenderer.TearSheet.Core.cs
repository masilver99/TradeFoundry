using System.Globalization;
using System.Net;
using TradeFoundry.Core;

namespace TradeFoundry.Services;

public static partial class ChartRenderer
{
    private const int TearSheetTradeWindow = 20;
    private const int TearSheetDailyWindow = 126;

    public static string TearSheetEquity(
        IEnumerable<Trade> source,
        IEnumerable<AccountBalanceEvent> balanceSource,
        decimal? startingEquity)
    {
        var daily = DailyEquity(source, balanceSource, startingEquity);
        if (daily.Count == 0) return Empty("Import completed trades or account balances to see the equity curve.");

        var layout = CartesianLayout("x unified");
        SetAxis(layout, "xaxis", "date", date: true);
        SetAxis(layout, "yaxis", "balance");
        var hasRaw = daily.Any(x => x.RawBalance.HasValue);
        layout["showlegend"] = hasRaw;

        var traces = new List<object>
        {
            new
            {
                type = "scatter",
                x = daily.Select(x => DateLabel(x.Date)).ToArray(),
                y = daily.Select(x => x.AdjustedBalance).ToArray(),
                mode = "lines",
                name = hasRaw ? "Adjusted balance" : "Cumulative balance",
                line = new { color = Blue, width = 2 },
                hovertemplate = "%{x}<br>%{y:,.2f}<extra>Adjusted balance</extra>"
            }
        };

        if (hasRaw)
        {
            traces.Add(new
            {
                type = "scatter",
                x = daily.Select(x => DateLabel(x.Date)).ToArray(),
                y = daily.Select(x => x.RawBalance).ToArray(),
                mode = "lines",
                name = "Raw balance",
                line = new { color = Muted, width = 1.5, dash = "dot" },
                opacity = .7,
                hovertemplate = "%{x}<br>%{y:,.2f}<extra>Raw balance</extra>"
            });
        }

        return Plotly("Equity curve with adjusted and raw balance", traces, layout);
    }

    public static string TearSheetReturns(
        IEnumerable<Trade> source,
        IEnumerable<BenchmarkPoint> benchmarkSource,
        IEnumerable<AccountBalanceEvent> balanceSource,
        decimal? startingEquity)
    {
        var daily = DailyEquity(source, balanceSource, startingEquity);
        if (daily.Count < 2) return Empty("Returns appear after at least two equity observations.");

        if (!HasReturnBaseline(daily)) return Empty("Set starting equity or import account balances to compare percentage returns.");

        var startBalance = daily[0].RawBalance!.Value;
        if (startBalance <= 0m) return Empty("Set starting equity or import account balances to compare percentage returns.");

        var lastDate = daily[^1].Date;
        var layout = CartesianLayout("x unified");
        SetAxis(layout, "xaxis", "date", date: true);
        SetAxis(layout, "yaxis", "cumulative return", percent: true);
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = .98 };

        var traces = new List<object>
        {
            new
            {
                type = "scatter",
                x = daily.Select(x => DateLabel(x.Date)).ToArray(),
                y = daily.Select(x => (x.AdjustedBalance / startBalance - 1m) * 100m).ToArray(),
                mode = "lines",
                name = "Strategy",
                line = new { color = Blue, width = 2 },
                hovertemplate = "%{x}<br>Strategy: %{y:.2f}%<extra></extra>"
            }
        };

        var benchmarkColors = new[] { Gold, "#a371f7", "#3fb950", "#f85149" };
        var benchmarkIndex = 0;
        foreach (var benchmark in benchmarkSource
            .Where(point => point.Value > 0m && !string.IsNullOrWhiteSpace(point.Symbol))
            .GroupBy(point => point.Symbol, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var orderedBenchmark = benchmark
                .OrderBy(point => point.EventUtc)
                .Select(point => new BenchmarkValue(point.Symbol, DateOnly.FromDateTime(point.EventUtc.UtcDateTime.Date), point.Value))
                .ToArray();
            if (orderedBenchmark.Length == 0) continue;
            var firstStrategyDate = daily[0].Date;
            var basePoint = orderedBenchmark.LastOrDefault(point => point.Date <= firstStrategyDate);
            if (basePoint == default) basePoint = orderedBenchmark[0];
            var comparison = orderedBenchmark
                .Where(x => x.Date >= basePoint.Date && x.Date <= lastDate)
                .ToArray();
            if (comparison.Length > 0 && basePoint.Value > 0m)
            {
                var symbol = benchmark.Key;
                var color = benchmarkColors[benchmarkIndex++ % benchmarkColors.Length];
                traces.Add(new
                {
                    type = "scatter",
                    x = comparison.Select(x => DateLabel(x.Date)).ToArray(),
                    y = comparison.Select(x => (x.Value / basePoint.Value - 1m) * 100m).ToArray(),
                    mode = "lines",
                    name = symbol,
                    line = new { color, width = 1.7, dash = "dot" },
                    hovertemplate = $"%{{x}}<br>{WebUtility.HtmlEncode(symbol)}: %{{y:.2f}}%<extra></extra>"
                });
            }
        }

        layout["shapes"] = new object[] { HorizontalMarker(0m, Zero, "dash") };
        return Plotly("Strategy and benchmark cumulative returns", traces, layout);
    }

    public static string TearSheetMonteCarlo(IEnumerable<Trade> source, decimal? startingEquity)
    {
        var pnls = ClosedTrades(source).Select(x => (double)x.NetPnl).ToArray();
        if (pnls.Length < 5) return Empty("Monte Carlo simulation appears after at least five completed trades.");

        const int simulationCount = 400;
        var random = new Random(2048);
        var starting = (double)(startingEquity ?? 0m);
        var paths = new double[simulationCount][];
        for (var simulation = 0; simulation < simulationCount; simulation++)
        {
            var path = new double[pnls.Length + 1];
            path[0] = starting;
            for (var index = 1; index < path.Length; index++)
                path[index] = path[index - 1] + pnls[random.Next(pnls.Length)];
            paths[simulation] = path;
        }

        var p5 = new double[pnls.Length + 1];
        var p25 = new double[pnls.Length + 1];
        var p50 = new double[pnls.Length + 1];
        var p75 = new double[pnls.Length + 1];
        var p95 = new double[pnls.Length + 1];
        for (var index = 0; index < p50.Length; index++)
        {
            var values = paths.Select(path => path[index]).OrderBy(value => value).ToArray();
            p5[index] = Quantile(values, .05);
            p25[index] = Quantile(values, .25);
            p50[index] = Quantile(values, .50);
            p75[index] = Quantile(values, .75);
            p95[index] = Quantile(values, .95);
        }

        var actual = new double[pnls.Length + 1];
        actual[0] = starting;
        for (var index = 1; index < actual.Length; index++) actual[index] = actual[index - 1] + pnls[index - 1];

        var x = Enumerable.Range(0, pnls.Length + 1).ToArray();
        var layout = CartesianLayout();
        layout["height"] = 340;
        layout["showlegend"] = true;
        layout["legend"] = new { orientation = "h", y = 1.02, x = 1, xanchor = "right" };
        SetAxis(layout, "xaxis", "trade number");
        SetAxis(layout, "yaxis", startingEquity.HasValue ? "balance" : "cumulative P&L");
        return Plotly("Monte Carlo simulated equity paths", new object[]
        {
            new { type = "scatter", x, y = p95, mode = "lines", name = "P95", line = new { color = "rgba(88,166,255,.35)", width = 1 }, hovertemplate = "Trade %{x}<br>%{y:,.0f}<extra>P95</extra>" },
            new { type = "scatter", x, y = p5, mode = "lines", name = "P5", fill = "tonexty", fillcolor = "rgba(248,81,73,.12)", line = new { color = "rgba(248,81,73,.35)", width = 1 }, hovertemplate = "Trade %{x}<br>%{y:,.0f}<extra>P5</extra>" },
            new { type = "scatter", x, y = p75, mode = "lines", name = "P75", line = new { color = "rgba(63,185,80,.55)", width = 1.5 }, hovertemplate = "Trade %{x}<br>%{y:,.0f}<extra>P75</extra>" },
            new { type = "scatter", x, y = p25, mode = "lines", name = "P25", fill = "tonexty", fillcolor = "rgba(63,185,80,.16)", line = new { color = "rgba(63,185,80,.55)", width = 1.5 }, hovertemplate = "Trade %{x}<br>%{y:,.0f}<extra>P25</extra>" },
            new { type = "scatter", x, y = p50, mode = "lines", name = "Median", line = new { color = Gold, width = 2 }, hovertemplate = "Trade %{x}<br>%{y:,.0f}<extra>Median</extra>" },
            new { type = "scatter", x, y = actual, mode = "lines", name = "Actual", line = new { color = Blue, width = 2.5 }, hovertemplate = "Trade %{x}<br>%{y:,.0f}<extra>Actual</extra>" }
        }, layout);
    }

    public static string TearSheetDrawdown(
        IEnumerable<Trade> source,
        IEnumerable<AccountBalanceEvent> balanceSource,
        decimal? startingEquity)
    {
        var daily = DailyEquity(source, balanceSource, startingEquity);
        if (daily.Count == 0) return Empty("Drawdown appears after the first equity observation.");

        var peak = daily[0].AdjustedBalance;
        var drawdowns = new List<decimal>(daily.Count);
        foreach (var point in daily)
        {
            peak = Math.Max(peak, point.AdjustedBalance);
            drawdowns.Add(point.AdjustedBalance - peak);
        }

        var layout = CartesianLayout();
        SetAxis(layout, "xaxis", "date", date: true);
        SetAxis(layout, "yaxis", "drawdown");
        return Plotly("Underwater drawdown", new object[]
        {
            new
            {
                type = "scatter",
                x = daily.Select(x => DateLabel(x.Date)).ToArray(),
                y = drawdowns.ToArray(),
                mode = "lines",
                fill = "tozeroy",
                line = new { color = Red, width = 1.5 },
                fillcolor = "rgba(248,81,73,.16)",
                hovertemplate = "%{x}<br>Drawdown: %{y:,.2f}<extra></extra>"
            }
        }, layout);
    }

    public static string TearSheetWorstDrawdownPeriods(
        IEnumerable<Trade> source,
        IEnumerable<AccountBalanceEvent> balanceSource,
        decimal? startingEquity)
    {
        var daily = DailyEquity(source, balanceSource, startingEquity);
        if (daily.Count < 2) return Empty("Worst drawdown periods need at least two equity observations.");

        var episodes = DrawdownEpisodes(daily).OrderBy(x => x.MaxDepth).Take(5).ToArray();
        var isReturn = HasReturnBaseline(daily) && daily[0].AdjustedBalance > 0m;
        var baseBalance = isReturn ? daily[0].AdjustedBalance : 0m;
        var values = daily.Select(x => isReturn ? (x.AdjustedBalance / baseBalance - 1m) * 100m : x.AdjustedBalance - baseBalance).ToArray();
        var shapes = episodes.Select(episode => new
        {
            type = "rect",
            xref = "x",
            yref = "paper",
            x0 = DateLabel(episode.StartDate),
            x1 = DateLabel(episode.RecoveryDate ?? daily[^1].Date),
            y0 = 0,
            y1 = 1,
            fillcolor = "rgba(248,81,73,.14)",
            line = new { width = 0 }
        }).Cast<object>().ToArray();

        var layout = CartesianLayout();
        layout["height"] = 320;
        layout["shapes"] = shapes;
        SetAxis(layout, "xaxis", "date", date: true);
        SetAxis(layout, "yaxis", isReturn ? "cumulative return" : "cumulative P&L", percent: isReturn);
        return Plotly("Worst five drawdown periods", new object[]
        {
            new
            {
                type = "scatter",
                x = daily.Select(x => DateLabel(x.Date)).ToArray(),
                y = values,
                mode = "lines",
                line = new { color = Blue, width = 1.7 },
                hovertemplate = isReturn ? "%{x}<br>Return: %{y:.2f}%<extra></extra>" : "%{x}<br>P&L: %{y:,.2f}<extra></extra>"
            }
        }, layout);
    }

    public static string TearSheetDrawdownRecovery(
        IEnumerable<Trade> source,
        IEnumerable<AccountBalanceEvent> balanceSource,
        decimal? startingEquity)
    {
        var episodes = DrawdownEpisodes(DailyEquity(source, balanceSource, startingEquity));
        if (episodes.Count == 0) return Empty("No drawdown episodes have been observed yet.");

        var labels = episodes.Select((_, index) => $"DD{index + 1}").ToArray();
        var layout = CartesianLayout();
        layout["height"] = 320;
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = .98 };
        SetAxis(layout, "xaxis", "drawdown episode");
        SetAxis(layout, "yaxis", "days");
        return Plotly("Drawdown duration and recovery", new object[]
        {
            new
            {
                type = "bar",
                x = labels,
                y = episodes.Select(x => x.DurationDays).ToArray(),
                name = "Duration",
                marker = new { color = episodes.Select(x => x.RecoveryDate.HasValue ? Gold : Red).ToArray() },
                customdata = episodes.Select(x => new object?[] { DateLabel(x.StartDate), DateLabel(x.TroughDate), x.RecoveryDate.HasValue ? DateLabel(x.RecoveryDate.Value) : "Open", Math.Abs(x.MaxDepth), x.RecoveryDays.HasValue ? (object?)x.RecoveryDays.Value : null }).ToArray(),
                hovertemplate = "Episode %{x}<br>Start: %{customdata[0]}<br>Trough: %{customdata[1]}<br>Recovery: %{customdata[2]}<br>Duration: %{y} days<br>Recovery after trough: %{customdata[4]} days<br>Depth: %{customdata[3]:,.2f}<extra></extra>"
            },
            new
            {
                type = "scatter",
                x = labels,
                y = episodes.Select(x => x.RecoveryDays).ToArray(),
                mode = "lines+markers",
                name = "Recovery days",
                connectgaps = false,
                marker = new { color = Blue, size = 8, symbol = "diamond" },
                line = new { color = Blue, width = 1.5 },
                hovertemplate = "Episode %{x}<br>Recovery: %{y} days<extra></extra>"
            }
        }, layout);
    }

    public static string TearSheetRolling(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source);
        if (trades.Length == 0) return Empty("Rolling analytics appear after the first completed trade.");

        var labels = new string[trades.Length];
        var expectancies = new decimal[trades.Length];
        var winRates = new decimal[trades.Length];
        var sharpes = new decimal?[trades.Length];
        for (var index = 0; index < trades.Length; index++)
        {
            var start = Math.Max(0, index - TearSheetTradeWindow + 1);
            var window = trades[start..(index + 1)].Select(x => x.GrossPnl).ToArray();
            var mean = window.Average();
            labels[index] = $"T{index + 1}";
            expectancies[index] = mean;
            winRates[index] = (decimal)window.Count(x => x > 0m) / window.Length * 100m;
            var standardDeviation = SampleStd(window);
            if (standardDeviation > 0m) sharpes[index] = mean / standardDeviation;
        }

        var layout = CartesianLayout();
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = .98 };
        layout["yaxis2"] = new Dictionary<string, object?> { ["title"] = "Win rate (%)", ["overlaying"] = "y", ["side"] = "right", ["range"] = new[] { 0, 100 }, ["ticksuffix"] = "%", ["color"] = Blue, ["showgrid"] = false };
        layout["yaxis3"] = new Dictionary<string, object?> { ["title"] = "Sharpe", ["overlaying"] = "y", ["side"] = "right", ["anchor"] = "free", ["position"] = 1.0, ["color"] = Gold, ["showgrid"] = false };
        SetAxis(layout, "xaxis", "trade");
        SetAxis(layout, "yaxis", "rolling expectancy");
        return Plotly($"Rolling analytics ({TearSheetTradeWindow}-trade window)", new object[]
        {
            new { type = "scatter", x = labels, y = expectancies, mode = "lines+markers", name = "Expectancy", marker = new { color = expectancies.Select(x => x >= 0m ? Green : Red).ToArray(), size = 6 }, line = new { color = Green, width = 2 }, hovertemplate = "%{x}<br>Expectancy: %{y:,.2f}<extra></extra>" },
            new { type = "scatter", x = labels, y = winRates, mode = "lines", name = "Win rate", yaxis = "y2", line = new { color = Blue, width = 1.5, dash = "dot" }, hovertemplate = "%{x}<br>Win rate: %{y:.1f}%<extra></extra>" },
            new { type = "scatter", x = labels, y = sharpes, mode = "lines", name = "Sharpe", yaxis = "y3", line = new { color = Gold, width = 1.5, dash = "dash" }, hovertemplate = "%{x}<br>Sharpe: %{y:.2f}<extra></extra>" }
        }, layout);
    }

    public static string TearSheetRollingVolatility(
        IEnumerable<Trade> source,
        IEnumerable<AccountBalanceEvent> balanceSource,
        IEnumerable<BenchmarkPoint> benchmarkSource,
        decimal? startingEquity)
    {
        var daily = DailyEquity(source, balanceSource, startingEquity);
        if (!HasReturnBaseline(daily)) return Empty("Set starting equity or import account balances to calculate percentage volatility.");

        var returns = DailyReturns(daily);
        if (returns.Count < 10) return Empty("Rolling volatility needs at least ten daily return observations.");

        var rolling = RollingStatistic(returns, TearSheetDailyWindow, values => SampleStd(values) * (decimal)Math.Sqrt(252));
        if (rolling.Count == 0) return Empty("Insufficient data for rolling volatility.");

        var traces = new List<object>
        {
            new { type = "scatter", x = rolling.Select(x => DateLabel(x.Date)).ToArray(), y = rolling.Select(x => x.Value).ToArray(), mode = "lines", name = "Strategy", line = new { color = Blue, width = 1.7 }, hovertemplate = "%{x}<br>Strategy volatility: %{y:.2%}<extra></extra>" }
        };
        var benchmarkReturns = BenchmarkDailyReturns(benchmarkSource);
        var benchmarkRolling = RollingStatistic(benchmarkReturns.OrderBy(x => x.Key).Select(x => (x.Key, (double)x.Value)).ToArray(), TearSheetDailyWindow, values => SampleStd(values) * (decimal)Math.Sqrt(252));
        if (benchmarkRolling.Count > 0)
        {
            var symbol = SelectBenchmark(benchmarkSource).FirstOrDefault().Symbol;
            traces.Add(new { type = "scatter", x = benchmarkRolling.Select(x => DateLabel(x.Date)).ToArray(), y = benchmarkRolling.Select(x => x.Value).ToArray(), mode = "lines", name = string.IsNullOrWhiteSpace(symbol) ? "Benchmark" : symbol, line = new { color = Gold, width = 1.5 }, hovertemplate = "%{x}<br>Benchmark volatility: %{y:.2%}<extra></extra>" });
        }

        var layout = CartesianLayout();
        layout["showlegend"] = true;
        layout["legend"] = new { x = .72, y = .98 };
        SetAxis(layout, "xaxis", "date", date: true);
        SetAxis(layout, "yaxis", "annualised volatility");
        layout["yaxis"] = new Dictionary<string, object?>
        {
            ["title"] = new { text = "Annualised volatility", font = new { color = Muted, size = 11 } },
            ["tickformat"] = ".0%",
            ["showgrid"] = true,
            ["gridcolor"] = Grid,
            ["zeroline"] = true,
            ["zerolinecolor"] = Zero,
            ["automargin"] = true
        };
        return Plotly("Rolling six-month volatility", traces, layout);
    }

    public static string TearSheetRollingSharpe(
        IEnumerable<Trade> source,
        IEnumerable<AccountBalanceEvent> balanceSource,
        decimal? startingEquity)
    {
        var daily = DailyEquity(source, balanceSource, startingEquity);
        if (!HasReturnBaseline(daily)) return Empty("Set starting equity or import account balances to calculate return ratios.");

        var returns = DailyReturns(daily);
        var rolling = RollingStatistic(returns, TearSheetDailyWindow, values =>
        {
            var mean = values.Average();
            var standardDeviation = SampleStd(values);
            return standardDeviation > 0m ? mean / standardDeviation * (decimal)Math.Sqrt(252) : 0m;
        });
        if (rolling.Count == 0) return Empty("Rolling Sharpe needs enough daily return observations.");
        return RollingRatioChart("Rolling six-month Sharpe", "Sharpe ratio", rolling, 1m, Red);
    }

    public static string TearSheetRollingSortino(
        IEnumerable<Trade> source,
        IEnumerable<AccountBalanceEvent> balanceSource,
        decimal? startingEquity)
    {
        var daily = DailyEquity(source, balanceSource, startingEquity);
        if (!HasReturnBaseline(daily)) return Empty("Set starting equity or import account balances to calculate return ratios.");

        var returns = DailyReturns(daily);
        var rolling = RollingStatistic(returns, TearSheetDailyWindow, values =>
        {
            var mean = values.Average();
            var downsideVariance = values.Where(x => x < 0m).Select(x => x * x).Sum() / values.Count;
            var downsideDeviation = (decimal)Math.Sqrt((double)downsideVariance);
            return downsideDeviation > 0m ? mean / downsideDeviation * (decimal)Math.Sqrt(252) : 0m;
        });
        if (rolling.Count == 0) return Empty("Rolling Sortino needs enough daily return observations.");
        var mean = rolling.Average(x => x.Value);
        return RollingRatioChart("Rolling six-month Sortino", "Sortino ratio", rolling, mean, Red);
    }

    public static string TearSheetWinRateOverTime(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source);
        var groups = trades.GroupBy(x => new DateOnly(x.ExitUtc!.Value.UtcDateTime.Year, x.ExitUtc.Value.UtcDateTime.Month, 1))
            .OrderBy(x => x.Key)
            .Select(x => new
            {
                Label = x.Key.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                Rate = (decimal)x.Count(t => t.GrossPnl > 0m) / x.Count() * 100m,
                Count = x.Count()
            })
            .ToArray();
        if (groups.Length == 0) return Empty("Monthly win rate appears after the first completed trade.");

        var layout = CartesianLayout();
        layout["height"] = 280;
        SetAxis(layout, "xaxis", string.Empty);
        SetAxis(layout, "yaxis", "win rate", percent: true);
        SetRange(layout, "yaxis", new[] { 0, 118 });
        layout["bargap"] = .3;
        return Plotly("Win rate over time", new object[]
        {
            new
            {
                type = "bar",
                x = groups.Select(x => x.Label).ToArray(),
                y = groups.Select(x => x.Rate).ToArray(),
                marker = new { color = groups.Select(x => x.Rate >= 50m ? Green : Red).ToArray() },
                text = groups.Select(x => $"{x.Rate:0}%").ToArray(),
                textposition = "outside",
                customdata = groups.Select(x => x.Count).ToArray(),
                hovertemplate = "%{x}<br>Win rate: %{y:.1f}%<br>Trades: %{customdata}<extra></extra>"
            }
        }, layout);
    }

    public static string TearSheetAnnualReturns(
        IEnumerable<Trade> source,
        IEnumerable<AccountBalanceEvent> balanceSource,
        IEnumerable<BenchmarkPoint> benchmarkSource,
        decimal? startingEquity)
    {
        var daily = DailyEquity(source, balanceSource, startingEquity);
        if (daily.Count < 2) return Empty("Annual returns need equity history.");
        if (!HasReturnBaseline(daily)) return Empty("Set starting equity or import account balances to compare percentage returns.");

        var strategy = PeriodReturns(daily, x => x.Year);
        var benchmark = BenchmarkPeriodReturns(benchmarkSource, x => x.Year);
        var years = strategy.Keys.Concat(benchmark.Keys).Distinct().OrderBy(x => x).ToArray();
        if (years.Length == 0) return Empty("No annual return data available.");

        var layout = CartesianLayout();
        layout["height"] = 320;
        layout["barmode"] = "group";
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = .98 };
        SetAxis(layout, "xaxis", "year");
        SetAxis(layout, "yaxis", "annual return", percent: true);
        var traces = new List<object>();
        if (benchmark.Count > 0)
        {
            traces.Add(new { type = "bar", x = years.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray(), y = years.Select(x => benchmark.TryGetValue(x, out var value) ? value : (decimal?)null).ToArray(), name = SelectBenchmark(benchmarkSource).FirstOrDefault().Symbol is { Length: > 0 } symbol ? symbol : "Benchmark", marker = new { color = Gold }, hovertemplate = "%{x}<br>Benchmark: %{y:.2f}%<extra></extra>" });
        }
        traces.Add(new
        {
            type = "bar",
            x = years.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray(),
            y = years.Select(x => strategy.TryGetValue(x, out var value) ? value : (decimal?)null).ToArray(),
            name = "Strategy",
            marker = new { color = years.Select(x => strategy.TryGetValue(x, out var value) && value >= 0m ? Blue : Red).ToArray() },
            hovertemplate = "%{x}<br>Strategy: %{y:.2f}%<extra></extra>"
        });
        layout["shapes"] = new object[] { HorizontalMarker(strategy.Values.DefaultIfEmpty().Average(), Red, "dash"), HorizontalMarker(0m, Zero, "dash") };
        return Plotly("Annual strategy returns versus benchmark", traces, layout);
    }

    public static string TearSheetMonthlyReturnsDistribution(
        IEnumerable<Trade> source,
        IEnumerable<AccountBalanceEvent> balanceSource,
        IEnumerable<BenchmarkPoint> benchmarkSource,
        decimal? startingEquity)
    {
        var daily = DailyEquity(source, balanceSource, startingEquity);
        if (!HasReturnBaseline(daily)) return Empty("Set starting equity or import account balances to compare percentage returns.");

        var strategy = PeriodReturns(daily, x => x.Year * 100 + x.Month).Values.ToArray();
        if (strategy.Length == 0) return Empty("Monthly return distribution needs equity history.");
        var benchmark = BenchmarkPeriodReturns(benchmarkSource, x => x.Year * 100 + x.Month).Values.ToArray();
        var traces = new List<object> { HistogramTrace(strategy, "Strategy", Blue) };
        if (benchmark.Length > 0) traces.Add(HistogramTrace(benchmark, SelectBenchmark(benchmarkSource).FirstOrDefault().Symbol is { Length: > 0 } symbol ? symbol : "Benchmark", Gold));
        var layout = CartesianLayout();
        layout["barmode"] = "overlay";
        layout["showlegend"] = true;
        layout["legend"] = new { x = .76, y = .98 };
        SetAxis(layout, "xaxis", "monthly return", percent: true);
        SetAxis(layout, "yaxis", "count");
        layout["bargap"] = .05;
        return Plotly("Distribution of monthly returns", traces, layout);
    }

    public static string TearSheetDailyActiveReturns(
        IEnumerable<Trade> source,
        IEnumerable<AccountBalanceEvent> balanceSource,
        IEnumerable<BenchmarkPoint> benchmarkSource,
        decimal? startingEquity)
    {
        var daily = DailyEquity(source, balanceSource, startingEquity);
        if (!HasReturnBaseline(daily)) return Empty("Set starting equity or import account balances to compare percentage returns.");

        var strategy = DailyReturns(daily);
        var benchmark = BenchmarkDailyReturns(benchmarkSource);
        var points = strategy.Where(x => benchmark.ContainsKey(x.Date)).Select(x => (x.Date, Value: (decimal)(x.Return - (double)benchmark[x.Date]) * 100m)).ToArray();
        if (points.Length == 0) return Empty("Active returns require overlapping strategy and benchmark daily data.");

        var layout = CartesianLayout();
        SetAxis(layout, "xaxis", "date", date: true);
        SetAxis(layout, "yaxis", "active return", percent: true);
        return Plotly("Daily active returns versus benchmark", new object[]
        {
            new
            {
                type = "bar",
                x = points.Select(x => DateLabel(x.Date)).ToArray(),
                y = points.Select(x => x.Value).ToArray(),
                marker = new { color = points.Select(x => x.Value >= 0m ? Green : Red).ToArray() },
                hovertemplate = "%{x}<br>Active return: %{y:.2f}%<extra></extra>"
            }
        }, layout);
    }

    private static IReadOnlyList<DailyEquityPoint> DailyEquity(
        IEnumerable<Trade> source,
        IEnumerable<AccountBalanceEvent> balanceSource,
        decimal? startingEquity)
    {
        var trades = ClosedTrades(source);
        var pnlByDate = trades
            .GroupBy(x => DateOnly.FromDateTime(x.ExitUtc!.Value.UtcDateTime.Date))
            .ToDictionary(x => x.Key, x => x.Sum(t => t.NetPnl));
        var rawByDate = balanceSource
            .Where(x => x.Balance.HasValue)
            .GroupBy(x => DateOnly.FromDateTime(x.EventUtc.UtcDateTime.Date))
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.EventUtc).ThenBy(y => y.RowNumber).Last().Balance!.Value);
        var dates = pnlByDate.Keys.Concat(rawByDate.Keys).Distinct().OrderBy(x => x).ToList();
        if (dates.Count == 0) return Array.Empty<DailyEquityPoint>();

        decimal? initialBalance = startingEquity ?? rawByDate.OrderBy(x => x.Key).Select(x => (decimal?)x.Value).FirstOrDefault();
        var baseline = initialBalance ?? 0m;
        if (baseline != 0m) dates.Insert(0, dates[0].AddDays(-1));

        var cumulative = 0m;
        var result = new List<DailyEquityPoint>(dates.Count);
        foreach (var date in dates)
        {
            if (pnlByDate.TryGetValue(date, out var pnl)) cumulative += pnl;
            result.Add(new DailyEquityPoint(date, baseline + cumulative, rawByDate.TryGetValue(date, out var raw) ? raw : (date == dates[0] && baseline != 0m ? baseline : null)));
        }
        return result;
    }

    private static bool HasReturnBaseline(IReadOnlyList<DailyEquityPoint> daily) =>
        daily.Count > 0 && daily[0].RawBalance is > 0m;

    private static IReadOnlyList<(DateOnly Date, double Return)> DailyReturns(IReadOnlyList<DailyEquityPoint> daily)
    {
        var values = new List<(DateOnly Date, double Return)>();
        for (var index = 1; index < daily.Count; index++)
        {
            var previous = daily[index - 1].AdjustedBalance;
            if (previous != 0m) values.Add((daily[index].Date, (double)((daily[index].AdjustedBalance - previous) / previous)));
        }
        return values;
    }

    private static Dictionary<DateOnly, decimal> BenchmarkDailyReturns(IEnumerable<BenchmarkPoint> source)
    {
        var points = SelectBenchmark(source);
        var result = new Dictionary<DateOnly, decimal>();
        for (var index = 1; index < points.Count; index++)
        {
            if (points[index - 1].Value != 0m)
                result[points[index].Date] = (points[index].Value - points[index - 1].Value) / points[index - 1].Value;
        }
        return result;
    }

    private static List<BenchmarkValue> SelectBenchmark(IEnumerable<BenchmarkPoint> source)
    {
        return source
            .Where(x => x.Value > 0m)
            .GroupBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()?
            .OrderBy(x => x.EventUtc)
            .Select(x => new BenchmarkValue(x.Symbol, DateOnly.FromDateTime(x.EventUtc.UtcDateTime.Date), x.Value))
            .ToList() ?? new List<BenchmarkValue>();
    }

    private static Dictionary<TKey, decimal> PeriodReturns<TKey>(IReadOnlyList<DailyEquityPoint> daily, Func<DateOnly, TKey> key) where TKey : notnull
    {
        var result = new Dictionary<TKey, decimal>();
        decimal? previousBalance = null;
        foreach (var group in daily.GroupBy(x => key(x.Date)).OrderBy(x => x.Min(y => y.Date)))
        {
            var first = group.OrderBy(x => x.Date).First().AdjustedBalance;
            var last = group.OrderBy(x => x.Date).Last().AdjustedBalance;
            var start = previousBalance ?? first;
            if (start != 0m) result[group.Key] = (last / start - 1m) * 100m;
            previousBalance = last;
        }
        return result;
    }

    private static Dictionary<TKey, decimal> BenchmarkPeriodReturns<TKey>(IEnumerable<BenchmarkPoint> source, Func<DateOnly, TKey> key) where TKey : notnull
    {
        var points = SelectBenchmark(source);
        var result = new Dictionary<TKey, decimal>();
        decimal? previous = null;
        foreach (var group in points.GroupBy(x => key(x.Date)).OrderBy(x => x.Min(y => y.Date)))
        {
            var first = group.OrderBy(x => x.Date).First().Value;
            var last = group.OrderBy(x => x.Date).Last().Value;
            var start = previous ?? first;
            if (start != 0m) result[group.Key] = (last / start - 1m) * 100m;
            previous = last;
        }
        return result;
    }

    private static IReadOnlyList<(DateOnly Date, decimal Value)> RollingStatistic(
        IReadOnlyList<(DateOnly Date, double Return)> values,
        int requestedWindow,
        Func<IReadOnlyList<decimal>, decimal> statistic)
    {
        if (values.Count == 0) return Array.Empty<(DateOnly, decimal)>();
        var window = Math.Min(requestedWindow, values.Count);
        var result = new List<(DateOnly, decimal)>();
        for (var index = window - 1; index < values.Count; index++)
        {
            var slice = values.Skip(index - window + 1).Take(window).Select(x => (decimal)x.Return).ToArray();
            result.Add((values[index].Date, statistic(slice)));
        }
        return result;
    }

    private static string RollingRatioChart(string label, string axisLabel, IReadOnlyList<(DateOnly Date, decimal Value)> values, decimal reference, string referenceColor)
    {
        var layout = CartesianLayout();
        SetAxis(layout, "xaxis", "date", date: true);
        SetAxis(layout, "yaxis", axisLabel);
        layout["shapes"] = new object[] { HorizontalMarker(reference, referenceColor, "dash"), HorizontalMarker(0m, Zero, "dash") };
        return Plotly(label, new object[]
        {
            new { type = "scatter", x = values.Select(x => DateLabel(x.Date)).ToArray(), y = values.Select(x => x.Value).ToArray(), mode = "lines", name = axisLabel, line = new { color = Blue, width = 1.7 }, hovertemplate = $"%{{x}}<br>{axisLabel}: %{{y:.2f}}<extra></extra>" }
        }, layout);
    }

    private static IReadOnlyList<DrawdownEpisode> DrawdownEpisodes(IReadOnlyList<DailyEquityPoint> daily)
    {
        if (daily.Count < 2) return Array.Empty<DrawdownEpisode>();
        var peak = daily[0].AdjustedBalance;
        DrawdownEpisodeBuilder? active = null;
        var episodes = new List<DrawdownEpisode>();
        foreach (var point in daily)
        {
            if (point.AdjustedBalance >= peak)
            {
                if (active is not null)
                {
                    episodes.Add(active.Recover(point.Date));
                    active = null;
                }
                peak = point.AdjustedBalance;
                continue;
            }

            if (active is null) active = new DrawdownEpisodeBuilder(point.Date, point.Date, point.AdjustedBalance, point.AdjustedBalance - peak);
            else if (point.AdjustedBalance < active.TroughBalance) active = active.WithTrough(point.Date, point.AdjustedBalance, peak);
        }
        if (active is not null) episodes.Add(active.Close(daily[^1].Date));
        return episodes;
    }

    private static decimal SampleStd(IReadOnlyList<decimal> values)
    {
        if (values.Count < 2) return 0m;
        var mean = values.Average();
        var variance = values.Sum(value => (value - mean) * (value - mean)) / (values.Count - 1);
        return (decimal)Math.Sqrt((double)variance);
    }

    private static double Quantile(IReadOnlyList<double> values, double quantile)
    {
        if (values.Count == 0) return 0d;
        var position = (values.Count - 1) * quantile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return values[lower];
        var fraction = position - lower;
        return values[lower] + (values[upper] - values[lower]) * fraction;
    }

    private static string DateLabel(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static object HorizontalMarker(decimal value, string color, string dash) => new
    {
        type = "line",
        x0 = 0,
        x1 = 1,
        xref = "paper",
        y0 = value,
        y1 = value,
        line = new { color, dash, width = 1.3 }
    };

    private readonly record struct DailyEquityPoint(DateOnly Date, decimal AdjustedBalance, decimal? RawBalance);
    private readonly record struct BenchmarkValue(string Symbol, DateOnly Date, decimal Value);
    private readonly record struct DrawdownEpisode(DateOnly StartDate, DateOnly TroughDate, DateOnly? RecoveryDate, decimal MaxDepth, int DurationDays, int? RecoveryDays);

    private sealed class DrawdownEpisodeBuilder
    {
        public DrawdownEpisodeBuilder(DateOnly startDate, DateOnly troughDate, decimal troughBalance, decimal maxDepth)
        {
            StartDate = startDate;
            TroughDate = troughDate;
            TroughBalance = troughBalance;
            MaxDepth = maxDepth;
        }

        public DateOnly StartDate { get; }
        public DateOnly TroughDate { get; private set; }
        public decimal TroughBalance { get; private set; }
        public decimal MaxDepth { get; private set; }

        public DrawdownEpisodeBuilder WithTrough(DateOnly date, decimal balance, decimal peak)
        {
            TroughDate = date;
            TroughBalance = balance;
            MaxDepth = balance - peak;
            return this;
        }

        public DrawdownEpisode Recover(DateOnly date) => new(StartDate, TroughDate, date, MaxDepth, Math.Max(0, date.DayNumber - StartDate.DayNumber), Math.Max(0, date.DayNumber - TroughDate.DayNumber));

        public DrawdownEpisode Close(DateOnly date) => new(StartDate, TroughDate, null, MaxDepth, Math.Max(0, date.DayNumber - StartDate.DayNumber), null);
    }
}

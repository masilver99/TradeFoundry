using System.Globalization;
using TradeFoundry.Core;

namespace TradeFoundry.Services;

public static partial class ChartRenderer
{
    public static string TearSheetFeeDrag(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source);
        if (trades.Length == 0) return Empty("Fee drag appears after the first completed trade.");

        var labels = trades.Select((_, index) => $"T{index + 1}").ToArray();
        var gross = new decimal[trades.Length];
        var net = new decimal[trades.Length];
        var fees = new decimal[trades.Length];
        var exchangeFees = new decimal[trades.Length];
        var nfaFees = new decimal[trades.Length];
        var clearingFees = new decimal[trades.Length];
        decimal grossRunning = 0m, netRunning = 0m, feesRunning = 0m, exchangeRunning = 0m, nfaRunning = 0m, clearingRunning = 0m;
        for (var index = 0; index < trades.Length; index++)
        {
            grossRunning += trades[index].GrossPnl;
            netRunning += trades[index].NetPnl;
            feesRunning += trades[index].Fees;
            exchangeRunning += trades[index].ExchangeFees;
            nfaRunning += trades[index].NfaFees;
            clearingRunning += trades[index].ClearingFees;
            gross[index] = grossRunning;
            net[index] = netRunning;
            fees[index] = feesRunning;
            exchangeFees[index] = exchangeRunning;
            nfaFees[index] = nfaRunning;
            clearingFees[index] = clearingRunning;
        }

        var layout = CartesianLayout();
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = .98 };
        SetAxis(layout, "xaxis", "trade");
        SetAxis(layout, "yaxis", "cumulative P&L");
        return Plotly("Cumulative gross, net, and fee drag", new object[]
        {
            new { type = "scatter", x = labels, y = gross, mode = "lines", name = "Gross P&L", line = new { color = Blue, width = 2 }, hovertemplate = "%{x}<br>Gross: %{y:,.2f}<extra></extra>" },
            new { type = "scatter", x = labels, y = net, mode = "lines", name = "Net P&L", line = new { color = Green, width = 2 }, fill = "tonexty", fillcolor = "rgba(248,81,73,.14)", hovertemplate = "%{x}<br>Net: %{y:,.2f}<extra></extra>" },
            new { type = "scatter", x = labels, y = fees, mode = "lines", name = "Total fees", line = new { color = Red, width = 1.5, dash = "dot" }, hovertemplate = "%{x}<br>Total fees: %{y:,.2f}<extra></extra>" },
            new { type = "scatter", x = labels, y = exchangeFees, mode = "lines", name = "Exchange fees", line = new { color = "#d29922", width = 1, dash = "dash" }, hovertemplate = "%{x}<br>Exchange: %{y:,.2f}<extra></extra>" },
            new { type = "scatter", x = labels, y = nfaFees, mode = "lines", name = "NFA fees", line = new { color = "#a371f7", width = 1, dash = "dash" }, hovertemplate = "%{x}<br>NFA: %{y:,.2f}<extra></extra>" },
            new { type = "scatter", x = labels, y = clearingFees, mode = "lines", name = "Clearing / commission", line = new { color = "#58a6ff", width = 1, dash = "dash" }, hovertemplate = "%{x}<br>Clearing / commission: %{y:,.2f}<extra></extra>" }
        }, layout);
    }

    public static string TearSheetDailyPnl(IEnumerable<Trade> source)
    {
        var values = GrossDailyValues(source);
        if (values.Count == 0) return Empty("Daily P&L appears after the first completed trade.");

        var layout = CartesianLayout();
        SetAxis(layout, "xaxis", "exit date", date: true);
        SetAxis(layout, "yaxis", "gross P&L");
        layout["bargap"] = .3;
        return Plotly("Daily gross P&L", new object[]
        {
            new
            {
                type = "bar",
                x = values.Select(x => DateLabel(x.Date)).ToArray(),
                y = values.Select(x => x.Value).ToArray(),
                marker = new { color = values.Select(x => x.Value >= 0m ? Green : Red).ToArray() },
                customdata = values.Select(x => x.Count).ToArray(),
                hovertemplate = "%{x}<br>Gross P&L: %{y:,.2f}<br>%{customdata} trades<extra></extra>"
            }
        }, layout);
    }

    public static string TearSheetDailyDistribution(IEnumerable<Trade> source)
    {
        var values = GrossDailyValues(source).Select(x => x.Value).ToArray();
        return values.Length == 0
            ? Empty("Daily P&L distribution appears after the first completed trade.")
            : Histogram(values, "daily gross P&L distribution", Blue);
    }

    public static string TearSheetPnlDistribution(IEnumerable<Trade> source)
    {
        var values = ClosedTrades(source).Select(x => x.GrossPnl).ToArray();
        return values.Length == 0
            ? Empty("Trade P&L distribution appears after the first completed trade.")
            : Histogram(values, "trade gross P&L distribution", Blue);
    }

    public static string TearSheetTradePnlRangeDistribution(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source);
        if (trades.Length == 0) return Empty("Trade P&L ranges appear after the first completed trade.");

        var values = trades.Select(x => x.GrossPnl).ToArray();
        var step = DollarRangeStep(values.Select(Math.Abs).DefaultIfEmpty(0m).Max());
        var largestBin = values
            .Where(x => x != 0m)
            .Select(x => DollarRangeIndex(Math.Abs(x), step))
            .DefaultIfEmpty(0)
            .Max();
        var rangeLimit = (largestBin + 1) * step;
        var buckets = Enumerable.Range(0, largestBin + 1)
            .Select(index =>
            {
                var lower = index * step;
                var upper = (index + 1) * step;
                return new
                {
                    Lower = lower,
                    Upper = upper,
                    Losses = values.Count(value => value < 0m && DollarRangeIndex(Math.Abs(value), step) == index),
                    Wins = values.Count(value => value > 0m && DollarRangeIndex(value, step) == index)
                };
            })
            .ToArray();

        var layout = CartesianLayout();
        layout["barmode"] = "overlay";
        layout["bargap"] = .08;
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = 1.02, orientation = "h" };
        layout["shapes"] = new object[] { VerticalMarker(0m, Zero, "solid") };
        SetAxis(layout, "xaxis", "gross P&L ($)");
        if (layout["xaxis"] is Dictionary<string, object?> xAxis)
        {
            xAxis["tickprefix"] = "$";
            xAxis["tickformat"] = ",.0f";
            xAxis["dtick"] = step;
            xAxis["tick0"] = 0m;
        }
        SetAxis(layout, "yaxis", "trade count");
        SetRange(layout, "xaxis", new[] { -rangeLimit, rangeLimit });

        var traces = new List<object>();
        if (values.Any(value => value < 0m))
        {
            traces.Add(new
            {
                type = "bar",
                x = buckets.Select(bucket => -(bucket.Lower + bucket.Upper) / 2m).ToArray(),
                y = buckets.Select(bucket => bucket.Losses).ToArray(),
                width = step * .86m,
                name = "Losses",
                marker = new { color = Red },
                customdata = buckets.Select(bucket => DollarRangeLabel(-bucket.Upper, -bucket.Lower)).ToArray(),
                hovertemplate = "%{customdata}<br>%{y} losing trades<extra></extra>"
            });
        }

        if (values.Any(value => value > 0m))
        {
            traces.Add(new
            {
                type = "bar",
                x = buckets.Select(bucket => (bucket.Lower + bucket.Upper) / 2m).ToArray(),
                y = buckets.Select(bucket => bucket.Wins).ToArray(),
                width = step * .86m,
                name = "Wins",
                marker = new { color = Green },
                customdata = buckets.Select(bucket => DollarRangeLabel(bucket.Lower, bucket.Upper)).ToArray(),
                hovertemplate = "%{customdata}<br>%{y} winning trades<extra></extra>"
            });
        }

        var breakeven = values.Count(value => value == 0m);
        if (breakeven > 0)
        {
            traces.Add(new
            {
                type = "bar",
                x = new[] { 0m },
                y = new[] { breakeven },
                width = step * .42m,
                name = "Breakeven",
                marker = new { color = Gold },
                hovertemplate = "$0<br>%{y} breakeven trades<extra></extra>"
            });
        }

        return Plotly("Trade P&L by dollar range", traces, layout);
    }

    public static string TearSheetWaterfall(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source);
        if (trades.Length == 0) return Empty("The trade waterfall appears after the first completed trade.");

        var labels = trades.Select((_, index) => $"T{index + 1}").ToArray();
        var pnls = trades.Select(x => x.GrossPnl).ToArray();
        var cumulative = new decimal[pnls.Length];
        decimal running = 0m;
        for (var index = 0; index < pnls.Length; index++)
        {
            running += pnls[index];
            cumulative[index] = running;
        }

        var layout = CartesianLayout();
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = .98 };
        layout["yaxis2"] = new Dictionary<string, object?> { ["title"] = "cumulative P&L", ["overlaying"] = "y", ["side"] = "right", ["color"] = Gold, ["showgrid"] = false };
        SetAxis(layout, "xaxis", "trade");
        SetAxis(layout, "yaxis", "trade P&L");
        return Plotly("Trade P&L waterfall with cumulative path", new object[]
        {
            new { type = "bar", x = labels, y = pnls, name = "Trade P&L", marker = new { color = pnls.Select(x => x >= 0m ? Green : Red).ToArray() }, hovertemplate = "%{x}: %{y:,.2f}<extra>Trade P&L</extra>" },
            new { type = "scatter", x = labels, y = cumulative, name = "Cumulative P&L", mode = "lines", yaxis = "y2", line = new { color = Gold, width = 2 }, hovertemplate = "%{x}: %{y:,.2f}<extra>Cumulative P&L</extra>" }
        }, layout);
    }

    public static string TearSheetMfeMae(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source)
            .Where(x => ExcursionCurrency(x, x.MaePoints).HasValue && ExcursionCurrency(x, x.MfePoints).HasValue)
            .ToArray();
        if (trades.Length == 0) return Empty("MFE and MAE require Sierra high/low-during-position fields.");

        var mae = trades.Select(x => ExcursionCurrency(x, x.MaePoints)!.Value).ToArray();
        var mfe = trades.Select(x => ExcursionCurrency(x, x.MfePoints)!.Value).ToArray();
        var layout = CartesianLayout();
        SetAxis(layout, "xaxis", "MAE");
        SetAxis(layout, "yaxis", "MFE");
        layout["shapes"] = new object[] { HorizontalMarker(0m, Zero, "solid"), VerticalMarker(0m, Zero, "solid") };
        return Plotly("Maximum favourable versus adverse excursion", new object[]
        {
            new
            {
                type = "scatter",
                x = mae,
                y = mfe,
                mode = "markers",
                name = "Trades",
                marker = new { color = trades.Select(x => x.GrossPnl >= 0m ? Green : Red).ToArray(), size = 8, opacity = .82 },
                customdata = trades.Select(x => new object[] { $"{x.Symbol} · {x.Direction}", x.GrossPnl }).ToArray(),
                hovertemplate = "%{customdata[0]}<br>MAE: %{x:,.2f}<br>MFE: %{y:,.2f}<br>Gross P&L: %{customdata[1]:,.2f}<extra></extra>"
            }
        }, layout);
    }

    public static string TearSheetMaeWinners(IEnumerable<Trade> source)
    {
        var winners = ClosedTrades(source)
            .Where(x => x.GrossPnl > 0m && ExcursionCurrency(x, x.MaePoints).HasValue)
            .ToArray();
        if (winners.Length < 3) return Empty("At least three winning trades with MAE data are needed for this chart.");

        var mae = winners.Select(x => ExcursionCurrency(x, x.MaePoints)!.Value).ToArray();
        var pnl = winners.Select(x => x.GrossPnl).ToArray();
        var meanMae = mae.Average();
        var meanPnl = pnl.Average();
        var stdMae = SampleStd(mae);
        var stdPnl = SampleStd(pnl);
        var layout = CartesianLayout();
        layout["shapes"] = new object[]
        {
            HorizontalBand(meanPnl - 2m * stdPnl, meanPnl + 2m * stdPnl, "rgba(88,166,255,.07)"),
            HorizontalBand(meanPnl - stdPnl, meanPnl + stdPnl, "rgba(88,166,255,.13)"),
            VerticalBand(Math.Max(0m, meanMae - 2m * stdMae), meanMae + 2m * stdMae, "rgba(248,81,73,.07)"),
            VerticalBand(Math.Max(0m, meanMae - stdMae), meanMae + stdMae, "rgba(248,81,73,.13)"),
            HorizontalMarker(meanPnl, Blue, "dash"),
            VerticalMarker(meanMae, Red, "dash"),
            HorizontalMarker(0m, Zero, "solid"),
            VerticalMarker(0m, Zero, "solid")
        };
        layout["annotations"] = new object[] { Annotation($"μ MAE {meanMae:C0} · μ P&L {meanPnl:C0} · n={winners.Length}", .98, .98) };
        SetAxis(layout, "xaxis", "|MAE| — heat taken");
        SetAxis(layout, "yaxis", "gross P&L");
        return Plotly("Winning trades and heat taken", new object[]
        {
            new { type = "scatter", x = mae, y = pnl, mode = "markers", name = "Winning trades", marker = new { color = Green, size = 8, opacity = .82 }, customdata = winners.Select(x => new object[] { $"{x.Symbol} · {x.Direction}", x.GrossPnl }).ToArray(), hovertemplate = "%{customdata[0]}<br>MAE: %{x:,.2f}<br>Gross P&L: %{customdata[1]:,.2f}<extra></extra>" }
        }, layout);
    }

    public static string TearSheetDurationProfit(IEnumerable<Trade> source)
    {
        var points = ClosedTrades(source)
            .Where(x => x.Duration.HasValue && x.Duration.Value >= TimeSpan.Zero)
            .Select(x => new DurationPnlPoint(x.Duration!.Value.TotalMinutes, x.GrossPnl, x.Symbol, x.Direction))
            .ToArray();
        if (points.Length == 0) return Empty("Time-in-trade and P&L data are not available yet.");

        var durations = points.Select(x => (decimal)x.DurationMinutes).ToArray();
        var pnls = points.Select(x => x.Pnl).ToArray();
        var meanDuration = durations.Average();
        var meanPnl = pnls.Average();
        var durationStd = SampleStd(durations);
        var pnlStd = SampleStd(pnls);
        var layout = CartesianLayout();
        layout["shapes"] = new object[]
        {
            HorizontalBand(meanPnl - 2m * pnlStd, meanPnl + 2m * pnlStd, "rgba(88,166,255,.07)"),
            HorizontalBand(meanPnl - pnlStd, meanPnl + pnlStd, "rgba(88,166,255,.13)"),
            VerticalBand(Math.Max(0m, meanDuration - 2m * durationStd), meanDuration + 2m * durationStd, "rgba(210,153,34,.07)"),
            VerticalBand(Math.Max(0m, meanDuration - durationStd), meanDuration + durationStd, "rgba(210,153,34,.13)"),
            HorizontalMarker(meanPnl, Blue, "dash"),
            VerticalMarker(meanDuration, Gold, "dash"),
            HorizontalMarker(0m, Zero, "solid")
        };
        layout["annotations"] = new object[] { Annotation($"μ duration {meanDuration:0.0}m · μ P&L {meanPnl:C0} · n={points.Length}", .98, .98) };
        SetAxis(layout, "xaxis", "time in trade (minutes)");
        SetAxis(layout, "yaxis", "gross P&L");
        return Plotly("Time in trade versus gross P&L", new object[]
        {
            new { type = "scatter", x = durations, y = pnls, mode = "markers", name = "Trades", marker = new { color = pnls.Select(x => x >= 0m ? Green : Red).ToArray(), size = 8, opacity = .82 }, customdata = points.Select(x => new object[] { $"{x.Symbol} · {x.Direction}", x.DurationMinutes, x.Pnl }).ToArray(), hovertemplate = "%{customdata[0]}<br>Duration: %{customdata[1]:.1f} minutes<br>Gross P&L: %{customdata[2]:,.2f}<extra></extra>" }
        }, layout);
    }

    public static string TearSheetTimeBucketExpectancy(IEnumerable<Trade> source, string? timeZoneId)
    {
        var trades = ClosedTrades(source);
        if (trades.Length == 0) return Empty("Time-bucket analysis appears after the first completed trade.");

        var datasets = new[]
        {
            new BucketDataset("By session", trades.GroupBy(x => SessionName(x.EntryUtc, timeZoneId), StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => BucketStat.From(x.Key, x.Select(t => t.GrossPnl))).ToArray()),
            new BucketDataset("By day of week", trades.GroupBy(x => DayLabel(InZone(x.EntryUtc, timeZoneId).DayOfWeek), StringComparer.OrdinalIgnoreCase).OrderBy(x => DayOrder(x.Key)).Select(x => BucketStat.From(x.Key, x.Select(t => t.GrossPnl))).ToArray()),
            new BucketDataset("By entry hour", trades.GroupBy(x => InZone(x.EntryUtc, timeZoneId).Hour).OrderBy(x => x.Key).Select(x => BucketStat.From($"{x.Key:00}:00", x.Select(t => t.GrossPnl))).ToArray())
        };
        if (datasets.All(x => x.Values.Length == 0)) return Empty("No time-bucket data available.");

        var layout = CartesianLayout();
        layout["height"] = 700;
        layout["showlegend"] = false;
        layout["margin"] = new { l = 58, r = 22, t = 24, b = 48 };
        layout["xaxis"] = BucketXAxis(.70, 1.0, "session");
        layout["xaxis2"] = BucketXAxis(.36, .64, "day");
        layout["xaxis3"] = BucketXAxis(0.0, .28, "entry hour");
        layout["yaxis"] = BucketYAxis(.70, 1.0, "expectancy");
        layout["yaxis2"] = BucketYAxis(.36, .64, "expectancy");
        layout["yaxis3"] = BucketYAxis(0.0, .28, "expectancy");
        layout["annotations"] = datasets.Select((dataset, index) => new
        {
            x = .02,
            y = new[] { .98, .62, .26 }[index],
            xref = "paper",
            yref = "paper",
            text = dataset.Title,
            showarrow = false,
            xanchor = "left",
            font = new { color = Ink, size = 12 }
        }).Cast<object>().ToArray();

        var traces = new List<object>();
        for (var index = 0; index < datasets.Length; index++)
        {
            var values = datasets[index].Values;
            if (values.Length == 0) continue;
            var axis = index == 0 ? "" : (index + 1).ToString(CultureInfo.InvariantCulture);
            traces.Add(new
            {
                type = "bar",
                x = values.Select(x => x.Label).ToArray(),
                y = values.Select(x => x.Expectancy).ToArray(),
                text = values.Select(x => $"n={x.Count}").ToArray(),
                textposition = "outside",
                xaxis = $"x{axis}",
                yaxis = $"y{axis}",
                marker = new { color = values.Select(x => x.Expectancy >= 0m ? Green : Red).ToArray() },
                customdata = values.Select(x => x.Count).ToArray(),
                hovertemplate = "%{x}<br>Expectancy: %{y:,.2f}<br>Trades: %{customdata}<extra></extra>"
            });
        }
        return traces.Count == 0 ? Empty("No time-bucket data available.") : Plotly("Expectancy by session, day, and entry hour", traces, layout);
    }

    public static string TearSheetExcursionPercentile(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source).ToArray();
        var percentiles = new[] { .50, .75, .90, .95 };
        var labels = new[] { "P50", "P75", "P90", "P95" };
        var mae = trades.Select(x => ExcursionCurrency(x, x.MaePoints)).Where(x => x.HasValue).Select(x => Math.Abs(x!.Value)).ToArray();
        var mfe = trades.Select(x => ExcursionCurrency(x, x.MfePoints)).Where(x => x.HasValue).Select(x => Math.Max(0m, x!.Value)).ToArray();
        if (mae.Length == 0 && mfe.Length == 0) return Empty("Excursion percentiles require MAE or MFE data.");

        var traces = new List<object>();
        if (mae.Length > 0) traces.Add(new { type = "scatter", x = labels, y = percentiles.Select(x => Quantile(mae, x)).ToArray(), mode = "lines+markers", name = "|MAE|", line = new { color = Red, width = 2 }, hovertemplate = "%{x}<br>|MAE|: %{y:,.2f}<extra></extra>" });
        if (mfe.Length > 0) traces.Add(new { type = "scatter", x = labels, y = percentiles.Select(x => Quantile(mfe, x)).ToArray(), mode = "lines+markers", name = "MFE", line = new { color = Green, width = 2 }, hovertemplate = "%{x}<br>MFE: %{y:,.2f}<extra></extra>" });

        var maeR = trades.Select(x => RiskCurrency(x) is > 0m && ExcursionCurrency(x, x.MaePoints).HasValue ? Math.Abs(ExcursionCurrency(x, x.MaePoints)!.Value) / RiskCurrency(x)!.Value : (decimal?)null).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var mfeR = trades.Select(x => RiskCurrency(x) is > 0m && ExcursionCurrency(x, x.MfePoints).HasValue ? Math.Max(0m, ExcursionCurrency(x, x.MfePoints)!.Value) / RiskCurrency(x)!.Value : (decimal?)null).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        if (maeR.Length > 0) traces.Add(new { type = "scatter", x = labels, y = percentiles.Select(x => Quantile(maeR, x)).ToArray(), mode = "lines+markers", name = "|MAE| (R)", yaxis = "y2", line = new { color = Red, width = 1.5, dash = "dash" }, hovertemplate = "%{x}<br>|MAE|: %{y:.2f}R<extra></extra>" });
        if (mfeR.Length > 0) traces.Add(new { type = "scatter", x = labels, y = percentiles.Select(x => Quantile(mfeR, x)).ToArray(), mode = "lines+markers", name = "MFE (R)", yaxis = "y2", line = new { color = Green, width = 1.5, dash = "dash" }, hovertemplate = "%{x}<br>MFE: %{y:.2f}R<extra></extra>" });

        var layout = CartesianLayout();
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = 1.02, orientation = "h" };
        layout["yaxis2"] = new Dictionary<string, object?> { ["title"] = "excursion (R)", ["overlaying"] = "y", ["side"] = "right", ["color"] = Purple, ["showgrid"] = false };
        SetAxis(layout, "xaxis", "percentile");
        SetAxis(layout, "yaxis", "excursion");
        return Plotly("Excursion percentile profile", traces, layout);
    }

    public static string TearSheetHoldingTimeEfficiency(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source).Where(x => x.Duration.HasValue && x.Duration.Value >= TimeSpan.Zero).ToArray();
        if (trades.Length == 0) return Empty("Holding-time efficiency appears after the first completed trade.");

        var labels = new[] { "<1m", "1-5m", "5-15m", "15-30m", "30m+" };
        var buckets = labels.ToDictionary(x => x, _ => new List<Trade>(), StringComparer.Ordinal);
        foreach (var trade in trades) buckets[SourceDurationBucket(trade.Duration!.Value)].Add(trade);
        var populated = labels.Where(x => buckets[x].Count > 0).ToArray();
        if (populated.Length == 0) return Empty("No duration data available.");

        var expectancy = populated.Select(label => buckets[label].Average(x => x.GrossPnl)).ToArray();
        var winRates = populated.Select(label => (decimal)buckets[label].Count(x => x.GrossPnl > 0m) / buckets[label].Count * 100m).ToArray();
        var capture = populated.Select(label =>
        {
            var values = buckets[label].Select(x =>
            {
                var mfe = ExcursionCurrency(x, x.MfePoints);
                return mfe is > 0m ? x.GrossPnl / mfe.Value * 100m : (decimal?)null;
            }).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
            return values.Length == 0 ? (decimal?)null : values.Average();
        }).ToArray();

        var layout = CartesianLayout();
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = 1.02, orientation = "h" };
        layout["yaxis2"] = new Dictionary<string, object?> { ["title"] = "percent", ["overlaying"] = "y", ["side"] = "right", ["range"] = new[] { 0, 110 }, ["ticksuffix"] = "%", ["color"] = Blue, ["showgrid"] = false };
        SetAxis(layout, "xaxis", "hold-time bucket");
        SetAxis(layout, "yaxis", "expectancy");
        var traces = new List<object>
        {
            new { type = "bar", x = populated, y = expectancy, name = "Expectancy", marker = new { color = expectancy.Select(x => x >= 0m ? Green : Red).ToArray() }, text = populated.Select(x => $"n={buckets[x].Count}").ToArray(), textposition = "outside", hovertemplate = "%{x}<br>Expectancy: %{y:,.2f}<extra></extra>" },
            new { type = "scatter", x = populated, y = winRates, name = "Win rate", yaxis = "y2", mode = "lines+markers", line = new { color = Blue, width = 2 }, hovertemplate = "%{x}<br>Win rate: %{y:.1f}%<extra></extra>" }
        };
        if (capture.Any(x => x.HasValue)) traces.Add(new { type = "scatter", x = populated, y = capture, name = "MFE capture", yaxis = "y2", mode = "lines+markers", line = new { color = Gold, width = 2, dash = "dash" }, hovertemplate = "%{x}<br>MFE capture: %{y:.1f}%<extra></extra>" });
        return Plotly("Holding-time efficiency", traces, layout);
    }

    public static string TearSheetStreakState(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source);
        var states = new Dictionary<string, List<decimal>>(StringComparer.Ordinal)
        {
            ["After 1L"] = new(), ["After 2L"] = new(), ["After 3L+"] = new(),
            ["After 1W"] = new(), ["After 2W"] = new(), ["After 3W+"] = new()
        };
        string? priorSign = null;
        var priorRun = 0;
        foreach (var trade in trades)
        {
            if (priorSign is not null)
            {
                var label = $"After {Math.Min(priorRun, 3)}{priorSign}" + (priorRun >= 3 ? "+" : string.Empty);
                states[label].Add(trade.GrossPnl);
            }
            if (trade.GrossPnl > 0m)
            {
                priorRun = priorSign == "W" ? priorRun + 1 : 1;
                priorSign = "W";
            }
            else if (trade.GrossPnl < 0m)
            {
                priorRun = priorSign == "L" ? priorRun + 1 : 1;
                priorSign = "L";
            }
            else
            {
                priorRun = 0;
                priorSign = null;
            }
        }

        var populated = states.Where(x => x.Value.Count > 0).ToArray();
        if (populated.Length == 0) return Empty("Not enough trade history for streak-state analysis.");
        var labels = populated.Select(x => x.Key).ToArray();
        var expectancy = populated.Select(x => x.Value.Average()).ToArray();
        var winRates = populated.Select(x => (decimal)x.Value.Count(value => value > 0m) / x.Value.Count * 100m).ToArray();
        var counts = populated.Select(x => x.Value.Count).ToArray();
        var layout = CartesianLayout();
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = 1.02, orientation = "h" };
        layout["yaxis2"] = new Dictionary<string, object?> { ["title"] = "win rate", ["overlaying"] = "y", ["side"] = "right", ["range"] = new[] { 0, 110 }, ["ticksuffix"] = "%", ["color"] = Blue, ["showgrid"] = false };
        SetAxis(layout, "xaxis", "prior streak state");
        SetAxis(layout, "yaxis", "next-trade expectancy");
        return Plotly("Next-trade expectancy after streak states", new object[]
        {
            new { type = "bar", x = labels, y = expectancy, name = "Expectancy", marker = new { color = expectancy.Select(x => x >= 0m ? Green : Red).ToArray() }, text = counts.Select(x => $"n={x}").ToArray(), textposition = "outside", hovertemplate = "%{x}<br>Expectancy: %{y:,.2f}<extra></extra>" },
            new { type = "scatter", x = labels, y = winRates, name = "Win rate", yaxis = "y2", mode = "lines+markers", line = new { color = Blue, width = 2 }, hovertemplate = "%{x}<br>Win rate: %{y:.1f}%<extra></extra>" }
        }, layout);
    }

    public static string TearSheetExitEfficiency(IEnumerable<Trade> source)
    {
        var groups = new Dictionary<string, ExitEfficiencyBucket>(StringComparer.OrdinalIgnoreCase);
        foreach (var trade in ClosedTrades(source))
        {
            var label = string.IsNullOrWhiteSpace(trade.ExitType) ? "untagged" : trade.ExitType.Trim();
            if (!groups.TryGetValue(label, out var bucket)) groups[label] = bucket = new ExitEfficiencyBucket();
            bucket.Count++;
            var mfe = ExcursionCurrency(trade, trade.MfePoints);
            if (mfe is > 0m)
            {
                bucket.Captures.Add(trade.GrossPnl / mfe.Value * 100m);
                bucket.Givebacks.Add(Math.Max(mfe.Value - trade.GrossPnl, 0m));
            }
        }
        var populated = groups.Where(x => x.Value.Count > 0).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        if (populated.Length == 0) return Empty("Exit efficiency requires classified exits and MFE data.");

        var labels = populated.Select(x => x.Key).ToArray();
        var captures = populated.Select(x => x.Value.Captures.Count == 0 ? (decimal?)null : x.Value.Captures.Average()).ToArray();
        var givebacks = populated.Select(x => x.Value.Givebacks.Count == 0 ? (decimal?)null : x.Value.Givebacks.Average()).ToArray();
        var layout = CartesianLayout();
        layout["height"] = 320;
        layout["showlegend"] = false;
        layout["xaxis2"] = new Dictionary<string, object?> { ["domain"] = new[] { .54, 1.0 }, ["title"] = "exit type", ["showgrid"] = false, ["linecolor"] = Grid, ["tickfont"] = new { color = Muted, size = 10 }, ["automargin"] = true };
        layout["xaxis"] = new Dictionary<string, object?> { ["domain"] = new[] { 0.0, .46 }, ["title"] = "exit type", ["showgrid"] = false, ["linecolor"] = Grid, ["tickfont"] = new { color = Muted, size = 10 }, ["automargin"] = true };
        layout["yaxis"] = new Dictionary<string, object?> { ["title"] = "MFE capture", ["range"] = new[] { 0, 110 }, ["ticksuffix"] = "%", ["showgrid"] = true, ["gridcolor"] = Grid, ["automargin"] = true };
        layout["yaxis2"] = new Dictionary<string, object?> { ["title"] = "profit left on table", ["domain"] = new[] { 0.0, 1.0 }, ["showgrid"] = true, ["gridcolor"] = Grid, ["automargin"] = true };
        layout["annotations"] = new object[] { Annotation("Average MFE capture", .23, 1.05), Annotation("Average profit left on table", .77, 1.05) };
        return Plotly("Exit efficiency by exit type", new object[]
        {
            new { type = "bar", x = labels, y = captures, xaxis = "x", yaxis = "y", marker = new { color = Blue }, text = populated.Select(x => $"n={x.Value.Count}").ToArray(), textposition = "outside", hovertemplate = "%{x}<br>Capture: %{y:.1f}%<extra></extra>" },
            new { type = "bar", x = labels, y = givebacks, xaxis = "x2", yaxis = "y2", marker = new { color = Gold }, text = populated.Select(x => $"n={x.Value.Count}").ToArray(), textposition = "outside", hovertemplate = "%{x}<br>Left on table: %{y:,.2f}<extra></extra>" }
        }, layout);
    }

    public static string TearSheetProfitConcentration(IEnumerable<Trade> source)
    {
        var winners = ClosedTrades(source).Where(x => x.GrossPnl > 0m).Select(x => x.GrossPnl).OrderBy(x => x).ToArray();
        if (winners.Length < 2) return Empty("At least two winning trades are needed for concentration analysis.");

        var total = winners.Sum();
        var cumulative = new List<decimal> { 0m };
        decimal running = 0m;
        foreach (var winner in winners)
        {
            running += winner;
            cumulative.Add(running / total);
        }
        var tradeShare = Enumerable.Range(0, winners.Length + 1).Select(x => (decimal)x / winners.Length).ToArray();
        var weighted = winners.Select((value, index) => (decimal)(index + 1) * value).Sum();
        var gini = total == 0m ? 0m : 2m * weighted / (winners.Length * total) - (winners.Length + 1m) / winners.Length;
        var layout = CartesianLayout();
        layout["annotations"] = new object[] { Annotation($"Winner Gini: {gini:0.00}", .98, .08) };
        SetAxis(layout, "xaxis", "share of winning trades", percent: true);
        SetAxis(layout, "yaxis", "share of gross profit", percent: true);
        return Plotly("Winner profit concentration", new object[]
        {
            new { type = "scatter", x = new[] { 0m, 1m }, y = new[] { 0m, 1m }, mode = "lines", line = new { color = Muted, dash = "dash", width = 1.4 }, hoverinfo = "skip", showlegend = false },
            new { type = "scatter", x = tradeShare, y = cumulative.ToArray(), mode = "lines+markers", line = new { color = Blue, width = 2 }, marker = new { color = Blue, size = 5 }, hovertemplate = "Winner share: %{x:.0%}<br>Profit share: %{y:.0%}<extra></extra>", showlegend = false }
        }, layout);
    }

    public static string TearSheetPositionSize(IEnumerable<Trade> source)
    {
        var groups = ClosedTrades(source).Where(x => x.Quantity > 0).GroupBy(x => x.Quantity).OrderBy(x => x.Key).ToArray();
        if (groups.Length == 0) return Empty("Position-size sensitivity appears after the first completed trade.");

        var labels = groups.Select(x => x.Key.ToString(CultureInfo.InvariantCulture)).ToArray();
        var expectancy = groups.Select(x => x.Average(t => t.GrossPnl)).ToArray();
        var winRates = groups.Select(x => (decimal)x.Count(t => t.GrossPnl > 0m) / x.Count() * 100m).ToArray();
        var layout = CartesianLayout();
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = 1.02, orientation = "h" };
        layout["yaxis2"] = new Dictionary<string, object?> { ["title"] = "win rate", ["overlaying"] = "y", ["side"] = "right", ["range"] = new[] { 0, 110 }, ["ticksuffix"] = "%", ["color"] = Blue, ["showgrid"] = false };
        SetAxis(layout, "xaxis", "contracts / position size");
        SetAxis(layout, "yaxis", "expectancy");
        return Plotly("Expectancy and win rate by position size", new object[]
        {
            new { type = "bar", x = labels, y = expectancy, name = "Expectancy", marker = new { color = expectancy.Select(x => x >= 0m ? Green : Red).ToArray() }, text = groups.Select(x => $"n={x.Count()}").ToArray(), textposition = "outside", hovertemplate = "Qty %{x}<br>Expectancy: %{y:,.2f}<extra></extra>" },
            new { type = "scatter", x = labels, y = winRates, name = "Win rate", yaxis = "y2", mode = "lines+markers", line = new { color = Blue, width = 2 }, hovertemplate = "Qty %{x}<br>Win rate: %{y:.1f}%<extra></extra>" }
        }, layout);
    }

    public static string TearSheetTradeSizeDistribution(IEnumerable<Trade> source)
    {
        var groups = ClosedTrades(source)
            .Where(x => x.Quantity > 0)
            .GroupBy(x => x.Quantity)
            .OrderBy(x => x.Key)
            .Select(group => new
            {
                Size = group.Key,
                Profitable = group.Count(trade => trade.GrossPnl > 0m),
                Losing = group.Count(trade => trade.GrossPnl < 0m),
                Breakeven = group.Count(trade => trade.GrossPnl == 0m)
            })
            .ToArray();
        if (groups.Length == 0) return Empty("Trade-size distribution appears after the first completed trade.");

        var labels = groups.Select(x => x.Size.ToString(CultureInfo.InvariantCulture)).ToArray();
        var largestSide = groups.Max(x => Math.Max(x.Profitable, x.Losing));
        var axisLimit = Math.Max(1, largestSide) + 1;
        var layout = CartesianLayout();
        layout["barmode"] = "relative";
        layout["bargap"] = .16;
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = 1.02, orientation = "h" };
        layout["shapes"] = new object[] { VerticalMarker(0m, Zero, "solid") };
        SetAxis(layout, "xaxis", "trade count (losses ← 0 → profitable)");
        SetAxis(layout, "yaxis", "trade size (contracts)");
        SetRange(layout, "xaxis", new[] { -axisLimit, axisLimit });

        var traces = new List<object>();
        if (groups.Any(x => x.Losing > 0))
        {
            traces.Add(new
            {
                type = "bar",
                orientation = "h",
                x = groups.Select(x => -x.Losing).ToArray(),
                y = labels,
                name = "Losses",
                marker = new { color = Red },
                customdata = groups.Select(x => x.Losing).ToArray(),
                hovertemplate = "Size %{y} contracts<br>Losses: %{customdata}<extra></extra>"
            });
        }

        if (groups.Any(x => x.Profitable > 0))
        {
            traces.Add(new
            {
                type = "bar",
                orientation = "h",
                x = groups.Select(x => x.Profitable).ToArray(),
                y = labels,
                name = "Profitable",
                marker = new { color = Green },
                customdata = groups.Select(x => x.Profitable).ToArray(),
                hovertemplate = "Size %{y} contracts<br>Profitable: %{customdata}<extra></extra>"
            });
        }

        var breakeven = groups.Where(x => x.Breakeven > 0).ToArray();
        if (breakeven.Length > 0)
        {
            traces.Add(new
            {
                type = "scatter",
                x = breakeven.Select(_ => 0).ToArray(),
                y = breakeven.Select(x => x.Size.ToString(CultureInfo.InvariantCulture)).ToArray(),
                mode = "markers+text",
                name = "Breakeven",
                text = breakeven.Select(x => x.Breakeven.ToString(CultureInfo.InvariantCulture)).ToArray(),
                textposition = "middle center",
                marker = new { color = Gold, size = 8 },
                customdata = breakeven.Select(x => x.Breakeven).ToArray(),
                hovertemplate = "Size %{y} contracts<br>Breakeven: %{customdata}<extra></extra>"
            });
        }

        return Plotly("Trade-size distribution by outcome", traces, layout);
    }

    public static string TearSheetMonthlyReturnHeatmap(
        IEnumerable<Trade> source,
        IEnumerable<AccountBalanceEvent> balanceSource,
        decimal? startingEquity)
    {
        var daily = DailyEquity(source, balanceSource, startingEquity);
        if (daily.Count < 2) return Empty("Monthly return heatmap needs equity history.");
        if (!HasReturnBaseline(daily)) return Empty("Set starting equity or import account balances to calculate monthly returns.");

        var records = new List<MonthlyReturnPoint>();
        decimal? previous = null;
        foreach (var group in daily.GroupBy(x => new DateOnly(x.Date.Year, x.Date.Month, 1)).OrderBy(x => x.Key))
        {
            var ordered = group.OrderBy(x => x.Date).ToArray();
            var first = ordered[0].AdjustedBalance;
            var last = ordered[^1].AdjustedBalance;
            var start = previous ?? first;
            if (start != 0m) records.Add(new MonthlyReturnPoint(group.Key, (last / start - 1m) * 100m, last - start));
            previous = last;
        }
        if (records.Count == 0) return Empty("Not enough monthly return data.");

        var months = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
        var years = records.Select(x => x.Month.Year).Distinct().OrderBy(x => x).ToArray();
        var monthLookup = records.ToDictionary(x => (x.Month.Year, x.Month.Month));
        var z = years.Select(year => months.Select((_, month) => monthLookup.TryGetValue((year, month + 1), out var record) ? record.ReturnPct : (decimal?)null).ToArray()).ToArray();
        var text = years.Select(year => months.Select((_, month) => monthLookup.TryGetValue((year, month + 1), out var record) ? $"{record.ReturnPct:0.##}%<br>{record.Pnl:,.0}" : string.Empty).ToArray()).ToArray();
        var layout = CartesianLayout();
        layout["height"] = 360;
        SetAxis(layout, "xaxis", "month");
        SetAxis(layout, "yaxis", "year");
        return Plotly("Monthly return heatmap", new object[]
        {
            new { type = "heatmap", x = months, y = years.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray(), z, text, texttemplate = "%{text}", textfont = new { color = Ink, size = 10 }, colorscale = new object[] { new object[] { 0, Red }, new object[] { .5, Grid }, new object[] { 1, Green } }, zmid = 0, colorbar = new { title = "return %" }, hovertemplate = "%{y} %{x}<br>%{text}<extra></extra>" }
        }, layout);
    }

    public static string TearSheetTimingHeatmap(IEnumerable<Trade> source, string? timeZoneId)
    {
        var trades = ClosedTrades(source);
        if (trades.Length == 0) return Empty("Timing heatmap appears after the first completed trade.");
        var cells = trades.GroupBy(x =>
        {
            var local = InZone(x.EntryUtc, timeZoneId);
            return (Day: ((int)local.DayOfWeek + 6) % 7, Hour: local.Hour);
        }).ToDictionary(x => x.Key, x => (Average: x.Average(t => t.GrossPnl), Count: x.Count()));
        var activeHours = cells.Keys.Select(x => x.Hour).Distinct().OrderBy(x => x).ToArray();
        var days = new[] { "Mon", "Tue", "Wed", "Thu", "Fri" };
        var z = days.Select((_, day) => activeHours.Select(hour => cells.TryGetValue((day, hour), out var cell) ? (decimal?)cell.Average : null).ToArray()).ToArray();
        var text = days.Select((_, day) => activeHours.Select(hour => cells.TryGetValue((day, hour), out var cell) ? cell.Count.ToString(CultureInfo.InvariantCulture) : string.Empty).ToArray()).ToArray();
        if (activeHours.Length == 0) return Empty("No timing data available.");

        var layout = CartesianLayout();
        layout["height"] = 390;
        layout["margin"] = new { l = 52, r = 24, t = 18, b = 54 };
        SetAxis(layout, "xaxis", "entry hour");
        SetAxis(layout, "yaxis", "entry day");
        return Plotly("Gross P&L by entry day and hour", new object[]
        {
            new { type = "heatmap", x = activeHours.Select(x => x.ToString("00", CultureInfo.InvariantCulture)).ToArray(), y = days, z, text, texttemplate = "%{text}", textfont = new { color = Ink, size = 10 }, colorscale = new object[] { new object[] { 0, Red }, new object[] { .5, Grid }, new object[] { 1, Green } }, zmid = 0, colorbar = new { title = "avg P&L" }, hovertemplate = "%{y} %{x}:00<br>Average: %{z:,.2f}<br>Trades: %{text}<extra></extra>" }
        }, layout);
    }

    public static string TearSheetExitType(IEnumerable<Trade> source)
    {
        var groups = ClosedTrades(source)
            .GroupBy(x => string.IsNullOrWhiteSpace(x.ExitType) ? "untagged" : x.ExitType.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new { Label = x.Key, Average = x.Average(t => t.GrossPnl), WinRate = (decimal)x.Count(t => t.GrossPnl > 0m) / x.Count() * 100m, Count = x.Count() })
            .ToArray();
        if (groups.Length == 0) return Empty("Exit-type analysis appears after classified exits are imported.");

        var layout = CartesianLayout();
        layout["height"] = Math.Max(260, 70 + 50 * groups.Length);
        layout["xaxis2"] = new Dictionary<string, object?> { ["title"] = "win rate", ["overlaying"] = "x", ["side"] = "top", ["range"] = new[] { 0, 110 }, ["ticksuffix"] = "%", ["color"] = Blue, ["showgrid"] = false };
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = .02 };
        SetAxis(layout, "xaxis", "average gross P&L");
        SetAxis(layout, "yaxis", string.Empty);
        return Plotly("Average P&L and win rate by exit type", new object[]
        {
            new { type = "bar", x = groups.Select(x => x.Average).ToArray(), y = groups.Select(x => x.Label).ToArray(), orientation = "h", name = "Average P&L", marker = new { color = groups.Select(x => x.Average >= 0m ? Green : Red).ToArray() }, text = groups.Select(x => $"{x.Average:,.2f} · n={x.Count}").ToArray(), textposition = "outside", hovertemplate = "%{y}: %{x:,.2f} average gross P&L<extra></extra>" },
            new { type = "scatter", x = groups.Select(x => x.WinRate).ToArray(), y = groups.Select(x => x.Label).ToArray(), xaxis = "x2", mode = "markers+text", name = "Win rate", marker = new { color = Blue, size = 10, symbol = "diamond" }, text = groups.Select(x => $"{x.WinRate:0}%").ToArray(), textposition = "middle right", hovertemplate = "%{y}: %{x:.1f}% win rate<extra></extra>" }
        }, layout);
    }

    public static string TearSheetDirectionMix(IEnumerable<Trade> source)
    {
        var values = ClosedTrades(source).GroupBy(x => x.Direction).ToDictionary(x => x.Key, x => x.Count());
        return TearSheetDonut("Direction mix", values, "No direction data available.");
    }

    public static string TearSheetSessionMix(IEnumerable<Trade> source, string? timeZoneId)
    {
        var values = ClosedTrades(source).GroupBy(x => SessionName(x.EntryUtc, timeZoneId)).ToDictionary(x => x.Key, x => x.Count());
        return TearSheetDonut("Session mix", values, "No session data available.");
    }

    public static string TearSheetOutcomeMix(IEnumerable<Trade> source)
    {
        var trades = ClosedTrades(source);
        return TearSheetDonut("Outcome mix", new Dictionary<string, int> { ["Wins"] = trades.Count(x => x.GrossPnl > 0m), ["Losses"] = trades.Count(x => x.GrossPnl < 0m), ["Breakeven"] = trades.Count(x => x.GrossPnl == 0m) }, "No outcome data available.");
    }

    private static string TearSheetDonut(string label, IReadOnlyDictionary<string, int> values, string emptyMessage)
    {
        var entries = values.Where(x => x.Value > 0).ToArray();
        if (entries.Length == 0) return Empty(emptyMessage);
        var layout = CartesianLayout();
        layout.Remove("xaxis");
        layout.Remove("yaxis");
        layout["height"] = 250;
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = .02, orientation = "h" };
        return Plotly(label, new object[]
        {
            new { type = "pie", labels = entries.Select(x => x.Key).ToArray(), values = entries.Select(x => x.Value).ToArray(), hole = .62, sort = false, textinfo = "label+percent", textposition = "outside", marker = new { colors = new[] { Gold, Blue, Red, Green, Purple } } }
        }, layout);
    }

    private static IReadOnlyList<(DateOnly Date, decimal Value, int Count)> GrossDailyValues(IEnumerable<Trade> source)
    {
        return ClosedTrades(source)
            .GroupBy(x => DateOnly.FromDateTime(x.ExitUtc!.Value.UtcDateTime.Date))
            .OrderBy(x => x.Key)
            .Select(x => (x.Key, x.Sum(t => t.GrossPnl), x.Count()))
            .ToArray();
    }

    private static decimal? ExcursionCurrency(Trade trade, decimal? points)
    {
        if (!points.HasValue) return null;
        var quantity = Math.Max(1, trade.ClosedQuantity > 0 ? trade.ClosedQuantity : trade.Quantity);
        var pointValue = trade.PointValue > 0m ? trade.PointValue : 1m;
        return Math.Abs(points.Value) * quantity * pointValue;
    }

    private static decimal? RiskCurrency(Trade trade)
    {
        if (trade.InitialRiskCurrency is > 0m) return trade.InitialRiskCurrency;
        if (trade.InitialRiskPoints is > 0m)
        {
            var quantity = Math.Max(1, trade.ClosedQuantity > 0 ? trade.ClosedQuantity : trade.Quantity);
            var pointValue = trade.PointValue > 0m ? trade.PointValue : 1m;
            return trade.InitialRiskPoints.Value * quantity * pointValue;
        }
        return null;
    }

    private static string SourceDurationBucket(TimeSpan duration) => duration.TotalMinutes switch
    {
        < 1 => "<1m",
        < 5 => "1-5m",
        < 15 => "5-15m",
        < 30 => "15-30m",
        _ => "30m+"
    };

    private static string DayLabel(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "Mon",
        DayOfWeek.Tuesday => "Tue",
        DayOfWeek.Wednesday => "Wed",
        DayOfWeek.Thursday => "Thu",
        DayOfWeek.Friday => "Fri",
        DayOfWeek.Saturday => "Sat",
        _ => "Sun"
    };

    private static int DayOrder(string label) => label switch { "Mon" => 0, "Tue" => 1, "Wed" => 2, "Thu" => 3, "Fri" => 4, _ => 9 };

    private static Dictionary<string, object?> BucketXAxis(double bottom, double top, string title) => new()
    {
        ["domain"] = new[] { 0.0, 1.0 },
        ["showgrid"] = false,
        ["linecolor"] = Grid,
        ["tickfont"] = new { color = Muted, size = 10 },
        ["automargin"] = true,
        ["title"] = new { text = title, font = new { color = Muted, size = 10 } },
        ["anchor"] = "y"
    };

    private static Dictionary<string, object?> BucketYAxis(double bottom, double top, string title) => new()
    {
        ["domain"] = new[] { bottom, top },
        ["showgrid"] = true,
        ["gridcolor"] = Grid,
        ["zeroline"] = true,
        ["zerolinecolor"] = Zero,
        ["automargin"] = true,
        ["title"] = new { text = title, font = new { color = Muted, size = 10 } }
    };

    private static object HorizontalBand(decimal y0, decimal y1, string fillColor) => new
    {
        type = "rect",
        xref = "paper",
        yref = "y",
        x0 = 0,
        x1 = 1,
        y0,
        y1,
        fillcolor = fillColor,
        line = new { width = 0 }
    };

    private static object VerticalBand(decimal x0, decimal x1, string fillColor) => new
    {
        type = "rect",
        xref = "x",
        yref = "paper",
        x0,
        x1,
        y0 = 0,
        y1 = 1,
        fillcolor = fillColor,
        line = new { width = 0 }
    };

    private static object VerticalMarker(decimal x, string color, string dash) => new
    {
        type = "line",
        x0 = x,
        x1 = x,
        y0 = 0,
        y1 = 1,
        yref = "paper",
        line = new { color, dash, width = 1.2 }
    };

    private static decimal DollarRangeStep(decimal maximumMagnitude)
    {
        if (maximumMagnitude <= 0m) return 1m;

        var targetStep = maximumMagnitude / 20m;
        var magnitude = (decimal)Math.Pow(10d, Math.Floor(Math.Log10((double)targetStep)));
        var normalized = targetStep / magnitude;
        var niceMultiplier = normalized <= 1m ? 1m : normalized <= 2m ? 2m : normalized <= 5m ? 5m : 10m;
        return Math.Max(1m, niceMultiplier * magnitude);
    }

    private static int DollarRangeIndex(decimal magnitude, decimal step) => Math.Max(0, (int)decimal.Ceiling(magnitude / step) - 1);

    private static string DollarRangeLabel(decimal lower, decimal upper) => $"{DollarLabel(lower)} to {DollarLabel(upper)}";

    private static string DollarLabel(decimal value) => value == 0m
        ? "$0"
        : value < 0m
            ? value.ToString("-$#,##0.##", CultureInfo.InvariantCulture)
            : value.ToString("$#,##0.##", CultureInfo.InvariantCulture);

    private static decimal Quantile(IReadOnlyList<decimal> values, double quantile)
    {
        if (values.Count == 0) return 0m;
        var ordered = values.OrderBy(x => x).ToArray();
        var position = (ordered.Length - 1) * quantile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return ordered[lower];
        var fraction = (decimal)(position - lower);
        return ordered[lower] + (ordered[upper] - ordered[lower]) * fraction;
    }

    private readonly record struct DurationPnlPoint(double DurationMinutes, decimal Pnl, string Symbol, string Direction);
    private readonly record struct BucketDataset(string Title, BucketStat[] Values);
    private readonly record struct BucketStat(string Label, decimal Expectancy, int Count)
    {
        public static BucketStat From(string label, IEnumerable<decimal> values)
        {
            var array = values.ToArray();
            return new BucketStat(label, array.Length == 0 ? 0m : array.Average(), array.Length);
        }
    }
    private readonly record struct MonthlyReturnPoint(DateOnly Month, decimal ReturnPct, decimal Pnl);

    private sealed class ExitEfficiencyBucket
    {
        public int Count { get; set; }
        public List<decimal> Captures { get; } = new();
        public List<decimal> Givebacks { get; } = new();
    }
}

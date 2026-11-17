namespace TradeFoundry.Publishing;

// Trade statistics (Performance section) and the Analysis section: breakdowns, monthly returns, benchmark and tear sheet.
public static partial class PublicationPresentation
{
    private sealed partial class Reader
    {
        private const int RollingWindow = 20;
        private const string NoSetup = "No setup recorded";

        private void TradeStatistics()
        {
            if (trades.Length == 0) return;
            var pnl = trades.Select(t => t.Trade.NetPnl).ToArray();
            var wins = pnl.Where(v => v > 0).ToArray();
            var losses = pnl.Where(v => v < 0).ToArray();
            decimal? averageWin = wins.Length > 0 ? wins.Average() : null;
            decimal? averageLoss = losses.Length > 0 ? losses.Average() : null;
            var expectancy = pnl.Average();
            var drawdown = Drawdown();
            var streaks = Streaks(pnl);
            var green = active.Count(d => d.NetPnl > 0);
            var best = active.MaxBy(d => d.NetPnl)!;
            var worst = active.MinBy(d => d.NetPnl)!;

            html.Append("<div class=\"panel curve trade-statistics\"><div class=\"eyebrow\">TRADE STATISTICS</div><h3>Edge, risk and consistency</h3><p class=\"muted\">Every figure uses realized net P&amp;L after fees.</p><div class=\"stat-groups\">");
            StatGroup("Edge", [
                ("Expectancy", Money(expectancy), "Average net result per trade", Tone(expectancy)),
                ("Average win", averageWin is { } w ? Money(w) : "—", Count(wins.Length, "winning trade"), "positive"),
                ("Average loss", averageLoss is { } l ? Money(l) : "—", Count(losses.Length, "losing trade"), "negative"),
                ("Payoff ratio", averageWin is { } aw && averageLoss is { } al ? (aw / -al).ToString("0.00", Inv) : "—", "Average win ÷ average loss", "neutral"),
                ("Largest win", wins.Length > 0 ? Money(wins.Max()) : "—", "Single best trade", "positive"),
                ("Largest loss", losses.Length > 0 ? Money(losses.Min()) : "—", "Single worst trade", "negative")]);
            StatGroup("Risk", [
                ("Max drawdown", Money(drawdown.Deepest), drawdown.DeepestNote, drawdown.Deepest < 0 ? "negative" : "neutral"),
                ("Current drawdown", Money(drawdown.Current), drawdown.Current < 0 ? $"Below the {ShortDate(drawdown.PeakDate)} high" : "At a cumulative high", drawdown.Current < 0 ? "negative" : "positive"),
                ("Recovery time", drawdown.Recovery, "Max drawdown's peak back to a new high", "neutral"),
                ("Longest drawdown", Count(drawdown.LongestDays, "day"), "Calendar days below a previous high", "neutral"),
                ("Longest losing streak", Count(streaks.LongestLoss, "loss", "losses"), "Consecutive losing trades", "neutral")]);
            StatGroup("Consistency", [
                ("Green days", $"{(green * 100m / active.Length).ToString("0.0", Inv)}%", $"{green:N0} of {active.Length:N0} trading days", "neutral"),
                ("Best day", Money(best.NetPnl), ShortDate(best.Date), Tone(best.NetPnl)),
                ("Worst day", Money(worst.NetPnl), ShortDate(worst.Date), Tone(worst.NetPnl)),
                ("Current streak", streaks.Current, "Breakeven trades end a streak", streaks.CurrentTone),
                ("Longest winning streak", Count(streaks.LongestWin, "win"), "Consecutive winning trades", "neutral")]);
            html.Append("</div>");
            Histogram(pnl);
            RollingWinRate(pnl);
            html.Append("</div>");
        }

        private void StatGroup(string title, (string Label, string Value, string Note, string Tone)[] rows)
        {
            html.Append($"<section class=\"stat-group\" aria-label=\"{E(title)}\"><h4>{E(title)}</h4><dl>");
            foreach (var (label, value, note, tone) in rows)
                html.Append($"<div><dt>{E(label)}</dt><dd class=\"{tone}\">{E(value)}{(note.Length > 0 ? $"<small>{E(note)}</small>" : "")}</dd></div>");
            html.Append("</dl></section>");
        }

        private static string Count(int count, string singular, string? plural = null) => $"{count:N0} {(count == 1 ? singular : plural ?? singular + "s")}";

        /// <summary>Drawdown on daily closing cumulative P&amp;L, measured from the zero start like the drawdown chart.</summary>
        private (decimal Deepest, string DeepestNote, decimal Current, DateOnly PeakDate, string Recovery, int LongestDays) Drawdown()
        {
            decimal cumulative = 0, peak = 0, deepest = 0;
            var peakDate = active[0].Date.AddDays(-1);
            var deepestPeak = peakDate;
            var deepestTrough = peakDate;
            DateOnly? recovered = null;
            var underwater = false;
            var longest = 0;
            foreach (var day in active)
            {
                cumulative += day.NetPnl;
                if (cumulative >= peak)
                {
                    if (underwater)
                    {
                        longest = Math.Max(longest, day.Date.DayNumber - peakDate.DayNumber);
                        if (deepest < 0 && recovered is null && deepestPeak == peakDate) recovered = day.Date;
                        underwater = false;
                    }
                    peak = cumulative;
                    peakDate = day.Date;
                    continue;
                }
                underwater = true;
                if (cumulative - peak < deepest)
                {
                    deepest = cumulative - peak;
                    deepestPeak = peakDate;
                    deepestTrough = day.Date;
                    recovered = null;
                }
            }
            if (underwater) longest = Math.Max(longest, data.AsOfDate.DayNumber - peakDate.DayNumber);
            var recovery = deepest == 0 ? "No drawdown" : recovered is { } date ? Count(date.DayNumber - deepestPeak.DayNumber, "day") : "Not yet recovered";
            var note = deepest == 0 ? "No decline from a cumulative high" : $"{ShortDate(deepestPeak)} → {ShortDate(deepestTrough)}";
            return (deepest, note, cumulative - peak, peakDate, recovery, longest);
        }

        private static (int LongestWin, int LongestLoss, string Current, string CurrentTone) Streaks(decimal[] pnl)
        {
            int wins = 0, losses = 0, longestWin = 0, longestLoss = 0;
            foreach (var value in pnl)
            {
                (wins, losses) = value > 0 ? (wins + 1, 0) : value < 0 ? (0, losses + 1) : (0, 0);
                longestWin = Math.Max(longestWin, wins);
                longestLoss = Math.Max(longestLoss, losses);
            }
            return wins > 0 ? (longestWin, longestLoss, Count(wins, "win"), "positive")
                : losses > 0 ? (longestWin, longestLoss, Count(losses, "loss", "losses"), "negative")
                : (longestWin, longestLoss, "None", "neutral");
        }

        private void Histogram(decimal[] pnl)
        {
            if (pnl.Length < 5) return;
            var low = pnl.Min();
            var high = pnl.Max();
            var spread = Math.Max(high, 0) - Math.Min(low, 0);
            if (spread == 0) return;
            // Bin edges fall on multiples of a $5 width, so zero is always an edge and no bin mixes gains with losses.
            // Extremely wide P&L ranges fall back to a coarser round width (still a multiple of $5) to keep bars visible.
            var width = spread / HistogramBinWidth <= MaxHistogramBins ? HistogramBinWidth : NiceWidth(spread / MaxHistogramBins);
            var first = (long)Math.Floor(low / width);
            var bins = (int)((long)Math.Floor(high / width) - first + 1);
            if (bins is < 1 or > MaxHistogramBins + 2) return;
            var counts = new int[bins];
            foreach (var value in pnl) counts[(long)Math.Floor(value / width) - first]++;
            var tallest = counts.Max();
            var pitch = 920m / bins;
            html.Append($"<h4 class=\"subhead\">Distribution of trade results</h4><p class=\"muted\">Trades per {E(Money(width))} band of net P&amp;L.</p><svg class=\"histogram\" viewBox=\"0 0 1000 240\" role=\"img\" aria-label=\"Histogram of {pnl.Length:N0} trade results\">");
            html.Append($"<line class=\"gridline\" x1=\"60\" x2=\"980\" y1=\"20\" y2=\"20\"/><text x=\"52\" y=\"24\" text-anchor=\"end\">{tallest}</text><text x=\"52\" y=\"204\" text-anchor=\"end\">0</text><line class=\"zero-line\" x1=\"60\" x2=\"980\" y1=\"200\" y2=\"200\"/>");
            // Axis labels sit on round dollar values (a multiple of the bin width) so narrow bins still read cleanly.
            var labelStep = Math.Max(width, Math.Ceiling(NiceWidth(bins * width / 8m) / width) * width);
            var gap = Math.Min(2m, pitch * .15m);
            for (var index = 0; index < bins; index++)
            {
                var edge = (first + index) * width;
                var x = 60 + index * pitch;
                var height = counts[index] * 180m / tallest;
                if (counts[index] > 0)
                    html.Append($"<rect class=\"{(edge >= 0 ? "positive" : "negative")}\" x=\"{N(x + gap / 2)}\" y=\"{N(200 - height)}\" width=\"{N(Math.Max(.5m, pitch - gap))}\" height=\"{N(height)}\"><title>{E(Money(edge))} to {E(Money(edge + width))}: {Count(counts[index], "trade")}</title></rect>");
                if (edge % labelStep == 0) html.Append($"<text x=\"{N(x)}\" y=\"222\" text-anchor=\"middle\">{E(edge.ToString("N0", Inv))}</text>");
            }
            if (first < 0) html.Append($"<line class=\"reference-line\" x1=\"{N(60 - first * pitch)}\" x2=\"{N(60 - first * pitch)}\" y1=\"14\" y2=\"200\"/>");
            html.Append("</svg>");
        }

        private const decimal HistogramBinWidth = 5m;
        private const int MaxHistogramBins = 400;

        private static decimal NiceWidth(decimal raw)
        {
            if (raw <= 0) return 1;
            var magnitude = (decimal)Math.Pow(10, Math.Floor(Math.Log10((double)raw)));
            var fraction = raw / magnitude;
            return (fraction <= 1 ? 1m : fraction <= 2 ? 2m : fraction <= 2.5m ? 2.5m : fraction <= 5 ? 5m : 10m) * magnitude;
        }

        private void RollingWinRate(decimal[] pnl)
        {
            if (pnl.Length < RollingWindow + 5) return;
            var overall = pnl.Count(v => v > 0) * 100m / pnl.Length;
            var count = pnl.Length - RollingWindow + 1;
            decimal X(int index) => 60 + index * 915m / (count - 1);
            static decimal Y(decimal percent) => 170 - percent * 1.5m;
            var points = Enumerable.Range(0, count).Select(index => $"{N(X(index))},{N(Y(pnl.Skip(index).Take(RollingWindow).Count(v => v > 0) * 100m / RollingWindow))}");
            html.Append($"<h4 class=\"subhead\">Rolling {RollingWindow}-trade win rate</h4><p class=\"muted\">Share of winners in each run of {RollingWindow} consecutive trades. The dashed line is the overall {overall.ToString("0.0", Inv)}%.</p><svg class=\"rolling\" viewBox=\"0 0 1000 200\" role=\"img\" aria-label=\"Rolling {RollingWindow}-trade win rate\">");
            foreach (var level in new[] { 0, 50, 100 })
                html.Append($"<line class=\"gridline\" x1=\"60\" x2=\"975\" y1=\"{N(Y(level))}\" y2=\"{N(Y(level))}\"/><text x=\"52\" y=\"{N(Y(level) + 4)}\" text-anchor=\"end\">{level}%</text>");
            html.Append($"<line class=\"reference-line\" x1=\"60\" x2=\"975\" y1=\"{N(Y(overall))}\" y2=\"{N(Y(overall))}\"/><polyline class=\"rolling-line\" points=\"{string.Join(" ", points)}\"/>");
            html.Append($"<text x=\"60\" y=\"192\">Trade {RollingWindow}</text><text x=\"975\" y=\"192\" text-anchor=\"end\">Trade {pnl.Length:N0}</text></svg>");
        }

        private void Analysis()
        {
            html.Append("<section id=\"analysis\" class=\"analysis\" aria-labelledby=\"analysis-title\"><div class=\"eyebrow\">ANALYSIS</div><h2 id=\"analysis-title\">Where the results come from</h2>");
            if (trades.Length == 0) { html.Append("<p class=\"panel empty\">Breakdowns appear once completed trades are published.</p></section>"); return; }
            html.Append("<div class=\"breakdowns\">");
            if (data.SchemaVersion >= 4) SetupBreakdowns();
            var symbols = trades.GroupBy(t => t.Trade.Symbol, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
            var shown = symbols.Take(10).Select(g => (g.Key, g.Select(t => t.Trade).ToArray())).ToList();
            if (symbols.Length > 10) shown.Add(($"Other ({symbols.Length - 10} symbols)", symbols.Skip(10).SelectMany(g => g.Select(t => t.Trade)).ToArray()));
            Breakdown("By symbol", "Symbol", symbols.Length > 10 ? "The ten most traded symbols." : "", shown);
            Breakdown("By direction", "Direction", "", trades.GroupBy(t => t.Trade.Direction).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.Key, g.Select(t => t.Trade).ToArray())));
            Breakdown("By weekday", "Day", "Grouped by trading day.", trades.GroupBy(t => t.Date.DayOfWeek).OrderBy(g => ((int)g.Key + 6) % 7)
                .Select(g => (g.Key.ToString(), g.Select(t => t.Trade).ToArray())));
            var timed = trades.Where(t => t.Trade.EntryUtc.HasValue).ToArray();
            if (timed.Length > 0)
                Breakdown("By entry hour", "Hour", $"Entry time in {data.TimeZone}{(timed.Length < trades.Length ? $"; {Count(trades.Length - timed.Length, "trade")} without an exact entry time excluded" : "")}.",
                    timed.GroupBy(t => TimeZoneInfo.ConvertTime(t.Trade.EntryUtc!.Value, zone).Hour).OrderBy(g => g.Key)
                        .Select(g => ($"{g.Key:00}:00–{g.Key:00}:59", g.Select(t => t.Trade).ToArray())));
            html.Append("</div>");
            MonthlyReturns();
            BenchmarkComparison();
            TearSheetGroups();
            html.Append("</section>");
        }

        private void SetupBreakdowns()
        {
            var bySetup = trades.GroupBy(t => SetupName(t.Trade)).OrderBy(g => g.Key == NoSetup).ThenByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
            if (bySetup.All(g => g.Key == NoSetup)) return;
            Breakdown("By setup", "Setup", "Each trade counts under its primary setup.", bySetup.Select(g => (g.Key, g.Select(t => t.Trade).ToArray())));
            var adherence = trades.Select(t => (Label: Adherence(t.Trade), t.Trade)).Where(x => x.Label is not null).ToArray();
            if (adherence.Length == 0) return;
            Breakdown("Rules followed vs. broken", "Setup rules",
                "Followed: every required criterion met and no disqualifier present. Broken: a required criterion not met or a disqualifier present.",
                new[] { "Rules followed", "Rules broken", "Not fully evaluated" }.Select(label => (label, adherence.Where(x => x.Label == label).Select(x => x.Trade).ToArray())));
        }

        private static string SetupName(PublicationTrade trade) => trade.Setup is not { } setup ? NoSetup
            : setup.Definitions.FirstOrDefault(d => d.Role == "Primary")?.Name ?? setup.Definitions.FirstOrDefault()?.Name
              ?? (string.IsNullOrWhiteSpace(setup.Label) ? NoSetup : setup.Label);

        private static string? Adherence(PublicationTrade trade)
        {
            var rules = trade.Setup?.Definitions.SelectMany(d => d.Criteria).Where(c => c.Type is "Required" or "Disqualifier").ToArray() ?? [];
            if (rules.Length == 0) return null;
            if (rules.Any(c => (c.Type == "Required" && c.State == "NotMet") || (c.Type == "Disqualifier" && c.State == "Met"))) return "Rules broken";
            return rules.Any(c => c.State == "Unknown") ? "Not fully evaluated" : "Rules followed";
        }

        private void Breakdown(string title, string column, string note, IEnumerable<(string Label, PublicationTrade[] Trades)> groups)
        {
            var rows = groups.Where(g => g.Trades.Length > 0).ToArray();
            if (rows.Length == 0) return;
            var scale = Math.Max(1m, rows.Max(r => Math.Abs(r.Trades.Sum(t => t.NetPnl))));
            html.Append($"<div class=\"panel breakdown\"><h3>{E(title)}</h3>{(note.Length > 0 ? $"<p class=\"muted\">{E(note)}</p>" : "")}<div class=\"table-scroll\"><table><thead><tr><th scope=\"col\">{E(column)}</th><th scope=\"col\">Trades</th><th scope=\"col\">Win rate</th><th scope=\"col\">Expectancy</th><th scope=\"col\">Net P&amp;L</th></tr></thead><tbody>");
            foreach (var (label, group) in rows)
            {
                var net = group.Sum(t => t.NetPnl);
                var expectancy = net / group.Length;
                var winRate = group.Count(t => t.NetPnl > 0) * 100m / group.Length;
                html.Append($"<tr><th scope=\"row\">{E(label)}</th><td>{group.Length.ToString("N0", Inv)}</td><td>{winRate.ToString("0.0", Inv)}%</td><td class=\"{Tone(expectancy)}\">{E(Money(expectancy))}</td><td><span class=\"{Tone(net)}\">{E(Money(net))}</span><svg class=\"row-bar\" viewBox=\"0 0 100 4\" preserveAspectRatio=\"none\" aria-hidden=\"true\"><rect class=\"{Tone(net)}\" x=\"0\" y=\"0\" width=\"{N(Math.Abs(net) / scale * 100)}\" height=\"4\"/></svg></td></tr>");
            }
            html.Append("</tbody></table></div></div>");
        }

        private void MonthlyReturns()
        {
            if (months.Length == 0) return;
            var percent = months.Any(m => m.OpeningBalance is > 0);
            var lookup = months.ToDictionary(m => m.Month);
            static string Pct(decimal value) => $"{(value > 0 ? "+" : "")}{value.ToString("0.0", Inv)}%";
            var caption = percent
                ? "Realized net P&L ÷ account balance at the start of each month. Deposits and withdrawals change the balance but are not returns. Years compound their months; amounts appear where no opening balance was recorded."
                : "Realized net P&L per month. Percentage returns appear once account balances are published.";
            html.Append($"<div class=\"panel curve monthly-returns\"><div class=\"eyebrow\">MONTHLY RETURNS</div><h3>{(percent ? "Return by month" : "Net P&amp;L by month")}</h3><p class=\"muted\">{E(caption)}</p><div class=\"table-scroll\"><table class=\"returns-table\"><thead><tr><th scope=\"col\">Year</th>");
            for (var number = 1; number <= 12; number++) html.Append($"<th scope=\"col\">{new DateOnly(2000, number, 1).ToString("MMM", Inv)}</th>");
            html.Append("<th scope=\"col\">Year</th></tr></thead><tbody>");
            foreach (var year in months.Select(m => m.Month.Year).Distinct().OrderDescending())
            {
                html.Append($"<tr><th scope=\"row\">{year}</th>");
                decimal compound = 1, sum = 0;
                var compounded = false;
                for (var number = 1; number <= 12; number++)
                {
                    if (!lookup.TryGetValue(new DateOnly(year, number, 1), out var month)) { html.Append("<td class=\"ret\"></td>"); continue; }
                    sum += month.NetPnl;
                    string value;
                    if (percent && month.OpeningBalance is decimal opening && opening > 0)
                    {
                        compound *= 1 + month.NetPnl / opening;
                        compounded = true;
                        value = Pct(month.NetPnl / opening * 100);
                    }
                    else value = month.NetPnl.ToString("N0", Inv);
                    html.Append($"<td class=\"ret {Tone(month.NetPnl)}\" title=\"{E($"{month.Month.ToString("MMMM yyyy", Inv)}: {Money(month.NetPnl)}, {TradeCount(month.TradeCount)}")}\"><a href=\"#{MonthId(month.Month)}\">{E(value)}</a></td>");
                }
                var total = percent && compounded ? Pct((compound - 1) * 100) : sum.ToString("N0", Inv);
                html.Append($"<td class=\"ret year {Tone(sum)}\" title=\"{E($"{year}: {Money(sum)}")}\">{E(total)}</td></tr>");
            }
            html.Append("</tbody></table></div></div>");
        }

        private void BenchmarkComparison()
        {
            if (data.Benchmark is not { } benchmark) return;
            html.Append($"<div class=\"panel curve benchmark\"><div class=\"eyebrow\">BENCHMARK</div><h3>Compared with {E(benchmark.Symbol)}</h3><p class=\"muted\">Strategy returns are measured on the account balance; the benchmark uses imported daily closes over the same period.</p><dl class=\"stats benchmark-stats\">");
            void Stat(string label, decimal? value, Func<decimal, string> format, string note)
            {
                if (value is { } number)
                    html.Append($"<div class=\"stat\"><dt>{E(label)}</dt><dd class=\"{Tone(number)}\">{E(format(number))}<small>{E(note)}</small></dd></div>");
            }
            static string Points(decimal value) => $"{value.ToString("0.00", Inv)}%";
            static string Ratio(decimal value) => value.ToString("0.00", Inv);
            Stat("Strategy return", benchmark.StrategyReturnPercent, Points, "Cumulative");
            Stat($"{benchmark.Symbol} return", benchmark.BenchmarkReturnPercent, Points, "Cumulative");
            Stat("Alpha", benchmark.AlphaPercent, Points, "Strategy minus benchmark");
            Stat("Alpha (ann.)", benchmark.AnnualizedAlphaPercent, v => $"{v.ToString("0.0", Inv)}%", "Annualized excess return");
            Stat("Beta", benchmark.Beta, Ratio, "Sensitivity to the benchmark");
            Stat("Correlation", benchmark.Correlation, Ratio, "Daily return correlation");
            html.Append("</dl>");
            BenchmarkChart(benchmark);
            html.Append("</div>");
        }

        private void BenchmarkChart(PublicationBenchmark benchmark)
        {
            if (benchmark.StartingBalance is not decimal start || start <= 0 || data.Equity.Count == 0 || benchmark.Points.Count < 2) return;
            var origin = data.Equity[0].Date.AddDays(-1);
            var strategy = new[] { (Date: origin, Value: 0m) }.Concat(data.Equity.Select(p => (p.Date, Value: p.CumulativePnl / start * 100))).ToArray();
            // Rebase the benchmark on its last close at or before the strategy's start.
            var baseIndex = Math.Max(0, benchmark.Points.FindLastIndex(p => p.Date <= origin));
            var basis = benchmark.Points[baseIndex].Close;
            var market = benchmark.Points.Skip(baseIndex).Select(p => (p.Date, Value: (p.Close / basis - 1) * 100)).ToArray();
            var first = new[] { strategy[0].Date, market[0].Date }.Min();
            var last = new[] { strategy[^1].Date, market[^1].Date }.Max();
            var span = Math.Max(1, last.DayNumber - first.DayNumber);
            decimal X(DateOnly date) => 60 + (date.DayNumber - first.DayNumber) * 915m / span;
            var values = strategy.Select(p => p.Value).Concat(market.Select(p => p.Value)).ToArray();
            var min = Math.Min(0, values.Min());
            var max = Math.Max(0, values.Max());
            var range = max == min ? 1 : max - min;
            decimal Y(decimal value) => 220 - (value - min) * 200 / range;
            html.Append($"<h4 class=\"subhead\">Cumulative return</h4><p class=\"muted\">Strategy: realized net P&amp;L as a share of the {E(Money(start))} starting balance.</p><svg class=\"benchmark-chart\" viewBox=\"0 0 1000 260\" role=\"img\" aria-label=\"Cumulative return of the strategy and {E(benchmark.Symbol)}\">");
            for (var tick = 0; tick < 4; tick++)
            {
                var value = min + range * tick / 3;
                html.Append($"<line class=\"gridline\" x1=\"60\" x2=\"975\" y1=\"{N(Y(value))}\" y2=\"{N(Y(value))}\"/><text x=\"52\" y=\"{N(Y(value) + 4)}\" text-anchor=\"end\">{E(value.ToString("0.#", Inv))}%</text>");
            }
            var ticks = Ticks(first, last, X);
            if (ticks.Count >= 2)
                foreach (var (x, label) in ticks) html.Append($"<text x=\"{N(Math.Clamp(x, 85, 950))}\" y=\"250\" text-anchor=\"middle\">{E(label)}</text>");
            if (min < 0) html.Append($"<line class=\"zero-line\" x1=\"60\" x2=\"975\" y1=\"{N(Y(0))}\" y2=\"{N(Y(0))}\"/>");
            html.Append($"<polyline class=\"line-benchmark\" points=\"{string.Join(" ", market.Select(p => $"{N(X(p.Date))},{N(Y(p.Value))}"))}\"/>");
            html.Append($"<polyline class=\"line-strategy\" points=\"{string.Join(" ", strategy.Select(p => $"{N(X(p.Date))},{N(Y(p.Value))}"))}\"/></svg>");
            html.Append($"<div class=\"legend\"><span><i class=\"key strategy\"></i>Strategy {E(strategy[^1].Value.ToString("0.00", Inv))}%</span><span><i class=\"key benchmark\"></i>{E(benchmark.Symbol)} {E(market[^1].Value.ToString("0.00", Inv))}%</span></div>");
        }

        private void TearSheetGroups()
        {
            if (data.TearSheet is not { Count: > 0 } groups) return;
            html.Append("<div class=\"tear-sheet\"><div class=\"eyebrow\">TEAR SHEET</div><h3>Detailed indicators</h3><p class=\"muted\">Computed by TradeFoundry from the published trades. Some indicators use gross P&amp;L; each note says what it measures.</p><div class=\"tear-sheet-groups\">");
            foreach (var group in groups)
            {
                html.Append($"<section class=\"panel tear-group\"><h4>{E(group.Title)}</h4><p class=\"muted\">{E(group.Description)}</p><dl>");
                foreach (var metric in group.Metrics)
                    html.Append($"<div><dt>{E(metric.Label)}</dt><dd class=\"{E(metric.Tone)}\">{E(metric.Value)}{(metric.Note.Length > 0 ? $"<small>{E(metric.Note)}</small>" : "")}</dd></div>");
                html.Append("</dl></section>");
            }
            html.Append("</div></div>");
        }
    }
}

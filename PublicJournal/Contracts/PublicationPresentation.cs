using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace TradeFoundry.Publishing;

public static class PublicationPresentation
{
    public const int PageSize = 10;
    public static string Style()
    {
        using var stream = typeof(PublicationPresentation).Assembly.GetManifestResourceStream("TradeFoundry.Publishing.Contracts.publication.css")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string Document(PublicationSnapshot snapshot, DateTimeOffset? publishedUtc, Func<string, string> imageUrl,
        string pageUrl, string styleUrl, DateOnly? before = null, DateOnly? selected = null, bool preview = false)
    {
        string E(string? value) => WebUtility.HtmlEncode(value ?? "");
        string Money(decimal? value) => value.HasValue ? $"{snapshot.Currency} {value.Value.ToString("N2", CultureInfo.InvariantCulture)}" : "Unavailable";
        string Tone(decimal value) => value > 0 ? "positive" : value < 0 ? "negative" : "neutral";
        string DateLink(DateOnly date) => preview ? $"#day-{date:yyyy-MM-dd}" : $"{pageUrl}{(pageUrl.Contains('?') ? '&' : '?')}date={date:yyyy-MM-dd}&snapshot={snapshot.SnapshotId:D}#day-{date:yyyy-MM-dd}";
        var html = new StringBuilder($"<!doctype html><html lang=\"en\"><head>{(preview ? "" : snapshot.GoogleTagScript)}<meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{E(snapshot.Title)}</title><link rel=\"stylesheet\" href=\"{E(styleUrl)}\"></head><body><main>");
        html.Append($"<header><div class=\"eyebrow\">PUBLIC TRADING JOURNAL{(preview ? " · PUBLICATION PREVIEW" : "")}</div><h1>{E(snapshot.Title)}</h1><div class=\"metadata\"><span>{E(snapshot.Currency)} · {E(snapshot.TimeZone)}</span><span>{(preview ? "Snapshot prepared" : "Last published")} {(publishedUtc ?? snapshot.GeneratedUtc).ToString("MMM d, yyyy 'at' HH:mm 'UTC'", CultureInfo.InvariantCulture)}</span></div>");
        if (snapshot.IntroductionContent.Count > 0)
            html.Append($"<div class=\"introduction journal-content\">{PublicationContent.Render(snapshot.IntroductionContent, imageUrl)}</div>");
        else if (!string.IsNullOrWhiteSpace(snapshot.Introduction))
            html.Append($"<p class=\"introduction\">{E(snapshot.Introduction)}</p>");
        html.Append("</header>");
        html.Append("<section aria-label=\"Performance dashboard\" class=\"metrics\">");
        void Metric(string label, string value, string? note = null) => html.Append($"<article class=\"metric\"><span>{E(label)}</span><strong>{E(value)}</strong>{(note is null ? "" : $"<small>{E(note)}</small>")}</article>");
        Metric("Calculated account balance", Money(snapshot.Dashboard.Balance), snapshot.Dashboard.BalanceNote);
        Metric("Lifetime net trading profit", Money(snapshot.Dashboard.LifetimeNetPnl), "After trading fees");
        Metric($"{snapshot.AsOfDate.ToString("MMMM yyyy", CultureInfo.InvariantCulture)} net profit", Money(snapshot.Dashboard.CurrentMonthNetPnl), $"As of {snapshot.AsOfDate:yyyy-MM-dd}");
        var activeDays = snapshot.Days.Where(d => d.Trades.Count > 0).OrderBy(d => d.Date).ToArray();
        Metric("Trading since", activeDays.Length > 0 ? activeDays[0].Date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) : "Unavailable", $"{activeDays.Length:N0} trading days with completed trades");
        Metric("Completed trades", snapshot.Dashboard.CompletedTrades.ToString("N0", CultureInfo.InvariantCulture));
        Metric("Alpha", snapshot.Dashboard.Alpha ?? "Unavailable", $"Alpha (ann) {snapshot.Dashboard.AnnualizedAlpha ?? "Unavailable"}");
        Metric("Win rate", snapshot.Dashboard.WinRate is { } rate ? $"{rate:0.0}%" : "Unavailable");
        Metric("Profit factor", snapshot.Dashboard.ProfitFactor is { } factor ? factor.ToString("0.00", CultureInfo.InvariantCulture) : "Unavailable", snapshot.Dashboard.ProfitFactor.HasValue ? "Winning net P&L ÷ losing net P&L" : "Requires realized losses");
        html.Append("</section><section class=\"panel curve\" aria-labelledby=\"curve-title\"><div class=\"section-heading\"><div><div class=\"eyebrow\">EQUITY CURVE</div><h2 id=\"curve-title\">Cumulative realized net P&amp;L</h2></div></div><p class=\"muted\">Daily closing results after fees. Deposits and withdrawals are excluded.</p>");
        if (snapshot.Equity.Count == 0) html.Append("<p class=\"empty\">No completed trades have been published.</p>");
        else
        {
            var values = new[] { 0m }.Concat(snapshot.Equity.Select(p => p.CumulativePnl)).ToArray();
            var min = Math.Min(0, values.Min());
            var max = Math.Max(0, values.Max());
            var range = max == min ? 1 : max - min;
            string N(decimal number) => number.ToString("0.##", CultureInfo.InvariantCulture);
            decimal X(int index) => 85m + index * 890m / Math.Max(1, values.Length - 1);
            decimal Y(decimal value) => 255m - (value - min) * 215m / range;
            html.Append($"<svg class=\"equity\" viewBox=\"0 0 1000 300\" role=\"img\" aria-labelledby=\"equity-title\"><title id=\"equity-title\">Cumulative realized net P&amp;L in {E(snapshot.Currency)}, ending at {E(Money(values[^1]))}</title>");
            for (var tick = 0; tick < 4; tick++)
            {
                var value = min + range * tick / 3;
                html.Append($"<line class=\"gridline\" x1=\"85\" x2=\"975\" y1=\"{N(Y(value))}\" y2=\"{N(Y(value))}\"/><text x=\"77\" y=\"{N(Y(value) + 4)}\" text-anchor=\"end\">{E(value.ToString("N0", CultureInfo.InvariantCulture))}</text>");
            }
            html.Append($"<polyline class=\"equity-line\" points=\"{string.Join(" ", values.Select((value, index) => $"{N(X(index))},{N(Y(value))}"))}\"/>");
            html.Append($"<text x=\"85\" y=\"287\">{snapshot.Equity[0].Date:yyyy-MM-dd}</text><text x=\"975\" y=\"287\" text-anchor=\"end\">{snapshot.Equity[^1].Date:yyyy-MM-dd}</text></svg>");
        }
        html.Append("</section><section class=\"panel curve trade-results\" aria-labelledby=\"results-title\"><h2 id=\"results-title\">Trade P&amp;L</h2><p class=\"muted\">One bar per completed trade, after fees, ordered by trading date.</p>");
        var results = activeDays.SelectMany(d => d.Trades.Select(t => (Date: d.Date, Trade: t))).ToArray();
        if (results.Length == 0) html.Append("<p class=\"empty\">No completed trades have been published.</p>");
        else
        {
            var scale = Math.Max(1m, results.Max(r => Math.Abs(r.Trade.NetPnl)));
            html.Append("<div class=\"pnl-bars\" role=\"img\" aria-label=\"Net profit and loss per completed trade\">");
            for (var i = 0; i < results.Length; i++)
            {
                var row = results[i];
                var height = Math.Max(1m, Math.Abs(row.Trade.NetPnl) / scale * 100).ToString("0.##", CultureInfo.InvariantCulture);
                html.Append($"<div class=\"pnl-column\"><div class=\"pnl-half gain\">{(row.Trade.NetPnl >= 0 ? $"<span style=\"height:{height}%\"></span>" : "")}</div><div class=\"pnl-half loss\">{(row.Trade.NetPnl < 0 ? $"<span style=\"height:{height}%\"></span>" : "")}</div><span class=\"pnl-tooltip\">Trade {i + 1} · {row.Date:yyyy-MM-dd} · {E(row.Trade.Symbol)} · {E(Money(row.Trade.NetPnl))}</span></div>");
            }
            html.Append("</div><p class=\"muted\">Trades run left to right. Hover a bar for its result.</p>");
        }
        html.Append("</section><section class=\"calendar-section\" aria-labelledby=\"calendar-title\"><div class=\"eyebrow\">DAY BY DAY</div><h2 id=\"calendar-title\">Monthly performance</h2>");
        var dayLookup = snapshot.Days.ToDictionary(d => d.Date);
        var orderedMonths = snapshot.Months.OrderByDescending(m => m.Month).ToArray();
        for (var index = 0; index < orderedMonths.Length; index++)
        {
            var month = orderedMonths[index];
            var open = selected.HasValue ? selected?.Year == month.Month.Year && selected?.Month == month.Month.Month : index == 0;
            html.Append($"<details class=\"month panel\"{(open ? " open" : "")}><summary><span>{month.Month.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}</span><span class=\"month-total {Tone(month.NetPnl)}\">{E(Money(month.NetPnl))}<small>{TradeCount(month.TradeCount)}</small></span></summary><div class=\"calendar\">");
            foreach (var name in new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" }) html.Append($"<div class=\"weekday\">{name}</div>");
            for (var blank = 0; blank < (int)month.Month.DayOfWeek; blank++) html.Append("<div class=\"calendar-blank\" aria-hidden=\"true\"></div>");
            for (var number = 1; number <= DateTime.DaysInMonth(month.Month.Year, month.Month.Month); number++)
            {
                var date = new DateOnly(month.Month.Year, month.Month.Month, number);
                if (dayLookup.TryGetValue(date, out var day))
                    html.Append($"<a class=\"calendar-day {Tone(day.NetPnl)}\" href=\"{E(DateLink(date))}\" aria-label=\"{date:yyyy-MM-dd}: {E(Money(day.NetPnl))}, {TradeCount(day.Trades.Count)}\"><span>{number}</span>{(day.Trades.Count > 0 ? $"<strong>{E(day.NetPnl.ToString("N2", CultureInfo.InvariantCulture))}</strong><small>{TradeCount(day.Trades.Count)}</small>" : "<small>Journal</small>")}</a>");
                else html.Append($"<div class=\"calendar-day quiet\"><span>{number}</span></div>");
            }
            html.Append("</div></details>");
        }
        if (orderedMonths.Length == 0) html.Append("<p class=\"empty panel\">No activity has been published yet.</p>");
        html.Append("</section><section id=\"entries\" aria-labelledby=\"entries-title\"><div class=\"eyebrow\">THE JOURNAL</div><h2 id=\"entries-title\">Daily notes &amp; trades</h2>");
        var ordered = snapshot.Days.OrderByDescending(d => d.Date).Where(d => !before.HasValue || d.Date < before).ToArray();
        var displayed = selected.HasValue ? snapshot.Days.Where(d => d.Date == selected).ToArray() : ordered.Take(preview ? int.MaxValue : PageSize).ToArray();
        foreach (var day in displayed)
        {
            html.Append($"<article class=\"panel day-entry\" id=\"day-{day.Date:yyyy-MM-dd}\"><div class=\"day-heading\"><h3>{day.Date.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)}</h3><strong class=\"{Tone(day.NetPnl)}\">{E(Money(day.NetPnl))}</strong></div>");
            if (day.Content.Count > 0) html.Append($"<div class=\"journal-content\">{PublicationContent.Render(day.Content, imageUrl)}</div>");
            html.Append($"<div class=\"trade-summary\">{day.Trades.Count} completed {(day.Trades.Count == 1 ? "trade" : "trades")} · {day.Wins} {(day.Wins == 1 ? "win" : "wins")} · {day.Losses} {(day.Losses == 1 ? "loss" : "losses")} · {day.Breakevens} breakeven</div>");
            if (day.Trades.Count > 0)
            {
                html.Append("<div class=\"trade-table-wrap\"><table><thead><tr><th>Symbol</th><th>Direction</th><th>Quantity</th><th>Net P&amp;L</th><th>Chart</th></tr></thead><tbody>");
                foreach (var trade in day.Trades)
                {
                    var chartButton = trade.Chart is { } chart
                        ? $"<button type=\"button\" class=\"chart-button\" data-public-chart=\"{E(JsonSerializer.Serialize(chart, PublicationJson.Options))}\" data-chart-title=\"{E($"{trade.Symbol} · {trade.Direction} · {day.Date:yyyy-MM-dd}")}\" data-chart-timezone=\"{E(snapshot.TimeZone)}\" data-chart-direction=\"{E(trade.Direction)}\" data-chart-pnl=\"{trade.NetPnl.ToString(CultureInfo.InvariantCulture)}\" aria-haspopup=\"dialog\" aria-controls=\"trade-chart-dialog\" aria-label=\"View chart for {E(trade.Symbol)} {E(trade.Direction)} trade on {day.Date:yyyy-MM-dd}\">View chart</button>"
                        : "<span class=\"muted\">Chart unavailable</span>";
                    html.Append($"<tr><td>{E(trade.Symbol)}</td><td>{E(trade.Direction)}</td><td>{trade.Quantity}</td><td class=\"{Tone(trade.NetPnl)}\">{E(Money(trade.NetPnl))}</td><td>{chartButton}</td></tr>");
                }
                html.Append("</tbody></table></div>");
            }
            html.Append("</article>");
        }
        if (displayed.Length == 0) html.Append("<p class=\"panel empty\">There are no journal entries or completed trades to display.</p>");
        if (selected.HasValue || before.HasValue) html.Append($"<a class=\"button\" href=\"{E(pageUrl)}#entries\">Back to latest entries</a> ");
        if (!preview && !selected.HasValue && ordered.Length > PageSize)
            html.Append($"<a class=\"button\" rel=\"next\" href=\"{E(pageUrl)}?before={displayed[^1].Date:yyyy-MM-dd}&amp;snapshot={snapshot.SnapshotId:D}#entries\">Load older entries</a>");
        html.Append("</section><footer>Published from TradeFoundry · Historical realized trading results</footer></main>");
        if (displayed.Any(d => d.Trades.Any(t => t.Chart is not null)))
        {
            html.Append("<dialog id=\"trade-chart-dialog\" class=\"trade-chart-dialog\" aria-labelledby=\"trade-chart-title\"><div class=\"chart-dialog-heading\"><div><h2 id=\"trade-chart-title\">Trade chart</h2><p class=\"muted\" data-chart-description></p></div><button type=\"button\" class=\"chart-button\" data-chart-close aria-label=\"Close trade chart\" autofocus>Close</button></div><div class=\"public-trade-chart\" data-public-chart-host role=\"img\" aria-label=\"Trade candlestick chart\"></div><div class=\"chart-dialog-toolbar\"><p data-chart-prices></p><button type=\"button\" class=\"chart-button\" data-chart-focus>Focus trade</button></div><p class=\"muted\">▶ Entry · ◀ Exit · Triangles mark the recorded trade prices.</p><p class=\"muted\">TradingView Lightweight Charts™ · <a href=\"https://www.tradingview.com/\" target=\"_blank\" rel=\"noopener noreferrer\">TradingView</a></p></dialog>");
            foreach (var script in new[] { "lightweight-charts.standalone.production.js", "trade-execution-drawing.js", "publication-charts.js" })
                html.Append($"<script defer src=\"/publication-assets/{script}\"></script>");
        }
        html.Append("</body></html>");
        return html.ToString();
    }

    private static string TradeCount(int count) => $"{count} {(count == 1 ? "trade" : "trades")}";
}



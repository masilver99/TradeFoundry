using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace TradeFoundry.Publishing;

public static class PublicationPresentation
{
    public const int PageSize = 10;
    public const int FeedSize = 30;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Style()
    {
        using var stream = typeof(PublicationPresentation).Assembly.GetManifestResourceStream("TradeFoundry.Publishing.Contracts.publication.css")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Renders the complete reader page. <paramref name="siteUrl"/> is the absolute site root (ending in "/") used for canonical and Open Graph URLs.</summary>
    public static string Document(PublicationSnapshot snapshot, DateTimeOffset? publishedUtc, Func<string, string> imageUrl,
        string pageUrl, string styleUrl, DateOnly? before = null, DateOnly? selected = null, bool preview = false, string? siteUrl = null)
        => new Reader(snapshot, publishedUtc, imageUrl, pageUrl, styleUrl, before, selected, preview, siteUrl).Render();

    /// <summary>Atom feed of the newest daily entries; every URL is absolute under <paramref name="siteUrl"/>.</summary>
    public static string Feed(PublicationSnapshot snapshot, DateTimeOffset? publishedUtc, string siteUrl, Func<string, string> imageUrl)
    {
        const string ns = "http://www.w3.org/2005/Atom";
        static string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv);
        string Money(decimal value) => $"{snapshot.Currency} {value.ToString("N2", Inv)}";
        using var buffer = new MemoryStream();
        using (var xml = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("feed", ns);
            xml.WriteElementString("title", ns, snapshot.Title);
            xml.WriteElementString("id", ns, siteUrl);
            xml.WriteElementString("updated", ns, Stamp(publishedUtc ?? snapshot.GeneratedUtc));
            WriteLink(xml, ns, "self", siteUrl + "feed.xml");
            WriteLink(xml, ns, "alternate", siteUrl);
            xml.WriteStartElement("author", ns);
            xml.WriteElementString("name", ns, snapshot.Title);
            xml.WriteEndElement();
            foreach (var day in snapshot.Days.OrderByDescending(d => d.Date).Take(FeedSize))
            {
                var iso = Iso(day.Date);
                var link = $"{siteUrl}?date={iso}";
                // Days carry no edit timestamps; the trading date keeps entries stable across republishes.
                var stamp = Stamp(new DateTimeOffset(day.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
                xml.WriteStartElement("entry", ns);
                xml.WriteElementString("title", ns, day.Trades.Count > 0
                    ? $"{LongDate(day.Date)} · {Money(day.NetPnl)}" : LongDate(day.Date));
                xml.WriteElementString("id", ns, link);
                WriteLink(xml, ns, "alternate", $"{link}#day-{iso}");
                xml.WriteElementString("published", ns, stamp);
                xml.WriteElementString("updated", ns, stamp);
                var content = new StringBuilder(PublicationContent.Render(day.Content, imageUrl));
                if (day.Trades.Count > 0)
                {
                    content.Append($"<p>{TradeCount(day.Trades.Count)} · {day.Wins} won · {day.Losses} lost · {day.Breakevens} breakeven</p><ul>");
                    foreach (var trade in day.Trades)
                        content.Append($"<li>{WebUtility.HtmlEncode(trade.Symbol)} {WebUtility.HtmlEncode(trade.Direction)} ×{trade.Quantity}: {Money(trade.NetPnl)}</li>");
                    content.Append("</ul>");
                }
                xml.WriteStartElement("content", ns);
                xml.WriteAttributeString("type", "html");
                xml.WriteString(content.ToString());
                xml.WriteEndElement();
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteLink(XmlWriter xml, string ns, string rel, string href)
    {
        xml.WriteStartElement("link", ns);
        xml.WriteAttributeString("rel", rel);
        xml.WriteAttributeString("href", href);
        xml.WriteEndElement();
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", Inv);
    private static string LongDate(DateOnly date) => date.ToString("dddd, MMMM d, yyyy", Inv);
    private static string ShortDate(DateOnly date) => date.ToString("MMM d, yyyy", Inv);
    private static string TradeCount(int count) => $"{count:N0} {(count == 1 ? "trade" : "trades")}";
    private static string Tone(decimal value) => value > 0 ? "positive" : value < 0 ? "negative" : "neutral";
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string N(decimal number) => number.ToString("0.##", Inv);

    private sealed class Reader(PublicationSnapshot snapshot, DateTimeOffset? publishedUtc, Func<string, string> imageUrl,
        string pageUrl, string styleUrl, DateOnly? before, DateOnly? selected, bool preview, string? siteUrl)
    {
        private readonly StringBuilder html = new();
        // Newest first: the order of the journal list, the pager and previous/next navigation.
        private readonly PublicationDay[] all = snapshot.Days.OrderByDescending(d => d.Date).ToArray();
        private readonly PublicationDay[] active = snapshot.Days.Where(d => d.Trades.Count > 0).OrderBy(d => d.Date).ToArray();
        private readonly Dictionary<DateOnly, PublicationDay> lookup = snapshot.Days.ToDictionary(d => d.Date);
        private readonly PublicationMonth[] months = snapshot.Months.OrderByDescending(m => m.Month).ToArray();
        private readonly TimeZoneInfo zone = Zone(snapshot.TimeZone);
        private readonly decimal heatScale = HeatScale(snapshot.Days.Where(d => d.Trades.Count > 0));
        private PublicationDay? SelectedDay => selected is { } date ? lookup.GetValueOrDefault(date) : null;

        private string Money(decimal? value) => value.HasValue ? $"{snapshot.Currency} {value.Value.ToString("N2", Inv)}" : "Unavailable";
        private string DateLink(DateOnly date) => preview ? $"#day-{Iso(date)}" : $"{WithQuery(pageUrl, $"date={Iso(date)}")}#day-{Iso(date)}";
        private static string WithQuery(string url, string query) => $"{url}{(url.Contains('?') ? '&' : '?')}{query}";
        private static string MonthId(DateOnly month) => $"month-{month.ToString("yyyy-MM", Inv)}";

        public string Render()
        {
            Head();
            Navigation();
            html.Append("<main>");
            Header();
            Dashboard();
            html.Append("<section id=\"performance\" class=\"performance\" aria-labelledby=\"performance-title\"><div class=\"eyebrow\">PERFORMANCE</div><h2 id=\"performance-title\">Results over time</h2>");
            EquityCurve();
            TradeResults();
            html.Append("</section>");
            Calendar();
            Entries();
            html.Append($"<footer>Published from TradeFoundry · Historical realized trading results{(preview ? "" : " · <a href=\"/feed.xml\">Atom feed</a>")}</footer></main>");
            ChartDialog();
            html.Append("<script defer src=\"/publication-assets/publication-reader.js\"></script></body></html>");
            return html.ToString();
        }

        private void Head()
        {
            var day = SelectedDay;
            var title = day is null ? snapshot.Title : $"{LongDate(day.Date)} · {snapshot.Title}";
            var description = Description(day);
            html.Append($"<!doctype html><html lang=\"en\"><head>{(preview ? "" : snapshot.GoogleTagScript)}<meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"color-scheme\" content=\"light dark\"><title>{E(title)}</title><meta name=\"description\" content=\"{E(description)}\">");
            html.Append($"<meta property=\"og:type\" content=\"{(day is null ? "website" : "article")}\"><meta property=\"og:site_name\" content=\"{E(snapshot.Title)}\"><meta property=\"og:title\" content=\"{E(title)}\"><meta property=\"og:description\" content=\"{E(description)}\"><meta name=\"twitter:card\" content=\"summary\">");
            if (!preview && !string.IsNullOrEmpty(siteUrl))
            {
                var canonical = selected is { } date ? $"{siteUrl}?date={Iso(date)}" : before is { } cursor ? $"{siteUrl}?before={Iso(cursor)}" : siteUrl;
                html.Append($"<link rel=\"canonical\" href=\"{E(canonical)}\"><meta property=\"og:url\" content=\"{E(canonical)}\">");
            }
            if (!preview)
                html.Append($"<link rel=\"icon\" href=\"/favicon.svg\" type=\"image/svg+xml\"><link rel=\"alternate\" type=\"application/atom+xml\" title=\"{E(snapshot.Title)}\" href=\"/feed.xml\">");
            html.Append($"<link rel=\"stylesheet\" href=\"{E(styleUrl)}\"></head><body>");
        }

        private string Description(PublicationDay? day)
        {
            if (day is not null)
                return Truncate((day.Trades.Count > 0 ? $"{TradeCount(day.Trades.Count)}, net {Money(day.NetPnl)} after fees. " : "") + PlainText(day.Content));
            var intro = snapshot.IntroductionContent.Count > 0 ? PlainText(snapshot.IntroductionContent) : snapshot.Introduction.Trim();
            return Truncate(string.IsNullOrWhiteSpace(intro)
                ? $"Public trading journal: {snapshot.Dashboard.CompletedTrades:N0} completed trades and {Money(snapshot.Dashboard.LifetimeNetPnl)} lifetime net trading profit after fees."
                : intro);
        }

        private void Navigation()
        {
            var lifetime = snapshot.Dashboard.LifetimeNetPnl;
            html.Append($"<nav class=\"site-nav\" aria-label=\"Journal sections\"><div class=\"site-nav-inner\"><a class=\"brand\" href=\"#overview\">{E(snapshot.Title)}</a><div class=\"nav-links\">");
            foreach (var (id, label) in new[] { ("overview", "Overview"), ("performance", "Performance"), ("calendar", "Calendar"), ("entries", "Journal") })
                html.Append($"<a href=\"#{id}\" data-section=\"{id}\">{label}</a>");
            html.Append($"</div><div class=\"nav-status\"><strong class=\"{Tone(lifetime)}\">{E(Money(lifetime))}</strong><small>Lifetime net · as of {ShortDate(snapshot.AsOfDate)}</small></div></div></nav>");
        }

        private void Header()
        {
            html.Append($"<header id=\"overview\"><div class=\"eyebrow\">PUBLIC TRADING JOURNAL{(preview ? " · PUBLICATION PREVIEW" : "")}</div><h1>{E(snapshot.Title)}</h1><div class=\"metadata\"><span>{E(snapshot.Currency)} · {E(snapshot.TimeZone)}</span><span>{(preview ? "Snapshot prepared" : "Last published")} {(publishedUtc ?? snapshot.GeneratedUtc).ToString("MMM d, yyyy 'at' HH:mm 'UTC'", Inv)}</span></div>");
            if (snapshot.IntroductionContent.Count > 0)
                html.Append($"<div class=\"introduction journal-content\">{PublicationContent.Render(snapshot.IntroductionContent, imageUrl)}</div>");
            else if (!string.IsNullOrWhiteSpace(snapshot.Introduction))
                html.Append($"<p class=\"introduction\">{E(snapshot.Introduction)}</p>");
            html.Append("</header>");
        }

        private void Dashboard()
        {
            var dashboard = snapshot.Dashboard;
            var asOf = snapshot.AsOfDate;
            var monthDays = active.Where(d => d.Date.Year == asOf.Year && d.Date.Month == asOf.Month).ToArray();
            decimal running = 0;
            var lifetimeSeries = new[] { 0m }.Concat(snapshot.Equity.Select(p => p.CumulativePnl)).ToArray();
            var monthSeries = new[] { 0m }.Concat(monthDays.Select(d => running += d.NetPnl)).ToArray();

            html.Append("<section class=\"metrics\" aria-label=\"Key results\">");
            Hero("Account balance", Money(dashboard.Balance), "neutral", dashboard.BalanceNote, "");
            Hero("Lifetime net trading profit", Money(dashboard.LifetimeNetPnl), Tone(dashboard.LifetimeNetPnl), "After trading fees",
                Spark(lifetimeSeries, Tone(dashboard.LifetimeNetPnl)));
            Hero($"{asOf.ToString("MMMM yyyy", Inv)} net profit", Money(dashboard.CurrentMonthNetPnl), Tone(dashboard.CurrentMonthNetPnl),
                $"{TradeCount(monthDays.Sum(d => d.Trades.Count))} · as of {Iso(asOf)}", Spark(monthSeries, Tone(dashboard.CurrentMonthNetPnl)));
            html.Append("</section><dl class=\"stats\" aria-label=\"Trading statistics\">");
            if (active.Length > 0) Stat("Trading since", ShortDate(active[0].Date), $"{active.Length:N0} trading days with completed trades");
            Stat("Completed trades", dashboard.CompletedTrades.ToString("N0", Inv));
            if (dashboard.WinRate is { } rate) Stat("Win rate", $"{rate.ToString("0.0", Inv)}%");
            if (dashboard.ProfitFactor is { } factor) Stat("Profit factor", factor.ToString("0.00", Inv), "Winning ÷ losing net P&L");
            else if (dashboard.CompletedTrades > 0) Stat("Profit factor", "No losses", "Needs a realized loss");
            if (dashboard.Alpha is { } alpha) Stat("Alpha", alpha, dashboard.AnnualizedAlpha is { } annualized ? $"Annualized {annualized}" : null);
            html.Append("</dl>");

            void Hero(string label, string value, string tone, string note, string spark) =>
                html.Append($"<article class=\"metric\"><span>{E(label)}</span><strong class=\"{tone}\">{E(value)}</strong><small>{E(note)}</small>{spark}</article>");
            void Stat(string label, string value, string? note = null) =>
                html.Append($"<div class=\"stat\"><dt>{E(label)}</dt><dd>{E(value)}{(note is null ? "" : $"<small>{E(note)}</small>")}</dd></div>");
        }

        private static string Spark(IReadOnlyList<decimal> values, string tone)
        {
            if (values.Count < 2) return "";
            var min = values.Min();
            var range = values.Max() - min is var r && r == 0 ? 1 : r;
            var points = string.Join(" ", values.Select((v, i) => $"{N(i * 120m / (values.Count - 1))},{N(33 - (v - min) * 30 / range)}"));
            return $"<svg class=\"spark {tone}\" viewBox=\"0 0 120 36\" preserveAspectRatio=\"none\" aria-hidden=\"true\"><polyline points=\"{points}\"/></svg>";
        }

        private void EquityCurve()
        {
            html.Append("<div class=\"panel curve\"><div class=\"eyebrow\">EQUITY CURVE</div><h3 id=\"curve-title\">Cumulative realized net P&amp;L</h3><p class=\"muted\">Daily closing results after fees, spaced by calendar date. Deposits and withdrawals are excluded.</p>");
            if (snapshot.Equity.Count == 0) { html.Append("<p class=\"empty\">No completed trades have been published.</p></div>"); return; }

            var points = new List<(DateOnly Date, decimal Value, decimal Drawdown, bool Origin)> { (snapshot.Equity[0].Date.AddDays(-1), 0, 0, true) };
            decimal peak = 0;
            foreach (var point in snapshot.Equity)
            {
                peak = Math.Max(peak, point.CumulativePnl);
                points.Add((point.Date, point.CumulativePnl, point.CumulativePnl - peak, false));
            }
            var start = points[0].Date.DayNumber;
            var span = Math.Max(1, points[^1].Date.DayNumber - start);
            decimal X(DateOnly date) => 85m + (date.DayNumber - start) * 890m / span;
            var min = Math.Min(0, points.Min(p => p.Value));
            var max = Math.Max(0, points.Max(p => p.Value));
            var range = max == min ? 1 : max - min;
            decimal Y(decimal value) => 255m - (value - min) * 215m / range;
            var deepest = points.Min(p => p.Drawdown);
            decimal Yd(decimal value) => deepest == 0 ? 12m : 12m + value / deepest * 108m;
            var ticks = Ticks(points[0].Date, points[^1].Date, X);

            var data = JsonSerializer.Serialize(points.Select(p => new object[]
            {
                Math.Round(X(p.Date), 2), Math.Round(Y(p.Value), 2), Math.Round(Yd(p.Drawdown), 2),
                p.Origin ? "Start" : ShortDate(p.Date), Money(p.Value), Money(p.Drawdown)
            }));
            var last = points[^1];
            html.Append($"<svg class=\"equity\" viewBox=\"0 0 1000 300\" role=\"img\" tabindex=\"0\" aria-labelledby=\"equity-title\" aria-describedby=\"equity-readout\" data-equity-points=\"{E(data)}\"><title id=\"equity-title\">Cumulative realized net P&amp;L in {E(snapshot.Currency)}, ending at {E(Money(last.Value))}. Use the arrow keys to step through days.</title>");
            for (var tick = 0; tick < 4; tick++)
            {
                var value = min + range * tick / 3;
                html.Append($"<line class=\"gridline\" x1=\"85\" x2=\"975\" y1=\"{N(Y(value))}\" y2=\"{N(Y(value))}\"/><text x=\"77\" y=\"{N(Y(value) + 4)}\" text-anchor=\"end\">{E(value.ToString("N0", Inv))}</text>");
            }
            if (ticks.Count >= 2)
                foreach (var (x, label) in ticks)
                    html.Append($"<line class=\"tick\" x1=\"{N(x)}\" x2=\"{N(x)}\" y1=\"255\" y2=\"262\"/><text x=\"{N(Math.Clamp(x, 110, 950))}\" y=\"283\" text-anchor=\"middle\">{E(label)}</text>");
            else
                html.Append($"<text x=\"85\" y=\"283\">{Iso(snapshot.Equity[0].Date)}</text><text x=\"975\" y=\"283\" text-anchor=\"end\">{Iso(last.Date)}</text>");
            if (min < 0) html.Append($"<line class=\"zero-line\" x1=\"85\" x2=\"975\" y1=\"{N(Y(0))}\" y2=\"{N(Y(0))}\"/>");
            html.Append($"<polyline class=\"equity-line\" points=\"{string.Join(" ", points.Select(p => $"{N(X(p.Date))},{N(Y(p.Value))}"))}\"/>");
            html.Append("<g class=\"hover-marker\" aria-hidden=\"true\"><line class=\"hover-line\" x1=\"0\" x2=\"0\" y1=\"40\" y2=\"255\"/><circle class=\"hover-dot\" r=\"5\" cx=\"0\" cy=\"0\"/></g></svg>");
            html.Append($"<p class=\"chart-readout\" id=\"equity-readout\" data-equity-readout aria-live=\"polite\">Latest {ShortDate(last.Date)} · Cumulative {E(Money(last.Value))} · Drawdown {E(Money(last.Drawdown))}</p>");

            html.Append($"<h4 class=\"subhead\">Drawdown from peak</h4><p class=\"muted\">Maximum {E(Money(deepest))} · current {E(Money(last.Drawdown))}. Distance below the previous cumulative high.</p>");
            html.Append($"<svg class=\"drawdown\" viewBox=\"0 0 1000 135\" role=\"img\" aria-label=\"Drawdown from peak, maximum {E(Money(deepest))}\" data-drawdown-chart>");
            foreach (var (x, _) in ticks) html.Append($"<line class=\"gridline\" x1=\"{N(x)}\" x2=\"{N(x)}\" y1=\"12\" y2=\"120\"/>");
            html.Append($"<line class=\"zero-line\" x1=\"85\" x2=\"975\" y1=\"12\" y2=\"12\"/><text x=\"77\" y=\"16\" text-anchor=\"end\">0</text>");
            if (deepest < 0)
                html.Append($"<text x=\"77\" y=\"124\" text-anchor=\"end\">{E(deepest.ToString("N0", Inv))}</text><path class=\"drawdown-area\" d=\"M85,12 {string.Join(" ", points.Select(p => $"L{N(X(p.Date))},{N(Yd(p.Drawdown))}"))} L{N(X(last.Date))},12 Z\"/>");
            html.Append("<g class=\"hover-marker\" aria-hidden=\"true\"><line class=\"hover-line\" x1=\"0\" x2=\"0\" y1=\"12\" y2=\"120\"/><circle class=\"hover-dot\" r=\"4\" cx=\"0\" cy=\"0\"/></g></svg></div>");
        }

        private static List<(decimal X, string Label)> Ticks(DateOnly start, DateOnly end, Func<DateOnly, decimal> x)
        {
            var monthsSpanned = (end.Year - start.Year) * 12 + end.Month - start.Month;
            var step = monthsSpanned <= 12 ? 1 : monthsSpanned <= 36 ? 3 : monthsSpanned <= 72 ? 6 : 12;
            var month = new DateOnly(start.Year, start.Month, 1);
            if (month < start) month = month.AddMonths(1);
            while ((month.Month - 1) % step != 0) month = month.AddMonths(1);
            var ticks = new List<(decimal, string)>();
            for (var first = true; month <= end; month = month.AddMonths(step), first = false)
                ticks.Add((x(month), step == 12 ? month.ToString("yyyy", Inv) : first || month.Month == 1 ? month.ToString("MMM yyyy", Inv) : month.ToString("MMM", Inv)));
            return ticks;
        }

        private void TradeResults()
        {
            html.Append("<div class=\"panel curve trade-results\"><h3 id=\"results-title\">Trade P&amp;L</h3><p class=\"muted\">One bar per completed trade, after fees, ordered by trading date. Select a bar to open that day.</p>");
            var results = active.SelectMany(d => d.Trades.Select(t => (d.Date, Trade: t))).ToArray();
            if (results.Length == 0) { html.Append("<p class=\"empty\">No completed trades have been published.</p></div>"); return; }
            var scale = Math.Max(1m, results.Max(r => Math.Abs(r.Trade.NetPnl)));
            html.Append("<div class=\"pnl-bars\" aria-labelledby=\"results-title\">");
            for (var i = 0; i < results.Length; i++)
            {
                var (date, trade) = results[i];
                // Geometry attributes remain effective when CSP blocks inline styles.
                var magnitude = trade.NetPnl == 0 ? 0m : Math.Max(1m, Math.Abs(trade.NetPnl) / scale * 100);
                var y = trade.NetPnl >= 0 ? 100m - magnitude : 100m;
                var label = $"Trade {i + 1} · {Iso(date)} · {trade.Symbol} · {Money(trade.NetPnl)}";
                html.Append($"<a class=\"pnl-column{(i >= results.Length / 2 ? " end" : "")}\" href=\"{E(DateLink(date))}\" aria-label=\"{E(label)}\"><svg class=\"pnl-bar\" viewBox=\"0 0 10 200\" preserveAspectRatio=\"none\" aria-hidden=\"true\"><line class=\"pnl-zero\" x1=\"0\" x2=\"10\" y1=\"100\" y2=\"100\"/><rect class=\"{Tone(trade.NetPnl)}\" x=\"1\" y=\"{N(y)}\" width=\"8\" height=\"{N(magnitude)}\"/></svg><span class=\"pnl-tooltip\" aria-hidden=\"true\">{E(label)}</span></a>");
            }
            html.Append($"</div><div class=\"axis-labels muted\"><span>{ShortDate(results[0].Date)}</span><span>{ShortDate(results[^1].Date)}</span></div></div>");
        }

        private void Calendar()
        {
            html.Append("<section id=\"calendar\" class=\"calendar-section\" aria-labelledby=\"calendar-title\"><div class=\"eyebrow\">DAY BY DAY</div><h2 id=\"calendar-title\">Monthly performance</h2>");
            if (months.Length == 0) { html.Append("<p class=\"empty panel\">No activity has been published yet.</p></section>"); return; }
            Heatmap();
            var current = selected is { } date ? Array.FindIndex(months, m => m.Month.Year == date.Year && m.Month.Month == date.Month) : 0;
            if (current < 0) current = 0;
            html.Append("<div class=\"months\">");
            for (var index = 0; index < months.Length; index++) Month(index, index == current);
            html.Append("</div></section>");
        }

        private void Heatmap()
        {
            var scale = heatScale;
            var withData = months.Select(m => m.Month).ToHashSet();
            const int pitch = 15, cell = 12, left = 30, top = 20;
            html.Append("<div class=\"panel heatmap-panel\"><div class=\"heatmap-legend\" aria-hidden=\"true\"><span>Loss</span>");
            for (var level = 4; level >= 1; level--) html.Append($"<i class=\"swatch negative l{level}\"></i>");
            html.Append("<i class=\"swatch\"></i>");
            for (var level = 1; level <= 4; level++) html.Append($"<i class=\"swatch positive l{level}\"></i>");
            html.Append("<span>Gain</span><i class=\"swatch note\"></i><span>Notes only</span></div>");
            foreach (var year in snapshot.Days.Select(d => d.Date.Year).Distinct().OrderDescending())
            {
                var days = snapshot.Days.Where(d => d.Date.Year == year).ToArray();
                var total = days.Sum(d => d.NetPnl);
                var january = new DateOnly(year, 1, 1);
                var offset = (int)january.DayOfWeek;
                int Week(DateOnly date) => (date.DayOfYear - 1 + offset) / 7;
                var width = left + (Week(new DateOnly(year, 12, 31)) + 1) * pitch;
                var height = top + 7 * pitch;
                html.Append($"<div class=\"heatmap-year\"><h3>{year} <span class=\"{Tone(total)}\">{E(Money(total))}</span><small>{TradeCount(days.Sum(d => d.Trades.Count))}</small></h3><div class=\"heatmap-scroll\"><svg class=\"heatmap\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" role=\"group\" aria-label=\"{year} daily results\">");
                for (var number = 1; number <= 12; number++)
                {
                    var month = new DateOnly(year, number, 1);
                    var x = left + Week(month) * pitch;
                    var name = month.ToString("MMM", Inv);
                    html.Append(withData.Contains(month)
                        ? $"<a href=\"#{MonthId(month)}\" aria-label=\"Open {month.ToString("MMMM yyyy", Inv)}\"><text class=\"heatmap-month\" x=\"{x}\" y=\"12\">{name}</text></a>"
                        : $"<text x=\"{x}\" y=\"12\">{name}</text>");
                }
                foreach (var (row, name) in new[] { (1, "Mon"), (3, "Wed"), (5, "Fri") })
                    html.Append($"<text x=\"0\" y=\"{top + row * pitch + 10}\">{name}</text>");
                for (var date = january; date.Year == year; date = date.AddDays(1))
                {
                    var x = left + Week(date) * pitch;
                    var y = top + (int)date.DayOfWeek * pitch;
                    if (!lookup.TryGetValue(date, out var day)) { html.Append($"<rect class=\"hm\" x=\"{x}\" y=\"{y}\" width=\"{cell}\" height=\"{cell}\" rx=\"2\"/>"); continue; }
                    var label = $"{ShortDate(date)}: {(day.Trades.Count > 0 ? $"{Money(day.NetPnl)}, {TradeCount(day.Trades.Count)}" : "journal notes")}";
                    html.Append($"<a href=\"{E(DateLink(date))}\" aria-label=\"{E(label)}\"><rect class=\"hm {DayClass(day, scale)}\" x=\"{x}\" y=\"{y}\" width=\"{cell}\" height=\"{cell}\" rx=\"2\"><title>{E(label)}</title></rect></a>");
                }
                html.Append("</svg></div></div>");
            }
            html.Append("</div>");
        }

        // Shade by magnitude against the 90th-percentile day so a single outlier does not wash out the rest.
        private static decimal HeatScale(IEnumerable<PublicationDay> days)
        {
            var magnitudes = days.Where(d => d.NetPnl != 0).Select(d => Math.Abs(d.NetPnl)).Order().ToArray();
            return magnitudes.Length == 0 ? 1m : magnitudes[(int)Math.Ceiling(magnitudes.Length * 0.9m) - 1];
        }

        private static string DayClass(PublicationDay day, decimal scale)
        {
            if (day.Trades.Count == 0) return "note";
            if (day.NetPnl == 0) return "neutral";
            var level = Math.Clamp((int)Math.Ceiling(Math.Abs(day.NetPnl) / scale * 4), 1, 4);
            return $"{Tone(day.NetPnl)} l{level}";
        }

        private void Month(int index, bool current)
        {
            var month = months[index];
            var id = MonthId(month.Month);
            var title = month.Month.ToString("MMMM yyyy", Inv);
            html.Append($"<article class=\"month panel{(current ? " current" : "")}\" id=\"{id}\" aria-labelledby=\"{id}-title\"><div class=\"month-header\">");
            Step(index + 1 < months.Length ? months[index + 1] : null, "‹", "Previous month");
            html.Append($"<h3 id=\"{id}-title\">{title}</h3>");
            Step(index > 0 ? months[index - 1] : null, "›", "Next month");
            html.Append($"<span class=\"month-total {Tone(month.NetPnl)}\">{E(Money(month.NetPnl))}<small>{TradeCount(month.TradeCount)}</small></span></div><div class=\"calendar\">");
            foreach (var name in new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" }) html.Append($"<div class=\"weekday\">{name}</div>");
            html.Append("<div class=\"weekday week-heading\">Week</div>");
            var offset = (int)month.Month.DayOfWeek;
            var length = DateTime.DaysInMonth(month.Month.Year, month.Month.Month);
            var cells = (offset + length + 6) / 7 * 7;
            decimal weekPnl = 0;
            var weekTrades = 0;
            for (var position = 0; position < cells; position++)
            {
                var number = position - offset + 1;
                if (number < 1 || number > length) html.Append("<div class=\"calendar-blank\" aria-hidden=\"true\"></div>");
                else
                {
                    var date = new DateOnly(month.Month.Year, month.Month.Month, number);
                    if (lookup.TryGetValue(date, out var day))
                    {
                        weekPnl += day.NetPnl;
                        weekTrades += day.Trades.Count;
                        html.Append($"<a class=\"calendar-day {DayClass(day, heatScale)}\" href=\"{E(DateLink(date))}\" aria-label=\"{Iso(date)}: {E(Money(day.NetPnl))}, {TradeCount(day.Trades.Count)}\"{(selected == date ? " aria-current=\"date\"" : "")}><span>{number}</span>{(day.Trades.Count > 0 ? $"<strong>{E(day.NetPnl.ToString("N2", Inv))}</strong><small>{TradeCount(day.Trades.Count)}</small>" : "<small>Journal</small>")}</a>");
                    }
                    else html.Append($"<div class=\"calendar-day quiet\"><span>{number}</span></div>");
                }
                if (position % 7 != 6) continue;
                html.Append(weekTrades > 0
                    ? $"<div class=\"calendar-week {Tone(weekPnl)}\"><span>Week</span><strong>{E(weekPnl.ToString("N2", Inv))}</strong><small>{TradeCount(weekTrades)}</small></div>"
                    : "<div class=\"calendar-week quiet\" aria-hidden=\"true\"></div>");
                weekPnl = 0;
                weekTrades = 0;
            }
            html.Append("</div></article>");

            void Step(PublicationMonth? target, string glyph, string label) => html.Append(target is null
                ? $"<span class=\"month-step disabled\" aria-hidden=\"true\">{glyph}</span>"
                : $"<a class=\"month-step\" href=\"#{MonthId(target.Month)}\" aria-label=\"{label}: {target.Month.ToString("MMMM yyyy", Inv)}\">{glyph}</a>");
        }

        private void Entries()
        {
            html.Append("<section id=\"entries\" aria-labelledby=\"entries-title\"><div class=\"eyebrow\">THE JOURNAL</div><h2 id=\"entries-title\">Daily notes &amp; trades</h2>");
            var ordered = all.Where(d => !before.HasValue || d.Date < before).ToArray();
            var skipped = all.Length - ordered.Length;
            PublicationDay[] displayed;
            if (selected is { } date)
            {
                displayed = all.Where(d => d.Date == date).ToArray();
                html.Append($"<nav class=\"breadcrumb\" aria-label=\"Breadcrumb\"><a href=\"{E(pageUrl)}#entries\">Journal</a><span aria-hidden=\"true\">›</span>");
                var month = new DateOnly(date.Year, date.Month, 1);
                html.Append(months.Any(m => m.Month == month) ? $"<a href=\"#{MonthId(month)}\">{month.ToString("MMMM yyyy", Inv)}</a>" : $"<span>{month.ToString("MMMM yyyy", Inv)}</span>");
                html.Append($"<span aria-hidden=\"true\">›</span><span aria-current=\"page\">{ShortDate(date)}</span></nav>");
                DayPager(date);
            }
            else
            {
                displayed = ordered.Take(preview ? int.MaxValue : PageSize).ToArray();
                if (!preview && displayed.Length > 0)
                    html.Append($"<p class=\"muted page-range\">Showing {ShortDate(displayed[^1].Date)} – {ShortDate(displayed[0].Date)} · entries {skipped + 1:N0}–{skipped + displayed.Length:N0} of {all.Length:N0}</p>");
            }
            foreach (var day in displayed) DayEntry(day);
            if (displayed.Length == 0) html.Append("<p class=\"panel empty\">There are no journal entries or completed trades to display.</p>");
            if (selected is { } shown) DayPager(shown);
            html.Append("<div class=\"pager\">");
            if (selected.HasValue || before.HasValue) html.Append($"<a class=\"button\" href=\"{E(pageUrl)}#entries\">Back to latest entries</a>");
            if (!preview && !selected.HasValue)
            {
                if (skipped > PageSize)
                {
                    var newer = skipped - PageSize;
                    html.Append($"<a class=\"button\" rel=\"prev\" href=\"{E(WithQuery(pageUrl, $"before={Iso(all[newer - 1].Date)}&snapshot={snapshot.SnapshotId:D}"))}#entries\">Newer entries</a>");
                }
                if (ordered.Length > PageSize)
                    html.Append($"<a class=\"button\" rel=\"next\" href=\"{E(WithQuery(pageUrl, $"before={Iso(displayed[^1].Date)}&snapshot={snapshot.SnapshotId:D}"))}#entries\">Load older entries</a>");
            }
            html.Append("</div></section>");
        }

        private void DayPager(DateOnly date)
        {
            var index = Array.FindIndex(all, d => d.Date == date);
            if (index < 0) return;
            html.Append("<nav class=\"day-pager\" aria-label=\"Adjacent days\">");
            html.Append(index + 1 < all.Length
                ? $"<a class=\"button\" rel=\"prev\" href=\"{E(DateLink(all[index + 1].Date))}\">← {ShortDate(all[index + 1].Date)}</a>"
                : "<span></span>");
            html.Append(index > 0
                ? $"<a class=\"button\" rel=\"next\" href=\"{E(DateLink(all[index - 1].Date))}\">{ShortDate(all[index - 1].Date)} →</a>"
                : "<span></span>");
            html.Append("</nav>");
        }

        private void DayEntry(PublicationDay day)
        {
            var iso = Iso(day.Date);
            html.Append($"<article class=\"panel day-entry\" id=\"day-{iso}\"><div class=\"day-heading\"><h3><a href=\"{E(DateLink(day.Date))}\">{LongDate(day.Date)}</a></h3><strong class=\"{Tone(day.NetPnl)}\">{E(Money(day.NetPnl))}</strong></div>");
            if (day.Content.Count > 0) html.Append($"<div class=\"journal-content\">{PublicationContent.Render(day.Content, imageUrl)}</div>");
            html.Append($"<div class=\"trade-summary\">{day.Trades.Count} completed {(day.Trades.Count == 1 ? "trade" : "trades")} · {day.Wins} {(day.Wins == 1 ? "win" : "wins")} · {day.Losses} {(day.Losses == 1 ? "loss" : "losses")} · {day.Breakevens} breakeven</div>");
            if (day.Trades.Count > 0)
            {
                html.Append("<div class=\"trade-accordion\" aria-label=\"Completed trades and journal entries\">");
                foreach (var trade in day.Trades)
                {
                    var chartButton = trade.Chart is { } chart
                        ? $"<button type=\"button\" class=\"chart-button\" data-public-chart=\"{E(JsonSerializer.Serialize(chart, PublicationJson.Options))}\" data-chart-title=\"{E($"{trade.Symbol} · {trade.Direction} · {iso}")}\" data-chart-timezone=\"{E(snapshot.TimeZone)}\" data-chart-direction=\"{E(trade.Direction)}\" data-chart-pnl=\"{trade.NetPnl.ToString(Inv)}\" aria-haspopup=\"dialog\" aria-controls=\"trade-chart-dialog\" aria-label=\"View chart for {E(trade.Symbol)} {E(trade.Direction)} trade on {iso}\">View chart</button>"
                        : "<span class=\"muted\">Chart unavailable</span>";
                    var hasJournal = trade.JournalContent.Count > 0;
                    html.Append($"<details class=\"published-trade\"{(preview ? " open" : "")}><summary><span class=\"trade-symbol\">{E(trade.Symbol)}</span><span>{E(trade.Direction)} · {trade.Quantity} qty</span><strong class=\"{Tone(trade.NetPnl)}\">{E(Money(trade.NetPnl))}</strong><span class=\"trade-journal-label\">{(hasJournal ? "Journal entry" : "No journal entry")}</span>{Timing(trade)}</summary><div class=\"trade-entry-body\">");
                    html.Append(SetupContent(trade.Setup, snapshot.SchemaVersion));
                    if (hasJournal) html.Append($"<div class=\"journal-content\">{PublicationContent.Render(trade.JournalContent, imageUrl)}</div>");
                    else html.Append("<p class=\"muted\">No trade journal entry.</p>");
                    html.Append($"<div class=\"trade-entry-chart\">{chartButton}</div></div></details>");
                }
                html.Append("</div>");
            }
            html.Append("</article>");
        }

        // Chart executions are anchored to the 5-minute bar holding each fill, so times are bar starts and the hold is approximate; prices are exact.
        private string Timing(PublicationTrade trade)
        {
            if (trade.Chart is not { } chart || chart.Exit.Time < chart.Entry.Time) return "";
            var entry = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(chart.Entry.Time), zone);
            var exit = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(chart.Exit.Time), zone);
            var minutes = (chart.Exit.Time - chart.Entry.Time) / 60;
            var hold = minutes < 5 ? "under 5m" : minutes < 60 ? $"~{minutes}m" : minutes < 1440 ? $"~{minutes / 60}h {minutes % 60:00}m" : $"~{minutes / 1440}d {minutes % 1440 / 60}h";
            var exitFormat = exit.Date == entry.Date ? "HH:mm" : "MMM d HH:mm";
            static string Price(decimal value) => value.ToString("#,0.00######", Inv);
            return $"<span class=\"trade-timing\" title=\"Times are the start of the 5-minute bar holding each execution ({E(snapshot.TimeZone)}); prices are exact.\">{entry.ToString("HH:mm", Inv)} @ {Price(chart.Entry.Price)} → {exit.ToString(exitFormat, Inv)} @ {Price(chart.Exit.Price)} · held {hold}</span>";
        }

        private void ChartDialog()
        {
            if (!all.Any(d => d.Trades.Any(t => t.Chart is not null))) return;
            html.Append("<dialog id=\"trade-chart-dialog\" class=\"trade-chart-dialog\" aria-labelledby=\"trade-chart-title\"><div class=\"chart-dialog-heading\"><div><h2 id=\"trade-chart-title\">Trade chart</h2><p class=\"muted\" data-chart-description></p></div><button type=\"button\" class=\"chart-button\" data-chart-close aria-label=\"Close trade chart\" autofocus>Close</button></div><div class=\"public-trade-chart\" data-public-chart-host role=\"img\" aria-label=\"Trade candlestick chart\"></div><div class=\"chart-dialog-toolbar\"><p data-chart-prices></p><button type=\"button\" class=\"chart-button\" data-chart-focus>Focus trade</button></div><p class=\"muted\">▶ Entry · ◀ Exit · Triangles mark the recorded trade prices.</p><p class=\"muted\">TradingView Lightweight Charts™ · <a href=\"https://www.tradingview.com/\" target=\"_blank\" rel=\"noopener noreferrer\">TradingView</a></p></dialog>");
            foreach (var script in new[] { "lightweight-charts.standalone.production.js", "trade-execution-drawing.js", "publication-charts.js" })
                html.Append($"<script defer src=\"/publication-assets/{script}\"></script>");
        }
    }

    private static TimeZoneInfo Zone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    private static string PlainText(IEnumerable<PublicationNode> nodes)
    {
        var text = new StringBuilder();
        void Walk(IEnumerable<PublicationNode> items)
        {
            foreach (var node in items)
            {
                if (node.Type == "text") text.Append(node.Text);
                else if (node.Type is "linebreak" or "tab") text.Append(' ');
                Walk(node.Children);
                if (node.Type is not ("text" or "link")) text.Append(' ');
            }
        }
        Walk(nodes);
        return Regex.Replace(text.ToString(), @"\s+", " ").Trim();
    }

    private static string Truncate(string value, int max = 200) => value.Length <= max ? value : value[..(max - 1)].TrimEnd() + "…";

    private static string SetupContent(PublicationTradeSetupInfo? info, int schemaVersion)
    {
        var html = new StringBuilder("<section class=\"public-trade-setups\" aria-label=\"Trade setups\"><h4>Setup</h4>");
        if (info is null)
            html.Append($"<p class=\"muted\">{(schemaVersion < 4 ? "Setup information was not included in this publication." : "Setup not recorded.")}</p>");
        else
        {
            if (!string.IsNullOrWhiteSpace(info.Label)) html.Append($"<p><span class=\"muted\">Setup label:</span> {E(info.Label)}</p>");
            foreach (var setup in info.Definitions)
            {
                html.Append($"<article class=\"public-setup\"><h5>{E(setup.Name)}</h5><p class=\"muted\">{E(setup.Role)} · Version {setup.Version}</p>");
                if (setup.Criteria.Count == 0) html.Append("<p class=\"muted\">No criteria recorded for this version.</p>");
                else
                {
                    html.Append("<ul class=\"public-setup-criteria\">");
                    foreach (var criterion in setup.Criteria)
                    {
                        var state = criterion.State switch { "Met" => "Met", "NotMet" => "Not met", "NotApplicable" => "N/A", _ => "Not evaluated" };
                        html.Append($"<li><div><strong>{E(criterion.Name)}</strong><small>{E(criterion.Type)} · {E(criterion.Stage)}</small></div><span class=\"setup-criterion-state\">{state}</span></li>");
                    }
                    html.Append("</ul>");
                }
                html.Append("</article>");
            }
        }
        return html.Append("</section>").ToString();
    }
}

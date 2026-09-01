using System.Globalization;
using System.Net;
using System.Text;
using TradeFoundry.Core;

namespace TradeFoundry.Services;

public static class ChartRenderer
{
    public static string Equity(IEnumerable<Trade> source)
    {
        var trades = source.Where(x => x.ExitUtc.HasValue).OrderBy(x => x.ExitUtc).ToArray();
        if (trades.Length == 0) return Empty("Import a completed trade to see the equity curve.");
        var values = new List<decimal> { 0m };
        var cumulative = 0m;
        foreach (var trade in trades)
        {
            cumulative += trade.NetPnl;
            values.Add(cumulative);
        }
        return Line(values, "cumulative net P&L", "#e6b566");
    }

    public static string Daily(IEnumerable<DailyPnl> daily)
    {
        var values = daily.OrderBy(x => x.Date).ToArray();
        if (values.Length == 0) return Empty("Daily bars will appear after the first closed trade.");
        var width = 720m;
        var height = 190m;
        var max = Math.Max(values.Max(x => Math.Abs(x.NetPnl)), 1m);
        var zeroY = height / 2m;
        var barWidth = Math.Max(4m, width / values.Length * .62m);
        var gap = width / values.Length;
        var svg = new StringBuilder($"<svg class=\"chart-svg\" viewBox=\"0 0 720 220\" role=\"img\" aria-label=\"Daily net P and L\"><line class=\"zero-line\" x1=\"0\" y1=\"{F(zeroY)}\" x2=\"720\" y2=\"{F(zeroY)}\" />");
        for (var i = 0; i < values.Length; i++)
        {
            var x = i * gap + (gap - barWidth) / 2m;
            var amount = values[i].NetPnl / max * (height / 2m - 12m);
            var y = amount >= 0 ? zeroY - amount : zeroY;
            var h = Math.Max(1m, Math.Abs(amount));
            var color = values[i].NetPnl >= 0 ? "#77c6a3" : "#e98278";
            svg.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(barWidth)}\" height=\"{F(h)}\" rx=\"3\" fill=\"{color}\"><title>{WebUtility.HtmlEncode(values[i].Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}: {values[i].NetPnl.ToString("C2", CultureInfo.CurrentCulture)}</title></rect>");
        }
        svg.Append("</svg>");
        return svg.ToString();
    }

    public static string Candles(Trade trade, IReadOnlyList<Bar> bars)
    {
        if (bars.Count == 0) return Empty("No matching OHLCV bars yet. Import a bar file for this symbol and time window.");
        var width = 760m;
        var height = 300m;
        var min = bars.Min(x => x.Low);
        var max = bars.Max(x => x.High);
        var range = Math.Max(max - min, .0000001m);
        var gap = width / bars.Count;
        var candleWidth = Math.Max(2m, gap * .58m);
        var svg = new StringBuilder($"<svg class=\"chart-svg candle-svg\" viewBox=\"0 0 760 330\" role=\"img\" aria-label=\"Candlestick chart for {WebUtility.HtmlEncode(trade.Symbol)}\">");
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var x = i * gap + gap / 2m;
            var yHigh = Y(bar.High, min, range, height);
            var yLow = Y(bar.Low, min, range, height);
            var yOpen = Y(bar.Open, min, range, height);
            var yClose = Y(bar.Close, min, range, height);
            var bodyY = Math.Min(yOpen, yClose);
            var bodyH = Math.Max(1m, Math.Abs(yOpen - yClose));
            var color = bar.Close >= bar.Open ? "#77c6a3" : "#e98278";
            svg.Append($"<line x1=\"{F(x)}\" y1=\"{F(yHigh)}\" x2=\"{F(x)}\" y2=\"{F(yLow)}\" stroke=\"{color}\" stroke-width=\"1.5\"/><rect x=\"{F(x - candleWidth / 2m)}\" y=\"{F(bodyY)}\" width=\"{F(candleWidth)}\" height=\"{F(bodyH)}\" fill=\"{color}\" rx=\"1\"><title>{bar.EventUtc.ToLocalTime():MMM d HH:mm} O {bar.Open} H {bar.High} L {bar.Low} C {bar.Close}</title></rect>");
        }
        AppendMarker(svg, bars, trade.EntryUtc, min, range, height, "entry", "#e6b566");
        if (trade.ExitUtc.HasValue) AppendMarker(svg, bars, trade.ExitUtc.Value, min, range, height, "exit", "#f4f1ea");
        svg.Append($"<text x=\"8\" y=\"14\" class=\"axis-label\">{max.ToString("0.########", CultureInfo.InvariantCulture)}</text><text x=\"8\" y=\"{F(height + 16)}\" class=\"axis-label\">{min.ToString("0.########", CultureInfo.InvariantCulture)}</text></svg>");
        return svg.ToString();
    }

    private static string Line(IReadOnlyList<decimal> values, string label, string color)
    {
        const decimal width = 720m;
        const decimal height = 190m;
        var min = Math.Min(0m, values.Min());
        var max = Math.Max(0m, values.Max());
        var range = Math.Max(max - min, .0000001m);
        var points = values.Select((value, i) => $"{F(values.Count == 1 ? 0 : i * width / (values.Count - 1))},{F((max - value) / range * (height - 24m) + 12m)}");
        var zeroY = (max - 0m) / range * (height - 24m) + 12m;
        return $"<svg class=\"chart-svg\" viewBox=\"0 0 720 220\" role=\"img\" aria-label=\"{WebUtility.HtmlEncode(label)}\"><line class=\"zero-line\" x1=\"0\" y1=\"{F(zeroY)}\" x2=\"720\" y2=\"{F(zeroY)}\"/><polyline points=\"{string.Join(" ", points)}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"3\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>";
    }

    private static string Empty(string message) => $"<div class=\"chart-empty\">{WebUtility.HtmlEncode(message)}</div>";
    private static decimal Y(decimal value, decimal min, decimal range, decimal height) => (min + range - value) / range * height + 20m;
    private static string F(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static void AppendMarker(StringBuilder svg, IReadOnlyList<Bar> bars, DateTimeOffset time, decimal min, decimal range, decimal height, string label, string color)
    {
        var index = bars.Select((bar, i) => (bar, i)).OrderBy(x => Math.Abs((x.bar.EventUtc - time).TotalSeconds)).First().i;
        var x = index * (760m / bars.Count) + (760m / bars.Count) / 2m;
        svg.Append($"<line x1=\"{F(x)}\" y1=\"18\" x2=\"{F(x)}\" y2=\"{F(height + 20m)}\" stroke=\"{color}\" stroke-width=\"1.5\" stroke-dasharray=\"4 4\"/><text x=\"{F(Math.Min(x + 4m, 700m))}\" y=\"30\" fill=\"{color}\" class=\"marker-label\">{label}</text>");
    }
}

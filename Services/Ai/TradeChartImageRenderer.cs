using System.Globalization;
using ImageMagick;
using ImageMagick.Drawing;
using TradeFoundry.Core;

namespace TradeFoundry.Services.Ai;

/// <summary>
/// Renders a trade's candle chart to PNG for AI review: the bars around the trade, entry and exit markers on the bars
/// that contain them, the entry-to-exit path, and the initial stop and target. Drawn with Magick.NET so it needs no browser.
/// </summary>
public static class TradeChartImageRenderer
{
    public const int Width = 1280;
    public const int Height = 720;
    private const double Left = 16, Right = 96, Top = 44, Bottom = 40;

    private static readonly MagickColor Background = new("#0d1117");
    private static readonly MagickColor GridColor = new("#30363d");
    private static readonly MagickColor Ink = new("#c9d1d9");
    private static readonly MagickColor Muted = new("#8b949e");
    private static readonly MagickColor Up = new("#3fb950");
    private static readonly MagickColor Down = new("#f85149");
    private static readonly MagickColor Gold = new("#d29922");
    private static readonly MagickColor Blue = new("#58a6ff");

    private static readonly Lazy<string?> FontPath = new(FindFont);

    public static byte[]? Render(Trade trade, IReadOnlyList<Bar> sourceBars, TimeZoneInfo zone, string intervalLabel, string title)
    {
        var bars = sourceBars.OrderBy(b => b.EventUtc).ToArray();
        if (bars.Length == 0) return null;

        var barDuration = bars.Length > 1
            ? bars.Zip(bars.Skip(1), (a, b) => b.EventUtc - a.EventUtc).Where(d => d > TimeSpan.Zero).DefaultIfEmpty(TimeSpan.FromMinutes(1)).Min()
            : TimeSpan.FromMinutes(1);
        var entryIndex = ContainingIndex(bars, trade.EntryUtc, barDuration);
        var exitIndex = trade.ExitUtc is { } exitUtc ? ContainingIndex(bars, exitUtc, barDuration) : entryIndex;
        var first = Math.Min(entryIndex, exitIndex);
        var tradeBars = Math.Abs(exitIndex - entryIndex) + 1;
        var visibleCount = Math.Min(bars.Length, Math.Max(60, tradeBars + 20));
        var start = Math.Clamp(first - (visibleCount - tradeBars) / 2, 0, bars.Length - visibleCount);
        var visible = bars.AsSpan(start, visibleCount).ToArray();
        entryIndex -= start;
        exitIndex -= start;

        var prices = visible.SelectMany(b => new[] { b.High, b.Low }).Append(trade.EntryPrice).ToList();
        if (trade.ExitPrice is { } exitPrice) prices.Add(exitPrice);
        if (trade.InitialStopPrice is { } stop) prices.Add(stop);
        if (trade.InitialTargetPrice is { } target) prices.Add(target);
        var low = (double)prices.Min();
        var high = (double)prices.Max();
        var pad = Math.Max((high - low) * 0.06, (double)Math.Max(trade.TickSize, 0.01m) * 4);
        low -= pad;
        high += pad;

        var plotWidth = Width - Left - Right;
        var plotHeight = Height - Top - Bottom;
        var slot = plotWidth / visible.Length;
        double X(int index) => Left + (index + 0.5) * slot;
        double Y(double price) => Top + (high - price) / (high - low) * plotHeight;

        using var image = new MagickImage(Background, Width, Height);
        var draw = new Drawables();

        // Grid and price axis.
        var step = NiceStep((high - low) / 6);
        var gridLabels = new List<(double Y, string Text)>();
        for (var price = Math.Ceiling(low / step) * step; price <= high; price += step)
        {
            var y = Y(price);
            draw.StrokeColor(GridColor).StrokeWidth(1).Line(Left, y, Width - Right, y);
            gridLabels.Add((y, price.ToString(step < 1 ? "0.00" : "0.##", CultureInfo.InvariantCulture)));
        }

        // Candles.
        var body = Math.Max(1, slot * 0.62);
        for (var i = 0; i < visible.Length; i++)
        {
            var bar = visible[i];
            var color = bar.Close >= bar.Open ? Up : Down;
            var x = X(i);
            draw.StrokeColor(color).StrokeWidth(1).Line(x, Y((double)bar.High), x, Y((double)bar.Low));
            var top = Y((double)Math.Max(bar.Open, bar.Close));
            var bottom = Math.Max(top + 1, Y((double)Math.Min(bar.Open, bar.Close)));
            draw.FillColor(color).StrokeColor(color).Rectangle(x - body / 2, top, x + body / 2, bottom);
        }

        // Trade overlays.
        var isLong = !trade.Direction.Equals("Short", StringComparison.OrdinalIgnoreCase);
        var entryX = X(entryIndex);
        var entryY = Y((double)trade.EntryPrice);
        var labels = new List<(double X, double Y, string Text, MagickColor Color)>();
        if (trade.InitialStopPrice is { } stopPrice)
        {
            draw.StrokeColor(Down).StrokeWidth(1.5).StrokeDashArray(8, 6).Line(entryX, Y((double)stopPrice), Width - Right, Y((double)stopPrice)).StrokeDashArray();
            labels.Add((entryX + 6, Y((double)stopPrice) - 4, $"Initial stop {stopPrice.ToString(CultureInfo.InvariantCulture)}", Down));
        }
        if (trade.InitialTargetPrice is { } targetPrice)
        {
            draw.StrokeColor(Up).StrokeWidth(1.5).StrokeDashArray(8, 6).Line(entryX, Y((double)targetPrice), Width - Right, Y((double)targetPrice)).StrokeDashArray();
            labels.Add((entryX + 6, Y((double)targetPrice) - 4, $"Initial target {targetPrice.ToString(CultureInfo.InvariantCulture)}", Up));
        }
        if (trade.ExitUtc is { } exitTime)
        {
            var exitX = X(exitIndex);
            var exitY = Y((double)(trade.ExitPrice ?? trade.EntryPrice));
            draw.StrokeColor(Blue).StrokeWidth(2).StrokeDashArray(4, 4).Line(entryX, entryY, exitX, exitY).StrokeDashArray();
            var exitColor = trade.NetPnl > 0 ? Up : trade.NetPnl < 0 ? Down : Gold;
            Triangle(draw, exitX + slot * 0.6, exitY, pointing: "left", exitColor);
            labels.Add((exitX + slot * 0.6 + 12, exitY + 4,
                $"Exit {(trade.ExitPrice ?? trade.EntryPrice).ToString(CultureInfo.InvariantCulture)} {Local(exitTime, zone)}", exitColor));
        }
        Triangle(draw, entryX - slot * 0.6, entryY, pointing: "right", isLong ? Up : Down);
        labels.Add((Math.Max(Left, entryX - slot * 0.6 - 230), entryY - 10,
            $"{(isLong ? "Buy" : "Sell")} {trade.Quantity} @ {trade.EntryPrice.ToString(CultureInfo.InvariantCulture)} {Local(trade.EntryUtc, zone)}", isLong ? Up : Down));

        draw.Draw(image);

        // Text is optional: a host without fonts still gets a usable chart.
        if (FontPath.Value is { } font)
        {
            try
            {
                var text = new Drawables().Font(font).FontPointSize(15).StrokeColor(MagickColors.Transparent);
                text.FillColor(Ink).Text(Left, 26, title);
                text.FontPointSize(12).FillColor(Muted).Text(Width - Right - 140, 26, $"{intervalLabel} bars · {zone.Id}");
                foreach (var (y, label) in gridLabels) text.FillColor(Muted).Text(Width - Right + 8, y + 4, label);
                var every = Math.Max(1, visible.Length / 8);
                for (var i = 0; i < visible.Length; i += every)
                    text.FillColor(Muted).Text(X(i) - 16, Height - Bottom + 22, TimeZoneInfo.ConvertTime(visible[i].EventUtc, zone).ToString("HH:mm", CultureInfo.InvariantCulture));
                text.FontPointSize(13);
                var backing = new MagickColor(Background.R, Background.G, Background.B, (byte)225);
                foreach (var (x, y, label, color) in labels)
                {
                    // Keep marker labels legible where they cross candles.
                    text.FillColor(backing).Rectangle(x - 3, y - 14, x + label.Length * 7.4 + 3, y + 5);
                    text.FillColor(color).Text(x, y, label);
                }
                text.Draw(image);
            }
            catch (MagickException)
            {
            }
        }

        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    private static void Triangle(Drawables draw, double x, double y, string pointing, MagickColor color)
    {
        const double size = 9;
        var points = pointing == "right"
            ? new[] { new PointD(x - size, y - size), new PointD(x + size * 0.6, y), new PointD(x - size, y + size) }
            : new[] { new PointD(x + size, y - size), new PointD(x - size * 0.6, y), new PointD(x + size, y + size) };
        draw.StrokeColor(MagickColors.White).StrokeWidth(1).FillColor(color).Polygon(points);
    }

    private static int ContainingIndex(Bar[] bars, DateTimeOffset time, TimeSpan duration)
    {
        for (var i = bars.Length - 1; i >= 0; i--)
            if (bars[i].EventUtc <= time) return time < bars[i].EventUtc + duration || i == bars.Length - 1 ? i : Math.Min(i + 1, bars.Length - 1);
        return 0;
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw)) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var normalized = raw / magnitude;
        return (normalized < 1.5 ? 1 : normalized < 3 ? 2 : normalized < 7 ? 5 : 10) * magnitude;
    }

    private static string Local(DateTimeOffset utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(utc, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string? FindFont()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"),
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/TTF/DejaVuSans.ttf",
            "/System/Library/Fonts/Supplemental/Arial.ttf",
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}

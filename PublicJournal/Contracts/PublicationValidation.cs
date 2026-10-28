using System.Globalization;

namespace TradeFoundry.Publishing;

public static class PublicationValidation
{
    public static void Validate(PublicationSnapshot snapshot)
    {
        if (snapshot.SchemaVersion is not (1 or 2)) throw new InvalidOperationException("Unsupported publication schema version. Update the public website before publishing.");
        if (snapshot.JournalId == Guid.Empty || snapshot.SnapshotId == Guid.Empty || snapshot.GeneratedUtc == default || snapshot.AsOfDate == default)
            throw new InvalidOperationException("Publication identity or date is missing.");
        if (string.IsNullOrWhiteSpace(snapshot.Title) || snapshot.Title.Length > 150 || snapshot.Introduction is null || snapshot.Introduction.Length > 5000
            || snapshot.Currency is null || snapshot.Currency.Length != 3 || !snapshot.Currency.All(char.IsAsciiLetter)
            || string.IsNullOrWhiteSpace(snapshot.TimeZone) || snapshot.TimeZone.Length > 100)
            throw new InvalidOperationException("Invalid public title, introduction, currency, or timezone.");
        if (snapshot.Days is null || snapshot.Months is null || snapshot.Equity is null || snapshot.Images is null || snapshot.Dashboard is null
            || snapshot.Days.Count > 100_000 || snapshot.Images.Count > 10_000)
            throw new InvalidOperationException("Publication collections are missing or too large.");
        if (snapshot.IntroductionContent is null) throw new InvalidOperationException("Introduction content is missing.");
        PublicationContent.Validate(snapshot.IntroductionContent, new HashSet<string>());
        var imageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in snapshot.Images)
        {
            if (image is null || !Guid.TryParseExact(image.Id, "N", out _) || !imageIds.Add(image.Id)
                || image.Content is null || image.Content.Length is <= 0 or > PublicationJson.MaxImageBytes || !ValidImage(image.ContentType, image.Content))
                throw new InvalidOperationException("Images must have unique IDs and valid PNG, JPEG, or WebP content, at most 10 MB each.");
        }
        var dates = new HashSet<DateOnly>();
        foreach (var day in snapshot.Days)
        {
            if (day is null || day.Date == default || !dates.Add(day.Date) || day.Trades is null || day.Content is null)
                throw new InvalidOperationException("Invalid or duplicate publication day.");
            if (day.Trades.Any(t => t is null || string.IsNullOrWhiteSpace(t.Symbol) || t.Symbol.Length > 100
                || t.Direction is null || t.Direction.Length > 30 || t.Quantity <= 0)
                || day.NetPnl != day.Trades.Sum(t => t.NetPnl)
                || day.Wins != day.Trades.Count(t => t.NetPnl > 0) || day.Losses != day.Trades.Count(t => t.NetPnl < 0)
                || day.Breakevens != day.Trades.Count(t => t.NetPnl == 0))
                throw new InvalidOperationException("Daily totals do not match the published trade summaries.");
            PublicationContent.Validate(day.Content, imageIds);
            foreach (var trade in day.Trades)
                if (trade.Chart is { } chart) ValidateChart(chart, snapshot.SchemaVersion);
        }
        var months = snapshot.Days.GroupBy(d => new DateOnly(d.Date.Year, d.Date.Month, 1))
            .Select(g => new PublicationMonth(g.Key, g.Sum(d => d.NetPnl), g.Sum(d => d.Trades.Count))).OrderBy(m => m.Month).ToArray();
        if (!months.SequenceEqual(snapshot.Months.OrderBy(m => m.Month))) throw new InvalidOperationException("Monthly totals do not match published days.");
        decimal cumulative = 0;
        var equity = snapshot.Days.Where(d => d.Trades.Count > 0).OrderBy(d => d.Date)
            .Select(d => new PublicationEquityPoint(d.Date, cumulative += d.NetPnl)).ToArray();
        if (!equity.SequenceEqual(snapshot.Equity)) throw new InvalidOperationException("Equity points do not match daily realized P&L.");
        var trades = snapshot.Days.SelectMany(d => d.Trades).ToArray();
        var profit = trades.Where(t => t.NetPnl > 0).Sum(t => t.NetPnl);
        var loss = -trades.Where(t => t.NetPnl < 0).Sum(t => t.NetPnl);
        var dashboard = snapshot.Dashboard;
        if (dashboard.LifetimeNetPnl != trades.Sum(t => t.NetPnl) || dashboard.CompletedTrades != trades.Length
            || dashboard.CurrentMonthNetPnl != snapshot.Days.Where(d => d.Date.Year == snapshot.AsOfDate.Year && d.Date.Month == snapshot.AsOfDate.Month).Sum(d => d.NetPnl)
            || dashboard.WinRate != (trades.Length == 0 ? (decimal?)null : (decimal)trades.Count(t => t.NetPnl > 0) / trades.Length * 100m)
            || dashboard.ProfitFactor != (loss == 0 ? (decimal?)null : profit / loss)
            || dashboard.BalanceNote is null || dashboard.BalanceNote.Length > 1000)
            throw new InvalidOperationException("Dashboard metrics do not match the published trading history.");
    }

    private static void ValidateChart(PublicationTradeChart chart, int version)
    {
        if (version < 2 || chart.Interval != "5m" || chart.Bars is null || chart.Bars.Count is < 1 or > 2000
            || chart.Entry is null || chart.Exit is null || chart.Entry.Time > chart.Exit.Time)
            throw new InvalidOperationException("Invalid published trade chart.");
        long previous = 0;
        foreach (var bar in chart.Bars)
        {
            if (bar is null || bar.Time <= previous || bar.Time > 253402300799 || bar.Volume < 0
                || bar.High < Math.Max(bar.Open, bar.Close) || bar.Low > Math.Min(bar.Open, bar.Close) || bar.Low > bar.High)
                throw new InvalidOperationException("Published candles must be ordered, unique and valid OHLCV bars.");
            previous = bar.Time;
        }
        if (!chart.Bars.Any(b => b.Time == chart.Entry.Time) || !chart.Bars.Any(b => b.Time == chart.Exit.Time))
            throw new InvalidOperationException("Published trade executions must map to published candles.");
    }

    private static bool ValidImage(string type, byte[] bytes) => type switch
    {
        "image/png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/jpeg" => bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }),
        "image/webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        _ => false
    };
}

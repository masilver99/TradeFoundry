using System.Globalization;
using System.Net;
using TradeFoundry.Core;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class DrawdownBaselineTests
{
    // Three days that open with a loss: the trough is 600 below the start, not 100 below the first close.
    private static readonly Trade[] Trades = [Trade(2, -500m), Trade(3, -100m), Trade(4, 900m)];

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(50000)]
    public void Drawdown_metrics_count_a_first_day_loss(int? startingEquity)
    {
        var metrics = TearSheetMetrics.Build(Trades, OrderExecutionCounts.None, [], [], startingEquity, "UTC", "USD")
            .RiskStability.ToDictionary(metric => metric.Label, metric => metric.Value);

        // The app formats money in the machine's culture, and CI's container has no "$".
        Assert.Equal($"-{600m.ToString("C2", CultureInfo.CurrentCulture)}", metrics["Max Drawdown"]);
        Assert.Equal("0.50", metrics["Recovery Factor"]);
        Assert.Equal("2d", metrics["Longest DD"]);
        Assert.Equal("50.0%", metrics["% Time at Highs"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(50000)]
    public void Drawdown_charts_count_a_first_day_loss(int? startingEquity)
    {
        var underwater = WebUtility.HtmlDecode(ChartRenderer.TearSheetDrawdown(Trades, [], startingEquity));
        var worst = WebUtility.HtmlDecode(ChartRenderer.TearSheetWorstDrawdownPeriods(Trades, [], startingEquity));
        var recovery = WebUtility.HtmlDecode(ChartRenderer.TearSheetDrawdownRecovery(Trades, [], startingEquity));

        Assert.Contains("\"y\":[0,-500,-600,0]", underwater);
        Assert.Contains("\"x0\":\"2026-03-02\"", worst);
        Assert.Contains("\"y\":[2]", recovery);
    }

    private static Trade Trade(int day, decimal netPnl) => new()
    {
        EntryUtc = new DateTimeOffset(2026, 3, day, 14, 0, 0, TimeSpan.Zero),
        ExitUtc = new DateTimeOffset(2026, 3, day, 15, 0, 0, TimeSpan.Zero),
        GrossPnl = netPnl,
        NetPnl = netPnl
    };
}

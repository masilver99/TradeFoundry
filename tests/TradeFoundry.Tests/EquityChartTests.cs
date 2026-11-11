using System.Net;
using System.Text.Json;
using TradeFoundry.Core;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class EquityChartTests
{
    [Fact]
    public void Trade_day_equity_chart_groups_trades_and_skips_empty_days()
    {
        var chart = ChartRenderer.Equity(new[]
        {
            new DailyPnl { Date = new DateOnly(2026, 1, 2), NetPnl = 2m, TradeCount = 2 },
            new DailyPnl { Date = new DateOnly(2026, 1, 1), NetPnl = -1m, TradeCount = 3 },
            new DailyPnl { Date = new DateOnly(2026, 1, 1), NetPnl = .5m, TradeCount = 1 },
            new DailyPnl { Date = new DateOnly(2026, 1, 3), NetPnl = 0m, TradeCount = 0 }
        });

        var payload = ReadPayload(chart);
        var root = payload.RootElement;
        var trace = root.GetProperty("data")[0];

        Assert.Equal("linear", root.GetProperty("layout").GetProperty("xaxis").GetProperty("type").GetString());
        Assert.Equal("trade day number", root.GetProperty("layout").GetProperty("xaxis").GetProperty("title").GetProperty("text").GetString());
        Assert.Equal("auto", root.GetProperty("layout").GetProperty("xaxis").GetProperty("tickmode").GetString());
        Assert.Equal(new[] { 0, 2 }, root.GetProperty("layout").GetProperty("xaxis").GetProperty("range").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, trace.GetProperty("x").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal(new[] { 0m, -.5m, 1.5m }, trace.GetProperty("y").EnumerateArray().Select(x => x.GetDecimal()).ToArray());
    }

    [Fact]
    public void Trade_equity_chart_uses_trade_numbers()
    {
        var chart = ChartRenderer.Equity(new[]
        {
            new EquityPoint { ExitUtc = new DateTimeOffset(2026, 1, 2, 14, 0, 0, TimeSpan.Zero), CumulativePnl = 2m },
            new EquityPoint { ExitUtc = new DateTimeOffset(2026, 1, 1, 14, 0, 0, TimeSpan.Zero), CumulativePnl = -1m }
        });

        using var payload = ReadPayload(chart);
        var root = payload.RootElement;
        var trace = root.GetProperty("data")[0];

        Assert.Equal("linear", root.GetProperty("layout").GetProperty("xaxis").GetProperty("type").GetString());
        Assert.Equal("trade number", root.GetProperty("layout").GetProperty("xaxis").GetProperty("title").GetProperty("text").GetString());
        Assert.Equal(new[] { 0, 2 }, root.GetProperty("layout").GetProperty("xaxis").GetProperty("range").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, trace.GetProperty("x").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal(new[] { 0m, -1m, 2m }, trace.GetProperty("y").EnumerateArray().Select(x => x.GetDecimal()).ToArray());
    }

    [Theory]
    [InlineData(5, "lines+markers")]
    [InlineData(200, "lines")]
    public void Equity_chart_drops_point_markers_on_long_histories(int tradeCount, string expectedMode)
    {
        var start = new DateTimeOffset(2026, 1, 1, 14, 0, 0, TimeSpan.Zero);
        var chart = ChartRenderer.Equity(Enumerable.Range(1, tradeCount).Select(index => new EquityPoint { ExitUtc = start.AddMinutes(index), CumulativePnl = index }));

        using var payload = ReadPayload(chart);
        var trace = payload.RootElement.GetProperty("data")[0];
        Assert.Equal(expectedMode, trace.GetProperty("mode").GetString());
        Assert.Equal("tozeroy", trace.GetProperty("fill").GetString());
    }

    private static JsonDocument ReadPayload(string chart)
    {
        const string attribute = "data-plotly-chart=\"";
        var start = chart.IndexOf(attribute, StringComparison.Ordinal) + attribute.Length;
        var end = chart.IndexOf('\"', start);
        return JsonDocument.Parse(WebUtility.HtmlDecode(chart[start..end]));
    }
}

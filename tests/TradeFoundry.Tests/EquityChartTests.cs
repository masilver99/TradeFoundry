using System.Net;
using System.Text.Json;
using TradeFoundry.Core;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class EquityChartTests
{
    [Fact]
    public void Daily_equity_chart_accumulates_one_point_per_day()
    {
        var chart = ChartRenderer.Equity(new[]
        {
            new DailyPnl { Date = new DateOnly(2026, 1, 2), NetPnl = 2m, TradeCount = 2 },
            new DailyPnl { Date = new DateOnly(2026, 1, 1), NetPnl = -1m, TradeCount = 3 },
            new DailyPnl { Date = new DateOnly(2026, 1, 1), NetPnl = .5m, TradeCount = 1 }
        });

        var payload = ReadPayload(chart);
        var trace = payload.RootElement.GetProperty("data")[0];

        Assert.Equal(new[] { "2025-12-31", "2026-01-01", "2026-01-02" }, trace.GetProperty("x").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(new[] { 0m, -.5m, 1.5m }, trace.GetProperty("y").EnumerateArray().Select(x => x.GetDecimal()).ToArray());
    }

    private static JsonDocument ReadPayload(string chart)
    {
        const string attribute = "data-plotly-chart=\"";
        var start = chart.IndexOf(attribute, StringComparison.Ordinal) + attribute.Length;
        var end = chart.IndexOf('\"', start);
        return JsonDocument.Parse(WebUtility.HtmlDecode(chart[start..end]));
    }
}

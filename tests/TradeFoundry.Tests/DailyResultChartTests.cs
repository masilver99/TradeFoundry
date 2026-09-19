using System.Net;
using System.Text.Json;
using TradeFoundry.Core;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class DailyResultChartTests
{
    [Fact]
    public void Daily_result_chart_hides_empty_days_and_calendar_gaps()
    {
        var chart = ChartRenderer.Daily(new[]
        {
            new DailyPnl { Date = new DateOnly(2026, 9, 8), NetPnl = 15m, TradeCount = 1 },
            new DailyPnl { Date = new DateOnly(2026, 9, 9), NetPnl = 0m, TradeCount = 0 },
            new DailyPnl { Date = new DateOnly(2026, 9, 10), NetPnl = 25m, TradeCount = 2 }
        });

        using var payload = ReadPayload(chart);
        var root = payload.RootElement;
        var trace = root.GetProperty("data")[0];

        Assert.Equal("category", root.GetProperty("layout").GetProperty("xaxis").GetProperty("type").GetString());
        Assert.Equal(new[] { "2026-09-08", "2026-09-10" }, trace.GetProperty("x").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    private static JsonDocument ReadPayload(string chart)
    {
        const string attribute = "data-plotly-chart=\"";
        var start = chart.IndexOf(attribute, StringComparison.Ordinal) + attribute.Length;
        var end = chart.IndexOf('\"', start);
        return JsonDocument.Parse(WebUtility.HtmlDecode(chart[start..end]));
    }
}

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

    [Fact]
    public void Daily_result_chart_labels_a_few_evenly_spaced_dates()
    {
        var start = new DateOnly(2026, 6, 1);
        var days = Enumerable.Range(0, 60).Select(offset => new DailyPnl { Date = start.AddDays(offset), NetPnl = offset % 3 == 0 ? -5m : 10m, TradeCount = 1 }).ToArray();

        using var payload = ReadPayload(ChartRenderer.Daily(days));
        var axis = payload.RootElement.GetProperty("layout").GetProperty("xaxis");
        var tickValues = axis.GetProperty("tickvals").EnumerateArray().Select(x => x.GetString()).ToArray();
        var tickText = axis.GetProperty("ticktext").EnumerateArray().Select(x => x.GetString()).ToArray();

        Assert.Equal("array", axis.GetProperty("tickmode").GetString());
        Assert.Equal(8, tickValues.Length);
        Assert.Equal("2026-06-01", tickValues[0]);
        Assert.Equal("2026-07-30", tickValues[^1]);
        Assert.Equal("Jun 1", tickText[0]);
        Assert.Equal(0, axis.GetProperty("tickangle").GetInt32());
    }

    [Fact]
    public void Daily_result_chart_labels_every_date_when_there_are_only_a_few()
    {
        var chart = ChartRenderer.Daily(new[]
        {
            new DailyPnl { Date = new DateOnly(2026, 9, 8), NetPnl = 15m, TradeCount = 1 },
            new DailyPnl { Date = new DateOnly(2026, 9, 10), NetPnl = -5m, TradeCount = 1 }
        });

        using var payload = ReadPayload(chart);
        var tickValues = payload.RootElement.GetProperty("layout").GetProperty("xaxis").GetProperty("tickvals").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "2026-09-08", "2026-09-10" }, tickValues);
    }

    private static JsonDocument ReadPayload(string chart)
    {
        const string attribute = "data-plotly-chart=\"";
        var start = chart.IndexOf(attribute, StringComparison.Ordinal) + attribute.Length;
        var end = chart.IndexOf('\"', start);
        return JsonDocument.Parse(WebUtility.HtmlDecode(chart[start..end]));
    }
}

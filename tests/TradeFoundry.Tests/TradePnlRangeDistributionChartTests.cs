using System.Net;
using System.Text.Json;
using TradeFoundry.Core;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class TradePnlRangeDistributionChartTests
{
    [Fact]
    public void Trade_pnl_range_distribution_uses_dollar_bins_with_losses_left_and_wins_right()
    {
        var chart = ChartRenderer.TearSheetTradePnlRangeDistribution(new[]
        {
            Trade(grossPnl: -1m, sequence: 1),
            Trade(grossPnl: -3m, sequence: 2),
            Trade(grossPnl: -9m, sequence: 3),
            Trade(grossPnl: 2m, sequence: 4),
            Trade(grossPnl: 6m, sequence: 5),
            Trade(grossPnl: 0m, sequence: 6)
        });

        using var payload = ReadPayload(chart);
        var root = payload.RootElement;
        var data = root.GetProperty("data");

        Assert.Equal(3, data.GetArrayLength());
        Assert.Equal("Losses", data[0].GetProperty("name").GetString());
        Assert.Equal(new[] { -.5m, -1.5m, -2.5m, -3.5m, -4.5m, -5.5m, -6.5m, -7.5m, -8.5m }, data[0].GetProperty("x").EnumerateArray().Select(x => x.GetDecimal()).ToArray());
        Assert.Equal(new[] { 1, 0, 1, 0, 0, 0, 0, 0, 1 }, data[0].GetProperty("y").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal("Wins", data[1].GetProperty("name").GetString());
        Assert.Equal(new[] { .5m, 1.5m, 2.5m, 3.5m, 4.5m, 5.5m, 6.5m, 7.5m, 8.5m }, data[1].GetProperty("x").EnumerateArray().Select(x => x.GetDecimal()).ToArray());
        Assert.Equal(new[] { 0, 1, 0, 0, 0, 1, 0, 0, 0 }, data[1].GetProperty("y").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal("Breakeven", data[2].GetProperty("name").GetString());
        Assert.Equal(new[] { 0 }, data[2].GetProperty("x").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal(new[] { 1 }, data[2].GetProperty("y").EnumerateArray().Select(x => x.GetInt32()).ToArray());

        var xAxis = root.GetProperty("layout").GetProperty("xaxis");
        Assert.Equal("gross P&L ($)", xAxis.GetProperty("title").GetProperty("text").GetString());
        Assert.Equal("$", xAxis.GetProperty("tickprefix").GetString());
        Assert.Equal(1m, xAxis.GetProperty("dtick").GetDecimal());
        Assert.Equal(new[] { -9m, 9m }, xAxis.GetProperty("range").EnumerateArray().Select(x => x.GetDecimal()).ToArray());
        var zero = root.GetProperty("layout").GetProperty("shapes")[0];
        Assert.Equal(0, zero.GetProperty("x0").GetInt32());
        Assert.Equal(0, zero.GetProperty("x1").GetInt32());
        Assert.Equal("trade count", root.GetProperty("layout").GetProperty("yaxis").GetProperty("title").GetProperty("text").GetString());
    }

    [Fact]
    public void Trade_pnl_range_distribution_has_a_clear_empty_state_without_completed_trades()
    {
        var chart = ChartRenderer.TearSheetTradePnlRangeDistribution(new[]
        {
            new Trade { GrossPnl = 12m, EntryUtc = DateTimeOffset.UnixEpoch }
        });

        Assert.Contains("Trade P&L ranges appear after the first completed trade.", WebUtility.HtmlDecode(chart), StringComparison.Ordinal);
        Assert.DoesNotContain("data-plotly-chart", chart, StringComparison.Ordinal);
    }

    private static JsonDocument ReadPayload(string chart)
    {
        const string attribute = "data-plotly-chart=\"";
        var start = chart.IndexOf(attribute, StringComparison.Ordinal) + attribute.Length;
        var end = chart.IndexOf('"', start);
        return JsonDocument.Parse(WebUtility.HtmlDecode(chart[start..end]));
    }

    private static Trade Trade(decimal grossPnl, int sequence) => new()
    {
        Id = Guid.NewGuid(),
        Sequence = sequence,
        Symbol = "MES",
        Instrument = "MES",
        Direction = "Long",
        EntryUtc = DateTimeOffset.UnixEpoch.AddMinutes(sequence),
        ExitUtc = DateTimeOffset.UnixEpoch.AddMinutes(sequence + 1),
        EntryPrice = 100m,
        ExitPrice = 101m,
        Quantity = 1,
        ClosedQuantity = 1,
        GrossPnl = grossPnl,
        NetPnl = grossPnl,
        Status = "closed"
    };
}

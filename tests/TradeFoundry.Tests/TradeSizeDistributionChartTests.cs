using System.Net;
using System.Text.Json;
using TradeFoundry.Core;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class TradeSizeDistributionChartTests
{
    [Fact]
    public void Trade_size_distribution_mirrors_losses_and_profitable_trades_around_zero()
    {
        var chart = ChartRenderer.TearSheetTradeSizeDistribution(new[]
        {
            Trade(quantity: 1, grossPnl: 10m, sequence: 1),
            Trade(quantity: 1, grossPnl: -5m, sequence: 2),
            Trade(quantity: 1, grossPnl: -4m, sequence: 3),
            Trade(quantity: 2, grossPnl: 8m, sequence: 4),
            Trade(quantity: 2, grossPnl: 0m, sequence: 5)
        });

        using var payload = ReadPayload(chart);
        var root = payload.RootElement;
        var data = root.GetProperty("data");

        Assert.Equal(3, data.GetArrayLength());
        Assert.Equal(new[] { -2, 0 }, data[0].GetProperty("x").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal(new[] { "1", "2" }, data[0].GetProperty("y").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal("Losses", data[0].GetProperty("name").GetString());
        Assert.Equal(new[] { 1, 1 }, data[1].GetProperty("x").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal("Profitable", data[1].GetProperty("name").GetString());
        Assert.Equal(new[] { 0 }, data[2].GetProperty("x").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal("Breakeven", data[2].GetProperty("name").GetString());

        var range = root.GetProperty("layout").GetProperty("xaxis").GetProperty("range");
        Assert.Equal(new[] { -3, 3 }, range.EnumerateArray().Select(x => x.GetInt32()).ToArray());
        var zero = root.GetProperty("layout").GetProperty("shapes")[0];
        Assert.Equal(0, zero.GetProperty("x0").GetInt32());
        Assert.Equal(0, zero.GetProperty("x1").GetInt32());
    }

    [Fact]
    public void Trade_size_distribution_has_a_clear_empty_state_without_completed_trades()
    {
        var chart = ChartRenderer.TearSheetTradeSizeDistribution(new[]
        {
            new Trade { Quantity = 3, GrossPnl = 12m, EntryUtc = DateTimeOffset.UtcNow }
        });

        Assert.Contains("Trade-size distribution appears after the first completed trade.", chart, StringComparison.Ordinal);
        Assert.DoesNotContain("data-plotly-chart", chart, StringComparison.Ordinal);
    }

    private static JsonDocument ReadPayload(string chart)
    {
        const string attribute = "data-plotly-chart=\"";
        var start = chart.IndexOf(attribute, StringComparison.Ordinal) + attribute.Length;
        var end = chart.IndexOf('\"', start);
        return JsonDocument.Parse(WebUtility.HtmlDecode(chart[start..end]));
    }

    private static Trade Trade(int quantity, decimal grossPnl, int sequence) => new()
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
        Quantity = quantity,
        ClosedQuantity = quantity,
        GrossPnl = grossPnl,
        NetPnl = grossPnl,
        Status = "closed"
    };
}

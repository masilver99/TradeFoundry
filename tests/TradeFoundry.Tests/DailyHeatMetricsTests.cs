using System.Net;
using System.Text.Json;
using TradeFoundry.Core;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class DailyHeatMetricsTests
{
    private static readonly DateTimeOffset Day = new(2026, 3, 2, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Worst_point_offsets_earlier_realized_gains_instead_of_summing_mae()
    {
        // +500, then a trade that sits 200 under water before closing +100: the day never goes below zero.
        var summary = DailyHeatMetrics.Build(new[]
        {
            Trade(500m, maePoints: 50m, entry: Day, minutes: 5),
            Trade(100m, maePoints: 200m, entry: Day.AddMinutes(10), minutes: 5)
        }, "UTC");

        var day = Assert.Single(summary.Days);
        Assert.Equal(600m, day.NetPnl);
        Assert.Equal(-50m, day.WorstPoint);
        Assert.Equal(250m, day.TotalHeat);
        Assert.Equal(650m, day.Recovery);
        Assert.Equal(600m / 250m, day.PnlPerHeat);
        Assert.Equal(12m, day.HeatFactor);
        Assert.False(day.HasOverlap);
        Assert.True(day.HasCompleteMae);
    }

    [Fact]
    public void Losing_day_low_reaches_the_deepest_mae_after_prior_losses()
    {
        var summary = DailyHeatMetrics.Build(new[]
        {
            Trade(-100m, maePoints: 120m, entry: Day, minutes: 5),
            Trade(-50m, maePoints: 300m, entry: Day.AddMinutes(10), minutes: 5)
        }, "UTC");

        var day = Assert.Single(summary.Days);
        Assert.Equal(-150m, day.NetPnl);
        Assert.Equal(-400m, day.WorstPoint);
        Assert.Equal(250m, day.Recovery);
        Assert.Equal(-150m / 400m, day.HeatFactor);
    }

    [Fact]
    public void Flags_overlap_and_partial_mae_and_skips_days_without_mae()
    {
        var summary = DailyHeatMetrics.Build(new[]
        {
            Trade(40m, maePoints: 10m, entry: Day, minutes: 30),
            Trade(-20m, maePoints: null, entry: Day.AddMinutes(5), minutes: 5),
            Trade(75m, maePoints: null, entry: Day.AddDays(1), minutes: 5)
        }, "UTC");

        var day = Assert.Single(summary.Days);
        Assert.True(day.HasOverlap);
        Assert.False(day.HasCompleteMae);
        Assert.Equal(1, day.MaeObservedTradeCount);
        Assert.Equal(20m, day.NetPnl);
        Assert.Equal(40m, day.ObservedNetPnl);
        Assert.Equal(-20m, day.WorstPoint);
        Assert.Equal(4m, day.PnlPerHeat);
        // Heat factor uses the whole day's net P&L, not only the MAE-observed trades.
        Assert.Equal(1m, day.HeatFactor);
    }

    [Fact]
    public void Heat_factor_is_null_when_the_day_never_goes_below_flat()
    {
        var summary = DailyHeatMetrics.Build(new[] { Trade(50m, maePoints: 0m, entry: Day, minutes: 5) }, "UTC");

        var day = Assert.Single(summary.Days);
        Assert.Equal(0m, day.WorstPoint);
        Assert.Null(day.HeatFactor);
        Assert.Null(summary.HeatFactor);
    }

    [Fact]
    public void Rolling_and_overall_heat_factor_divide_summed_pnl_by_summed_low_depth()
    {
        var trades = Enumerable.Range(0, 21)
            .Select(index => Trade(index == 0 ? 1000m : 10m, maePoints: index == 0 ? 1m : 20m, entry: Day.AddDays(index), minutes: 5))
            .ToArray();

        var summary = DailyHeatMetrics.Build(trades, "UTC");

        Assert.Equal((1000m + 19 * 10m) / (1m + 19 * 20m), summary.Rolling[0].HeatFactor!.Value, precision: 6);
        Assert.Equal(.5m, summary.Rolling[1].HeatFactor);
        Assert.Equal((1000m + 20 * 10m) / (1m + 20 * 20m), summary.HeatFactor!.Value, precision: 6);
    }

    [Fact]
    public void Rolling_ratio_divides_summed_pnl_by_summed_heat()
    {
        var trades = Enumerable.Range(0, 21)
            .Select(index => Trade(index == 0 ? 1000m : 10m, maePoints: index == 0 ? 1m : 10m, entry: Day.AddDays(index), minutes: 5))
            .ToArray();

        var summary = DailyHeatMetrics.Build(trades, "UTC");

        Assert.Equal(2, summary.Rolling.Count);
        Assert.Equal((1000m + 19 * 10m) / (1m + 19 * 10m), summary.Rolling[0].PnlPerHeat!.Value, precision: 6);
        Assert.Equal(1m, summary.Rolling[1].PnlPerHeat);
        Assert.Equal((1000m + 20 * 10m) / (1m + 20 * 10m), summary.PnlPerHeat!.Value, precision: 6);
    }

    [Fact]
    public void Range_chart_draws_wick_from_low_to_body_bottom()
    {
        var summary = DailyHeatMetrics.Build(new[]
        {
            Trade(100m, maePoints: 30m, entry: Day, minutes: 5),
            Trade(-60m, maePoints: 90m, entry: Day.AddDays(1), minutes: 5)
        }, "UTC");

        using var payload = ReadPayload(ChartRenderer.DailyHeatRange(summary));
        var wick = payload.RootElement.GetProperty("data")[0];
        var body = payload.RootElement.GetProperty("data")[1];

        Assert.Equal(new[] { -30m, -90m }, wick.GetProperty("base").EnumerateArray().Select(x => x.GetDecimal()).ToArray());
        Assert.Equal(new[] { 30m, 30m }, wick.GetProperty("y").EnumerateArray().Select(x => x.GetDecimal()).ToArray());
        Assert.Equal(new[] { 100m, -60m }, body.GetProperty("y").EnumerateArray().Select(x => x.GetDecimal()).ToArray());
    }

    [Fact]
    public void Charts_show_empty_state_without_mae()
    {
        var summary = DailyHeatMetrics.Build(new[] { Trade(10m, maePoints: null, entry: Day, minutes: 5) }, "UTC");

        Assert.Contains("chart-empty", ChartRenderer.DailyHeatRange(summary));
        Assert.Contains("chart-empty", ChartRenderer.DailyPnlPerHeat(summary));
        Assert.Contains("chart-empty", ChartRenderer.DailyHeatFactor(summary));
    }

    [Fact]
    public void Ratio_chart_clips_axis_around_low_heat_outliers()
    {
        var trades = Enumerable.Range(0, 25)
            .Select(index => Trade(index == 0 ? 500m : 10m, maePoints: index == 0 ? 1m : 10m, entry: Day.AddDays(index), minutes: 5))
            .ToArray();

        using var payload = ReadPayload(ChartRenderer.DailyPnlPerHeat(DailyHeatMetrics.Build(trades, "UTC")));
        var range = payload.RootElement.GetProperty("layout").GetProperty("yaxis").GetProperty("range")
            .EnumerateArray().Select(x => x.GetDecimal()).ToArray();

        Assert.True(range[1] < 500m);
        Assert.True(range[1] >= 1m);
    }

    [Fact]
    public void Heat_factor_chart_plots_daily_values()
    {
        var summary = DailyHeatMetrics.Build(new[]
        {
            Trade(100m, maePoints: 50m, entry: Day, minutes: 5),
            Trade(-60m, maePoints: 120m, entry: Day.AddDays(1), minutes: 5)
        }, "UTC");

        using var payload = ReadPayload(ChartRenderer.DailyHeatFactor(summary));
        var bars = payload.RootElement.GetProperty("data")[0];

        Assert.Equal(new[] { 2m, -.5m }, bars.GetProperty("y").EnumerateArray().Select(x => x.GetDecimal()).ToArray());
    }

    private static Trade Trade(decimal netPnl, decimal? maePoints, DateTimeOffset entry, int minutes) => new()
    {
        Id = Guid.NewGuid(),
        Sequence = (int)(entry - Day).TotalMinutes,
        Direction = "Long",
        EntryUtc = entry,
        ExitUtc = entry.AddMinutes(minutes),
        Quantity = 1,
        ClosedQuantity = 1,
        GrossPnl = netPnl,
        NetPnl = netPnl,
        MaePoints = maePoints,
        PointValue = 1m,
        TickSize = .25m,
        Status = "closed"
    };

    private static JsonDocument ReadPayload(string chart)
    {
        const string attribute = "data-plotly-chart=\"";
        var start = chart.IndexOf(attribute, StringComparison.Ordinal) + attribute.Length;
        var end = chart.IndexOf('\"', start);
        return JsonDocument.Parse(WebUtility.HtmlDecode(chart[start..end]));
    }
}

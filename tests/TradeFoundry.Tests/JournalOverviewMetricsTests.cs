using TradeFoundry.Core;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class JournalOverviewMetricsTests
{
    [Theory]
    [InlineData(new double[] { 1, 2, 3 }, 1d)]
    [InlineData(new double[] { 3, 2, 1 }, 1d)]
    [InlineData(new double[] { 1, 3, 2 }, .25d)]
    public void Equity_r_squared_measures_linear_fit_of_cumulative_pnl(double[] values, double expected)
    {
        var overview = WithEquity(values);

        Assert.NotNull(overview.EquityRSquared);
        Assert.Equal(expected, overview.EquityRSquared.Value, precision: 10);
    }

    [Theory]
    [InlineData(new double[] { })]
    [InlineData(new double[] { 1, 2 })]
    [InlineData(new double[] { 2, 2, 2 })]
    public void Equity_r_squared_is_unavailable_without_a_meaningful_fit(double[] values)
    {
        Assert.Null(WithEquity(values).EquityRSquared);
    }

    [Theory]
    [InlineData(new double[] { }, 0d)]
    [InlineData(new double[] { 10, 20, 30 }, 0d)]
    [InlineData(new double[] { 100, 40, 120, 70 }, 60d)]
    [InlineData(new double[] { -25, -10 }, 25d)]
    public void Max_drawdown_is_largest_peak_to_trough_decline_from_zero(double[] values, double expected)
    {
        Assert.Equal((decimal)expected, WithEquity(values).MaxDrawdown);
    }

    [Theory]
    [InlineData("America/New_York", "Eastern Time (New York)")]
    [InlineData("EST", "Eastern Time (New York)")]
    [InlineData("America/Argentina/Buenos_Aires", "Buenos Aires")]
    [InlineData(null, "UTC")]
    public void Time_zone_display_name_is_human_readable(string? timeZoneId, string expected)
    {
        Assert.Equal(expected, TimeZoneCatalog.DisplayName(timeZoneId));
    }

    private static JournalOverview WithEquity(double[] values) => new()
    {
        Equity = values.Select((value, index) => new EquityPoint
        {
            ExitUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(index),
            CumulativePnl = (decimal)value
        }).ToArray()
    };
}

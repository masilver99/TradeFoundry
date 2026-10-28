using TradeFoundry.Core;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class ScDayStreakTests
{
    [Fact]
    public void Aggregates_net_results_and_skips_breakeven_days_and_calendar_gaps()
    {
        var statistics = Build(
            Trade(1, 100m), Trade(1, -120m),
            Trade(2, 40m), Trade(3, 10m), Trade(3, -10m), Trade(6, 50m),
            Trade(7, -20m), Trade(8, 0m), Trade(9, -30m), Trade(10, 10m));

        AssertRows(statistics, ["2", "2", "—", "1"], ["2", "2", "—", "0"]);
        var titles = statistics.Sections.Select(section => section.Title).ToList();
        Assert.Equal("Day Streaks", titles[titles.IndexOf("Streaks") + 1]);
        Assert.Equal("Time in Trade", titles[titles.IndexOf("Day Streaks") + 1]);
    }

    [Fact]
    public void Computes_direction_subsets_independently_and_uses_latest_day_for_daily_column()
    {
        var statistics = Build(
            Trade(1, 20m), Trade(1, -30m, "Short"),
            Trade(2, 20m), Trade(2, -30m, "Short"),
            Trade(3, -10m), Trade(3, 20m, "Short"),
            Trade(4, 20m), Trade(4, -20m, "Short"));

        AssertRows(statistics, ["1", "2", "1", "0"], ["2", "1", "2", "0"]);
    }

    [Fact]
    public void Groups_by_localized_exit_date_and_ignores_open_trades()
    {
        var statistics = Build([
            Trade(1, 10m, exitUtc: DateTimeOffset.Parse("2026-10-02T02:00:00Z")),
            Trade(2, -20m, exitUtc: DateTimeOffset.Parse("2026-10-02T03:00:00Z")),
            Trade(3, -10m, exitUtc: DateTimeOffset.Parse("2026-10-02T16:00:00Z")),
            new Trade { NetPnl = 100m, Direction = "Long" }
        ], "America/New_York");

        AssertRows(statistics, ["0", "0", "—", "0"], ["2", "2", "—", "1"]);
    }

    [Fact]
    public void Empty_data_uses_existing_empty_indicators()
        => AssertRows(Build(), ["—", "—", "—", "—"], ["—", "—", "—", "—"]);

    [Theory]
    [InlineData(10, "2", "0", "1", "0")]
    [InlineData(-10, "0", "2", "0", "1")]
    [InlineData(0, "0", "0", "0", "0")]
    public void Handles_one_sided_and_all_breakeven_data(int pnl, string wins, string losses, string dailyWins, string dailyLosses)
        => AssertRows(Build(Trade(1, pnl), Trade(2, 0m), Trade(3, pnl)),
            [wins, wins, "—", dailyWins], [losses, losses, "—", dailyLosses]);

    private static TearSheetScStatistics Build(params Trade[] trades) => Build(trades, "UTC");

    private static TearSheetScStatistics Build(Trade[] trades, string timeZone)
        => TearSheetMetrics.Build(trades, [], [], [], 1000m, timeZone, "USD").ScTradeStatistics;

    private static void AssertRows(TearSheetScStatistics statistics, string[] wins, string[] losses)
    {
        var rows = Assert.Single(statistics.Sections, section => section.Title == "Day Streaks").Rows;
        Assert.Equal(2, rows.Count);
        Assert.Equal("Max winning days", rows[0].Label);
        Assert.Equal("Max losing days", rows[1].Label);
        Assert.Equal(wins, rows[0].Cells.Select(cell => cell.Value));
        Assert.Equal(losses, rows[1].Cells.Select(cell => cell.Value));
    }

    private static Trade Trade(int day, decimal netPnl, string direction = "Long", DateTimeOffset? exitUtc = null)
    {
        var exit = exitUtc ?? new DateTimeOffset(2026, 10, day, 16, 0, 0, TimeSpan.Zero);
        return new Trade
        {
            Id = Guid.NewGuid(), Sequence = day, Direction = direction,
            EntryUtc = exit.AddHours(-1), ExitUtc = exit, Quantity = 1, ClosedQuantity = 1,
            GrossPnl = netPnl + 5m, Fees = 5m, NetPnl = netPnl
        };
    }
}

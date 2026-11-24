using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class DayStatisticsTests
{
    private static readonly DateOnly Date = new(2026, 9, 10);
    private static readonly DateTimeOffset Open = new(2026, 9, 10, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Scorecard_sums_the_days_completed_trades()
    {
        var trades = new[]
        {
            Trade(Open, 10, 100m, fees: 4m, r: 2m),
            Trade(Open.AddMinutes(20), 10, -40m, fees: 4m, r: -1m),
            Trade(Open.AddMinutes(40), 10, 60m, fees: 4m)
        };

        var card = Build(trades).Scorecard;

        Assert.Equal(3, card.TradeCount);
        Assert.Equal(2, card.Wins);
        Assert.Equal(1, card.Losses);
        Assert.Equal(120m, card.NetPnl);
        Assert.Equal(132m, card.GrossPnl);
        Assert.Equal(12m, card.Fees);
        Assert.Equal(4m, card.ProfitFactor);
        Assert.Equal(80m, card.AverageWin);
        Assert.Equal(-40m, card.AverageLoss);
        Assert.Equal(100m, card.LargestWin);
        Assert.Equal(-40m, card.LargestLoss);
        Assert.Equal(1m, card.TotalR);
        Assert.Equal(2, card.RCoverage);
        Assert.Equal(TimeSpan.FromMinutes(30), card.TimeInMarket);
    }

    [Fact]
    public void Time_in_market_counts_overlapping_positions_once()
    {
        var card = Build(new[] { Trade(Open, 30, 10m), Trade(Open.AddMinutes(10), 30, 10m) }).Scorecard;

        Assert.Equal(TimeSpan.FromMinutes(40), card.TimeInMarket);
    }

    [Fact]
    public void Give_back_is_the_drop_from_the_days_high_to_the_close()
    {
        var report = Build(new[]
        {
            Trade(Open, 5, 300m),
            Trade(Open.AddMinutes(10), 5, -120m),
            Trade(Open.AddMinutes(20), 5, -60m)
        });

        Assert.Equal(300m, report.Peak);
        Assert.Equal(180m, report.GiveBack);
        Assert.Equal(new DateTime(2026, 9, 10, 13, 35, 0), report.PeakLocalTime);
        Assert.Equal(new[] { 0m, 300m, 180m, 120m }, report.Equity.Select(point => point.Cumulative));
        var afterPeak = Assert.Single(report.Flags, flag => flag.Key == "after-peak");
        Assert.Equal("warn", afterPeak.Tone);
    }

    [Fact]
    public void A_day_that_never_went_green_has_no_peak_or_give_back()
    {
        var report = Build(new[] { Trade(Open, 5, -50m), Trade(Open.AddMinutes(10), 5, 20m) });

        Assert.Equal(0m, report.Peak);
        Assert.Equal(0m, report.GiveBack);
        Assert.Null(report.PeakLocalTime);
        Assert.DoesNotContain(report.Flags, flag => flag.Key == "after-peak");
    }

    [Fact]
    public void Give_back_is_capped_at_the_high_when_the_day_closes_red()
    {
        var report = Build(new[] { Trade(Open, 5, 40m), Trade(Open.AddMinutes(10), 5, -400m) });

        Assert.Equal(40m, report.Peak);
        Assert.Equal(40m, report.GiveBack);
    }

    [Fact]
    public void Quick_re_entry_after_a_losing_exit_is_flagged()
    {
        var trades = new[]
        {
            Trade(Open, 5, -50m),
            Trade(Open.AddMinutes(7), 5, -30m),   // 2 minutes after a loss
            Trade(Open.AddMinutes(30), 5, 40m)    // 18 minutes after a loss
        };

        var flag = Assert.Single(Build(trades).Flags, flag => flag.Key == "revenge");

        Assert.Equal("warn", flag.Tone);
        Assert.StartsWith("1 quick re-entry", flag.Title);
    }

    [Fact]
    public void Re_entry_after_a_winner_is_not_flagged()
    {
        var flag = Assert.Single(Build(new[] { Trade(Open, 5, 50m), Trade(Open.AddMinutes(6), 5, -30m) }).Flags, flag => flag.Key == "revenge");

        Assert.Equal("ok", flag.Tone);
    }

    [Fact]
    public void Loss_limit_reports_trades_taken_after_it_was_hit()
    {
        var trades = new[]
        {
            Trade(Open, 5, -300m),
            Trade(Open.AddMinutes(10), 5, -250m),  // running −550, limit −500 hit at 13:45
            Trade(Open.AddMinutes(30), 5, 100m)
        };

        var report = Build(trades, limits: new DailyLimitSettings { MaxDailyLoss = 500m });
        var flag = Assert.Single(report.Flags, flag => flag.Key == "loss-limit");

        Assert.Equal("Max daily loss reached", flag.Title);
        Assert.Equal("warn", flag.Tone);
        Assert.Contains("13:45", flag.Detail);
        Assert.Contains("1 more trade", flag.Detail);
    }

    [Fact]
    public void Daily_goal_reports_whether_trading_continued()
    {
        var report = Build(new[] { Trade(Open, 5, 250m), Trade(Open.AddMinutes(10), 5, -80m) }, limits: new DailyLimitSettings { DailyProfitGoal = 200m });
        var flag = Assert.Single(report.Flags, flag => flag.Key == "goal");

        Assert.Equal("warn", flag.Tone);
        Assert.Contains("kept trading", flag.Detail);
    }

    [Fact]
    public void Limits_are_not_checked_until_they_are_set()
    {
        var report = Build(new[] { Trade(Open, 5, -900m) });

        Assert.DoesNotContain(report.Flags, flag => flag.Key is "loss-limit" or "goal");
    }

    [Fact]
    public void Comparisons_wait_for_enough_other_days()
    {
        var history = HistoryDays(3, day => new[] { Trade(DayOpen(day), 5, 10m) });

        var report = Build(new[] { Trade(Open, 5, 50m) }, history);

        Assert.Empty(report.Comparisons);
        Assert.Equal(3, report.ComparisonDayCount);
    }

    [Fact]
    public void Comparisons_rank_the_day_against_the_other_days()
    {
        // Ten other days, each with one trade worth 10, 20, … 100.
        var history = HistoryDays(10, day => new[] { Trade(DayOpen(day), 5, 10m * (day + 1)) });
        var trades = Enumerable.Range(0, 3).Select(index => Trade(Open.AddMinutes(10 * index), 5, 50m)).ToArray();

        var report = Build(trades, history);

        var net = Assert.Single(report.Comparisons, item => item.Key == "net");
        Assert.Equal("100th percentile", net.Value);
        Assert.Equal("good", net.Tone);
        var count = Assert.Single(report.Comparisons, item => item.Key == "trades");
        Assert.Equal("3.0× your median", count.Value);
        Assert.Equal("warn", count.Tone);
        var size = Assert.Single(report.Comparisons, item => item.Key == "size");
        Assert.Equal("Larger than 100%", size.Value);
    }

    [Fact]
    public void Comparisons_ignore_days_after_the_selected_day()
    {
        var earlier = HistoryDays(5, day => new[] { Trade(DayOpen(day), 5, 10m) });
        var later = Enumerable.Range(1, 5).Select(day => Summary(Date.AddDays(day), Trade(Open, 5, 1000m))).ToArray();

        var report = Build(new[] { Trade(Open, 5, 50m) }, earlier.Concat(later).ToArray());

        Assert.Equal(5, report.ComparisonDayCount);
        var net = Assert.Single(report.Comparisons, item => item.Key == "net");
        Assert.Equal("100th percentile", net.Value);
        Assert.Equal("of 5 earlier trading days", net.Detail);
    }

    [Fact]
    public void Percentile_counts_ties_as_half()
    {
        Assert.Equal(50m, DayStatistics.Percentile(5m, new[] { 1m, 5m, 5m, 9m }));
        Assert.Equal("1st", DayStatistics.Ordinal(1m));
        Assert.Equal("12th", DayStatistics.Ordinal(12m));
        Assert.Equal("23rd", DayStatistics.Ordinal(23m));
    }

    [Fact]
    public void Trade_numbers_average_each_position_in_the_day_and_cap_the_tail()
    {
        var history = HistoryDays(2, day => Enumerable.Range(0, 12).Select(index => Trade(DayOpen(day).AddMinutes(index * 5), 2, index == 0 ? 100m : -10m)).ToArray());
        var today = new[] { Trade(Open, 2, 40m), Trade(Open.AddMinutes(5), 2, -20m) };

        var buckets = Build(today, history).TradeNumbers;

        Assert.Equal(10, buckets.Count);
        var first = buckets[0];
        Assert.Equal("1", first.Label);
        Assert.Equal(3, first.TradeCount);
        Assert.Equal(80m, first.AveragePnl);   // (100 + 100 + 40) / 3
        Assert.Equal(100m, first.WinRate);
        Assert.Equal(40m, first.TodayPnl);
        Assert.Equal(-20m, buckets[1].TodayPnl);
        var tail = buckets[^1];
        Assert.Equal("10+", tail.Label);
        Assert.Equal(6, tail.TradeCount);       // trades 10, 11 and 12 on both history days
        Assert.Null(tail.TodayPnl);
    }

    [Fact]
    public void Green_day_stats_summarize_every_trading_day()
    {
        var nets = new[] { 100m, -50m, 200m, -150m, 50m };
        var history = HistoryDays(nets.Length, day => new[] { Trade(DayOpen(day), 5, nets[day]) });

        var green = Build(new[] { Trade(Open, 5, 30m) }, history).GreenDays;

        Assert.Equal(6, green.DayCount);
        Assert.Equal(4, green.GreenDays);
        Assert.Equal(2, green.RedDays);
        Assert.Equal(95m, green.AverageGreen);    // (100 + 200 + 50 + 30) / 4
        Assert.Equal(100m, green.AverageRed);
        Assert.Equal(-150m, green.WorstRed);
        Assert.Equal(2, green.StreakAtDate);      // the 50 day and this day
        Assert.Equal(new[] { 5, 6 }, green.Rolling.Select(point => point.WindowDayCount));
    }

    [Fact]
    public void Session_curve_averages_cumulative_pnl_by_exit_time()
    {
        // Another day: +100 exiting 13:40. This day: −40 exiting 13:35.
        var history = new[] { Summary(Date.AddDays(-1), Trade(new DateTimeOffset(2026, 9, 9, 13, 30, 0, TimeSpan.Zero), 10, 100m)) };

        var session = Build(new[] { Trade(Open, 5, -40m) }, history).Session;

        Assert.Equal(2, session.DayCount);
        Assert.Equal("13:15", session.Labels[0]);
        var at1345 = session.Labels.ToList().IndexOf("13:45");
        Assert.Equal(30m, session.Average[at1345]);     // (100 − 40) / 2
        Assert.Equal(-40m, session.Today[at1345]);
        Assert.Equal(0m, session.Average[0]);
    }

    [Fact]
    public void Heat_window_ends_at_the_selected_day()
    {
        var history = HistoryDays(40, day => new[] { Trade(DayOpen(day), 5, 10m, mae: 2m) });
        var selected = history[20].Date;
        var report = DayStatistics.Build(new DayStatisticsInput
        {
            Date = selected,
            DayTrades = history[20].CompletedTrades,
            History = history
        });

        Assert.Equal(DayStatistics.HeatWindowDays, report.HeatWindow.Count);
        Assert.Equal(history[0].Date, report.HeatWindow[0].Date);
        Assert.Contains(report.HeatWindow, day => day.Date == selected);
    }

    [Fact]
    public void Day_charts_render_and_mark_the_selected_day()
    {
        var history = HistoryDays(8, day => new[] { Trade(DayOpen(day), 5, day % 2 == 0 ? 50m : -20m, mae: 3m), Trade(DayOpen(day).AddMinutes(20), 5, 10m, mae: 1m) });
        var ratings = history.Take(4).Select((day, index) => new DailyRating { Date = day.Date, Rating = index + 1 })
            .Append(new DailyRating { Date = Date, Rating = 5 })
            .ToDictionary(rating => rating.Date);
        var report = DayStatistics.Build(new DayStatisticsInput
        {
            Date = Date,
            DayTrades = new[] { Trade(Open, 5, 70m, mae: 2m), Trade(Open.AddMinutes(10), 5, -20m, mae: 4m) },
            History = history,
            Ratings = ratings,
            Limits = new DailyLimitSettings { MaxDailyLoss = 200m },
            TimeZoneId = "UTC"
        });

        foreach (var chart in new[]
        {
            ChartRenderer.DayEquity(report), ChartRenderer.DayHeatWindow(report), ChartRenderer.DayTradeNumber(report),
            ChartRenderer.DaySessionCurve(report), ChartRenderer.DayGreenRate(report), ChartRenderer.DayTradeCountScatter(report),
            ChartRenderer.DayRatingScatter(report)
        })
        {
            Assert.Contains("data-plotly-chart", chart);
        }

        using var heat = ReadPayload(ChartRenderer.DayHeatWindow(report));
        var widths = heat.RootElement.GetProperty("data")[1].GetProperty("marker").GetProperty("line").GetProperty("width").EnumerateArray().Select(value => value.GetDouble()).ToArray();
        Assert.Equal(1, widths.Count(width => width > 0));
        Assert.Equal(2d, widths[^1]);

        using var scatter = ReadPayload(ChartRenderer.DayTradeCountScatter(report));
        Assert.Equal(2, scatter.RootElement.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public void Day_charts_explain_when_there_is_not_enough_data()
    {
        var report = Build(new[] { Trade(Open, 5, 10m) });

        Assert.Contains("chart-empty", ChartRenderer.DayRatingScatter(report));
        Assert.Contains("chart-empty", ChartRenderer.DayTradeCountScatter(report));
        Assert.Contains("chart-empty", ChartRenderer.DayGreenRate(report));
    }

    [Fact]
    public void Daily_limit_settings_round_trip_and_validate()
    {
        var root = Path.Combine(Path.GetTempPath(), "tf-daystats-" + Guid.NewGuid().ToString("N"));
        try
        {
            var database = new TradeFoundryDb(Options.Create(new StorageOptions { DataDirectory = root, DatabaseFileName = "journal.db" }));
            database.CreateOwner("Owner", PasswordService.Hash(""));
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");

            Assert.Null(database.GetDailyLimitSettings(journal.Id).MaxDailyLoss);
            Assert.Equal(DailyLimitSettings.DefaultRevengeWindowMinutes, database.GetDailyLimitSettings(journal.Id).RevengeWindowMinutes);

            database.SaveDailyLimitSettings(journal.Id, new DailyLimitSettings { MaxDailyLoss = 500.5m, DailyProfitGoal = 750m, RevengeWindowMinutes = 3 });
            var saved = database.GetDailyLimitSettings(journal.Id);
            Assert.Equal(500.5m, saved.MaxDailyLoss);
            Assert.Equal(750m, saved.DailyProfitGoal);
            Assert.Equal(3, saved.RevengeWindowMinutes);

            database.SaveDailyLimitSettings(journal.Id, new DailyLimitSettings { RevengeWindowMinutes = 10 });
            Assert.Null(database.GetDailyLimitSettings(journal.Id).MaxDailyLoss);

            Assert.Throws<ArgumentOutOfRangeException>(() => database.SaveDailyLimitSettings(journal.Id, new DailyLimitSettings { MaxDailyLoss = -5m }));
            Assert.Throws<ArgumentOutOfRangeException>(() => database.SaveDailyLimitSettings(journal.Id, new DailyLimitSettings { RevengeWindowMinutes = 0 }));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static DayStatisticsReport Build(Trade[] trades, IReadOnlyList<DailyTradeSummary>? history = null, DailyLimitSettings? limits = null)
        => DayStatistics.Build(new DayStatisticsInput
        {
            Date = Date,
            DayTrades = trades,
            History = (history ?? Array.Empty<DailyTradeSummary>()).Append(Summary(Date, trades)).ToArray(),
            Limits = limits ?? new DailyLimitSettings(),
            TimeZoneId = "UTC"
        });

    private static DailyTradeSummary[] HistoryDays(int count, Func<int, Trade[]> trades)
        => Enumerable.Range(0, count).Select(day => Summary(Date.AddDays(day - count), trades(day))).ToArray();

    /// <summary>The session open; <see cref="Summary"/> moves each trade onto its day.</summary>
    private static DateTimeOffset DayOpen(int day) => Open;

    private static DailyTradeSummary Summary(DateOnly date, params Trade[] trades)
    {
        // Rebase each trade onto the summary's date, keeping its time of day.
        var rebased = trades.Select(trade =>
        {
            var offset = date.ToDateTime(TimeOnly.MinValue) - trade.EntryUtc.UtcDateTime.Date;
            return Copy(trade, trade.EntryUtc + offset, trade.ExitUtc!.Value + offset);
        }).ToArray();
        return new DailyTradeSummary { Date = date, CompletedTrades = rebased };
    }

    private static Trade Copy(Trade trade, DateTimeOffset entry, DateTimeOffset exit) => new()
    {
        Id = trade.Id, Sequence = trade.Sequence, Symbol = trade.Symbol, Direction = trade.Direction, EntryUtc = entry, ExitUtc = exit,
        Quantity = trade.Quantity, ClosedQuantity = trade.ClosedQuantity, GrossPnl = trade.GrossPnl, Fees = trade.Fees, NetPnl = trade.NetPnl,
        MaePoints = trade.MaePoints, PointValue = trade.PointValue, TickSize = trade.TickSize, RMultiple = trade.RMultiple, Status = trade.Status
    };

    private static Trade Trade(DateTimeOffset entry, int minutes, decimal netPnl, decimal fees = 0m, decimal? r = null, decimal? mae = null) => new()
    {
        Id = Guid.NewGuid(),
        Sequence = (int)(entry - Open).TotalMinutes,
        Symbol = "MES",
        Direction = "Long",
        EntryUtc = entry,
        ExitUtc = entry.AddMinutes(minutes),
        Quantity = 1,
        ClosedQuantity = 1,
        GrossPnl = netPnl + fees,
        Fees = fees,
        NetPnl = netPnl,
        RMultiple = r,
        MaePoints = mae,
        PointValue = 1m,
        TickSize = .25m,
        Status = "closed"
    };

    private static JsonDocument ReadPayload(string chart)
    {
        const string attribute = "data-plotly-chart=\"";
        var start = chart.IndexOf(attribute, StringComparison.Ordinal) + attribute.Length;
        var end = chart.IndexOf('"', start);
        return JsonDocument.Parse(WebUtility.HtmlDecode(chart[start..end]));
    }
}

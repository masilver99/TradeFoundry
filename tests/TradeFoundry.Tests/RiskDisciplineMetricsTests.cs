using TradeFoundry.Core;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class RiskDisciplineMetricsTests
{
    [Fact]
    public void Builds_rescue_and_winner_heat_metrics_in_initial_risk_units()
    {
        var metrics = TearSheetMetrics.BuildRiskDiscipline(new[]
        {
            Trade(2m, .25m, 1m, 1),
            Trade(4m, .50m, 1m, 2),
            Trade(1m, .75m, 1m, 3),
            Trade(-1m, 1.20m, 1m, 4),
            Trade(-.5m, .60m, 1m, 5)
        });

        Assert.Equal(5, metrics.CompletedTradeCount);
        Assert.Equal(3, metrics.WinnerCount);
        Assert.Equal(1, metrics.MaeViolationCount);
        Assert.Equal(1m / 3m, metrics.RescueRate!.Value, precision: 5);
        Assert.Equal(1m / 7m, metrics.RescueProfitShare!.Value, precision: 5);
        Assert.Equal(.70m, metrics.WinnerMaeP90R!.Value, precision: 5);
        Assert.Equal(.35m, metrics.WinnerHeatRatio!.Value, precision: 5);
        Assert.Equal(1m, metrics.RiskDataCoverage!.Value);
        Assert.Equal(1m, metrics.WinnerRiskDataCoverage!.Value);
        Assert.True(metrics.Score.IsComplete);
        Assert.Equal(54m, metrics.Score.Value!.Value);
        Assert.Equal("Watch", metrics.Score.Tier);
        Assert.Equal("positive", metrics.Indicators.Single(indicator => indicator.Label == "Risk Data Coverage").Tone);
    }

    [Fact]
    public void Keeps_score_incomplete_when_mae_or_initial_risk_is_missing()
    {
        var metrics = TearSheetMetrics.BuildRiskDiscipline(new[]
        {
            Trade(2m, .25m, 1m, 1),
            Trade(1m, null, 1m, 2),
            Trade(-1m, 1.20m, 1m, 3)
        });

        Assert.Equal(2, metrics.RiskObservedTradeCount);
        Assert.Equal(2m / 3m, metrics.RiskDataCoverage!.Value, precision: 5);
        Assert.Null(metrics.Score.Value);
        Assert.Equal("Incomplete", metrics.Score.Tier);
        Assert.Contains("coverage", metrics.Score.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Uses_adjustable_thresholds_for_rescues_and_violations()
    {
        var thresholds = TearSheetMetrics.DefaultRiskDisciplineThresholds with
        {
            RescueMaeR = .25m,
            MaeViolationR = 1.50m
        };

        var metrics = TearSheetMetrics.BuildRiskDiscipline(new[]
        {
            Trade(2m, .25m, 1m, 1),
            Trade(1m, .75m, 1m, 2),
            Trade(-1m, 1.60m, 1m, 3)
        }, thresholds);

        Assert.Equal(1m, metrics.RescueRate!.Value, precision: 5);
        Assert.Equal(1, metrics.MaeViolationCount);
        Assert.Equal(.25m, metrics.Thresholds.RescueMaeR);
        Assert.Equal(1.50m, metrics.Thresholds.MaeViolationR);
    }

    [Fact]
    public void Analytics_view_contains_the_risk_discipline_rendering_contract()
    {
        var viewPath = Path.Combine(AppContext.BaseDirectory, "Pages", "Analytics.cshtml");
        var view = File.ReadAllText(viewPath);

        Assert.Contains("id=\"risk-metrics\"", view);
        Assert.Contains("Model.Indicators.RiskStability", view);
        Assert.Contains("id=\"risk-discipline\"", view);
        Assert.Contains("data-risk-discipline-tier", view);
        Assert.Contains("riskScore.Components", view);
        Assert.Contains("riskDiscipline.Indicators", view);
        Assert.Contains("tf-risk-discipline-score-value", view);
        Assert.Contains("The score is an equal-weight average", view);
        Assert.Contains("RISK OVER TIME", view);
        Assert.Contains("Rolling rescue dependency", view);
        Assert.Contains("Rolling winner heat and MAE", view);
        Assert.Contains("Rolling MAE violations", view);
        Assert.Contains("Monthly Risk Discipline score", view);
    }

    [Fact]
    public void Keeps_risk_stability_metrics_in_the_shared_analysis_collection()
    {
        var indicators = TearSheetMetrics.Build(
            new[] { Trade(2m, .25m, 1m, 1) },
            Array.Empty<OrderEvent>(),
            Array.Empty<AccountBalanceEvent>(),
            Array.Empty<BenchmarkPoint>(),
            100m,
            "UTC",
            "USD");

        Assert.Equal(20, indicators.RiskStability.Count);
        Assert.Equal("Max Drawdown", indicators.RiskStability[0].Label);
        Assert.Equal("% Time at Highs", indicators.RiskStability[^1].Label);
        Assert.DoesNotContain(indicators.Groups, group => group.Id is "indicators-risk" or "indicators-risk-discipline");
    }

    [Fact]
    public void Builds_fixed_rolling_windows_and_monthly_summaries()
    {
        var start = new DateTimeOffset(2026, 1, 20, 12, 0, 0, TimeSpan.Zero);
        var trades = Enumerable.Range(1, 21)
            .Select(sequence => Trade(1m, .25m, 1m, sequence, start.AddDays(sequence - 1)))
            .ToArray();

        var trend = RiskDisciplineTrend.Build(trades, "UTC");

        Assert.Equal(2, trend.Rolling.Count);
        Assert.All(trend.Rolling, point =>
        {
            Assert.Equal(RiskDisciplineTrend.RollingWindow, point.WindowTradeCount);
            Assert.Equal(RiskDisciplineTrend.RollingWindow, point.RiskQualifiedTradeCount);
            Assert.Equal(RiskDisciplineTrend.RollingWindow, point.WinnerCount);
            Assert.Equal(RiskDisciplineTrend.RollingWindow, point.RiskQualifiedWinnerCount);
            Assert.Equal(0, point.MaeViolationCount);
            Assert.Equal(0m, point.RescueRate);
            Assert.NotNull(point.Score);
        });

        Assert.Equal(2, trend.Monthly.Count);
        Assert.Equal(21, trend.Monthly.Sum(point => point.TradeCount));
        Assert.Equal(12, trend.Monthly[0].TradeCount);
        Assert.Equal(9, trend.Monthly[1].TradeCount);
        Assert.All(trend.Monthly, point =>
        {
            Assert.True(point.HasCompleteRiskData);
            Assert.True(point.HasCompleteWinnerRiskData);
            Assert.NotNull(point.Score);
        });
    }

    [Fact]
    public void Renders_risk_discipline_trend_charts_and_explains_short_history()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var trades = Enumerable.Range(1, 21)
            .Select(sequence => Trade(1m, .25m, 1m, sequence, start.AddDays(sequence - 1)))
            .ToArray();

        var rescueChart = ChartRenderer.TearSheetRiskDisciplineRollingRescue(trades);
        var heatChart = ChartRenderer.TearSheetRiskDisciplineRollingHeat(trades);
        var violationsChart = ChartRenderer.TearSheetRiskDisciplineRollingViolations(trades);
        var monthlyChart = ChartRenderer.TearSheetRiskDisciplineMonthly(trades, "UTC");

        Assert.Contains("data-plotly-chart", rescueChart);
        Assert.Contains("Rolling rescue dependency", rescueChart);
        Assert.Contains("data-plotly-chart", heatChart);
        Assert.Contains("Rolling winner heat and MAE", heatChart);
        Assert.Contains("data-plotly-chart", violationsChart);
        Assert.Contains("Rolling MAE violations", violationsChart);
        Assert.Contains("data-plotly-chart", monthlyChart);
        Assert.Contains("Monthly Risk Discipline score", monthlyChart);

        var shortHistory = ChartRenderer.TearSheetRiskDisciplineRollingRescue(trades.Take(19));
        Assert.Contains("chart-empty", shortHistory);
        Assert.Contains("20 completed trades", shortHistory);
    }

    private static Trade Trade(
        decimal grossPnl,
        decimal? maePoints,
        decimal riskPoints,
        int sequence,
        DateTimeOffset? timestamp = null)
    {
        var entry = timestamp ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(sequence);
        return new Trade
        {
            Id = Guid.NewGuid(),
            Sequence = sequence,
            Direction = "Long",
            EntryUtc = entry,
            ExitUtc = entry.AddMinutes(1),
            EntryPrice = 100m,
            ExitPrice = 100m + grossPnl,
            Quantity = 1,
            ClosedQuantity = 1,
            GrossPnl = grossPnl,
            NetPnl = grossPnl,
            MaePoints = maePoints,
            MfePoints = 2m,
            PointValue = 1m,
            TickSize = .25m,
            InitialRiskPoints = riskPoints,
            InitialRiskCurrency = riskPoints,
            RMultiple = grossPnl / riskPoints,
            Status = "closed"
        };
    }
}

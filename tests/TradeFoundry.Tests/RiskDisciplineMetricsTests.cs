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

        Assert.Contains("id=\"risk-discipline\"", view);
        Assert.Contains("data-risk-discipline-tier", view);
        Assert.Contains("riskScore.Components", view);
        Assert.Contains("riskDiscipline.Indicators", view);
        Assert.Contains("tf-risk-discipline-score-value", view);
        Assert.Contains("The score is an equal-weight average", view);
    }

    private static Trade Trade(decimal grossPnl, decimal? maePoints, decimal riskPoints, int sequence)
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(sequence);
        return new Trade
        {
            Id = Guid.NewGuid(),
            Sequence = sequence,
            Direction = "Long",
            EntryUtc = timestamp,
            ExitUtc = timestamp.AddMinutes(1),
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

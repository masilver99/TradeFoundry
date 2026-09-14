using TradeFoundry.Core;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class EdgePersistenceMetricsTests
{
    [Fact]
    public void Persistent_positive_expectancy_across_blocks_scores_well()
    {
        var metrics = EdgePersistenceCalculator.Build(StableTrades(300));

        Assert.Equal(300, metrics.CompletedTradeCount);
        Assert.True(metrics.Score.Value >= 70m);
        Assert.Contains(metrics.Score.Tier, new[] { "Strong", "Very Strong" });
        Assert.All(metrics.Blocks, set => Assert.All(set.Blocks, block => Assert.True(block.Expectancy > 0m)));
        Assert.All(metrics.RecentComparisons, comparison => Assert.Equal("Stable", comparison.Trend));
        Assert.Equal("Stronger evidence", metrics.Confidence.Label);
    }

    [Fact]
    public void One_lucky_winner_fails_profit_robustness_and_is_not_strong()
    {
        var trades = Enumerable.Range(1, 100)
            .Select(sequence => Trade(sequence == 1 ? 150m : -1m, .25m, 1m, sequence))
            .ToArray();
        var metrics = EdgePersistenceCalculator.Build(trades);

        Assert.True(metrics.Robustness.WinnerConcentration.Top1ProfitShare > .95m);
        Assert.True(metrics.Robustness.WithoutBest.Single(point => point.RemovedCount == 3).NetPnl < 0m);
        Assert.True(metrics.Score.Value < 70m);
        Assert.Contains(metrics.Robustness.Observations, observation => observation.Contains("turns net P&L negative", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Catastrophic_loser_is_visible_as_loss_concentration_and_risk_instability()
    {
        var trades = Enumerable.Range(1, 100)
            .Select(sequence => Trade(sequence == 100 ? -250m : 2m, sequence == 100 ? 10m : .25m, 1m, sequence))
            .ToArray();
        var metrics = EdgePersistenceCalculator.Build(trades);

        Assert.True(metrics.Robustness.LargestLossShare > .80m);
        Assert.True(metrics.Robustness.WithoutWorst.Single(point => point.RemovedCount == 1).NetPnl > 0m);
        Assert.True(metrics.RiskStability.NormalizedLossP90 > 1m);
        Assert.Contains(metrics.Robustness.Observations, observation => observation.Contains("largest losses", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Recent_deterioration_and_improvement_are_classified_from_comparable_samples()
    {
        var deteriorating = StableTrades(150, grossPnl: 2m)
            .Concat(Enumerable.Range(151, 50).Select(sequence => Trade(-2m, .25m, 1m, sequence)))
            .ToArray();
        var improving = Enumerable.Range(1, 150)
            .Select(sequence => Trade(-1m, .25m, 1m, sequence))
            .Concat(StableTrades(50, grossPnl: 2m, sequenceOffset: 150))
            .ToArray();

        var deterioratingMetrics = EdgePersistenceCalculator.Build(deteriorating);
        var improvingMetrics = EdgePersistenceCalculator.Build(improving);

        Assert.Equal("Deteriorating", deterioratingMetrics.RecentComparisons.Single(comparison => comparison.Window == 50).Trend);
        Assert.Equal("Improving", improvingMetrics.RecentComparisons.Single(comparison => comparison.Window == 50).Trend);
        Assert.Equal("Deteriorating", deterioratingMetrics.ExpectancyPersistence.RecentTrend);
        Assert.Equal("Improving", improvingMetrics.ExpectancyPersistence.RecentTrend);
    }

    [Fact]
    public void Small_lucky_sample_is_capped_and_marked_as_insufficient_evidence()
    {
        var metrics = EdgePersistenceCalculator.Build(Enumerable.Range(1, 12).Select(sequence => Trade(sequence == 1 ? 100m : -1m, .25m, 1m, sequence)));

        Assert.Equal("Insufficient evidence", metrics.Confidence.Label);
        Assert.True(metrics.Score.Value <= 39m);
        Assert.Equal("No Evidence / Unstable", metrics.Score.Tier);
        Assert.Empty(metrics.Rolling);
        Assert.Empty(metrics.Blocks);
    }

    [Fact]
    public void All_winners_all_losers_and_flat_trades_do_not_break_ratios()
    {
        var winners = EdgePersistenceCalculator.Build(Enumerable.Range(1, 30).Select(sequence => Trade(1m, .25m, 1m, sequence)));
        var losers = EdgePersistenceCalculator.Build(Enumerable.Range(1, 30).Select(sequence => Trade(-1m, .25m, 1m, sequence)));
        var flat = EdgePersistenceCalculator.Build(Enumerable.Range(1, 30).Select(sequence => Trade(0m, .25m, 1m, sequence)));

        Assert.All(winners.Rolling.SelectMany(window => window.Points), point => Assert.Null(point.ProfitFactor));
        Assert.All(losers.Rolling.SelectMany(window => window.Points), point => Assert.Equal(0m, point.ProfitFactor));
        Assert.All(flat.Rolling.SelectMany(window => window.Points), point => Assert.Null(point.ProfitFactor));
        Assert.Equal(0m, flat.Rolling.First().Latest!.Expectancy!.Value);
    }

    [Fact]
    public void Missing_mae_degrades_risk_score_without_failing_the_report()
    {
        var metrics = EdgePersistenceCalculator.Build(Enumerable.Range(1, 60).Select(sequence => Trade(1m, null, null, sequence)));

        Assert.Equal(0, metrics.RiskStability.MaeObservedTradeCount);
        Assert.Equal(0m, metrics.RiskStability.MaeCoverage);
        Assert.Equal(0m, metrics.RiskStability.RiskScore);
        Assert.NotNull(metrics.Score.Value);
        Assert.Contains(metrics.Interpretation, item => item.Contains("MAE evidence is missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Scaling_position_size_does_not_change_risk_normalized_score()
    {
        var oneContract = StableTrades(100, quantity: 1);
        var fiveContracts = StableTrades(100, quantity: 5);

        var first = EdgePersistenceCalculator.Build(oneContract);
        var second = EdgePersistenceCalculator.Build(fiveContracts);

        Assert.Equal(first.Score.Value, second.Score.Value);
        Assert.Equal(first.RiskStability.RiskScore, second.RiskStability.RiskScore);
        Assert.True(second.UsesRiskUnits);
    }

    [Fact]
    public void Renders_edge_persistence_charts_and_page_contract()
    {
        var trades = StableTrades(100);
        var metrics = EdgePersistenceCalculator.Build(trades);

        Assert.Contains("data-plotly-chart", ChartRenderer.TearSheetEdgePersistenceRollingExpectancy(metrics));
        Assert.Contains("Rolling expectancy", ChartRenderer.TearSheetEdgePersistenceRollingExpectancy(metrics));
        Assert.Contains("data-plotly-chart", ChartRenderer.TearSheetEdgePersistenceBlocks(metrics));
        Assert.Contains("Rolling MAE persistence", ChartRenderer.TearSheetEdgePersistenceRollingMae(metrics));

        var viewPath = Path.Combine(AppContext.BaseDirectory, "Pages", "Analytics.cshtml");
        var view = File.ReadAllText(viewPath);
        Assert.Contains("id=\"edge-persistence\"", view);
        Assert.Contains("Edge Persistence Score", view);
        Assert.Contains("P&amp;L by non-overlapping block", view);
        Assert.Contains("Edge breakdown", view);
    }

    private static IEnumerable<Trade> StableTrades(int count, decimal grossPnl = 2m, int quantity = 1, int sequenceOffset = 0)
        => Enumerable.Range(1, count)
            .Select(index => Trade(index % 4 == 0 ? -1m : grossPnl, .25m, 1m, sequenceOffset + index, quantity));

    private static Trade Trade(decimal grossPnl, decimal? maePoints, decimal? riskPoints, int sequence, int quantity = 1)
    {
        var entry = new DateTimeOffset(2026, 1, 1, 14, 0, 0, TimeSpan.Zero).AddMinutes(sequence);
        var initialRiskCurrency = riskPoints.HasValue ? riskPoints.Value * quantity : (decimal?)null;
        return new Trade
        {
            Id = Guid.NewGuid(),
            Sequence = sequence,
            Symbol = "MES",
            Instrument = "MES",
            Direction = "Long",
            EntryUtc = entry,
            ExitUtc = entry.AddMinutes(1),
            EntryPrice = 100m,
            ExitPrice = 100m + grossPnl,
            Quantity = quantity,
            ClosedQuantity = quantity,
            GrossPnl = grossPnl * quantity,
            NetPnl = grossPnl * quantity,
            MaePoints = maePoints,
            MfePoints = 2m,
            PointValue = 1m,
            TickSize = .25m,
            InitialRiskPoints = riskPoints,
            InitialRiskCurrency = initialRiskCurrency,
            RMultiple = riskPoints.HasValue ? grossPnl / riskPoints.Value : null,
            Status = "closed"
        };
    }
}

using System.Globalization;
using TradeFoundry.Core;

namespace TradeFoundry.Services;

public sealed record TearSheetIndicatorScale(string CssClass, string MarkerPosition, string Tier);

public sealed record TearSheetIndicator(
    string Label,
    string Value,
    string Note,
    string Tone = "neutral",
    string? Help = null,
    TearSheetIndicatorScale? Scale = null);

public sealed record TearSheetIndicatorGroup(
    string Id,
    string Kicker,
    string Title,
    string Description,
    IReadOnlyList<TearSheetIndicator> Indicators);

public sealed record TearSheetScCell(string Value, string Tone = "neutral");

public sealed record TearSheetScRow(string Label, IReadOnlyList<TearSheetScCell> Cells);

public sealed record TearSheetScSection(string Title, IReadOnlyList<TearSheetScRow> Rows);

public sealed class TearSheetScStatistics
{
    public IReadOnlyList<string> Columns { get; init; } = new[] { "All Trades", "Long Trades", "Short Trades", "Daily Trades" };
    public IReadOnlyList<TearSheetScSection> Sections { get; init; } = Array.Empty<TearSheetScSection>();
}

public sealed class TearSheetIndicatorSet
{
    public IReadOnlyList<TearSheetIndicator> Key { get; init; } = Array.Empty<TearSheetIndicator>();
    public IReadOnlyList<TearSheetIndicator> RiskStability { get; init; } = Array.Empty<TearSheetIndicator>();
    public IReadOnlyList<TearSheetIndicatorGroup> Groups { get; init; } = Array.Empty<TearSheetIndicatorGroup>();
    public TearSheetScStatistics ScTradeStatistics { get; init; } = new();
    public RiskDisciplineMetrics RiskDiscipline { get; init; } = new();
}

public sealed record RiskDisciplineThresholds
{
    public decimal RescueMaeR { get; init; } = .75m;
    public decimal MaeViolationR { get; init; } = 1.00m;
    public decimal RescueRateExcellent { get; init; } = .10m;
    public decimal RescueRateGood { get; init; } = .20m;
    public decimal RescueRateWatch { get; init; } = .30m;
    public decimal RescueProfitShareExcellent { get; init; } = .15m;
    public decimal RescueProfitShareGood { get; init; } = .30m;
    public decimal RescueProfitShareWatch { get; init; } = .50m;
    public decimal WinnerMaeP90ExcellentR { get; init; } = .50m;
    public decimal WinnerMaeP90GoodR { get; init; } = .75m;
    public decimal WinnerMaeP90WatchR { get; init; } = 1.00m;
    public decimal WinnerHeatRatioExcellent { get; init; } = .35m;
    public decimal WinnerHeatRatioGood { get; init; } = .60m;
    public decimal WinnerHeatRatioWatch { get; init; } = 1.00m;
    public decimal ScoreExcellent { get; init; } = 90m;
    public decimal ScoreGood { get; init; } = 70m;
    public decimal ScoreWatch { get; init; } = 50m;
}

public sealed record RiskDisciplineComponent(
    string Label,
    decimal? NumericValue,
    string Value,
    string Target,
    string Note,
    decimal? Points,
    string Tone);

public sealed class RiskDisciplineScore
{
    public decimal? Value { get; init; }
    public string DisplayValue { get; init; } = "—";
    public string Tier { get; init; } = "Insufficient data";
    public string Tone { get; init; } = "muted";
    public string Summary { get; init; } = string.Empty;
    public int AvailableComponentCount { get; init; }
    public int ComponentCount { get; init; }
    public bool IsComplete => Value.HasValue && AvailableComponentCount == ComponentCount;
    public TearSheetIndicatorScale? Scale { get; init; }
    public IReadOnlyList<RiskDisciplineComponent> Components { get; init; } = Array.Empty<RiskDisciplineComponent>();
}

public sealed class RiskDisciplineMetrics
{
    public RiskDisciplineThresholds Thresholds { get; init; } = new();
    public int CompletedTradeCount { get; init; }
    public int WinnerCount { get; init; }
    public int RiskObservedTradeCount { get; init; }
    public int RiskObservedWinnerCount { get; init; }
    public decimal? RiskDataCoverage { get; init; }
    public decimal? WinnerRiskDataCoverage { get; init; }
    public decimal? RescueRate { get; init; }
    public decimal? RescueProfitShare { get; init; }
    public decimal? WinnerHeatRatio { get; init; }
    public int? MaeViolationCount { get; init; }
    public decimal? AllMaeP90R { get; init; }
    public decimal? AllMaeP95R { get; init; }
    public decimal? WinnerMaeP50R { get; init; }
    public decimal? WinnerMaeP90R { get; init; }
    public decimal? WinnerMaeP95R { get; init; }
    public decimal? MfeMaeRatio { get; init; }
    public IReadOnlyList<TearSheetIndicator> Indicators { get; init; } = Array.Empty<TearSheetIndicator>();
    public RiskDisciplineScore Score { get; init; } = new();
}

public static class TearSheetMetrics
{
    private static readonly CultureInfo UiCulture = CultureInfo.CurrentCulture;
    private sealed record IndicatorDefinition(string Help, Func<decimal, TearSheetIndicatorScale>? Scale = null);

    public static RiskDisciplineThresholds DefaultRiskDisciplineThresholds { get; } = new();

    private static readonly IReadOnlyDictionary<string, IndicatorDefinition> IndicatorDefinitions =
        new Dictionary<string, IndicatorDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["Profit Factor"] = new(
                "Gross profit divided by gross loss. Above 1.0 is net-positive; very high values can signal a concentrated or overfit sample.",
                value => Gauge("tf-scale-profit-factor", value, 0m, 10m, current => current < 1m ? "Losing" : current < 1.3m ? "Marginal" : current < 1.75m ? "Respectable" : current < 3m ? "Strong" : "Exceptional")),
            ["Expectancy"] = new(
                "Average dollar gain or loss per trade. Positive expectancy indicates a profitable edge; larger values indicate more return per decision.",
                value => Gauge("tf-scale-expectancy", value, -10m, 50m, current => current < 0m ? "Losing" : current < 5m ? "Solid" : current < 25m ? "Very Good" : "Excellent")),
            ["SQN"] = new(
                "System Quality Number combines expectancy, trade volatility, and trade count to estimate how consistently the system produces results.",
                value => Gauge("tf-scale-sqn", value, 0m, 7.5m, current => current < 1.6m ? "Poor" : current < 2m ? "Below Average" : current < 2.5m ? "Average" : current < 3m ? "Good" : current <= 5m ? "Excellent" : current <= 6.9m ? "Superb" : "Exceptional")),
            ["Sharpe"] = new(
                "Risk-adjusted return per unit of volatility. Higher values indicate more return for the variability taken.",
                value => Gauge("tf-scale-sharpe", value, -1m, 4m, current => current < 1m ? "Suboptimal" : current < 2m ? "Good" : current < 3m ? "Very Good" : "Excellent")),
            ["Sortino"] = new(
                "Risk-adjusted return using downside volatility only. Higher values indicate stronger returns without relying on large downside swings.",
                value => Gauge("tf-scale-sortino", value, -1m, 4m, current => current < 0m ? "Undesirable" : current < 1m ? "Suboptimal" : current < 2m ? "Good" : current < 3m ? "Very Good" : "Excellent")),
            ["Omega"] = new(
                "Compares total daily gains above zero with total daily losses below zero and accounts for the full return distribution.",
                value => Gauge("tf-scale-omega", value, 0m, 5m, current => current < 1m ? "Losing" : current < 1.5m ? "Below Average" : current < 2.5m ? "Good" : current < 4m ? "Very Good" : "Excellent")),
            ["Upside Potential"] = new(
                "Compares average upside with downside deviation. Higher values indicate a more favorable gain-to-downside shape.",
                value => Gauge("tf-scale-upside-potential", value, 0m, 4m, current => current < .5m ? "Poor" : current < 1m ? "Below Average" : current < 2m ? "Good" : current < 3m ? "Very Good" : "Excellent")),
            ["Sterling"] = new(
                "Annualized return relative to drawdown risk, using maximum drawdown plus a conservative buffer.",
                value => Gauge("tf-scale-sterling", value, 0m, 6m, current => current < 0m ? "Negative" : current < .5m ? "Poor" : current < 1m ? "Below Average" : current < 3m ? "Good" : current < 5m ? "Excellent" : "Exceptional")),
            ["Calmar"] = new(
                "Annualized return relative to maximum drawdown. Higher values indicate more return for the worst equity decline experienced.",
                value => Gauge("tf-scale-calmar", value, 0m, 6m, current => current < .5m ? "Poor" : current < 1m ? "Marginal" : current < 3m ? "Good" : current < 5m ? "Excellent" : "Exceptional")),
            ["V2 Ratio"] = new(
                "Annualized return relative to ulcer-adjusted drawdown risk. Higher values indicate a smoother return path.",
                value => Gauge("tf-scale-v2", value, -1m, 4m, current => current < 0m ? "Negative" : current < 1m ? "Suboptimal" : current < 2m ? "Good" : current < 3m ? "Very Good" : "Excellent")),
            ["Ulcer Index"] = new(
                "Measures the depth and duration of equity drawdowns. Lower is better; near zero means the account stays close to new highs.",
                value => Gauge("tf-scale-ulcer", value, 0m, 12m, current => current < 1m ? "Near Zero" : current < 5m ? "Low Risk" : current < 10m ? "Moderate Risk" : "High Risk")),
            ["CVaR 95%"] = new(
                "Average daily P&L across the worst 5% of trading days. Lower absolute tail loss indicates less severe downside exposure."),
            ["CVaR 95% of equity"] = new(
                "CVaR expressed as a share of starting equity. Lower is better; the scale highlights the severity of tail losses.",
                value => Gauge("tf-scale-cvar", value * 100m, 0m, 25m, current => current < 1m ? "Very Low Risk" : current < 3m ? "Low Risk" : current < 8m ? "Moderate Risk" : current < 15m ? "High Risk" : "Very High Risk")),
            ["Payoff Ratio"] = new(
                "Average winning trade divided by the absolute average losing trade. Higher payoff means fewer wins are needed to cover losses.",
                value => Gauge("tf-scale-payoff", value, 0m, 5m, current => current < 1m ? "Poor" : current < 1.5m ? "Marginal" : current < 2.5m ? "Good" : current < 4m ? "Very Good" : "Excellent")),
            ["Gain-to-Pain"] = new(
                "Total gross gains divided by total gross losses. Values above 1 indicate gains are outpacing cumulative trading pain.",
                value => Gauge("tf-scale-gain-to-pain", value, 0m, 4m, current => current < 0m ? "Negative" : current < .5m ? "Poor" : current < 1m ? "Marginal" : current < 2m ? "Good" : current < 3m ? "Very Good" : "Excellent")),
            ["Avg MFE"] = new("Average maximum favorable excursion: the mean peak open profit reached before trades closed. Higher values can indicate favorable entry timing."),
            ["Avg MAE"] = new("Average maximum adverse excursion: the mean worst open loss experienced before trades closed. Lower values generally indicate less heat taken."),
            ["MFE Capture"] = new("Share of each trade's peak open profit that was retained at exit. Lower values can indicate premature exits or profit targets that are too close."),
            ["Kelly Criterion"] = new("Estimated fraction of capital to risk per trade using win rate and payoff. Treat it as a sizing reference, not a required allocation."),
            ["Breakeven Win %"] = new("Minimum win rate required to break even at the current payoff ratio: 1 divided by 1 plus payoff."),
            ["Concentration (Top 5)"] = new(
                "Share of gross winner profit supplied by the five largest winning trades. Lower values indicate a more diversified result stream.",
                value => Gauge("tf-scale-concentration", value, 0m, 1m, current => current < .3m ? "Diversified" : current < .5m ? "Moderate" : "Concentrated")),
            ["Rescue Rate"] = new("Share of risk-qualified gross winners that reached the rescue threshold before becoming profitable. Lower is better."),
            ["Rescue Profit Share"] = new("Share of risk-qualified gross winner profit supplied by rescued winners. Lower is better."),
            ["Winner Heat Ratio"] = new("P90 winner MAE divided by the median winner result, both normalized in initial-risk units. Lower is better."),
            ["MAE violation count"] = new("Count of risk-qualified completed trades whose absolute MAE reached or exceeded the configured violation threshold."),
            ["Winner MAE P50"] = new("Median maximum adverse excursion of gross winners, normalized in initial-risk units."),
            ["Winner MAE P90"] = new("90th percentile maximum adverse excursion of gross winners, normalized in initial-risk units. Lower is better."),
            ["Winner MAE P95"] = new("95th percentile maximum adverse excursion of gross winners, normalized in initial-risk units. Lower is better."),
            ["All-trade MAE P90"] = new("90th percentile maximum adverse excursion across all risk-qualified completed trades, normalized in initial-risk units."),
            ["All-trade MAE P95"] = new("95th percentile maximum adverse excursion across all risk-qualified completed trades, normalized in initial-risk units."),
            ["MFE/MAE Ratio (Risk Discipline)"] = new("Average favorable excursion divided by average adverse excursion, using the existing currency excursion fields. Higher is better."),
            ["Risk Data Coverage"] = new("Share of completed trades with both maximum adverse excursion and usable initial-risk data."),
            ["Winner Risk Coverage"] = new("Share of gross winners with both maximum adverse excursion and usable initial-risk data."),
            ["Avg MAE (Winners)"] = new("Average maximum adverse excursion of winning trades: how much adversity winning trades withstood before turning profitable."),
            ["MFE/MAE Ratio"] = new("Average MFE divided by absolute average MAE. Above 1 means trades moved further in your favor than against you."),
            ["Avg Entry Chase"] = new("Average points chased per entry order. Positive means paying more than the original price to get filled; lower is better."),
            ["Avg Exit Chase"] = new("Average points chased per exit order. Positive means accepting a worse price to get out; lower is better."),
            ["Max Entry Chase"] = new("Largest single entry chase recorded across the imported order history."),
            ["Max Exit Chase"] = new("Largest single exit chase recorded across the imported order history."),
            ["Avg Mods / Order"] = new("Average number of modifications per order. Frequent changes can indicate indecision or difficulty finding liquidity."),
            ["Pct Chased Entry"] = new("Percentage of entry orders modified to follow the market price. High values can indicate poor entry discipline."),
            ["Pct Chased Exit"] = new("Percentage of exit orders modified to follow the market price. High values can indicate difficulty exiting at target prices."),
            ["Strategy Return"] = new(
                "Cumulative return of the strategy over the available equity period.",
                value => Gauge("tf-scale-strategy-return", value, -20m, 100m, current => current < 0m ? "Losing" : current < 10m ? "Below Market" : current < 20m ? "Market Rate" : current < 35m ? "Strong" : "Exceptional")),
            ["Alpha"] = new(
                "Excess return versus the benchmark. Positive alpha indicates outperformance over the same period.",
                value => Gauge("tf-scale-alpha", value, -2m, 6m, current => current < 0m ? "Underperform" : current < .5m ? "Average" : current < 3m ? "Good" : current < 4m ? "Very Good" : "Excellent")),
            ["Beta"] = new(
                "Sensitivity to benchmark returns. Values near zero are market-neutral; positive values indicate market exposure.",
                value => Gauge("tf-scale-beta", value, -1m, 3m, current => current < -.5m ? "Inverse" : current < .5m ? "Market Neutral" : current < 1.5m ? "Market Tracking" : "High Exposure")),
            ["Treynor"] = new(
                "Annualized excess return per unit of benchmark risk. Higher values indicate more return for each unit of market exposure.",
                value => Gauge("tf-scale-treynor", value, -1m, 5m, current => current < 0m ? "Negative" : current < .5m ? "Suboptimal" : current < 1.5m ? "Good" : current < 3m ? "Very Good" : "Excellent")),
            ["M² (RAP)"] = new(
                "Risk-adjusted performance scaled to the benchmark's volatility. Positive values indicate outperformance at comparable risk.",
                value => Gauge("tf-scale-m2", value, -10m, 40m, current => current < 0m ? "Negative" : current < 5m ? "Suboptimal" : current < 15m ? "Good" : current < 25m ? "Very Good" : "Excellent")),
            ["Avg R"] = new("Average trade result expressed in initial-risk units. A value of 1R means the average trade earned one unit of its initial risk."),
            ["Median R"] = new("Median trade result in initial-risk units. It is less influenced by outlier wins or losses than average R."),
            ["Total R"] = new("Sum of all trade R-multiples, representing cumulative risk-adjusted return."),
            ["% ≥ 1R"] = new("Percentage of trades that earned at least one unit of their initial risk."),
            ["% ≥ 2R"] = new("Percentage of trades that earned at least two units of their initial risk."),
            ["Positive R"] = new("Count of trades with a positive result relative to their initial risk."),
            ["Negative R"] = new("Count of trades with a negative result relative to their initial risk.")
        };

    public static TearSheetIndicatorSet Build(
        IEnumerable<Trade> source,
        IEnumerable<OrderEvent> orderSource,
        IEnumerable<AccountBalanceEvent> balanceSource,
        IEnumerable<BenchmarkPoint> benchmarkSource,
        decimal? startingEquity,
        string? timeZoneId,
        string currency,
        RiskDisciplineThresholds? riskDisciplineThresholds = null)
    {
        var trades = source
            .Where(trade => trade.ExitUtc.HasValue)
            .OrderBy(trade => trade.ExitUtc)
            .ThenBy(trade => trade.Sequence)
            .ToArray();
        var orders = orderSource.ToArray();
        var balances = balanceSource.Where(balance => balance.Balance.HasValue).ToArray();
        var benchmarks = benchmarkSource.ToArray();
        var timeZone = TimeZoneCatalog.Resolve(timeZoneId);
        var daily = BuildDaily(trades, timeZone);
        var baseline = startingEquity ?? balances
            .OrderBy(balance => balance.EventUtc)
            .ThenBy(balance => balance.RowNumber)
            .Select(balance => balance.Balance)
            .FirstOrDefault(balance => balance.HasValue);
        var equity = BuildEquity(daily, balances, baseline, timeZone);
        var stats = BuildTradeStats(trades, daily, equity, baseline, timeZone);
        var execution = BuildExecutionStats(orders, trades);
        var benchmark = BuildBenchmarkStats(equity, benchmarks, baseline);
        var scTradeStatistics = BuildScTradeStatistics(trades, timeZone, currency);
        var riskDiscipline = BuildRiskDiscipline(trades, riskDisciplineThresholds);
        var riskStability = new[]
        {
            Drawdown("Max Drawdown", stats.MaxDrawdown, "Peak-to-trough decline", currency),
            Percent("Max Drawdown %", stats.MaxDrawdownPercent, "Requires a positive starting balance"),
            Ratio("Ulcer Index", stats.UlcerIndex, "Average percentage drawdown severity"),
            Ratio("Sharpe", stats.Sharpe, "Annualized daily gross P&L volatility"),
            Ratio("Sortino", stats.Sortino, "Annualized downside-adjusted daily P&L"),
            Ratio("Omega", stats.Omega, "Daily gains ÷ daily losses"),
            Ratio("Upside Potential", stats.UpsidePotential, "Upside relative to downside deviation"),
            Money("CVaR 95%", stats.Cvar95, "Average of the worst 5% daily gross results", currency),
            Percent("CVaR 95% of equity", stats.CvarPercent, "Tail loss ÷ starting balance"),
            Ratio("Calmar", stats.Calmar, "Net P&L ÷ max drawdown"),
            Ratio("Sterling", stats.Sterling, "Annualized return ÷ drawdown buffer"),
            Ratio("V2 Ratio", stats.V2, "Annualized return ÷ ulcer-adjusted risk"),
            Ratio("Recovery Factor", stats.RecoveryFactor, "Net P&L ÷ max drawdown"),
            Number("DD Episodes", stats.DrawdownEpisodeCount, "Observed peak-to-recovery periods"),
            Days("Longest DD", stats.LongestDrawdownDays, "Longest drawdown duration"),
            Days("Avg DD Length", stats.AvgDrawdownDays, "Average drawdown duration"),
            Days("Median Recovery", stats.MedianRecoveryDays, "Median trough-to-recovery duration"),
            Days("Current Underwater", stats.CurrentUnderwaterDays, "Current unrecovered duration"),
            Days("Days Since High", stats.DaysSinceLastEquityHigh, "Days since the last equity high"),
            Percent("% Time at Highs", stats.PercentTimeAtHighs, "Share of equity observations at a high")
        };

        return new TearSheetIndicatorSet
        {
            ScTradeStatistics = scTradeStatistics,
            RiskStability = riskStability,
            RiskDiscipline = riskDiscipline,
            Key = new[]
            {
                Money("Net P&L", stats.TotalNetPnl, "After fees", currency),
                Money("Gross P&L", stats.TotalGrossPnl, "Before fees", currency),
                Money("Avg Daily P&L", stats.AvgDailyNetPnl, "Completed trading days", currency),
                Money("Exchange Fees", stats.ExchangeFees, "Exchange component", currency, "negative"),
                Money("NFA Fees", stats.NfaFees, "NFA component", currency, "negative"),
                Money("Clearing / commission", stats.ClearingFees, "Clearing and commission component", currency, "negative"),
                Money("Total Fees", stats.TotalFees, "Sum of fee components or all-in values", currency, "negative"),
                Number("Closed trades", stats.TradeCount, "Completed positions"),
                Percent("Win Rate", stats.WinRate, "Gross P&L winners", positiveAt: .5m),
                Ratio("Profit Factor", stats.ProfitFactor, "Gross profit ÷ gross loss"),
                Money("Expectancy", stats.Expectancy, "Average gross P&L per trade", currency),
                Drawdown("Max Drawdown", stats.MaxDrawdown, "Peak-to-trough equity decline", currency),
                Number("Points", stats.Points, "Direction-adjusted contract points", "0.##")
            },
            Groups = new[]
            {
                new TearSheetIndicatorGroup(
                    "indicators-performance",
                    "PERFORMANCE",
                    "Core performance",
                    "Trade outcomes, fees, and edge quality.",
                    new[]
                    {
                        Money("Gross P&L", stats.TotalGrossPnl, "Before fees", currency),
                        Money("Net P&L", stats.TotalNetPnl, "After fees", currency),
                        Money("Avg Daily P&L", stats.AvgDailyNetPnl, "Net P&L ÷ trading days", currency),
                        Money("Exchange Fees", stats.ExchangeFees, "Exchange component", currency, "negative"),
                        Money("NFA Fees", stats.NfaFees, "NFA component", currency, "negative"),
                        Money("Clearing / commission", stats.ClearingFees, "Clearing and commission component", currency, "negative"),
                        Money("Total Fees", stats.TotalFees, "Sum of fee components or all-in values", currency, "negative"),
                        Number("Trades", stats.TradeCount, "Completed positions"),
                        Percent("Win Rate", stats.WinRate, "Gross P&L winners", positiveAt: .5m),
                        Percent("Win Rate (BE)", stats.WinRateBe, "Gross P&L above breakeven"),
                        Money("Avg Win", stats.AvgWin, "Average winning gross P&L", currency, "positive"),
                        Money("Avg Loss", stats.AvgLoss, "Average losing gross P&L", currency, "negative"),
                        Ratio("Profit Factor", stats.ProfitFactor, "Gross profit ÷ gross loss"),
                        Money("Expectancy", stats.Expectancy, "Average gross P&L per trade", currency),
                        Ratio("SQN", stats.Sqn, "Expectancy ÷ trade volatility × √n"),
                        Money("Biggest Winner", stats.BiggestWinner, "Largest winning gross P&L", currency, "positive"),
                        Money("Biggest Loser", stats.BiggestLoser, "Largest losing gross P&L", currency, "negative"),
                        Ratio("Payoff Ratio", stats.PayoffRatio, "Average win ÷ absolute average loss"),
                        Ratio("Gain-to-Pain", stats.GainToPain, "Gross gains ÷ gross losses"),
                        Number("Consec. Wins", stats.MaxConsecutiveWins, "Longest winning streak"),
                        Number("Consec. Losses", stats.MaxConsecutiveLosses, "Longest losing streak")
                    }),
                new TearSheetIndicatorGroup(
                    "indicators-trade-dynamics",
                    "TRADE DYNAMICS",
                    "Duration, excursions, and direction",
                    "Trade-level diagnostics show how long positions were held, how much room they had, and whether the edge differs by direction.",
                    new[]
                    {
                        Duration("Avg Duration", stats.AvgDurationSeconds, "All completed trades"),
                        Duration("Avg Win Duration", stats.AvgWinnerDurationSeconds, "Winning trades"),
                        Duration("Avg Loss Duration", stats.AvgLoserDurationSeconds, "Losing trades"),
                        Duration("Shortest Trade", stats.ShortestTradeSeconds, "Completed trades"),
                        Duration("Longest Trade", stats.LongestTradeSeconds, "Completed trades"),
                        Money("Avg MFE", stats.AvgMfe, "Average maximum favorable excursion", currency, "positive"),
                        Money("Avg MAE", stats.AvgMae, "Average maximum adverse excursion", currency, "negative"),
                        Percent("MFE Capture", stats.MfeCapture, "Gross P&L captured from positive MFE"),
                        Money("Avg MAE (Winners)", stats.AvgMaeWinners, "Adverse excursion on winners", currency, "negative"),
                        Ratio("MFE/MAE Ratio", stats.AvgMfeMaeRatio, "Average MFE ÷ absolute average MAE"),
                        Number("Long Count", stats.LongCount, "Completed long trades"),
                        Number("Short Count", stats.ShortCount, "Completed short trades"),
                        Percent("Long Win Rate", stats.LongWinRate, "Gross P&L winners among longs"),
                        Percent("Short Win Rate", stats.ShortWinRate, "Gross P&L winners among shorts"),
                        Text("MAE Percentiles", stats.MaePercentiles, "P50 · P75 · P90 · P95", stats.MaePercentiles is null ? "muted" : "neutral"),
                        Text("MFE Percentiles", stats.MfePercentiles, "P50 · P75 · P90 · P95", stats.MfePercentiles is null ? "muted" : "neutral"),
                        Text("MAE Percentiles (R)", stats.MaeRPercentiles, "P50 · P75 · P90 · P95", stats.MaeRPercentiles is null ? "muted" : "neutral"),
                        Text("MFE Percentiles (R)", stats.MfeRPercentiles, "P50 · P75 · P90 · P95", stats.MfeRPercentiles is null ? "muted" : "neutral")
                    }),
                new TearSheetIndicatorGroup(
                    "indicators-calendar",
                    "CALENDAR & EDGE",
                    "Consistency and position sizing",
                    "Calendar-level consistency and sizing diagnostics complement the monthly P&L calendar and the position-size chart below.",
                    new[]
                    {
                        Number("Trading Days", stats.TradingDays, "Days with completed trades"),
                        Percent("% Profitable Days", stats.PercentProfitableDays, "Days with positive gross P&L", positiveAt: .5m),
                        Percent("% Profitable Weeks", stats.PercentProfitableWeeks, "Weeks with positive gross P&L", positiveAt: .5m),
                        Percent("% Profitable Months", stats.PercentProfitableMonths, "Months with positive gross P&L", positiveAt: .5m),
                        Number("Avg Trades / Day", stats.AvgTradesPerDay, "Completed trades ÷ trading days", "0.0"),
                        Money("Best Day", stats.BestDay, "Largest daily gross P&L", currency, "positive"),
                        Money("Worst Day", stats.WorstDay, "Smallest daily gross P&L", currency, "negative"),
                        Percent("Kelly Criterion", stats.Kelly, "Estimated fraction of capital to risk"),
                        Percent("Breakeven Win %", stats.BreakevenWinRate, "Minimum win rate at current payoff"),
                        Percent("Concentration (Top 5)", stats.ConcentrationTop5, "Share of gross winner profit"),
                        Percent("Top 1 Profit Share", stats.Top1ProfitShare, "Share from the single largest winner"),
                        Percent("Top 10 Profit Share", stats.Top10ProfitShare, "Share from the ten largest winners"),
                        Ratio("Winner Gini", stats.WinnerGini, "Concentration of winning-trade profit")
                    }),
                new TearSheetIndicatorGroup(
                    "indicators-r-multiple",
                    "R-MULTIPLE",
                    "Risk-normalized trade results",
                    "R-multiple cards appear when initial stop or risk data was imported with the trades.",
                    new[]
                    {
                        Ratio("Avg R", stats.RAverage, "Average trade result in initial-risk units", suffix: "R"),
                        Ratio("Median R", stats.RMedian, "Median trade result in initial-risk units", suffix: "R"),
                        Ratio("Total R", stats.TotalR, "Sum of risk-normalized results", suffix: "R"),
                        Percent("% ≥ 1R", stats.RPercentAtLeastOne, "Trades earning at least 1R"),
                        Percent("% ≥ 2R", stats.RPercentAtLeastTwo, "Trades earning at least 2R"),
                        Number("Positive R", stats.PositiveRCount, "Trades with positive R"),
                        Number("Negative R", stats.NegativeRCount, "Trades with negative R")
                    }),
                new TearSheetIndicatorGroup(
                    "indicators-execution",
                    "EXECUTION",
                    "Execution quality",
                    "Order-level indicators use Sierra order rows; chase measures use classified entry and exit order prices on completed trades.",
                    new[]
                    {
                        Number("Total Orders", execution.TotalOrders, "Distinct order lifecycles"),
                        Percent("Fill Rate", execution.FillRate, "Orders reaching Filled status"),
                        Percent("Cancel Rate", execution.CancelRate, "Orders ending Canceled"),
                        Percent("Modify Rate", execution.ModifyRate, "Orders with a modification"),
                        Percent("Partial-fill Rate", execution.PartialFillRate, "Orders with a partial fill"),
                        Points("Avg Entry Chase", execution.AvgEntryChase, "Average signed entry chase"),
                        Points("Avg Exit Chase", execution.AvgExitChase, "Average signed exit chase"),
                        Points("Max Entry Chase", execution.MaxEntryChase, "Largest recorded entry chase"),
                        Points("Max Exit Chase", execution.MaxExitChase, "Largest recorded exit chase"),
                        Number("Avg Mods / Order", execution.AvgModificationsPerOrder, "Average modification events", "0.00"),
                        Percent("Pct Chased Entry", execution.PercentChasedEntry, "Entries with positive chase"),
                        Percent("Pct Chased Exit", execution.PercentChasedExit, "Exits with positive chase")
                    }),
                new TearSheetIndicatorGroup(
                    "indicators-benchmark",
                    "BENCHMARK",
                    "Benchmark comparison",
                    "Benchmark indicators require a starting balance and an imported daily benchmark series.",
                    new[]
                    {
                        Text("Benchmark", benchmark.Ticker, "Imported benchmark series", benchmark.Ticker is null ? "muted" : "neutral"),
                        PercentPoints("Strategy Return", benchmark.StrategyReturnPercent, "Cumulative strategy return"),
                        PercentPoints("Benchmark Return", benchmark.BenchmarkReturnPercent, "Cumulative benchmark return"),
                        PercentPoints("Alpha", benchmark.AlphaPercent, "Strategy return minus benchmark"),
                        Percent("Alpha (ann.)", benchmark.AnnualizedAlpha, "Annualized excess return"),
                        Ratio("Beta", benchmark.Beta, "Sensitivity to benchmark returns"),
                        Ratio("Correlation", benchmark.Correlation, "Daily return correlation"),
                        Ratio("Treynor", benchmark.Treynor, "Annualized return ÷ beta"),
                        PercentPoints("M² (RAP)", benchmark.M2, "Risk-adjusted return at benchmark volatility")
                    })
            }
        };
    }

    public static RiskDisciplineMetrics BuildRiskDiscipline(
        IEnumerable<Trade> source,
        RiskDisciplineThresholds? thresholds = null)
    {
        var configuration = thresholds ?? DefaultRiskDisciplineThresholds;
        var trades = source
            .Where(trade => trade.ExitUtc.HasValue)
            .OrderBy(trade => trade.ExitUtc)
            .ThenBy(trade => trade.Sequence)
            .ToArray();
        var winners = trades.Where(trade => trade.GrossPnl > 0m).ToArray();
        var samples = trades
            .Select(CreateRiskDisciplineSample)
            .Where(sample => sample is not null)
            .Select(sample => sample!)
            .ToArray();
        var winnerSamples = samples.Where(sample => sample.Trade.GrossPnl > 0m).ToArray();
        var maeR = samples.Select(sample => sample.MaeR).ToArray();
        var winnerMaeR = winnerSamples.Select(sample => sample.MaeR).ToArray();
        var winnerMedianR = Median(winnerSamples.Select(sample => sample.ResultR).ToArray());
        decimal? winnerMaeP90 = winnerMaeR.Length == 0 ? null : Quantile(winnerMaeR, .90m);
        var rescueSamples = winnerSamples.Where(sample => sample.MaeR >= configuration.RescueMaeR).ToArray();
        var allMaeCurrency = trades
            .Select(trade => ExcursionCurrency(trade, trade.MaePoints))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        var allMfeCurrency = trades
            .Select(trade => ExcursionCurrency(trade, trade.MfePoints))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        decimal? mfeMaeRatio = allMfeCurrency.Length > 0 && allMaeCurrency.Length > 0 && allMaeCurrency.Average() > 0m
            ? SafeDivide(allMfeCurrency.Average(), allMaeCurrency.Average())
            : null;
        decimal? rescueRate = winnerSamples.Length == 0
            ? null
            : (decimal)rescueSamples.Length / winnerSamples.Length;
        var winnerProfit = winnerSamples.Sum(sample => sample.Trade.GrossPnl);
        decimal? rescueProfitShare = winnerProfit > 0m
            ? SafeDivide(rescueSamples.Sum(sample => sample.Trade.GrossPnl), winnerProfit)
            : null;
        int? maeViolationCount = samples.Length == 0
            ? null
            : samples.Count(sample => sample.MaeR >= configuration.MaeViolationR);
        decimal? winnerHeatRatio = winnerMaeP90.HasValue && winnerMedianR.HasValue && winnerMedianR.Value > 0m
            ? SafeDivide(winnerMaeP90.Value, winnerMedianR.Value)
            : null;

        var components = new[]
        {
            LowerBetterComponent(
                "Rescue Rate",
                rescueRate,
                FormatRiskPercent(rescueRate),
                $"≤ {FormatRiskPercent(configuration.RescueRateExcellent)} excellent · ≤ {FormatRiskPercent(configuration.RescueRateGood)} good · ≤ {FormatRiskPercent(configuration.RescueRateWatch)} watch",
                $"Rescued winners / risk-qualified winners at or above {FormatRiskR(configuration.RescueMaeR)}; lower is better.",
                configuration.RescueRateExcellent,
                configuration.RescueRateGood,
                configuration.RescueRateWatch),
            LowerBetterComponent(
                "Rescue Profit Share",
                rescueProfitShare,
                FormatRiskPercent(rescueProfitShare),
                $"≤ {FormatRiskPercent(configuration.RescueProfitShareExcellent)} excellent · ≤ {FormatRiskPercent(configuration.RescueProfitShareGood)} good · ≤ {FormatRiskPercent(configuration.RescueProfitShareWatch)} watch",
                "Gross profit supplied by rescued winners; lower is better.",
                configuration.RescueProfitShareExcellent,
                configuration.RescueProfitShareGood,
                configuration.RescueProfitShareWatch),
            LowerBetterComponent(
                "Winner Heat Ratio",
                winnerHeatRatio,
                FormatRiskRatio(winnerHeatRatio),
                $"≤ {FormatRiskRatio(configuration.WinnerHeatRatioExcellent)} excellent · ≤ {FormatRiskRatio(configuration.WinnerHeatRatioGood)} good · ≤ {FormatRiskRatio(configuration.WinnerHeatRatioWatch)} watch",
                "P90 winner MAE ÷ median winner result, both in initial-risk units; lower is better.",
                configuration.WinnerHeatRatioExcellent,
                configuration.WinnerHeatRatioGood,
                configuration.WinnerHeatRatioWatch),
            ViolationComponent(maeViolationCount, configuration.MaeViolationR),
            LowerBetterComponent(
                "Winner MAE P90",
                winnerMaeP90,
                FormatRiskR(winnerMaeP90),
                $"≤ {FormatRiskR(configuration.WinnerMaeP90ExcellentR)} excellent · ≤ {FormatRiskR(configuration.WinnerMaeP90GoodR)} good · ≤ {FormatRiskR(configuration.WinnerMaeP90WatchR)} watch",
                "90th percentile winner MAE in initial-risk units; lower is better.",
                configuration.WinnerMaeP90ExcellentR,
                configuration.WinnerMaeP90GoodR,
                configuration.WinnerMaeP90WatchR)
        };
        var scoreComplete = trades.Length > 0
            && winners.Length > 0
            && samples.Length == trades.Length
            && winnerSamples.Length == winners.Length
            && components.All(component => component.Points.HasValue);
        var scoreValue = scoreComplete
            ? components.Average(component => component.Points!.Value)
            : (decimal?)null;
        var availableComponents = components.Count(component => component.Points.HasValue);
        var scoreSummary = scoreValue.HasValue
            ? $"Equal-weight average of five 0–100 components. MAE violations are a hard gate; observed violations: {maeViolationCount!.Value:N0}."
            : trades.Length == 0
                ? "Complete trades are needed before a Risk Discipline score can be calculated."
                : samples.Length != trades.Length
                    ? $"Add MAE and initial-risk data for every completed trade before scoring. Current coverage: {samples.Length:N0}/{trades.Length:N0} trades."
                    : winners.Length == 0
                        ? "At least one gross winner is needed to evaluate rescue dependency and winner heat."
                        : "The score is waiting for enough valid winner MAE and result data.";
        var score = new RiskDisciplineScore
        {
            Value = scoreValue,
            DisplayValue = scoreValue.HasValue ? scoreValue.Value.ToString("0", UiCulture) : "—",
            Tier = scoreValue.HasValue ? RiskScoreTier(scoreValue.Value, configuration) : "Incomplete",
            Tone = scoreValue.HasValue ? RiskScoreTone(scoreValue.Value, configuration) : "muted",
            Summary = scoreSummary,
            AvailableComponentCount = availableComponents,
            ComponentCount = components.Length,
            Scale = scoreValue.HasValue
                ? Gauge("tf-scale-risk-discipline", scoreValue.Value, 0m, 100m, current => RiskScoreTier(current, configuration))
                : null,
            Components = components
        };

        return new RiskDisciplineMetrics
        {
            Thresholds = configuration,
            CompletedTradeCount = trades.Length,
            WinnerCount = winners.Length,
            RiskObservedTradeCount = samples.Length,
            RiskObservedWinnerCount = winnerSamples.Length,
            RiskDataCoverage = trades.Length == 0 ? null : (decimal)samples.Length / trades.Length,
            WinnerRiskDataCoverage = winners.Length == 0 ? null : (decimal)winnerSamples.Length / winners.Length,
            RescueRate = rescueRate,
            RescueProfitShare = rescueProfitShare,
            WinnerHeatRatio = winnerHeatRatio,
            MaeViolationCount = maeViolationCount,
            AllMaeP90R = maeR.Length == 0 ? null : Quantile(maeR, .90m),
            AllMaeP95R = maeR.Length == 0 ? null : Quantile(maeR, .95m),
            WinnerMaeP50R = winnerMaeR.Length == 0 ? null : Quantile(winnerMaeR, .50m),
            WinnerMaeP90R = winnerMaeP90,
            WinnerMaeP95R = winnerMaeR.Length == 0 ? null : Quantile(winnerMaeR, .95m),
            MfeMaeRatio = mfeMaeRatio,
            Indicators = components
                .Select(ToRiskIndicator)
                .Concat(new[]
                {
                    RiskRIndicator("Winner MAE P50", winnerMaeR.Length == 0 ? null : Quantile(winnerMaeR, .50m), "Median winner MAE in initial-risk units."),
                    RiskRIndicator("Winner MAE P95", winnerMaeR.Length == 0 ? null : Quantile(winnerMaeR, .95m), "95th percentile winner MAE in initial-risk units; lower is better."),
                    RiskRIndicator("All-trade MAE P90", maeR.Length == 0 ? null : Quantile(maeR, .90m), "90th percentile MAE across risk-qualified trades."),
                    RiskRIndicator("All-trade MAE P95", maeR.Length == 0 ? null : Quantile(maeR, .95m), "95th percentile MAE across risk-qualified trades."),
                    RiskRatioIndicator("MFE/MAE Ratio (Risk Discipline)", mfeMaeRatio, "Existing currency MFE ÷ absolute MAE; higher is better."),
                    RiskPercentIndicator("Risk Data Coverage", trades.Length == 0 ? null : (decimal)samples.Length / trades.Length, "Completed trades with both MAE and usable initial-risk data.", higherIsBetter: true),
                    RiskPercentIndicator("Winner Risk Coverage", winners.Length == 0 ? null : (decimal)winnerSamples.Length / winners.Length, "Gross winners with both MAE and usable initial-risk data.", higherIsBetter: true)
                })
                .ToArray(),
            Score = score
        };
    }

    private static RiskDisciplineComponent LowerBetterComponent(
        string label,
        decimal? value,
        string formattedValue,
        string target,
        string note,
        decimal excellent,
        decimal good,
        decimal watch)
    {
        var points = value.HasValue ? LowerBetterPoints(value.Value, excellent, good, watch) : (decimal?)null;
        return new RiskDisciplineComponent(label, value, formattedValue, target, note, points, RiskPointsTone(points));
    }

    private static RiskDisciplineComponent ViolationComponent(int? value, decimal threshold)
    {
        var points = value.HasValue ? value.Value == 0 ? 100m : 0m : (decimal?)null;
        var formattedValue = value.HasValue ? value.Value.ToString("N0", UiCulture) : "—";
        var target = $"0 · violation at or above {FormatRiskR(threshold)}";
        var note = $"Risk-qualified completed trades with MAE at or above {FormatRiskR(threshold)}; any violation fails this component.";
        return new RiskDisciplineComponent("MAE violation count", value, formattedValue, target, note, points, RiskPointsTone(points));
    }

    private static TearSheetIndicator ToRiskIndicator(RiskDisciplineComponent component)
        => component.NumericValue.HasValue
            ? CreateIndicator(component.Label, component.Value, component.Note, component.Tone, component.NumericValue.Value)
            : Missing(component.Label, component.Note);

    private static TearSheetIndicator RiskRIndicator(string label, decimal? value, string note)
    {
        if (!value.HasValue) return Missing(label, note);
        var tone = value.Value <= .50m ? "positive" : value.Value <= 1.00m ? "neutral" : "negative";
        return CreateIndicator(label, FormatRiskR(value), note, tone, value.Value);
    }

    private static TearSheetIndicator RiskRatioIndicator(string label, decimal? value, string note)
    {
        if (!value.HasValue) return Missing(label, note);
        var tone = value.Value >= 1.50m ? "positive" : value.Value >= 1.00m ? "neutral" : "negative";
        return CreateIndicator(label, FormatRiskRatio(value), note, tone, value.Value);
    }

    private static TearSheetIndicator RiskPercentIndicator(string label, decimal? value, string note, bool higherIsBetter = false)
    {
        if (!value.HasValue) return Missing(label, note);
        var tone = higherIsBetter
            ? value.Value >= 1m ? "positive" : "neutral"
            : value.Value <= .10m ? "positive" : value.Value <= .30m ? "neutral" : "negative";
        return CreateIndicator(label, FormatRiskPercent(value), note, tone, value.Value);
    }

    private static RiskDisciplineSample? CreateRiskDisciplineSample(Trade trade)
    {
        var risk = RiskCurrency(trade);
        var mae = ExcursionCurrency(trade, trade.MaePoints);
        if (risk is not > 0m || !mae.HasValue) return null;

        return new RiskDisciplineSample(
            trade,
            mae.Value / risk.Value,
            trade.GrossPnl / risk.Value);
    }

    private static decimal LowerBetterPoints(decimal value, decimal excellent, decimal good, decimal watch)
        => value <= excellent ? 100m : value <= good ? 70m : value <= watch ? 40m : 0m;

    private static string RiskPointsTone(decimal? points)
        => points is null ? "muted" : points >= 70m ? "positive" : points >= 40m ? "neutral" : "negative";

    private static string RiskScoreTier(decimal value, RiskDisciplineThresholds thresholds)
        => value >= thresholds.ScoreExcellent
            ? "Excellent"
            : value >= thresholds.ScoreGood
                ? "Good"
                : value >= thresholds.ScoreWatch
                    ? "Watch"
                    : "Needs work";

    private static string RiskScoreTone(decimal value, RiskDisciplineThresholds thresholds)
        => value >= thresholds.ScoreGood ? "positive" : value >= thresholds.ScoreWatch ? "neutral" : "negative";

    private static string FormatRiskPercent(decimal? value)
        => value.HasValue ? $"{value.Value * 100m:0.0}%" : "—";

    private static string FormatRiskR(decimal? value)
        => value.HasValue ? $"{value.Value:0.00}R" : "—";

    private static string FormatRiskRatio(decimal? value)
        => value.HasValue ? value.Value.ToString("0.00", UiCulture) : "—";

    private static TearSheetScStatistics BuildScTradeStatistics(
        IReadOnlyList<Trade> trades,
        TimeZoneInfo timeZone,
        string currency)
    {
        _ = currency;
        var ordered = trades
            .Where(trade => trade.ExitUtc.HasValue)
            .OrderBy(trade => trade.ExitUtc)
            .ThenBy(trade => trade.Sequence)
            .ToArray();
        var latestDate = ordered.Length == 0
            ? (DateOnly?)null
            : ordered.Max(trade => DateInZone(trade.ExitUtc!.Value, timeZone));
        var all = BuildScSubsetStats(ordered);
        var longs = BuildScSubsetStats(ordered.Where(trade => trade.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase)).ToArray());
        var shorts = BuildScSubsetStats(ordered.Where(trade => trade.Direction.Equals("Short", StringComparison.OrdinalIgnoreCase)).ToArray());
        var daily = BuildScSubsetStats(latestDate.HasValue
            ? ordered.Where(trade => DateInZone(trade.ExitUtc!.Value, timeZone) == latestDate.Value).ToArray()
            : Array.Empty<Trade>());
        var columns = new[] { all, longs, shorts, daily };

        return new TearSheetScStatistics
        {
            Sections = new[]
            {
                new TearSheetScSection("Profit / Loss", new[]
                {
                    ScMoneyRow("Closed Trades Profit/Loss", columns, stats => stats.ClosedPnl),
                    ScMoneyRow("Closed Trades Total Profit", columns, stats => stats.TotalProfit),
                    ScMoneyRow("Closed Trades Total Loss", columns, stats => stats.TotalLoss),
                    ScDecimalRow("Profit Factor", columns, stats => stats.ProfitFactor)
                }),
                new TearSheetScSection("Equity / Drawdown", new[]
                {
                    ScMoneyRow("Highest Cumulative Profit", columns, stats => stats.HighestCumulativeProfit),
                    ScMoneyRow("Lowest Cumulative Loss", columns, stats => stats.LowestCumulativeLoss),
                    ScMoneyRow("Maximum Runup", columns, stats => stats.MaximumRunup),
                    ScMoneyRow("Maximum Drawdown", columns, stats => stats.MaximumDrawdown)
                }),
                new TearSheetScSection("Open Profit / Loss (position-level)", new[]
                {
                    ScMoneyRow("Maximum Trade Open Profit", columns, stats => stats.MaximumOpenProfit),
                    ScMoneyRow("Maximum Trade Open Loss", columns, stats => stats.MaximumOpenLoss),
                    ScMoneyRow("Average Trade Open Profit", columns, stats => stats.AverageOpenProfit),
                    ScMoneyRow("Average Trade Open Loss", columns, stats => stats.AverageOpenLoss),
                    ScMoneyRow("Average Winning Trade Open Profit", columns, stats => stats.AverageWinningOpenProfit),
                    ScMoneyRow("Average Winning Trade Open Loss", columns, stats => stats.AverageWinningOpenLoss),
                    ScMoneyRow("Average Losing Trade Open Profit", columns, stats => stats.AverageLosingOpenProfit),
                    ScMoneyRow("Average Losing Trade Open Loss", columns, stats => stats.AverageLosingOpenLoss),
                    ScMoneyRow("Exchange Fees", columns, stats => stats.ExchangeFees, forcedTone: "negative"),
                    ScMoneyRow("NFA Fees", columns, stats => stats.NfaFees, forcedTone: "negative"),
                    ScMoneyRow("Clearing / commission", columns, stats => stats.ClearingFees, forcedTone: "negative"),
                    ScMoneyRow("Total Commissions", columns, stats => stats.TotalCommissions, forcedTone: "negative")
                }),
                new TearSheetScSection("Trade Counts", new[]
                {
                    ScIntegerRow("Total FlatToFlat Trades", columns, stats => stats.TotalTrades),
                    ScPercentRow("FlatToFlat Percent Profitable", columns, stats => stats.PercentProfitable),
                    ScIntegerRow("Winning FlatToFlat Trades", columns, stats => stats.WinningTrades),
                    ScIntegerRow("Losing FlatToFlat Trades", columns, stats => stats.LosingTrades),
                    ScIntegerRow("Long FlatToFlat Trades", columns, stats => stats.LongTrades),
                    ScIntegerRow("Short FlatToFlat Trades", columns, stats => stats.ShortTrades)
                }),
                new TearSheetScSection("Averages", new[]
                {
                    ScMoneyRow("Average FlatToFlat Trade P/L", columns, stats => stats.AverageTradePnl),
                    ScMoneyRow("Average FlatToFlat Winning Trade", columns, stats => stats.AverageWinningTrade),
                    ScMoneyRow("Average FlatToFlat Losing Trade", columns, stats => stats.AverageLosingTrade),
                    ScDecimalRow("Average Profit Factor", columns, stats => stats.AverageProfitFactor)
                }),
                new TearSheetScSection("Extremes", new[]
                {
                    ScMoneyRow("Largest FlatToFlat Winning Trade", columns, stats => stats.LargestWinner),
                    ScMoneyRow("Largest FlatToFlat Losing Trade", columns, stats => stats.LargestLoser),
                    ScPercentRow("Largest FlatToFlat Winner % of Profit", columns, stats => stats.LargestWinnerPercent),
                    ScPercentRow("Largest FlatToFlat Loser % of Loss", columns, stats => stats.LargestLoserPercent)
                }),
                new TearSheetScSection("Streaks", new[]
                {
                    ScIntegerRow("Max Consecutive Winners", columns, stats => stats.MaxConsecutiveWinners),
                    ScIntegerRow("Max Consecutive Losers", columns, stats => stats.MaxConsecutiveLosers)
                }),
                new TearSheetScSection("Time in Trade", new[]
                {
                    ScDurationRow("Average Time In Trades", columns, stats => stats.AverageTimeSeconds),
                    ScDurationRow("Average Time In Winning Trades", columns, stats => stats.AverageWinningTimeSeconds),
                    ScDurationRow("Average Time In Losing Trades", columns, stats => stats.AverageLosingTimeSeconds),
                    ScDurationRow("Longest Held Winning Trade", columns, stats => stats.LongestWinningTimeSeconds),
                    ScDurationRow("Longest Held Losing Trade", columns, stats => stats.LongestLosingTimeSeconds)
                }),
                new TearSheetScSection("Quantity", new[]
                {
                    ScIntegerRow("Total Quantity", columns, stats => stats.TotalQuantity),
                    ScIntegerRow("Winning Quantity", columns, stats => stats.WinningQuantity),
                    ScIntegerRow("Losing Quantity", columns, stats => stats.LosingQuantity),
                    ScDecimalRow("Avg Quantity Per FlatToFlat Trade", columns, stats => stats.AverageQuantity),
                    ScDecimalRow("Avg Quantity Per Winning Trade", columns, stats => stats.AverageWinningQuantity),
                    ScDecimalRow("Avg Quantity Per Losing Trade", columns, stats => stats.AverageLosingQuantity),
                    ScIntegerRow("Largest FlatToFlat Trade Quantity", columns, stats => stats.LargestTradeQuantity)
                }),
                new TearSheetScSection("Last Trade / Expectancy", new[]
                {
                    ScMoneyRow("Last FlatToFlat Trade P/L", columns, stats => stats.LastTradePnl),
                    ScMoneyRow("Expectancy", columns, stats => stats.Expectancy)
                })
            }
        };
    }

    private static ScSubsetStats BuildScSubsetStats(IReadOnlyList<Trade> source)
    {
        if (source.Count == 0) return new ScSubsetStats();

        var trades = source
            .OrderBy(trade => trade.ExitUtc)
            .ThenBy(trade => trade.Sequence)
            .ToArray();
        var netPnl = trades.Select(trade => trade.NetPnl).ToArray();
        var winners = trades.Where(trade => trade.NetPnl > 0m).ToArray();
        var losers = trades.Where(trade => trade.NetPnl < 0m).ToArray();
        var winningPnl = winners.Select(trade => trade.NetPnl).ToArray();
        var losingPnl = losers.Select(trade => trade.NetPnl).ToArray();
        var totalProfit = winningPnl.Sum();
        var totalLoss = losingPnl.Sum();
        var closedPnl = totalProfit + totalLoss;
        var cumulative = BuildScCumulativeStats(trades);
        var openProfit = trades.Select(ScOpenProfit).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var openLoss = trades.Select(ScOpenLoss).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var winningOpenProfit = winners.Select(ScOpenProfit).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var winningOpenLoss = winners.Select(ScOpenLoss).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var losingOpenProfit = losers.Select(ScOpenProfit).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var losingOpenLoss = losers.Select(ScOpenLoss).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var averageWinning = winningPnl.Length == 0 ? (decimal?)null : winningPnl.Average();
        var averageLosing = losingPnl.Length == 0 ? (decimal?)null : losingPnl.Average();
        var durations = trades.Select(ScDurationSeconds).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var winningDurations = winners.Select(ScDurationSeconds).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var losingDurations = losers.Select(ScDurationSeconds).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var totalQuantity = trades.Sum(ScTradeQuantity);
        var winningQuantity = winners.Sum(ScTradeQuantity);
        var losingQuantity = losers.Sum(ScTradeQuantity);
        var largestWinner = winningPnl.Length == 0 ? (decimal?)null : winningPnl.Max();
        var largestLoser = losingPnl.Length == 0 ? (decimal?)null : losingPnl.Min();

        return new ScSubsetStats
        {
            ClosedPnl = closedPnl,
            TotalProfit = totalProfit,
            TotalLoss = totalLoss,
            ProfitFactor = totalLoss == 0m ? null : totalProfit / Math.Abs(totalLoss),
            HighestCumulativeProfit = cumulative.HighestCumulativeProfit,
            LowestCumulativeLoss = cumulative.LowestCumulativeLoss,
            MaximumRunup = cumulative.MaximumRunup,
            MaximumDrawdown = cumulative.MaximumDrawdown,
            MaximumOpenProfit = openProfit.Length == 0 ? null : openProfit.Max(),
            MaximumOpenLoss = openLoss.Length == 0 ? null : openLoss.Min(),
            AverageOpenProfit = openProfit.Length == 0 ? null : openProfit.Average(),
            AverageOpenLoss = openLoss.Length == 0 ? null : openLoss.Average(),
            AverageWinningOpenProfit = winningOpenProfit.Length == 0 ? null : winningOpenProfit.Average(),
            AverageWinningOpenLoss = winningOpenLoss.Length == 0 ? null : winningOpenLoss.Average(),
            AverageLosingOpenProfit = losingOpenProfit.Length == 0 ? null : losingOpenProfit.Average(),
            AverageLosingOpenLoss = losingOpenLoss.Length == 0 ? null : losingOpenLoss.Average(),
            ExchangeFees = trades.Sum(trade => trade.ExchangeFees),
            NfaFees = trades.Sum(trade => trade.NfaFees),
            ClearingFees = trades.Sum(trade => trade.ClearingFees),
            TotalCommissions = trades.Sum(trade => trade.Fees),
            TotalTrades = trades.Length,
            PercentProfitable = (decimal)winners.Length / trades.Length * 100m,
            WinningTrades = winners.Length,
            LosingTrades = losers.Length,
            LongTrades = trades.Count(trade => trade.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase)),
            ShortTrades = trades.Count(trade => trade.Direction.Equals("Short", StringComparison.OrdinalIgnoreCase)),
            AverageTradePnl = closedPnl / trades.Length,
            AverageWinningTrade = averageWinning,
            AverageLosingTrade = averageLosing,
            AverageProfitFactor = averageWinning.HasValue && averageLosing is { } losingAverage && losingAverage != 0m
                ? Math.Abs(averageWinning.Value) / Math.Abs(losingAverage)
                : null,
            LargestWinner = largestWinner,
            LargestLoser = largestLoser,
            LargestWinnerPercent = largestWinner.HasValue && totalProfit != 0m ? largestWinner.Value / totalProfit * 100m : null,
            LargestLoserPercent = largestLoser.HasValue && totalLoss != 0m ? Math.Abs(largestLoser.Value) / Math.Abs(totalLoss) * 100m : null,
            MaxConsecutiveWinners = MaxStreak(netPnl, positive: true),
            MaxConsecutiveLosers = MaxStreak(netPnl, positive: false),
            AverageTimeSeconds = durations.Length == 0 ? null : durations.Average(),
            AverageWinningTimeSeconds = winningDurations.Length == 0 ? null : winningDurations.Average(),
            AverageLosingTimeSeconds = losingDurations.Length == 0 ? null : losingDurations.Average(),
            LongestWinningTimeSeconds = winningDurations.Length == 0 ? null : winningDurations.Max(),
            LongestLosingTimeSeconds = losingDurations.Length == 0 ? null : losingDurations.Max(),
            TotalQuantity = totalQuantity,
            WinningQuantity = winningQuantity,
            LosingQuantity = losingQuantity,
            AverageQuantity = (decimal)totalQuantity / trades.Length,
            AverageWinningQuantity = winners.Length == 0 ? null : (decimal)winningQuantity / winners.Length,
            AverageLosingQuantity = losers.Length == 0 ? null : (decimal)losingQuantity / losers.Length,
            LargestTradeQuantity = trades.Max(ScTradeQuantity),
            LastTradePnl = trades[^1].NetPnl,
            Expectancy = closedPnl / trades.Length
        };
    }

    private static ScCumulativeStats BuildScCumulativeStats(IReadOnlyList<Trade> trades)
    {
        var cumulative = 0m;
        var highest = 0m;
        var lowest = 0m;
        var peak = 0m;
        var trough = 0m;
        var maximumDrawdown = 0m;
        var maximumRunup = 0m;

        foreach (var trade in trades)
        {
            cumulative += trade.NetPnl;
            highest = Math.Max(highest, cumulative);
            lowest = Math.Min(lowest, cumulative);

            if (cumulative > peak) peak = cumulative;
            else maximumDrawdown = Math.Max(maximumDrawdown, peak - cumulative);

            if (cumulative < trough) trough = cumulative;
            else maximumRunup = Math.Max(maximumRunup, cumulative - trough);
        }

        return new ScCumulativeStats(highest, lowest, maximumRunup, -maximumDrawdown);
    }

    private static decimal? ScOpenProfit(Trade trade) => ExcursionCurrency(trade, trade.MfePoints);

    private static decimal? ScOpenLoss(Trade trade)
    {
        var value = ExcursionCurrency(trade, trade.MaePoints);
        return value.HasValue ? -value.Value : null;
    }

    private static double? ScDurationSeconds(Trade trade)
        => trade.Duration is { } duration && duration >= TimeSpan.Zero ? duration.TotalSeconds : null;

    private static int ScTradeQuantity(Trade trade)
        => Math.Max(1, trade.ClosedQuantity > 0 ? trade.ClosedQuantity : trade.Quantity);

    private static TearSheetScRow ScMoneyRow(
        string label,
        IReadOnlyList<ScSubsetStats> columns,
        Func<ScSubsetStats, decimal?> selector,
        string? forcedTone = null)
        => new(label, columns.Select(stats =>
        {
            var value = selector(stats);
            return new TearSheetScCell(FormatScMoney(value), value.HasValue ? forcedTone ?? Tone(value.Value) : "muted");
        }).ToArray());

    private static TearSheetScRow ScIntegerRow(string label, IReadOnlyList<ScSubsetStats> columns, Func<ScSubsetStats, int?> selector)
        => new(label, columns.Select(stats => new TearSheetScCell(FormatScNumber(selector(stats)), selector(stats).HasValue ? "neutral" : "muted")).ToArray());

    private static TearSheetScRow ScPercentRow(string label, IReadOnlyList<ScSubsetStats> columns, Func<ScSubsetStats, decimal?> selector)
        => new(label, columns.Select(stats => new TearSheetScCell(FormatScPercent(selector(stats)), selector(stats).HasValue ? "neutral" : "muted")).ToArray());

    private static TearSheetScRow ScDecimalRow(string label, IReadOnlyList<ScSubsetStats> columns, Func<ScSubsetStats, decimal?> selector)
        => new(label, columns.Select(stats => new TearSheetScCell(FormatScDecimal(selector(stats)), selector(stats).HasValue ? "neutral" : "muted")).ToArray());

    private static TearSheetScRow ScDurationRow(string label, IReadOnlyList<ScSubsetStats> columns, Func<ScSubsetStats, double?> selector)
        => new(label, columns.Select(stats => new TearSheetScCell(FormatScTradeDuration(selector(stats)), selector(stats).HasValue ? "neutral" : "muted")).ToArray());

    private static string FormatScMoney(decimal? value) => value.HasValue ? value.Value.ToString("C2", UiCulture) : "—";

    private static string FormatScNumber(int? value) => value.HasValue ? value.Value.ToString("N0", UiCulture) : "—";

    private static string FormatScPercent(decimal? value) => value.HasValue ? $"{value.Value.ToString("0.00", UiCulture)}%" : "—";

    private static string FormatScDecimal(decimal? value) => value.HasValue ? value.Value.ToString("0.00", UiCulture) : "—";

    private static string FormatScTradeDuration(double? seconds)
    {
        if (!seconds.HasValue) return "—";
        var totalSeconds = Math.Max(0, (int)Math.Round(seconds.Value));
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var remainingSeconds = totalSeconds % 60;
        return hours > 0
            ? $"{hours}:{minutes:00}:{remainingSeconds:00}"
            : $"{minutes}:{remainingSeconds:00}";
    }

    private static TradeStats BuildTradeStats(
        IReadOnlyList<Trade> trades,
        IReadOnlyList<DailySnapshot> daily,
        IReadOnlyList<EquitySnapshot> equity,
        decimal? baseline,
        TimeZoneInfo timeZone)
    {
        var gross = trades.Select(trade => trade.GrossPnl).ToArray();
        var net = trades.Select(trade => trade.NetPnl).ToArray();
        var winners = trades.Where(trade => trade.GrossPnl > 0m).ToArray();
        var losers = trades.Where(trade => trade.GrossPnl < 0m).ToArray();
        var winningPnl = winners.Select(trade => trade.GrossPnl).ToArray();
        var losingPnl = losers.Select(trade => trade.GrossPnl).ToArray();
        var durations = trades.Where(trade => trade.Duration.HasValue && trade.Duration.Value >= TimeSpan.Zero).Select(trade => trade.Duration!.Value.TotalSeconds).ToArray();
        var winnerDurations = winners.Where(trade => trade.Duration.HasValue && trade.Duration.Value >= TimeSpan.Zero).Select(trade => trade.Duration!.Value.TotalSeconds).ToArray();
        var loserDurations = losers.Where(trade => trade.Duration.HasValue && trade.Duration.Value >= TimeSpan.Zero).Select(trade => trade.Duration!.Value.TotalSeconds).ToArray();
        var mfe = trades.Select(trade => ExcursionCurrency(trade, trade.MfePoints)).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var mae = trades.Select(trade => ExcursionCurrency(trade, trade.MaePoints)).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var winnerMae = winners.Select(trade => ExcursionCurrency(trade, trade.MaePoints)).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var captures = trades
            .Select(trade => (Pnl: trade.GrossPnl, Mfe: ExcursionCurrency(trade, trade.MfePoints)))
            .Where(value => value.Mfe is > 0m)
            .Select(value => value.Pnl / value.Mfe!.Value)
            .ToArray();
        var rMultiples = trades.Where(trade => trade.RMultiple.HasValue).Select(trade => trade.RMultiple!.Value).ToArray();
        var maeR = trades
            .Select(trade => (Excursion: ExcursionCurrency(trade, trade.MaePoints), Risk: RiskCurrency(trade)))
            .Where(value => value.Excursion.HasValue && value.Risk is > 0m)
            .Select(value => value.Excursion!.Value / value.Risk!.Value)
            .ToArray();
        var mfeR = trades
            .Select(trade => (Excursion: ExcursionCurrency(trade, trade.MfePoints), Risk: RiskCurrency(trade)))
            .Where(value => value.Excursion.HasValue && value.Risk is > 0m)
            .Select(value => value.Excursion!.Value / value.Risk!.Value)
            .ToArray();
        var dailyGross = daily.Select(value => value.GrossPnl).ToArray();
        var dailyNet = daily.Select(value => value.NetPnl).ToArray();
        var std = SampleStd(gross);
        var winRate = RatioValue(winners.Length, trades.Count);
        var payoff = SafeDivide(winningPnl.DefaultIfEmpty().Average(), Math.Abs(losingPnl.DefaultIfEmpty().Average()));
        var maxDrawdown = DrawdownStats(equity, baseline);
        var drawdownDiagnostics = DrawdownDiagnostics(equity);
        var topWinners = winningPnl.OrderByDescending(value => value).ToArray();
        var totalWinnerProfit = winningPnl.Sum();
        var annualizedReturn = AnnualizedReturn(equity);

        return new TradeStats
        {
            TradeCount = trades.Count,
            TotalGrossPnl = gross.Sum(),
            TotalNetPnl = net.Sum(),
            ExchangeFees = trades.Sum(trade => trade.ExchangeFees),
            NfaFees = trades.Sum(trade => trade.NfaFees),
            ClearingFees = trades.Sum(trade => trade.ClearingFees),
            TotalFees = trades.Sum(trade => trade.Fees),
            Points = trades.Sum(trade => trade.GrossPoints),
            WinRate = winRate,
            WinRateBe = RatioValue(trades.Count(trade => trade.GrossPnl > 0m), trades.Count),
            AvgWin = winningPnl.Length == 0 ? null : winningPnl.Average(),
            AvgLoss = losingPnl.Length == 0 ? null : losingPnl.Average(),
            ProfitFactor = losingPnl.Length == 0 ? null : SafeDivide(winningPnl.Sum(), Math.Abs(losingPnl.Sum())),
            Expectancy = trades.Count == 0 ? null : gross.Average(),
            Sqn = std > 0m && trades.Count > 0 ? SafeDivide(gross.Average(), std) * (decimal)Math.Sqrt(trades.Count) : null,
            BiggestWinner = winningPnl.Length == 0 ? null : winningPnl.Max(),
            BiggestLoser = losingPnl.Length == 0 ? null : losingPnl.Min(),
            PayoffRatio = payoff,
            GainToPain = losingPnl.Length == 0 ? null : SafeDivide(gross.Sum(), Math.Abs(losingPnl.Sum())),
            MaxConsecutiveWins = MaxStreak(gross, positive: true),
            MaxConsecutiveLosses = MaxStreak(gross, positive: false),
            MaxDrawdown = maxDrawdown.MaxDepth,
            MaxDrawdownPercent = maxDrawdown.MaxDepthPercent,
            UlcerIndex = maxDrawdown.UlcerIndex,
            Sharpe = Sharpe(dailyGross),
            Sortino = Sortino(dailyGross),
            Omega = Omega(dailyGross),
            UpsidePotential = UpsidePotential(dailyGross),
            Cvar95 = Cvar(dailyGross),
            CvarPercent = baseline is > 0m && Cvar(dailyGross) is { } cvar ? Math.Abs(cvar) / baseline.Value : null,
            Calmar = maxDrawdown.MaxDepth is { } maxDepth && maxDepth > 0m ? SafeDivide(net.Sum(), maxDepth) : null,
            Sterling = baseline is > 0m && maxDrawdown.MaxDepthPercent is { } ddPct && annualizedReturn is { } annual ? SafeDivide(annual, ddPct + .10m) : null,
            V2 = baseline is > 0m && annualizedReturn is { } annualized && maxDrawdown.UlcerIndex is { } ulcer ? SafeDivide(annualized, ulcer + 1m) : null,
            RecoveryFactor = maxDrawdown.MaxDepth is { } recoveryDepth && recoveryDepth > 0m ? SafeDivide(net.Sum(), recoveryDepth) : null,
            AvgDurationSeconds = durations.Length == 0 ? null : durations.Average(),
            AvgWinnerDurationSeconds = winnerDurations.Length == 0 ? null : winnerDurations.Average(),
            AvgLoserDurationSeconds = loserDurations.Length == 0 ? null : loserDurations.Average(),
            ShortestTradeSeconds = durations.Length == 0 ? null : durations.Min(),
            LongestTradeSeconds = durations.Length == 0 ? null : durations.Max(),
            AvgMfe = mfe.Length == 0 ? null : mfe.Average(),
            AvgMae = mae.Length == 0 ? null : -mae.Average(),
            MfeCapture = captures.Length == 0 ? null : captures.Average(),
            AvgMaeWinners = winnerMae.Length == 0 ? null : -winnerMae.Average(),
            AvgMfeMaeRatio = mfe.Length > 0 && mae.Length > 0 && mae.Average() > 0m ? SafeDivide(mfe.Average(), mae.Average()) : null,
            LongCount = trades.Count(trade => trade.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase)),
            ShortCount = trades.Count(trade => trade.Direction.Equals("Short", StringComparison.OrdinalIgnoreCase)),
            LongWinRate = DirectionWinRate(trades, "Long"),
            ShortWinRate = DirectionWinRate(trades, "Short"),
            MaePercentiles = PercentileSummary(mae),
            MfePercentiles = PercentileSummary(mfe),
            MaeRPercentiles = PercentileSummary(maeR),
            MfeRPercentiles = PercentileSummary(mfeR),
            TradingDays = daily.Count,
            PercentProfitableDays = RatioValue(daily.Count(value => value.GrossPnl > 0m), daily.Count),
            PercentProfitableWeeks = PeriodProfitRate(trades, timeZone, timePeriod: Period.Week),
            PercentProfitableMonths = PeriodProfitRate(trades, timeZone, timePeriod: Period.Month),
            AvgTradesPerDay = daily.Count == 0 ? null : (decimal)trades.Count / daily.Count,
            AvgDailyNetPnl = daily.Count == 0 ? null : dailyNet.Sum() / daily.Count,
            BestDay = daily.Count == 0 ? null : daily.Max(value => value.GrossPnl),
            WorstDay = daily.Count == 0 ? null : daily.Min(value => value.GrossPnl),
            Kelly = payoff is > 0m && SafeDivide(1m - winRate, payoff.Value) is { } lossOdds ? winRate - lossOdds : null,
            BreakevenWinRate = payoff is > 0m ? SafeDivide(1m, 1m + payoff.Value) : null,
            ConcentrationTop5 = totalWinnerProfit > 0m ? SafeDivide(topWinners.Take(5).Sum(), totalWinnerProfit) : null,
            Top1ProfitShare = totalWinnerProfit > 0m && topWinners.Length > 0 ? SafeDivide(topWinners[0], totalWinnerProfit) : null,
            Top10ProfitShare = totalWinnerProfit > 0m ? SafeDivide(topWinners.Take(10).Sum(), totalWinnerProfit) : null,
            WinnerGini = Gini(topWinners),
            RAverage = rMultiples.Length == 0 ? null : rMultiples.Average(),
            RMedian = Median(rMultiples),
            TotalR = rMultiples.Length == 0 ? null : rMultiples.Sum(),
            RPercentAtLeastOne = RatioValue(rMultiples.Count(value => value >= 1m), rMultiples.Length),
            RPercentAtLeastTwo = RatioValue(rMultiples.Count(value => value >= 2m), rMultiples.Length),
            PositiveRCount = rMultiples.Count(value => value > 0m),
            NegativeRCount = rMultiples.Count(value => value < 0m),
            DrawdownEpisodeCount = drawdownDiagnostics.EpisodeCount,
            LongestDrawdownDays = drawdownDiagnostics.LongestDays,
            AvgDrawdownDays = drawdownDiagnostics.AverageDays,
            MedianRecoveryDays = drawdownDiagnostics.MedianRecoveryDays,
            CurrentUnderwaterDays = drawdownDiagnostics.CurrentUnderwaterDays,
            PercentTimeAtHighs = drawdownDiagnostics.PercentTimeAtHighs,
            DaysSinceLastEquityHigh = drawdownDiagnostics.DaysSinceHigh
        };
    }

    private static ExecutionStats BuildExecutionStats(IReadOnlyList<OrderEvent> events, IReadOnlyList<Trade> trades)
    {
        var lifecycles = events
            .GroupBy(eventItem => $"{eventItem.Account}\u001f{eventItem.Symbol}\u001f{(string.IsNullOrWhiteSpace(eventItem.InternalOrderId) ? eventItem.SourceKey : eventItem.InternalOrderId)}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(eventItem => eventItem.EventUtc).ThenBy(eventItem => eventItem.RowNumber).ToArray())
            .ToArray();
        var total = lifecycles.Length;
        if (total == 0) return new ExecutionStats();

        var modificationCounts = lifecycles.Select(lifecycle => lifecycle.Count(eventItem => eventItem.OrderStatus.Contains("modify", StringComparison.OrdinalIgnoreCase))).ToArray();
        var filled = lifecycles.Count(lifecycle => lifecycle.Any(eventItem => eventItem.OrderStatus.Equals("Filled", StringComparison.OrdinalIgnoreCase)));
        var canceled = lifecycles.Count(lifecycle => lifecycle[^1].OrderStatus.Equals("Canceled", StringComparison.OrdinalIgnoreCase));
        var modified = modificationCounts.Count(count => count > 0);
        var partial = lifecycles.Count(lifecycle => lifecycle.Any(eventItem => eventItem.OrderStatus.Contains("partial", StringComparison.OrdinalIgnoreCase) || eventItem.OrderActionSource.Contains("partial", StringComparison.OrdinalIgnoreCase)));
        var entryChase = trades.Where(trade => trade.EntryChasePoints.HasValue).Select(trade => trade.EntryChasePoints!.Value).ToArray();
        var exitChase = trades.Where(trade => trade.ExitChasePoints.HasValue).Select(trade => trade.ExitChasePoints!.Value).ToArray();
        return new ExecutionStats
        {
            TotalOrders = total,
            FillRate = RatioValue(filled, total),
            CancelRate = RatioValue(canceled, total),
            ModifyRate = RatioValue(modified, total),
            PartialFillRate = RatioValue(partial, total),
            AvgModificationsPerOrder = (decimal)modificationCounts.Sum() / total,
            AvgEntryChase = entryChase.Length == 0 ? null : entryChase.Average(),
            AvgExitChase = exitChase.Length == 0 ? null : exitChase.Average(),
            MaxEntryChase = entryChase.Length == 0 ? null : entryChase.Max(),
            MaxExitChase = exitChase.Length == 0 ? null : exitChase.Max(),
            PercentChasedEntry = entryChase.Length == 0 ? null : (decimal)entryChase.Count(value => value > 0m) / entryChase.Length,
            PercentChasedExit = exitChase.Length == 0 ? null : (decimal)exitChase.Count(value => value > 0m) / exitChase.Length
        };
    }

    private static BenchmarkStats BuildBenchmarkStats(
        IReadOnlyList<EquitySnapshot> equity,
        IReadOnlyList<BenchmarkPoint> source,
        decimal? baseline)
    {
        if (baseline is not > 0m || equity.Count < 2) return new BenchmarkStats();

        var benchmark = source
            .Where(point => point.Value > 0m)
            .GroupBy(point => point.Symbol, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()?
            .OrderBy(point => point.EventUtc)
            .Select(point => new BenchmarkSnapshot(DateOnly.FromDateTime(point.EventUtc.UtcDateTime.Date), point.Value))
            .ToArray() ?? Array.Empty<BenchmarkSnapshot>();
        if (benchmark.Length == 0) return new BenchmarkStats();

        decimal? strategyReturn = baseline is > 0m && equity.Count >= 2
            ? (equity[^1].Balance / equity[0].Balance - 1m) * 100m
            : (decimal?)null;
        decimal? benchmarkReturn = benchmark.Length >= 2
            ? (benchmark[^1].Value / benchmark[0].Value - 1m) * 100m
            : (decimal?)null;
        var benchmarkDailyReturns = BuildBenchmarkReturns(benchmark);
        var strategyDailyReturns = BuildReturns(equity)
            .ToDictionary(value => value.Date, value => value.Return);
        var overlap = strategyDailyReturns
            .Where(value => benchmarkDailyReturns.ContainsKey(value.Key))
            .Select(value => (Strategy: value.Value, Benchmark: benchmarkDailyReturns[value.Key]))
            .ToArray();
        decimal? beta = null;
        if (overlap.Length >= 2)
        {
            var covariance = Covariance(overlap.Select(value => value.Strategy).ToArray(), overlap.Select(value => value.Benchmark).ToArray());
            beta = covariance.HasValue ? SafeDivide(covariance.Value, Variance(overlap.Select(value => value.Benchmark).ToArray())) : null;
        }
        var correlation = overlap.Length >= 2 ? Correlation(overlap.Select(value => value.Strategy).ToArray(), overlap.Select(value => value.Benchmark).ToArray()) : null;
        decimal? periodYears = equity.Count >= 2 ? Math.Max((equity[^1].Date.DayNumber - equity[0].Date.DayNumber) / 365.25m, 1m / 252m) : null;
        decimal? annualizedStrategy = strategyReturn is { } strategy && periodYears is { } years && strategy > -100m ? (decimal)Math.Pow((double)(1m + strategy / 100m), (double)(1m / years)) - 1m : null;
        decimal? annualizedBenchmark = benchmarkReturn is { } market && periodYears is { } benchmarkYears && market > -100m ? (decimal)Math.Pow((double)(1m + market / 100m), (double)(1m / benchmarkYears)) - 1m : null;
        decimal? annualizedAlpha = annualizedStrategy.HasValue && annualizedBenchmark.HasValue ? annualizedStrategy.Value - annualizedBenchmark.Value : null;
        decimal? benchmarkVolatility = overlap.Length >= 2 ? SampleStd(overlap.Select(value => value.Benchmark).ToArray()) * (decimal)Math.Sqrt(252) : null;
        decimal? strategyVolatility = overlap.Length >= 2 ? SampleStd(overlap.Select(value => value.Strategy).ToArray()) * (decimal)Math.Sqrt(252) : null;
        decimal? m2 = annualizedStrategy.HasValue && benchmarkVolatility is { } marketVolatility && strategyVolatility is { } strategyVolatilityValue && strategyVolatilityValue > 0m
            ? annualizedStrategy.Value / strategyVolatilityValue * marketVolatility
            : null;
        return new BenchmarkStats
        {
            Ticker = source.Where(point => point.Value > 0m).GroupBy(point => point.Symbol, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).Select(group => group.Key).FirstOrDefault(),
            StrategyReturnPercent = strategyReturn,
            BenchmarkReturnPercent = benchmarkReturn,
            AlphaPercent = strategyReturn.HasValue && benchmarkReturn.HasValue ? strategyReturn.Value - benchmarkReturn.Value : null,
            AnnualizedAlpha = annualizedAlpha,
            Beta = beta,
            Correlation = correlation,
            Treynor = annualizedStrategy.HasValue && beta.HasValue && beta.Value != 0m ? SafeDivide(annualizedStrategy.Value, beta.Value) : null,
            M2 = m2 * 100m
        };
    }

    private static IReadOnlyList<DailySnapshot> BuildDaily(IReadOnlyList<Trade> trades, TimeZoneInfo timeZone)
    {
        return trades
            .GroupBy(trade => DateInZone(trade.ExitUtc!.Value, timeZone))
            .OrderBy(group => group.Key)
            .Select(group => new DailySnapshot(group.Key, group.Sum(trade => trade.GrossPnl), group.Sum(trade => trade.NetPnl), group.Count()))
            .ToArray();
    }

    private static IReadOnlyList<EquitySnapshot> BuildEquity(
        IReadOnlyList<DailySnapshot> daily,
        IReadOnlyList<AccountBalanceEvent> balances,
        decimal? baseline,
        TimeZoneInfo timeZone)
    {
        var netByDate = daily.ToDictionary(value => value.Date, value => value.NetPnl);
        var rawByDate = balances
            .Where(balance => balance.Balance.HasValue)
            .GroupBy(balance => DateInZone(balance.EventUtc, timeZone))
            .ToDictionary(group => group.Key, group => group.OrderBy(balance => balance.EventUtc).ThenBy(balance => balance.RowNumber).Last().Balance!.Value);
        var dates = netByDate.Keys.Concat(rawByDate.Keys).Distinct().OrderBy(date => date).ToList();
        if (dates.Count == 0) return Array.Empty<EquitySnapshot>();

        var starting = baseline ?? 0m;
        if (baseline is not null && starting != 0m) dates.Insert(0, dates[0].AddDays(-1));
        var cumulative = 0m;
        var result = new List<EquitySnapshot>(dates.Count);
        foreach (var date in dates)
        {
            if (netByDate.TryGetValue(date, out var pnl)) cumulative += pnl;
            result.Add(new EquitySnapshot(date, starting + cumulative));
        }
        return result;
    }

    private static DrawdownStatsValue DrawdownStats(IReadOnlyList<EquitySnapshot> equity, decimal? baseline)
    {
        if (equity.Count == 0) return new DrawdownStatsValue();
        var peak = equity[0].Balance;
        var maxDepth = 0m;
        var maxDepthPercent = baseline is > 0m ? 0m : (decimal?)null;
        var squaredPercentDrawdowns = new List<decimal>();
        foreach (var point in equity)
        {
            peak = Math.Max(peak, point.Balance);
            var depth = Math.Max(peak - point.Balance, 0m);
            maxDepth = Math.Max(maxDepth, depth);
            if (peak > 0m)
            {
                var percent = depth / peak;
                squaredPercentDrawdowns.Add(percent * percent);
                if (maxDepthPercent.HasValue) maxDepthPercent = Math.Max(maxDepthPercent.Value, percent);
            }
        }
        return new DrawdownStatsValue(
            maxDepth,
            maxDepthPercent,
            squaredPercentDrawdowns.Count == 0 ? null : (decimal)Math.Sqrt((double)(squaredPercentDrawdowns.Sum() / squaredPercentDrawdowns.Count)));
    }

    private static DrawdownDiagnosticsValue DrawdownDiagnostics(IReadOnlyList<EquitySnapshot> equity)
    {
        if (equity.Count < 2) return new DrawdownDiagnosticsValue();
        var peak = equity[0].Balance;
        var lastHigh = equity[0].Date;
        var atHigh = 0;
        var episodes = new List<DrawdownEpisodeValue>();
        DrawdownEpisodeBuilder? active = null;
        foreach (var point in equity)
        {
            if (point.Balance >= peak)
            {
                atHigh++;
                if (active is not null)
                {
                    episodes.Add(active.Recover(point.Date));
                    active = null;
                }
                peak = point.Balance;
                lastHigh = point.Date;
                continue;
            }

            active ??= new DrawdownEpisodeBuilder(point.Date, point.Date, point.Balance);
            if (point.Balance < active.TroughBalance) active = active.WithTrough(point.Date, point.Balance);
        }
        if (active is not null) episodes.Add(active.Close(equity[^1].Date));
        var durations = episodes.Select(episode => episode.DurationDays).ToArray();
        var recoveries = episodes.Where(episode => episode.RecoveryDays.HasValue).Select(episode => episode.RecoveryDays!.Value).ToArray();
        var currentUnderwaterDays = active is null ? 0m : (decimal)(equity[^1].Date.DayNumber - active.StartDate.DayNumber);
        return new DrawdownDiagnosticsValue(
            episodes.Count,
            durations.Length == 0 ? (decimal?)null : durations.Max(),
            durations.Length == 0 ? (decimal?)null : (decimal)durations.Average(),
            Median(recoveries.Select(value => (decimal)value).ToArray()),
            currentUnderwaterDays,
            (decimal)atHigh / equity.Count,
            Math.Max(0, equity[^1].Date.DayNumber - lastHigh.DayNumber));
    }

    private static IReadOnlyList<(DateOnly Date, decimal Return)> BuildReturns(IReadOnlyList<EquitySnapshot> equity)
    {
        var result = new List<(DateOnly, decimal)>();
        for (var index = 1; index < equity.Count; index++)
        {
            if (equity[index - 1].Balance != 0m)
                result.Add((equity[index].Date, (equity[index].Balance - equity[index - 1].Balance) / equity[index - 1].Balance));
        }
        return result;
    }

    private static IReadOnlyDictionary<DateOnly, decimal> BuildBenchmarkReturns(IReadOnlyList<BenchmarkSnapshot> benchmark)
    {
        var result = new Dictionary<DateOnly, decimal>();
        for (var index = 1; index < benchmark.Count; index++)
        {
            if (benchmark[index - 1].Value != 0m)
                result[benchmark[index].Date] = (benchmark[index].Value - benchmark[index - 1].Value) / benchmark[index - 1].Value;
        }
        return result;
    }

    private static decimal? AnnualizedReturn(IReadOnlyList<EquitySnapshot> equity)
    {
        if (equity.Count < 2 || equity[0].Balance <= 0m || equity[^1].Balance <= 0m) return null;
        var years = Math.Max((equity[^1].Date.DayNumber - equity[0].Date.DayNumber) / 365.25m, 1m / 252m);
        return (decimal)Math.Pow((double)(equity[^1].Balance / equity[0].Balance), (double)(1m / years)) - 1m;
    }

    private static decimal? Sharpe(IReadOnlyList<decimal> values)
    {
        var standardDeviation = SampleStd(values);
        return values.Count >= 2 && standardDeviation > 0m ? SafeDivide(values.Average(), standardDeviation) * (decimal)Math.Sqrt(252) : null;
    }

    private static decimal? Sortino(IReadOnlyList<decimal> values)
    {
        if (values.Count < 2) return null;
        var downside = values.Where(value => value < 0m).ToArray();
        var downsideDeviation = SampleStd(downside);
        return downside.Length > 1 && downsideDeviation > 0m ? SafeDivide(values.Average(), downsideDeviation) * (decimal)Math.Sqrt(252) : null;
    }

    private static decimal? Omega(IReadOnlyList<decimal> values)
    {
        var gains = values.Where(value => value > 0m).Sum();
        var losses = values.Where(value => value < 0m).Sum(value => -value);
        return losses > 0m ? SafeDivide(gains, losses) : null;
    }

    private static decimal? UpsidePotential(IReadOnlyList<decimal> values)
    {
        if (values.Count < 2) return null;
        var gains = values.Where(value => value > 0m).Sum();
        var downsideSquared = values.Where(value => value < 0m).Sum(value => value * value);
        return downsideSquared > 0m ? SafeDivide(gains / values.Count, (decimal)Math.Sqrt((double)(downsideSquared / values.Count))) : null;
    }

    private static decimal? Cvar(IReadOnlyList<decimal> values)
    {
        if (values.Count < 5) return null;
        var tailCount = Math.Max(1, (int)(values.Count * .05m));
        return values.OrderBy(value => value).Take(tailCount).Average();
    }

    private static decimal PeriodProfitRate(IReadOnlyList<Trade> trades, TimeZoneInfo timeZone, Period timePeriod)
    {
        if (trades.Count == 0) return 0m;
        var groups = trades.GroupBy(trade =>
            {
                var localDate = DateInZone(trade.ExitUtc!.Value, timeZone);
                return timePeriod == Period.Week
                    ? $"{ISOWeek.GetYear(localDate.ToDateTime(TimeOnly.MinValue)):0000}-W{ISOWeek.GetWeekOfYear(localDate.ToDateTime(TimeOnly.MinValue)):00}"
                    : $"{localDate.Year:0000}-{localDate.Month:00}";
            })
            .ToArray();
        return RatioValue(groups.Count(group => group.Sum(trade => trade.GrossPnl) > 0m), groups.Length);
    }

    private static decimal? DirectionWinRate(IReadOnlyList<Trade> trades, string direction)
    {
        var directionTrades = trades.Where(trade => trade.Direction.Equals(direction, StringComparison.OrdinalIgnoreCase)).ToArray();
        return directionTrades.Length == 0 ? null : RatioValue(directionTrades.Count(trade => trade.GrossPnl > 0m), directionTrades.Length);
    }

    private static decimal? Gini(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return null;
        var ordered = values.Where(value => value >= 0m).OrderBy(value => value).ToArray();
        var total = ordered.Sum();
        return total <= 0m ? null : 2m * ordered.Select((value, index) => (index + 1m) * value).Sum() / (ordered.Length * total) - (ordered.Length + 1m) / ordered.Length;
    }

    private static string? PercentileSummary(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return null;
        return $"{Quantile(values, .50m):0.##} · {Quantile(values, .75m):0.##} · {Quantile(values, .90m):0.##} · {Quantile(values, .95m):0.##}";
    }

    private static decimal? Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return null;
        var ordered = values.OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2m : ordered[middle];
    }

    private static decimal Quantile(IReadOnlyList<decimal> values, decimal quantile)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        var position = (ordered.Length - 1) * quantile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return ordered[lower];
        var fraction = position - lower;
        return ordered[lower] + (ordered[upper] - ordered[lower]) * fraction;
    }

    private static decimal SampleStd(IReadOnlyList<decimal> values)
    {
        if (values.Count < 2) return 0m;
        var mean = values.Average();
        var variance = values.Sum(value => (value - mean) * (value - mean)) / (values.Count - 1);
        return (decimal)Math.Sqrt((double)variance);
    }

    private static decimal? Covariance(IReadOnlyList<decimal> first, IReadOnlyList<decimal> second)
    {
        if (first.Count < 2 || first.Count != second.Count) return null;
        var firstMean = first.Average();
        var secondMean = second.Average();
        return first.Zip(second, (x, y) => (x - firstMean) * (y - secondMean)).Sum() / (first.Count - 1);
    }

    private static decimal Variance(IReadOnlyList<decimal> values) => SampleStd(values) is var standardDeviation && standardDeviation > 0m ? standardDeviation * standardDeviation : 0m;

    private static decimal? Correlation(IReadOnlyList<decimal> first, IReadOnlyList<decimal> second)
    {
        var covariance = Covariance(first, second);
        var denominator = SampleStd(first) * SampleStd(second);
        return covariance.HasValue && denominator > 0m ? covariance.Value / denominator : null;
    }

    private static int MaxStreak(IReadOnlyList<decimal> values, bool positive)
    {
        var current = 0;
        var maximum = 0;
        foreach (var value in values)
        {
            if (positive ? value > 0m : value < 0m) current++;
            else current = 0;
            maximum = Math.Max(maximum, current);
        }
        return maximum;
    }

    private static decimal? SafeDivide(decimal numerator, decimal denominator) => denominator == 0m ? null : numerator / denominator;

    private static decimal RatioValue(int numerator, int denominator) => denominator == 0 ? 0m : (decimal)numerator / denominator;

    private static decimal? RatioValue(int? numerator, int denominator) => numerator.HasValue && denominator != 0 ? (decimal)numerator.Value / denominator : null;

    private static decimal? ExcursionCurrency(Trade trade, decimal? points)
    {
        if (!points.HasValue) return null;
        var quantity = Math.Max(1, trade.ClosedQuantity > 0 ? trade.ClosedQuantity : trade.Quantity);
        var pointValue = trade.PointValue > 0m ? trade.PointValue : 1m;
        return Math.Abs(points.Value) * quantity * pointValue;
    }

    private static decimal? RiskCurrency(Trade trade)
    {
        if (trade.InitialRiskCurrency is > 0m) return trade.InitialRiskCurrency;
        if (trade.InitialRiskPoints is > 0m)
        {
            var quantity = Math.Max(1, trade.ClosedQuantity > 0 ? trade.ClosedQuantity : trade.Quantity);
            var pointValue = trade.PointValue > 0m ? trade.PointValue : 1m;
            return trade.InitialRiskPoints.Value * quantity * pointValue;
        }
        return null;
    }

    private static DateOnly DateInZone(DateTimeOffset value, TimeZoneInfo timeZone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, timeZone).DateTime);

    private static TearSheetIndicator Money(string label, decimal? value, string note, string currency, string? forcedTone = null)
    {
        _ = currency;
        return value.HasValue
            ? CreateIndicator(label, value.Value.ToString("C2", UiCulture), note, forcedTone ?? Tone(value.Value), value.Value)
            : Missing(label, note);
    }

    private static TearSheetIndicator Drawdown(string label, decimal? value, string note, string currency)
    {
        _ = currency;
        if (!value.HasValue) return Missing(label, note);
        var formatted = value.Value == 0m
            ? value.Value.ToString("C2", UiCulture)
            : $"-{Math.Abs(value.Value).ToString("C2", UiCulture)}";
        return CreateIndicator(label, formatted, note, value.Value == 0m ? "neutral" : "negative", value.Value);
    }

    private static TearSheetIndicator Percent(string label, decimal? value, string note, decimal? positiveAt = null)
    {
        if (!value.HasValue) return Missing(label, note);
        var tone = positiveAt.HasValue ? (value.Value >= positiveAt.Value ? "positive" : "negative") : Tone(value.Value);
        return CreateIndicator(label, $"{value.Value * 100m:0.0}%", note, tone, value.Value);
    }

    private static TearSheetIndicator PercentPoints(string label, decimal? value, string note)
    {
        if (!value.HasValue) return Missing(label, note);
        return CreateIndicator(label, $"{value.Value:0.00}%", note, Tone(value.Value), value.Value);
    }

    private static TearSheetIndicator Ratio(string label, decimal? value, string note, string suffix = "")
    {
        return value.HasValue
            ? CreateIndicator(label, $"{value.Value:0.00}{suffix}", note, Tone(value.Value), value.Value)
            : Missing(label, note);
    }

    private static TearSheetIndicator Number(string label, int value, string note)
        => CreateIndicator(label, value.ToString("N0", UiCulture), note);

    private static TearSheetIndicator Number(string label, decimal? value, string note, string format)
        => value.HasValue ? CreateIndicator(label, value.Value.ToString(format, UiCulture), note, "neutral", value.Value) : Missing(label, note);

    private static TearSheetIndicator Points(string label, decimal? value, string note)
        => value.HasValue ? CreateIndicator(label, $"{value.Value:0.00} pts", note, Tone(value.Value), value.Value) : Missing(label, note);

    private static TearSheetIndicator Duration(string label, double? seconds, string note)
        => seconds.HasValue ? CreateIndicator(label, FormatDuration(seconds.Value), note) : Missing(label, note);

    private static TearSheetIndicator Days(string label, decimal? days, string note)
        => days.HasValue ? CreateIndicator(label, $"{days.Value:0.#}d", note) : Missing(label, note);

    private static TearSheetIndicator Text(string label, string? value, string note, string tone = "neutral")
        => string.IsNullOrWhiteSpace(value) ? Missing(label, note) : CreateIndicator(label, value, note, tone);

    private static TearSheetIndicator Missing(string label, string note) => CreateIndicator(label, "—", note, "muted");

    private static TearSheetIndicator CreateIndicator(string label, string value, string note, string tone = "neutral", decimal? numericValue = null)
    {
        var definition = IndicatorDefinitions.TryGetValue(label, out var match) ? match : null;
        var scale = numericValue is { } number && definition?.Scale is { } scaleFactory
            ? scaleFactory(number)
            : null;
        return new TearSheetIndicator(label, value, note, tone, definition?.Help, scale);
    }

    private static TearSheetIndicatorScale Gauge(string cssClass, decimal value, decimal min, decimal max, Func<decimal, string> tier)
    {
        var clamped = Math.Clamp(value, min, max);
        var position = max == min ? 0m : (clamped - min) / (max - min) * 100m;
        return new TearSheetIndicatorScale(cssClass, position.ToString("0.#", CultureInfo.InvariantCulture), tier(value));
    }

    private static string Tone(decimal value) => value > 0m ? "positive" : value < 0m ? "negative" : "neutral";

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60) return $"{seconds:0}s";
        if (seconds < 3600) return $"{seconds / 60:0.#}m";
        if (seconds < 86400) return $"{seconds / 3600:0.#}h";
        return $"{seconds / 86400:0.#}d";
    }

    private enum Period
    {
        Week,
        Month
    }

    private sealed class ScSubsetStats
    {
        public decimal? ClosedPnl { get; init; }
        public decimal? TotalProfit { get; init; }
        public decimal? TotalLoss { get; init; }
        public decimal? ProfitFactor { get; init; }
        public decimal? HighestCumulativeProfit { get; init; }
        public decimal? LowestCumulativeLoss { get; init; }
        public decimal? MaximumRunup { get; init; }
        public decimal? MaximumDrawdown { get; init; }
        public decimal? MaximumOpenProfit { get; init; }
        public decimal? MaximumOpenLoss { get; init; }
        public decimal? AverageOpenProfit { get; init; }
        public decimal? AverageOpenLoss { get; init; }
        public decimal? AverageWinningOpenProfit { get; init; }
        public decimal? AverageWinningOpenLoss { get; init; }
        public decimal? AverageLosingOpenProfit { get; init; }
        public decimal? AverageLosingOpenLoss { get; init; }
        public decimal? ExchangeFees { get; init; }
        public decimal? NfaFees { get; init; }
        public decimal? ClearingFees { get; init; }
        public decimal? TotalCommissions { get; init; }
        public int? TotalTrades { get; init; }
        public decimal? PercentProfitable { get; init; }
        public int? WinningTrades { get; init; }
        public int? LosingTrades { get; init; }
        public int? LongTrades { get; init; }
        public int? ShortTrades { get; init; }
        public decimal? AverageTradePnl { get; init; }
        public decimal? AverageWinningTrade { get; init; }
        public decimal? AverageLosingTrade { get; init; }
        public decimal? AverageProfitFactor { get; init; }
        public decimal? LargestWinner { get; init; }
        public decimal? LargestLoser { get; init; }
        public decimal? LargestWinnerPercent { get; init; }
        public decimal? LargestLoserPercent { get; init; }
        public int? MaxConsecutiveWinners { get; init; }
        public int? MaxConsecutiveLosers { get; init; }
        public double? AverageTimeSeconds { get; init; }
        public double? AverageWinningTimeSeconds { get; init; }
        public double? AverageLosingTimeSeconds { get; init; }
        public double? LongestWinningTimeSeconds { get; init; }
        public double? LongestLosingTimeSeconds { get; init; }
        public int? TotalQuantity { get; init; }
        public int? WinningQuantity { get; init; }
        public int? LosingQuantity { get; init; }
        public decimal? AverageQuantity { get; init; }
        public decimal? AverageWinningQuantity { get; init; }
        public decimal? AverageLosingQuantity { get; init; }
        public int? LargestTradeQuantity { get; init; }
        public decimal? LastTradePnl { get; init; }
        public decimal? Expectancy { get; init; }
    }

    private sealed record ScCumulativeStats(
        decimal HighestCumulativeProfit,
        decimal LowestCumulativeLoss,
        decimal MaximumRunup,
        decimal MaximumDrawdown);

    private sealed record RiskDisciplineSample(
        Trade Trade,
        decimal MaeR,
        decimal ResultR);

    private sealed class TradeStats
    {
        public int TradeCount { get; init; }
        public decimal TotalGrossPnl { get; init; }
        public decimal TotalNetPnl { get; init; }
        public decimal TotalFees { get; init; }
        public decimal ExchangeFees { get; init; }
        public decimal NfaFees { get; init; }
        public decimal ClearingFees { get; init; }
        public decimal Points { get; init; }
        public decimal WinRate { get; init; }
        public decimal WinRateBe { get; init; }
        public decimal? AvgWin { get; init; }
        public decimal? AvgLoss { get; init; }
        public decimal? ProfitFactor { get; init; }
        public decimal? Expectancy { get; init; }
        public decimal? Sqn { get; init; }
        public decimal? BiggestWinner { get; init; }
        public decimal? BiggestLoser { get; init; }
        public decimal? PayoffRatio { get; init; }
        public decimal? GainToPain { get; init; }
        public int MaxConsecutiveWins { get; init; }
        public int MaxConsecutiveLosses { get; init; }
        public decimal? MaxDrawdown { get; init; }
        public decimal? MaxDrawdownPercent { get; init; }
        public decimal? UlcerIndex { get; init; }
        public decimal? Sharpe { get; init; }
        public decimal? Sortino { get; init; }
        public decimal? Omega { get; init; }
        public decimal? UpsidePotential { get; init; }
        public decimal? Cvar95 { get; init; }
        public decimal? CvarPercent { get; init; }
        public decimal? Calmar { get; init; }
        public decimal? Sterling { get; init; }
        public decimal? V2 { get; init; }
        public decimal? RecoveryFactor { get; init; }
        public double? AvgDurationSeconds { get; init; }
        public double? AvgWinnerDurationSeconds { get; init; }
        public double? AvgLoserDurationSeconds { get; init; }
        public double? ShortestTradeSeconds { get; init; }
        public double? LongestTradeSeconds { get; init; }
        public decimal? AvgMfe { get; init; }
        public decimal? AvgMae { get; init; }
        public decimal? MfeCapture { get; init; }
        public decimal? AvgMaeWinners { get; init; }
        public decimal? AvgMfeMaeRatio { get; init; }
        public int LongCount { get; init; }
        public int ShortCount { get; init; }
        public decimal? LongWinRate { get; init; }
        public decimal? ShortWinRate { get; init; }
        public string? MaePercentiles { get; init; }
        public string? MfePercentiles { get; init; }
        public string? MaeRPercentiles { get; init; }
        public string? MfeRPercentiles { get; init; }
        public int TradingDays { get; init; }
        public decimal PercentProfitableDays { get; init; }
        public decimal PercentProfitableWeeks { get; init; }
        public decimal PercentProfitableMonths { get; init; }
        public decimal? AvgTradesPerDay { get; init; }
        public decimal? AvgDailyNetPnl { get; init; }
        public decimal? BestDay { get; init; }
        public decimal? WorstDay { get; init; }
        public decimal? Kelly { get; init; }
        public decimal? BreakevenWinRate { get; init; }
        public decimal? ConcentrationTop5 { get; init; }
        public decimal? Top1ProfitShare { get; init; }
        public decimal? Top10ProfitShare { get; init; }
        public decimal? WinnerGini { get; init; }
        public decimal? RAverage { get; init; }
        public decimal? RMedian { get; init; }
        public decimal? TotalR { get; init; }
        public decimal? RPercentAtLeastOne { get; init; }
        public decimal? RPercentAtLeastTwo { get; init; }
        public int PositiveRCount { get; init; }
        public int NegativeRCount { get; init; }
        public int DrawdownEpisodeCount { get; init; }
        public decimal? LongestDrawdownDays { get; init; }
        public decimal? AvgDrawdownDays { get; init; }
        public decimal? MedianRecoveryDays { get; init; }
        public decimal? CurrentUnderwaterDays { get; init; }
        public decimal? PercentTimeAtHighs { get; init; }
        public decimal? DaysSinceLastEquityHigh { get; init; }
    }

    private sealed class ExecutionStats
    {
        public int TotalOrders { get; init; }
        public decimal? FillRate { get; init; }
        public decimal? CancelRate { get; init; }
        public decimal? ModifyRate { get; init; }
        public decimal? PartialFillRate { get; init; }
        public decimal? AvgEntryChase { get; init; }
        public decimal? AvgExitChase { get; init; }
        public decimal? MaxEntryChase { get; init; }
        public decimal? MaxExitChase { get; init; }
        public decimal? AvgModificationsPerOrder { get; init; }
        public decimal? PercentChasedEntry { get; init; }
        public decimal? PercentChasedExit { get; init; }
    }

    private sealed class BenchmarkStats
    {
        public string? Ticker { get; init; }
        public decimal? StrategyReturnPercent { get; init; }
        public decimal? BenchmarkReturnPercent { get; init; }
        public decimal? AlphaPercent { get; init; }
        public decimal? AnnualizedAlpha { get; init; }
        public decimal? Beta { get; init; }
        public decimal? Correlation { get; init; }
        public decimal? Treynor { get; init; }
        public decimal? M2 { get; init; }
    }

    private sealed record DailySnapshot(DateOnly Date, decimal GrossPnl, decimal NetPnl, int TradeCount);
    private sealed record EquitySnapshot(DateOnly Date, decimal Balance);
    private sealed record BenchmarkSnapshot(DateOnly Date, decimal Value);
    private sealed record DrawdownStatsValue(decimal? MaxDepth = null, decimal? MaxDepthPercent = null, decimal? UlcerIndex = null);
    private sealed record DrawdownDiagnosticsValue(
        int EpisodeCount = 0,
        decimal? LongestDays = null,
        decimal? AverageDays = null,
        decimal? MedianRecoveryDays = null,
        decimal? CurrentUnderwaterDays = null,
        decimal? PercentTimeAtHighs = null,
        decimal? DaysSinceHigh = null);

    private sealed class DrawdownEpisodeBuilder
    {
        public DrawdownEpisodeBuilder(DateOnly startDate, DateOnly troughDate, decimal troughBalance)
        {
            StartDate = startDate;
            TroughDate = troughDate;
            TroughBalance = troughBalance;
        }

        public DateOnly StartDate { get; }
        public DateOnly TroughDate { get; private set; }
        public decimal TroughBalance { get; private set; }

        public DrawdownEpisodeBuilder WithTrough(DateOnly date, decimal balance)
        {
            TroughDate = date;
            TroughBalance = balance;
            return this;
        }

        public DrawdownEpisodeValue Recover(DateOnly date) => new(Math.Max(0, date.DayNumber - StartDate.DayNumber), Math.Max(0, date.DayNumber - TroughDate.DayNumber));

        public DrawdownEpisodeValue Close(DateOnly date) => new(Math.Max(0, date.DayNumber - StartDate.DayNumber), null);
    }

    private sealed record DrawdownEpisodeValue(int DurationDays, int? RecoveryDays);
}

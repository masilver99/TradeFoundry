using System.Globalization;
using TradeFoundry.Core;

namespace TradeFoundry.Services;

public static class EdgePersistenceCalculator
{
    public const int MinimumSegmentTrades = 10;
    public static IReadOnlyList<int> Windows { get; } = new[] { 25, 50, 100 };

    public static EdgePersistenceMetrics Build(IEnumerable<Trade> source, string? timeZoneId = null)
    {
        var observations = source
            .Where(trade => trade.ExitUtc.HasValue)
            .OrderBy(trade => trade.ExitUtc)
            .ThenBy(trade => trade.Sequence)
            .Select(CreateObservation)
            .ToArray();

        var rolling = Windows
            .Select(window => BuildRollingWindow(observations, window))
            .Where(window => window.Points.Count > 0)
            .ToArray();
        var blocks = Windows
            .Select(window => BuildBlockSet(observations, window))
            .Where(window => window.Blocks.Count > 0)
            .ToArray();
        var comparisons = Windows
            .Select(window => BuildComparison(observations, window))
            .Where(comparison => comparison is not null)
            .Select(comparison => comparison!)
            .ToArray();
        var robustness = BuildRobustness(observations);
        var risk = BuildRiskStability(observations, comparisons);
        var dimensions = BuildSegmentDimensions(observations, timeZoneId);
        var expectancy = BuildExpectancyPersistence(observations, rolling, blocks, comparisons);
        var segmentConsistency = BuildSegmentConsistency(dimensions);
        var score = BuildScore(observations.Length, expectancy, risk, robustness, segmentConsistency);
        var interpretation = BuildInterpretation(observations.Length, expectancy, risk, robustness, blocks, comparisons, dimensions);

        return new EdgePersistenceMetrics
        {
            CompletedTradeCount = observations.Length,
            TotalGrossPnl = observations.Length == 0 ? null : observations.Sum(observation => observation.GrossPnl),
            TotalNetPnl = observations.Length == 0 ? null : observations.Sum(observation => observation.NetPnl),
            NormalizedTradeCount = observations.Count(observation => observation.ResultR.HasValue),
            RiskObservedTradeCount = observations.Count(observation => observation.Mae.HasValue),
            RiskUnitObservedTradeCount = observations.Count(observation => observation.MaeR.HasValue),
            UsesRiskUnits = UsesRiskUnits(observations),
            Rolling = rolling,
            Blocks = blocks,
            RecentComparisons = comparisons,
            Robustness = robustness,
            RiskStability = risk,
            SegmentDimensions = dimensions,
            ExpectancyPersistence = expectancy,
            SegmentConsistency = segmentConsistency,
            Score = score,
            Interpretation = interpretation,
            Confidence = ConfidenceFor(observations.Length)
        };
    }

    private static EdgePersistenceObservation CreateObservation(Trade trade)
    {
        var mae = ExcursionCurrency(trade, trade.MaePoints);
        var risk = RiskCurrency(trade);
        var maeR = mae.HasValue && risk is > 0m ? mae.Value / risk.Value : (decimal?)null;
        var resultR = trade.RMultiple ?? (risk is > 0m ? trade.GrossPnl / risk.Value : (decimal?)null);
        return new EdgePersistenceObservation(
            trade,
            trade.GrossPnl,
            trade.NetPnl,
            mae,
            risk,
            maeR,
            resultR,
            trade.ExitUtc!.Value);
    }

    private static EdgePersistenceRollingWindow BuildRollingWindow(
        IReadOnlyList<EdgePersistenceObservation> observations,
        int window)
    {
        if (observations.Count < window)
            return new EdgePersistenceRollingWindow(window, Array.Empty<EdgePersistenceRollingPoint>());

        var points = Enumerable.Range(window - 1, observations.Count - window + 1)
            .Select(endIndex =>
            {
                var values = observations.Skip(endIndex - window + 1).Take(window).ToArray();
                return new EdgePersistenceRollingPoint(
                    endIndex + 1,
                    observations[endIndex].ExitUtc,
                    BuildTradeMetrics(values));
            })
            .ToArray();
        return new EdgePersistenceRollingWindow(window, points);
    }

    private static EdgePersistenceBlockSet BuildBlockSet(
        IReadOnlyList<EdgePersistenceObservation> observations,
        int window)
    {
        var blocks = new List<EdgePersistenceTradeBlock>();
        for (var start = 0; start + window <= observations.Count; start += window)
        {
            var values = observations.Skip(start).Take(window).ToArray();
            blocks.Add(new EdgePersistenceTradeBlock(
                blocks.Count + 1,
                start + 1,
                start + window,
                values[0].ExitUtc,
                values[^1].ExitUtc,
                BuildTradeMetrics(values)));
        }

        return new EdgePersistenceBlockSet(window, blocks);
    }

    private static EdgePersistenceComparison? BuildComparison(
        IReadOnlyList<EdgePersistenceObservation> observations,
        int window)
    {
        if (observations.Count < window * 2)
            return null;

        var previous = BuildTradeMetrics(observations.Skip(observations.Count - window * 2).Take(window).ToArray());
        var recent = BuildTradeMetrics(observations.Skip(observations.Count - window).Take(window).ToArray());
        return new EdgePersistenceComparison(window, recent, previous, ComparisonTrend(recent, previous));
    }

    private static EdgePersistenceTradeMetrics BuildTradeMetrics(IReadOnlyList<EdgePersistenceObservation> observations)
    {
        if (observations.Count == 0)
            return new EdgePersistenceTradeMetrics();

        var gross = observations.Select(observation => observation.GrossPnl).ToArray();
        var winners = gross.Where(value => value > 0m).ToArray();
        var losers = gross.Where(value => value < 0m).ToArray();
        var mae = observations
            .Select(observation => observation.Mae)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();

        return new EdgePersistenceTradeMetrics
        {
            TradeCount = observations.Count,
            GrossPnl = observations.Sum(observation => observation.GrossPnl),
            NetPnl = observations.Sum(observation => observation.NetPnl),
            Expectancy = gross.Average(),
            ProfitFactor = losers.Length == 0 ? null : SafeDivide(winners.Sum(), Math.Abs(losers.Sum())),
            WinRate = (decimal)winners.Length / observations.Count,
            AverageWinner = winners.Length == 0 ? null : winners.Average(),
            AverageLoss = losers.Length == 0 ? null : losers.Average(),
            MedianLoss = losers.Length == 0 ? null : Quantile(losers, .50m),
            LargestLoss = losers.Length == 0 ? null : losers.Min(),
            MedianMae = Median(mae),
            MaeP75 = mae.Length == 0 ? null : Quantile(mae, .75m),
            MaeP90 = mae.Length == 0 ? null : Quantile(mae, .90m),
            MaximumMae = mae.Length == 0 ? null : mae.Max()
        };
    }

    private static EdgePersistenceExpectancyPersistence BuildExpectancyPersistence(
        IReadOnlyList<EdgePersistenceObservation> observations,
        IReadOnlyList<EdgePersistenceRollingWindow> rolling,
        IReadOnlyList<EdgePersistenceBlockSet> blocks,
        IReadOnlyList<EdgePersistenceComparison> comparisons)
    {
        var rollingSets = rolling.Where(window => window.Points.Count >= 2).ToArray();
        var blockSets = blocks.Where(window => window.Blocks.Count >= 2).ToArray();
        var positiveRollingRate = rollingSets.Length == 0
            ? (decimal?)null
            : rollingSets.Average(window => (decimal)window.Points.Count(point => point.Expectancy > 0m) / window.Points.Count);
        var positiveBlockRate = blockSets.Length == 0
            ? (decimal?)null
            : blockSets.Average(window => (decimal)window.PositiveExpectancyBlockCount / window.Blocks.Count);
        var trend = comparisons.Count == 0 ? .50m : comparisons.Average(comparison => TrendScore(comparison.Trend));
        var fallbackRate = observations.Count == 0 ? 0m : observations.Average(observation => observation.GrossPnl) > 0m ? 1m : 0m;
        var rollingRate = positiveRollingRate ?? fallbackRate;
        var blockRate = positiveBlockRate ?? fallbackRate;
        var blockCount = blockSets.Sum(set => set.Blocks.Count);
        var reliability = blockCount == 0 ? .35m : Math.Min(1m, blockCount / 4m);
        var score = observations.Count == 0
            ? (decimal?)null
            : 100m * (.55m * blockRate + .30m * rollingRate + .15m * trend) * (.50m + .50m * reliability);

        return new EdgePersistenceExpectancyPersistence
        {
            PositiveRollingRate = positiveRollingRate,
            PositiveBlockRate = positiveBlockRate,
            PositiveExpectancyBlockCount = blocks.Sum(set => set.PositiveExpectancyBlockCount),
            TotalBlockCount = blocks.Sum(set => set.Blocks.Count),
            RecentTrend = comparisons.Count == 0 ? "Not enough history" : TrendLabel(comparisons.Average(comparison => TrendScore(comparison.Trend))),
            Score = score
        };
    }

    private static EdgePersistenceRiskStability BuildRiskStability(
        IReadOnlyList<EdgePersistenceObservation> observations,
        IReadOnlyList<EdgePersistenceComparison> comparisons)
    {
        if (observations.Count == 0)
            return new EdgePersistenceRiskStability();

        var mae = observations
            .Select(observation => observation.Mae)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        var riskUnitMae = observations
            .Select(observation => observation.MaeR)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        var usesRiskUnits = UsesRiskUnits(observations);
        var fallbackScale = Median(observations.Select(observation => Math.Abs(observation.GrossPnl)).Where(value => value > 0m).ToArray()) ?? 1m;
        var normalizedMae = observations
            .Select(observation => usesRiskUnits ? observation.MaeR : observation.Mae is { } value ? value / fallbackScale : (decimal?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        var normalizedLoss = observations
            .Where(observation => observation.GrossPnl < 0m)
            .Select(observation => usesRiskUnits && observation.ResultR is { } resultR ? Math.Abs(resultR) : Math.Abs(observation.GrossPnl) / fallbackScale)
            .ToArray();

        var p90Mae = normalizedMae.Length == 0 ? (decimal?)null : Quantile(normalizedMae, .90m);
        var maximumMae = normalizedMae.Length == 0 ? (decimal?)null : normalizedMae.Max();
        var p90Loss = normalizedLoss.Length == 0 ? (decimal?)null : Quantile(normalizedLoss, .90m);
        var maximumLoss = normalizedLoss.Length == 0 ? (decimal?)null : normalizedLoss.Max();
        var maeScore = TailScore(p90Mae, .50m, 1.00m, 2.50m);
        var lossScore = TailScore(p90Loss, 1.00m, 2.00m, 5.00m);
        var maxMaeScore = TailScore(maximumMae, 1.00m, 2.00m, 5.00m);
        var maxLossScore = normalizedLoss.Length == 0 ? 100m : TailScore(maximumLoss, 2.00m, 4.00m, 8.00m);
        var maeTrend = RiskTrend(observations, usesRiskUnits, fallbackScale, useMae: true);
        var lossTrend = RiskTrend(observations, usesRiskUnits, fallbackScale, useMae: false);
        var trendScore = (TrendScore(maeTrend) + TrendScore(lossTrend)) / 2m;
        var coverage = (decimal)mae.Length / observations.Count;
        var score = (.35m * maeScore + .25m * lossScore + .20m * maxMaeScore + .15m * maxLossScore + .05m * trendScore * 100m) * coverage;

        return new EdgePersistenceRiskStability
        {
            MaeObservedTradeCount = mae.Length,
            MaeCoverage = coverage,
            RiskUnitCoverage = (decimal)riskUnitMae.Length / observations.Count,
            MedianMae = Median(mae),
            MaeP75 = mae.Length == 0 ? null : Quantile(mae, .75m),
            MaeP90 = mae.Length == 0 ? null : Quantile(mae, .90m),
            MaximumMae = mae.Length == 0 ? null : mae.Max(),
            MedianLoss = Median(observations.Where(observation => observation.GrossPnl < 0m).Select(observation => observation.GrossPnl).ToArray()),
            LossP90 = normalizedLoss.Length == 0 ? null : -Quantile(normalizedLoss, .90m) * (usesRiskUnits ? 1m : fallbackScale),
            MaximumLoss = normalizedLoss.Length == 0 ? null : -normalizedLoss.Max() * (usesRiskUnits ? 1m : fallbackScale),
            NormalizedMaeP90 = p90Mae,
            NormalizedLossP90 = p90Loss,
            RiskScore = score,
            RiskUnitLabel = usesRiskUnits ? "initial-risk units (R)" : "relative to median absolute gross result",
            UsesRiskUnits = usesRiskUnits,
            MaeTrend = maeTrend,
            LossTrend = lossTrend,
            RecentComparisonAvailable = comparisons.Any(comparison => comparison.Window == 25)
        };
    }

    private static EdgePersistenceRobustnessMetrics BuildRobustness(IReadOnlyList<EdgePersistenceObservation> observations)
    {
        if (observations.Count == 0)
            return new EdgePersistenceRobustnessMetrics();

        var best = new[] { 1, 3, 5, 10 }
            .Where(count => count <= observations.Count)
            .Select(count => StressPoint(observations, count, removeBest: true))
            .ToArray();
        var worst = new[] { 1, 3, 5, 10 }
            .Where(count => count <= observations.Count)
            .Select(count => StressPoint(observations, count, removeBest: false))
            .ToArray();
        var winners = observations.Select(observation => observation.NetPnl).Where(value => value > 0m).OrderByDescending(value => value).ToArray();
        var winnerProfit = winners.Sum();
        var losses = observations.Select(observation => observation.NetPnl).Where(value => value < 0m).Select(Math.Abs).OrderByDescending(value => value).ToArray();
        var totalLoss = losses.Sum();
        var top1Count = Math.Max(1, (int)Math.Ceiling(observations.Count * .01m));
        var top5Count = Math.Max(1, (int)Math.Ceiling(observations.Count * .05m));
        var top10Count = Math.Max(1, (int)Math.Ceiling(observations.Count * .10m));
        var winnerConcentration = winnerProfit > 0m
            ? new EdgePersistenceWinnerConcentration(
                SafeDivide(winners.Take(Math.Min(top1Count, winners.Length)).Sum(), winnerProfit),
                SafeDivide(winners.Take(Math.Min(top5Count, winners.Length)).Sum(), winnerProfit),
                SafeDivide(winners.Take(Math.Min(top10Count, winners.Length)).Sum(), winnerProfit),
                top1Count,
                top5Count,
                top10Count)
            : new EdgePersistenceWinnerConcentration(null, null, null, top1Count, top5Count, top10Count);
        var lossConcentration = totalLoss > 0m ? SafeDivide(losses.Take(Math.Min(3, losses.Length)).Sum(), totalLoss) : null;
        var actual = observations.Sum(observation => observation.NetPnl);
        var withoutBest3 = best.FirstOrDefault(point => point.RemovedCount == 3)?.NetPnl;
        var survivalValues = best
            .Where(point => point.RemovedCount is 3 or 5 or 10)
            .Select(point => actual > 0m ? (point.NetPnl >= 0m ? 1m : 0m) : point.NetPnl >= 0m ? .50m : 0m)
            .ToArray();
        var survival = survivalValues.Length == 0 ? 0m : survivalValues.Average();
        var concentrationScore = winnerConcentration.Top5ProfitShare.HasValue ? 1m - Math.Min(1m, winnerConcentration.Top5ProfitShare.Value) : 0m;
        var lossScore = lossConcentration.HasValue ? 1m - Math.Min(1m, lossConcentration.Value) : 1m;
        var score = 100m * (.45m * survival + .35m * concentrationScore + .20m * lossScore);
        var result = new EdgePersistenceRobustnessMetrics
        {
            ActualGrossPnl = observations.Sum(observation => observation.GrossPnl),
            ActualNetPnl = actual,
            WithoutBest = best,
            WithoutWorst = worst,
            WinnerConcentration = winnerConcentration,
            LargestLossShare = lossConcentration,
            Score = score
        };

        var observationsText = new List<string>();
        if (actual > 0m && withoutBest3.HasValue)
        {
            if (withoutBest3.Value < 0m)
                observationsText.Add("Removing the three largest winners turns net P&L negative.");
            else
                observationsText.Add($"{withoutBest3.Value / actual:P0} of net P&L remains after removing the three largest winners.");
        }
        if (winnerConcentration.Top5ProfitShare is >= .60m)
            observationsText.Add($"The top {winnerConcentration.Top5TradeCount} positive trades supply {winnerConcentration.Top5ProfitShare:P0} of positive net P&L.");
        if (lossConcentration is >= .60m)
            observationsText.Add($"The three largest losses supply {lossConcentration:P0} of total losing net P&L.");
        if (observationsText.Count == 0)
            observationsText.Add("No single winner or small loss cluster dominates the available net result at the selected stress levels.");
        result.Observations = observationsText;
        return result;
    }

    private static EdgePersistenceStressPoint StressPoint(
        IReadOnlyList<EdgePersistenceObservation> observations,
        int count,
        bool removeBest)
    {
        var ordered = observations
            .OrderByDescending(observation => removeBest ? observation.NetPnl : -observation.NetPnl)
            .Take(count)
            .ToArray();
        var removed = ordered.Select(observation => observation.Trade.Id).ToHashSet();
        return new EdgePersistenceStressPoint(
            count,
            observations.Where(observation => !removed.Contains(observation.Trade.Id)).Sum(observation => observation.GrossPnl),
            observations.Where(observation => !removed.Contains(observation.Trade.Id)).Sum(observation => observation.NetPnl));
    }

    private static IReadOnlyList<EdgePersistenceSegmentDimension> BuildSegmentDimensions(
        IReadOnlyList<EdgePersistenceObservation> observations,
        string? timeZoneId)
    {
        if (observations.Count == 0)
            return Array.Empty<EdgePersistenceSegmentDimension>();

        var timeZone = TimeZoneCatalog.Resolve(timeZoneId);
        var definitions = new (string Name, string Note, Func<EdgePersistenceObservation, string> Key)[]
        {
            ("Direction", "Long and short trades", observation => Label(observation.Trade.Direction, "Unknown")),
            ("Instrument", "Instrument root from the imported trade", observation => Label(observation.Trade.Instrument, InstrumentCatalog.ExtractRoot(observation.Trade.Symbol))),
            ("Session", "Entry-time buckets in the journal time zone", observation => SessionLabel(observation.Trade.EntryUtc, timeZone)),
            ("Day of week", "Entry day in the journal time zone", observation => TimeZoneInfo.ConvertTime(observation.Trade.EntryUtc, timeZone).DayOfWeek.ToString())
        };

        return definitions
            .Select(definition =>
            {
                var candidates = observations
                    .GroupBy(definition.Key, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var segments = candidates
                    .Where(group => group.Count() >= MinimumSegmentTrades)
                    .Select(group => new EdgePersistenceSegment(group.Key, BuildTradeMetrics(group.ToArray())))
                    .ToArray();
                return new EdgePersistenceSegmentDimension(
                    definition.Name,
                    definition.Note,
                    MinimumSegmentTrades,
                    candidates.Length,
                    candidates.Count(group => group.Count() < MinimumSegmentTrades),
                    segments);
            })
            .ToArray();
    }

    private static EdgePersistenceSegmentConsistency BuildSegmentConsistency(IReadOnlyList<EdgePersistenceSegmentDimension> dimensions)
    {
        var segments = dimensions.SelectMany(dimension => dimension.Segments).ToArray();
        if (segments.Length == 0)
        {
            return new EdgePersistenceSegmentConsistency
            {
                Score = 45m,
                ReliableSegmentCount = 0,
                PositiveSegmentCount = 0,
                Note = "No segment reached the minimum sample size; specialization is not scored from small groups."
            };
        }

        var positive = segments.Count(segment => segment.Expectancy > 0m);
        return new EdgePersistenceSegmentConsistency
        {
            Score = (decimal)positive / segments.Length * 100m,
            ReliableSegmentCount = segments.Length,
            PositiveSegmentCount = positive,
            Note = $"{positive} of {segments.Length} reliable segments have positive expectancy."
        };
    }

    private static EdgePersistenceScore BuildScore(
        int tradeCount,
        EdgePersistenceExpectancyPersistence expectancy,
        EdgePersistenceRiskStability risk,
        EdgePersistenceRobustnessMetrics robustness,
        EdgePersistenceSegmentConsistency segments)
    {
        if (tradeCount == 0)
            return new EdgePersistenceScore { Tier = "No Evidence / Unstable", Tone = "muted", Summary = "Completed trades are needed before persistence can be scored." };

        var components = new[]
        {
            new EdgePersistenceScoreComponent("Expectancy persistence", .40m, expectancy.Score ?? 0m, "Positive expectancy across sequential blocks and rolling samples."),
            new EdgePersistenceScoreComponent("Risk stability", .25m, risk.RiskScore ?? 0m, $"MAE and loss tails; {risk.RiskUnitLabel}."),
            new EdgePersistenceScoreComponent("Profit robustness", .20m, robustness.Score, "Stress result after removing the largest winners and losses."),
            new EdgePersistenceScoreComponent("Segment consistency", .15m, segments.Score, segments.Note)
        };
        var raw = components.Sum(component => component.Points * component.Weight);
        var cap = SampleScoreCap(tradeCount);
        var value = Math.Min(raw, cap);
        var capped = raw > cap;
        var rounded = Math.Round(value, 0, MidpointRounding.AwayFromZero);
        return new EdgePersistenceScore
        {
            Value = rounded,
            Tier = ScoreTier(rounded),
            Tone = ScoreTone(rounded),
            Components = components,
            CappedBySampleSize = capped,
            Summary = capped
                ? $"Weighted score {raw:0}/100, capped at {cap:0}/100 for the {tradeCount:N0}-trade evidence base."
                : "Weighted score across expectancy persistence, risk stability, profit robustness, and segment consistency."
        };
    }

    private static IReadOnlyList<string> BuildInterpretation(
        int tradeCount,
        EdgePersistenceExpectancyPersistence expectancy,
        EdgePersistenceRiskStability risk,
        EdgePersistenceRobustnessMetrics robustness,
        IReadOnlyList<EdgePersistenceBlockSet> blocks,
        IReadOnlyList<EdgePersistenceComparison> comparisons,
        IReadOnlyList<EdgePersistenceSegmentDimension> dimensions)
    {
        if (tradeCount == 0)
            return new[] { "Completed trades are needed before the journal can be evaluated for edge persistence." };

        var findings = new List<string>();
        var blockSet = new[] { 50, 25, 100 }
            .Select(window => blocks.FirstOrDefault(set => set.Window == window && set.Blocks.Count > 0))
            .FirstOrDefault(set => set is not null);
        if (blockSet is not null)
            findings.Add($"Positive expectancy in {blockSet.PositiveExpectancyBlockCount} of {blockSet.Blocks.Count} non-overlapping {blockSet.Window}-trade blocks.");
        else if (expectancy.PositiveBlockRate is { } blockRate)
            findings.Add($"Positive expectancy appears in {blockRate:P0} of the available non-overlapping blocks.");

        findings.Add(risk.MaeObservedTradeCount == 0
            ? "MAE evidence is missing, so risk stability is scored conservatively."
            : $"Median MAE is {risk.MedianMae:C2}; its recent trend is {risk.MaeTrend.ToLowerInvariant()}.");

        foreach (var observation in robustness.Observations.Take(2))
            findings.Add(observation);

        var comparison = comparisons.FirstOrDefault(item => item.Window == 50) ?? comparisons.FirstOrDefault();
        if (comparison is not null)
            findings.Add($"The most recent {comparison.Window} trades show {comparison.Trend.ToLowerInvariant()} expectancy versus the previous {comparison.Window} trades.");

        var weakSegment = dimensions
            .SelectMany(dimension => dimension.Segments.Select(segment => (Dimension: dimension.Name, Segment: segment)))
            .Where(item => item.Segment.Expectancy < 0m)
            .OrderBy(item => item.Segment.Expectancy)
            .FirstOrDefault();
        if (weakSegment.Segment is not null)
            findings.Add($"Reliable segment warning: {weakSegment.Dimension} · {weakSegment.Segment.Label} has negative expectancy across {weakSegment.Segment.TradeCount:N0} trades.");

        if (tradeCount < 25)
            findings.Add("The evidence base is below 25 completed trades; the score is not evidence of a repeatable edge.");
        return findings.Take(6).ToArray();
    }

    private static EdgePersistenceConfidence ConfidenceFor(int tradeCount)
        => tradeCount switch
        {
            0 => new EdgePersistenceConfidence("No evidence", "No completed trades", "muted"),
            < 25 => new EdgePersistenceConfidence("Insufficient evidence", "Heuristic label: fewer than 25 completed trades.", "negative"),
            < 50 => new EdgePersistenceConfidence("Very low confidence", "Heuristic label: 25–49 completed trades; results are highly sensitive to sampling variation.", "negative"),
            < 100 => new EdgePersistenceConfidence("Preliminary", "Heuristic label: 50–99 completed trades; persistence evidence is still developing.", "neutral"),
            < 250 => new EdgePersistenceConfidence("Moderate evidence", "Heuristic label: 100–249 completed trades; more evidence is available but not a guarantee.", "neutral"),
            _ => new EdgePersistenceConfidence("Stronger evidence", "Heuristic label: 250 or more completed trades; sample size is more informative, not conclusive.", "positive")
        };

    private static string RiskTrend(
        IReadOnlyList<EdgePersistenceObservation> observations,
        bool usesRiskUnits,
        decimal fallbackScale,
        bool useMae)
    {
        if (observations.Count < 50) return "Not enough history";
        var window = Math.Min(25, observations.Count / 2);
        var previous = observations.Skip(observations.Count - window * 2).Take(window).ToArray();
        var recent = observations.Skip(observations.Count - window).Take(window).ToArray();
        var previousValue = RiskTail(previous, usesRiskUnits, fallbackScale, useMae);
        var recentValue = RiskTail(recent, usesRiskUnits, fallbackScale, useMae);
        if (!previousValue.HasValue || !recentValue.HasValue) return "Not enough data";
        if (recentValue > previousValue * 1.15m) return "Deteriorating";
        if (recentValue < previousValue * .85m) return "Improving";
        return "Stable";
    }

    private static decimal? RiskTail(
        IReadOnlyList<EdgePersistenceObservation> observations,
        bool usesRiskUnits,
        decimal fallbackScale,
        bool useMae)
    {
        IEnumerable<decimal?> values = useMae
            ? observations.Select(observation => usesRiskUnits ? observation.MaeR : observation.Mae / fallbackScale)
            : observations.Where(observation => observation.GrossPnl < 0m).Select(observation => (decimal?)(usesRiskUnits && observation.ResultR is { } resultR ? Math.Abs(resultR) : Math.Abs(observation.GrossPnl) / fallbackScale));
        var array = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return array.Length == 0 ? null : Quantile(array, .90m);
    }

    private static string ComparisonTrend(EdgePersistenceTradeMetrics recent, EdgePersistenceTradeMetrics previous)
    {
        var directions = new List<int>
        {
            Direction(recent.Expectancy - previous.Expectancy, MoneyTolerance(previous.Expectancy), higherIsBetter: true),
            Direction(recent.ProfitFactor - previous.ProfitFactor, RatioTolerance(previous.ProfitFactor), higherIsBetter: true),
            Direction(recent.WinRate - previous.WinRate, .05m, higherIsBetter: true),
            Direction(recent.MedianMae - previous.MedianMae, MaeTolerance(previous.MedianMae), higherIsBetter: false),
            Direction(recent.AverageLoss - previous.AverageLoss, MoneyTolerance(previous.AverageLoss), higherIsBetter: true),
            Direction(recent.LargestLoss - previous.LargestLoss, MoneyTolerance(previous.LargestLoss), higherIsBetter: true),
            Direction(recent.AverageWinner - previous.AverageWinner, MoneyTolerance(previous.AverageWinner), higherIsBetter: true)
        };
        var average = directions.Average();
        return average > .20 ? "Improving" : average < -.20 ? "Deteriorating" : "Stable";
    }

    private static int Direction(decimal? delta, decimal tolerance, bool higherIsBetter)
    {
        if (!delta.HasValue || Math.Abs(delta.Value) <= tolerance) return 0;
        var sign = delta.Value > 0m ? 1 : -1;
        return higherIsBetter ? sign : -sign;
    }

    private static decimal MoneyTolerance(decimal? value) => Math.Max(.25m, Math.Abs(value ?? 0m) * .15m);
    private static decimal MaeTolerance(decimal? value) => Math.Max(.01m, Math.Abs(value ?? 0m) * .10m);
    private static decimal RatioTolerance(decimal? value) => Math.Max(.25m, Math.Abs(value ?? 0m) * .25m);

    private static decimal TrendScore(string trend)
        => trend.Equals("Improving", StringComparison.OrdinalIgnoreCase) ? 1m
            : trend.Equals("Deteriorating", StringComparison.OrdinalIgnoreCase) ? 0m
            : .50m;

    private static string TrendLabel(decimal value)
        => value > .66m ? "Improving" : value < .34m ? "Deteriorating" : "Stable";

    private static decimal TailScore(decimal? value, decimal good, decimal watch, decimal bad)
    {
        if (!value.HasValue) return 50m;
        if (value <= good) return 100m;
        if (value <= watch) return 100m - (value.Value - good) / (watch - good) * 40m;
        if (value <= bad) return 60m - (value.Value - watch) / (bad - watch) * 60m;
        return 0m;
    }

    private static decimal SampleScoreCap(int tradeCount)
        => tradeCount < 25 ? 39m : tradeCount < 50 ? 54m : tradeCount < 100 ? 69m : tradeCount < 250 ? 84m : 100m;

    private static string ScoreTier(decimal value)
        => value >= 85m ? "Very Strong"
            : value >= 70m ? "Strong"
            : value >= 55m ? "Developing"
            : value >= 40m ? "Weak"
            : "No Evidence / Unstable";

    private static string ScoreTone(decimal value)
        => value >= 70m ? "positive" : value >= 40m ? "neutral" : "negative";

    private static bool UsesRiskUnits(IReadOnlyList<EdgePersistenceObservation> observations)
        => observations.Count > 0 && (decimal)observations.Count(observation => observation.ResultR.HasValue && observation.MaeR.HasValue) / observations.Count >= .50m;

    private static string SessionLabel(DateTimeOffset entryUtc, TimeZoneInfo timeZone)
    {
        var hour = TimeZoneInfo.ConvertTime(entryUtc, timeZone).Hour;
        return hour < 10 ? "Morning open" : hour < 12 ? "Late morning" : hour < 14 ? "Midday" : "Afternoon";
    }

    private static string Label(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static EdgePersistenceStressPoint? FindStress(IReadOnlyList<EdgePersistenceStressPoint> points, int count)
        => points.FirstOrDefault(point => point.RemovedCount == count);

    private static decimal? Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return null;
        var ordered = values.OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2m : ordered[middle];
    }

    private static decimal Quantile(IReadOnlyList<decimal> values, decimal quantile)
    {
        if (values.Count == 0) return 0m;
        var ordered = values.OrderBy(value => value).ToArray();
        var position = (ordered.Length - 1) * quantile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return ordered[lower];
        var fraction = position - lower;
        return ordered[lower] + (ordered[upper] - ordered[lower]) * fraction;
    }

    private static decimal? SafeDivide(decimal numerator, decimal denominator) => denominator == 0m ? null : numerator / denominator;

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

    private sealed record EdgePersistenceObservation(
        Trade Trade,
        decimal GrossPnl,
        decimal NetPnl,
        decimal? Mae,
        decimal? Risk,
        decimal? MaeR,
        decimal? ResultR,
        DateTimeOffset ExitUtc);
}

public sealed class EdgePersistenceMetrics
{
    public int CompletedTradeCount { get; init; }
    public decimal? TotalGrossPnl { get; init; }
    public decimal? TotalNetPnl { get; init; }
    public int NormalizedTradeCount { get; init; }
    public int RiskObservedTradeCount { get; init; }
    public int RiskUnitObservedTradeCount { get; init; }
    public bool UsesRiskUnits { get; init; }
    public IReadOnlyList<EdgePersistenceRollingWindow> Rolling { get; init; } = Array.Empty<EdgePersistenceRollingWindow>();
    public IReadOnlyList<EdgePersistenceBlockSet> Blocks { get; init; } = Array.Empty<EdgePersistenceBlockSet>();
    public IReadOnlyList<EdgePersistenceComparison> RecentComparisons { get; init; } = Array.Empty<EdgePersistenceComparison>();
    public EdgePersistenceRobustnessMetrics Robustness { get; init; } = new();
    public EdgePersistenceRiskStability RiskStability { get; init; } = new();
    public IReadOnlyList<EdgePersistenceSegmentDimension> SegmentDimensions { get; init; } = Array.Empty<EdgePersistenceSegmentDimension>();
    public EdgePersistenceExpectancyPersistence ExpectancyPersistence { get; init; } = new();
    public EdgePersistenceSegmentConsistency SegmentConsistency { get; init; } = new();
    public EdgePersistenceScore Score { get; init; } = new();
    public IReadOnlyList<string> Interpretation { get; init; } = Array.Empty<string>();
    public EdgePersistenceConfidence Confidence { get; init; } = new("No evidence", "No completed trades", "muted");
}

public sealed class EdgePersistenceScore
{
    public decimal? Value { get; init; }
    public string DisplayValue => Value?.ToString("0", CultureInfo.CurrentCulture) ?? "—";
    public string Tier { get; init; } = "No Evidence / Unstable";
    public string Tone { get; init; } = "muted";
    public string Summary { get; init; } = string.Empty;
    public bool CappedBySampleSize { get; init; }
    public IReadOnlyList<EdgePersistenceScoreComponent> Components { get; init; } = Array.Empty<EdgePersistenceScoreComponent>();
}

public sealed record EdgePersistenceScoreComponent(string Label, decimal Weight, decimal Points, string Note)
{
    public decimal Contribution => Points * Weight;
}

public sealed class EdgePersistenceConfidence
{
    public EdgePersistenceConfidence(string label, string note, string tone)
    {
        Label = label;
        Note = note;
        Tone = tone;
    }

    public string Label { get; }
    public string Note { get; }
    public string Tone { get; }
}

public sealed class EdgePersistenceTradeMetrics
{
    public int TradeCount { get; init; }
    public decimal GrossPnl { get; init; }
    public decimal NetPnl { get; init; }
    public decimal? Expectancy { get; init; }
    public decimal? ProfitFactor { get; init; }
    public decimal? WinRate { get; init; }
    public decimal? MedianMae { get; init; }
    public decimal? MaeP75 { get; init; }
    public decimal? MaeP90 { get; init; }
    public decimal? MaximumMae { get; init; }
    public decimal? AverageWinner { get; init; }
    public decimal? AverageLoss { get; init; }
    public decimal? MedianLoss { get; init; }
    public decimal? LargestLoss { get; init; }
}

public sealed class EdgePersistenceRollingWindow
{
    public EdgePersistenceRollingWindow(int window, IReadOnlyList<EdgePersistenceRollingPoint> points)
    {
        Window = window;
        Points = points;
    }

    public int Window { get; }
    public IReadOnlyList<EdgePersistenceRollingPoint> Points { get; }
    public int PositiveExpectancyCount => Points.Count(point => point.Expectancy > 0m);
    public decimal? PositiveExpectancyRate => Points.Count == 0 ? null : (decimal)PositiveExpectancyCount / Points.Count;
    public EdgePersistenceRollingPoint? Latest => Points.Count == 0 ? null : Points[^1];
}

public sealed class EdgePersistenceRollingPoint
{
    public EdgePersistenceRollingPoint(int endTradeNumber, DateTimeOffset exitUtc, EdgePersistenceTradeMetrics metrics)
    {
        EndTradeNumber = endTradeNumber;
        ExitUtc = exitUtc;
        Metrics = metrics;
    }

    public int EndTradeNumber { get; }
    public DateTimeOffset ExitUtc { get; }
    public EdgePersistenceTradeMetrics Metrics { get; }
    public int TradeCount => Metrics.TradeCount;
    public decimal? Expectancy => Metrics.Expectancy;
    public decimal? ProfitFactor => Metrics.ProfitFactor;
    public decimal? WinRate => Metrics.WinRate;
    public decimal? MedianMae => Metrics.MedianMae;
    public decimal? MaeP75 => Metrics.MaeP75;
    public decimal? MaeP90 => Metrics.MaeP90;
    public decimal? MaximumMae => Metrics.MaximumMae;
    public decimal? AverageLoss => Metrics.AverageLoss;
    public decimal? MedianLoss => Metrics.MedianLoss;
    public decimal NetPnl => Metrics.NetPnl;
}

public sealed class EdgePersistenceBlockSet
{
    public EdgePersistenceBlockSet(int window, IReadOnlyList<EdgePersistenceTradeBlock> blocks)
    {
        Window = window;
        Blocks = blocks;
    }

    public int Window { get; }
    public IReadOnlyList<EdgePersistenceTradeBlock> Blocks { get; }
    public int ProfitableBlockCount => Blocks.Count(block => block.NetPnl > 0m);
    public int LosingBlockCount => Blocks.Count(block => block.NetPnl < 0m);
    public int PositiveExpectancyBlockCount => Blocks.Count(block => block.Expectancy > 0m);
    public decimal? PositiveExpectancyRate => Blocks.Count == 0 ? null : (decimal)PositiveExpectancyBlockCount / Blocks.Count;
    public EdgePersistenceTradeBlock? BestBlock => Blocks.Count == 0 ? null : Blocks.OrderByDescending(block => block.NetPnl).First();
    public EdgePersistenceTradeBlock? WorstBlock => Blocks.Count == 0 ? null : Blocks.OrderBy(block => block.NetPnl).First();
}

public sealed class EdgePersistenceTradeBlock
{
    public EdgePersistenceTradeBlock(int blockNumber, int startTradeNumber, int endTradeNumber, DateTimeOffset startExitUtc, DateTimeOffset endExitUtc, EdgePersistenceTradeMetrics metrics)
    {
        BlockNumber = blockNumber;
        StartTradeNumber = startTradeNumber;
        EndTradeNumber = endTradeNumber;
        StartExitUtc = startExitUtc;
        EndExitUtc = endExitUtc;
        Metrics = metrics;
    }

    public int BlockNumber { get; }
    public int StartTradeNumber { get; }
    public int EndTradeNumber { get; }
    public DateTimeOffset StartExitUtc { get; }
    public DateTimeOffset EndExitUtc { get; }
    public EdgePersistenceTradeMetrics Metrics { get; }
    public decimal GrossPnl => Metrics.GrossPnl;
    public decimal NetPnl => Metrics.NetPnl;
    public decimal? Expectancy => Metrics.Expectancy;
    public decimal? ProfitFactor => Metrics.ProfitFactor;
    public decimal? WinRate => Metrics.WinRate;
    public decimal? MedianMae => Metrics.MedianMae;
    public decimal? AverageLoss => Metrics.AverageLoss;
    public decimal? LargestLoss => Metrics.LargestLoss;
}

public sealed class EdgePersistenceComparison
{
    public EdgePersistenceComparison(int window, EdgePersistenceTradeMetrics recent, EdgePersistenceTradeMetrics previous, string trend)
    {
        Window = window;
        Recent = recent;
        Previous = previous;
        Trend = trend;
    }

    public int Window { get; }
    public EdgePersistenceTradeMetrics Recent { get; }
    public EdgePersistenceTradeMetrics Previous { get; }
    public string Trend { get; }
    public decimal? ExpectancyChange => Difference(Recent.Expectancy, Previous.Expectancy);
    public decimal? ProfitFactorChange => Difference(Recent.ProfitFactor, Previous.ProfitFactor);
    public decimal? WinRateChange => Difference(Recent.WinRate, Previous.WinRate);
    public decimal? MedianMaeChange => Difference(Recent.MedianMae, Previous.MedianMae);
    public decimal? AverageLossChange => Difference(Recent.AverageLoss, Previous.AverageLoss);
    public decimal? LargestLossChange => Difference(Recent.LargestLoss, Previous.LargestLoss);
    public decimal? AverageWinnerChange => Difference(Recent.AverageWinner, Previous.AverageWinner);

    private static decimal? Difference(decimal? recent, decimal? previous) => recent.HasValue && previous.HasValue ? recent.Value - previous.Value : null;
}

public sealed class EdgePersistenceRobustnessMetrics
{
    public decimal ActualGrossPnl { get; init; }
    public decimal ActualNetPnl { get; init; }
    public IReadOnlyList<EdgePersistenceStressPoint> WithoutBest { get; init; } = Array.Empty<EdgePersistenceStressPoint>();
    public IReadOnlyList<EdgePersistenceStressPoint> WithoutWorst { get; init; } = Array.Empty<EdgePersistenceStressPoint>();
    public EdgePersistenceWinnerConcentration WinnerConcentration { get; init; } = new(null, null, null, 1, 1, 1);
    public decimal? LargestLossShare { get; init; }
    public decimal Score { get; init; }
    public IReadOnlyList<string> Observations { get; set; } = Array.Empty<string>();
}

public sealed record EdgePersistenceStressPoint(int RemovedCount, decimal GrossPnl, decimal NetPnl);

public sealed record EdgePersistenceWinnerConcentration(
    decimal? Top1ProfitShare,
    decimal? Top5ProfitShare,
    decimal? Top10ProfitShare,
    int Top1TradeCount,
    int Top5TradeCount,
    int Top10TradeCount);

public sealed class EdgePersistenceRiskStability
{
    public int MaeObservedTradeCount { get; init; }
    public decimal? MaeCoverage { get; init; }
    public decimal? RiskUnitCoverage { get; init; }
    public decimal? MedianMae { get; init; }
    public decimal? MaeP75 { get; init; }
    public decimal? MaeP90 { get; init; }
    public decimal? MaximumMae { get; init; }
    public decimal? MedianLoss { get; init; }
    public decimal? LossP90 { get; init; }
    public decimal? MaximumLoss { get; init; }
    public decimal? NormalizedMaeP90 { get; init; }
    public decimal? NormalizedLossP90 { get; init; }
    public decimal? RiskScore { get; init; }
    public string RiskUnitLabel { get; init; } = "risk-normalized units";
    public bool UsesRiskUnits { get; init; }
    public string MaeTrend { get; init; } = "Not enough history";
    public string LossTrend { get; init; } = "Not enough history";
    public bool RecentComparisonAvailable { get; init; }
}

public sealed class EdgePersistenceSegmentDimension
{
    public EdgePersistenceSegmentDimension(string name, string note, int minimumTrades, int candidateSegmentCount, int suppressedSegmentCount, IReadOnlyList<EdgePersistenceSegment> segments)
    {
        Name = name;
        Note = note;
        MinimumTrades = minimumTrades;
        CandidateSegmentCount = candidateSegmentCount;
        SuppressedSegmentCount = suppressedSegmentCount;
        Segments = segments;
    }

    public string Name { get; }
    public string Note { get; }
    public int MinimumTrades { get; }
    public int CandidateSegmentCount { get; }
    public int SuppressedSegmentCount { get; }
    public IReadOnlyList<EdgePersistenceSegment> Segments { get; }
}

public sealed class EdgePersistenceSegment
{
    public EdgePersistenceSegment(string label, EdgePersistenceTradeMetrics metrics)
    {
        Label = label;
        Metrics = metrics;
    }

    public string Label { get; }
    public EdgePersistenceTradeMetrics Metrics { get; }
    public int TradeCount => Metrics.TradeCount;
    public decimal NetPnl => Metrics.NetPnl;
    public decimal? Expectancy => Metrics.Expectancy;
    public decimal? ProfitFactor => Metrics.ProfitFactor;
    public decimal? WinRate => Metrics.WinRate;
    public decimal? MedianMae => Metrics.MedianMae;
    public decimal? AverageWinner => Metrics.AverageWinner;
    public decimal? AverageLoss => Metrics.AverageLoss;
}

public sealed class EdgePersistenceSegmentConsistency
{
    public decimal Score { get; init; }
    public int ReliableSegmentCount { get; init; }
    public int PositiveSegmentCount { get; init; }
    public string Note { get; init; } = string.Empty;
}

public sealed class EdgePersistenceExpectancyPersistence
{
    public decimal? PositiveRollingRate { get; init; }
    public decimal? PositiveBlockRate { get; init; }
    public int PositiveExpectancyBlockCount { get; init; }
    public int TotalBlockCount { get; init; }
    public string RecentTrend { get; init; } = "Not enough history";
    public decimal? Score { get; init; }
}

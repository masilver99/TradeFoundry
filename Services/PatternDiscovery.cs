using System.Globalization;
using TradeFoundry.Core;

namespace TradeFoundry.Services;

internal static class PatternDiscovery
{
    private const int MinimumTrades = 20;
    private const int MinimumDays = 10;
    private const int DisplayPerGroup = 6;
    private const decimal MinimumLift = .15m;
    private static readonly Condition[] Conditions = BuildConditions().ToArray();

    internal static PatternInstrumentReport Scan(string instrument, List<PatternObservation> rows,
        PatternInstrumentReport? previous, CancellationToken cancellationToken)
    {
        var dateGroups = rows.Select(row => row.Date).Distinct().Order().ToArray();
        var atrRows = rows.Where(row => row.Features.ContainsKey(PatternFeature.AtrPoints)).ToArray();
        var excursion = atrRows.Where(row => row.Trade.MfePoints is >= 0)
            .Select(row => row.Trade.MfePoints!.Value / row.Features[PatternFeature.AtrPoints]).ToArray();
        DateOnly? validationStart = null;
        IReadOnlyList<PatternFinding> findings = [];
        if (dateGroups.Length >= MinimumDays * 2)
        {
            validationStart = dateGroups[dateGroups.Length * 3 / 5];
            var validation = rows.Where(row => row.Date >= validationStart).ToArray();
            var firstValidationEntry = validation.Min(row => row.Trade.EntryUtc);
            var development = rows.Where(row => row.Date < validationStart && row.Trade.ExitUtc < firstValidationEntry).ToArray();
            var recentStart = dateGroups[(dateGroups.Length * 3 / 5 + dateGroups.Length) / 2];
            var recent = validation.Where(row => row.Date >= recentStart).ToArray();
            findings = Find(instrument, development, validation, recent, previous, cancellationToken);
        }
        else if (previous is not null)
        {
            var empty = new PatternCohort(0, 0, 0, null, null, null);
            findings = previous.Findings.Select(item => item with
            {
                Weakened = true, Discovery = empty, Validation = empty, Recent = empty, Examples = []
            }).ToArray();
        }

        return new PatternInstrumentReport(instrument, rows.Count, rows.Count(row => row.HasBar), atrRows.Length,
            rows.Count(row => row.HasWorkingStop), dateGroups.FirstOrDefault(), dateGroups.LastOrDefault(), validationStart,
            PatternEvidence.Median(atrRows.Select(row => row.Features[PatternFeature.AtrPoints])),
            PatternEvidence.Median(excursion), excursion.Length, new PatternManagementCoverage(
                rows.Count(row => row.Features.ContainsKey(PatternFeature.TargetAtr)),
                rows.Count(row => row.Features.ContainsKey(PatternFeature.BreakEven)),
                rows.Count(row => row.Features.GetValueOrDefault(PatternFeature.BreakEven) == 1),
                rows.Count(row => row.Features.ContainsKey(PatternFeature.Trailing)),
                rows.Count(row => row.Features.GetValueOrDefault(PatternFeature.Trailing) == 1),
                rows.Count(row => row.Features.GetValueOrDefault(PatternFeature.NativeTrailing) == 1)), findings);
    }

    private static IReadOnlyList<PatternFinding> Find(string instrument, PatternObservation[] development,
        PatternObservation[] validation, PatternObservation[] recent, PatternInstrumentReport? previous,
        CancellationToken cancellationToken)
    {
        var old = (previous?.Findings ?? []).ToDictionary(finding => finding.Key);
        var tested = new Dictionary<string, (Rule Rule, PatternCohort Development)>();
        foreach (var condition in Conditions)
            Discover(new Rule([condition]));

        // Search simple interactions too. Choose a bounded, diverse set using the earlier dates only.
        var seeds = tested.Values.Where(item => !item.Rule.Management && Enough(item.Development))
            .OrderByDescending(item => Math.Abs(item.Development.MatchedLift!.Value))
            .DistinctBy(item => item.Rule.Conditions[0].Feature).Take(12).Select(item => item.Rule.Conditions[0]).ToArray();
        for (var first = 0; first < seeds.Length; first++)
            for (var second = first + 1; second < seeds.Length; second++)
                Discover(new Rule([seeds[first], seeds[second]]));

        // Freeze the shortlist before scoring later dates. Previously shown rules always get rechecked.
        var shortlist = tested.Values.Where(item => Signal(item.Development) != 0)
            .GroupBy(item => (item.Rule.Management, Direction: Signal(item.Development)))
            .SelectMany(group => group.OrderByDescending(item => Math.Abs(item.Development.MatchedLift!.Value))
                .ThenBy(item => item.Rule.Conditions.Length).ThenBy(item => item.Rule.Key, StringComparer.Ordinal).Take(16))
            .Select(item => item.Rule).ToDictionary(rule => rule.Key);
        foreach (var key in old.Keys)
        {
            if (shortlist.ContainsKey(key)) continue;
            var parts = key.Split('+').Select(part => Conditions.FirstOrDefault(condition => condition.Key == part)).ToArray();
            if (parts.All(part => part is not null))
                shortlist[key] = new Rule(parts.OfType<Condition>().ToArray());
        }

        var output = new List<PatternFinding>();
        foreach (var rule in shortlist.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var early = tested.TryGetValue(rule.Key, out var found) ? found.Development : Measure(development, rule).Cohort;
            var later = Measure(validation, rule);
            var current = Measure(recent, rule).Cohort;
            var direction = Signal(early);
            var prior = old.GetValueOrDefault(rule.Key);
            if (prior is not null) direction = prior.Direction;
            if (direction == 0) continue;
            var weakened = Signal(early) != direction || Signal(later.Cohort) != direction
                || (current.Trades >= 12 && current.Days >= 6 && current.MatchedLift * direction < 0);
            if (weakened && prior is null) continue;
            output.Add(new PatternFinding(rule.Key, instrument, rule.Conditions.Select(condition => condition.Text).ToArray(),
                rule.Management, direction, weakened, early, later.Cohort, current, Examples(later.Matches)));
        }

        return output.GroupBy(item => (item.Management, item.Direction, item.Weakened))
            .OrderBy(group => group.Key.Weakened)
            .SelectMany(group => group.OrderByDescending(item => Math.Abs(item.Validation.MatchedLift ?? 0))
                .ThenBy(item => item.Criteria.Count).Take(DisplayPerGroup)).ToArray();

        void Discover(Rule rule)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tested.TryAdd(rule.Key, (rule, Measure(development, rule).Cohort));
        }
    }

    private static TradeGroup Group(PatternObservation row, Rule rule)
    {
        var risk = rule.Management && !rule.Uses(PatternFeature.StopAtr)
            ? row.Features.GetValueOrDefault(PatternFeature.StopAtr) : 0;
        var progress = rule.Uses(PatternFeature.BreakEven) ? row.BreakEvenProgress
            : rule.Uses(PatternFeature.Trailing) || rule.Uses(PatternFeature.NativeTrailing) ? row.TrailingProgress : null;
        return new TradeGroup(row.Trade.Account.ToUpperInvariant(), rule.Uses(PatternFeature.Side) ? "" : row.Trade.Direction,
            rule.Uses(PatternFeature.Session) ? 0 : row.Session,
            rule.Uses(PatternFeature.InitialSize) ? 0 : (int)Math.Min(2, row.Features.GetValueOrDefault(PatternFeature.InitialSize)),
            risk <= 0 ? 0 : risk < 1 ? 1 : risk < 2 ? 2 : 3, progress);
    }

    private static (PatternCohort Cohort, PatternObservation[] Matches) Measure(PatternObservation[] rows, Rule rule)
    {
        var eligible = rows.Where(row => rule.Conditions.All(condition => row.Features.ContainsKey(condition.Feature))).ToArray();
        var matched = eligible.Where(rule.Matches).ToArray();
        var others = eligible.Where(row => !rule.Matches(row)).GroupBy(row => Group(row, rule))
            .Where(group => group.Count() >= 5 && group.Select(row => row.Date).Distinct().Count() >= 3)
            .ToDictionary(group => group.Key, group => (Count: group.Count(), Points: group.Average(row => row.NetPoints)));
        var pairs = matched.Select(row => (Row: row, Group: Group(row, rule))).Where(pair => others.ContainsKey(pair.Group)).ToArray();
        if (pairs.Length == 0) return (new PatternCohort(0, 0, 0, null, null, null), []);

        // Give each trading day equal weight for lift so a very active day cannot dominate the ranking.
        var days = pairs.GroupBy(pair => pair.Row.Date)
            .Select(day => day.Average(pair => pair.Row.NetPoints - others[pair.Group].Points)).ToArray();
        var lift = days.Average();
        decimal? withoutStrongest = days.Length > 1 ? (days.Sum() - days.Max()) / (days.Length - 1) : null;
        decimal? withoutWeakest = days.Length > 1 ? (days.Sum() - days.Min()) / (days.Length - 1) : null;
        var used = pairs.Select(pair => pair.Row).ToArray();
        return (new PatternCohort(used.Length, days.Length, pairs.Select(pair => pair.Group).Distinct().Sum(key => others[key].Count),
            used.Average(row => row.NetPoints), lift, used.Sum(row => row.Trade.NetPnl), withoutStrongest, withoutWeakest), used);
    }

    private static bool Enough(PatternCohort cohort) =>
        cohort.Trades >= MinimumTrades && cohort.Days >= MinimumDays && cohort.ComparisonTrades >= MinimumTrades
        && cohort.MatchedLift.HasValue;

    private static int Signal(PatternCohort cohort)
    {
        if (!Enough(cohort)) return 0;
        if (cohort.NetPointsPerContract > 0 && cohort.MatchedLift >= MinimumLift && cohort.LiftWithoutStrongestDay > 0) return 1;
        if (cohort.NetPointsPerContract < 0 && cohort.MatchedLift <= -MinimumLift && cohort.LiftWithoutWeakestDay < 0) return -1;
        return 0;
    }

    private static PatternTradeExample[] Examples(PatternObservation[] rows)
    {
        var linkable = rows.Where(row => !string.IsNullOrWhiteSpace(row.Trade.ReviewKey)).ToArray();
        return new[] { linkable.MaxBy(row => row.NetPoints), linkable.MinBy(row => row.NetPoints),
                linkable.MaxBy(row => row.Trade.EntryUtc) }
            .OfType<PatternObservation>().DistinctBy(row => row.Trade.ReviewKey)
            .Select(row => new PatternTradeExample(row.Trade.ReviewKey, row.ReviewDate, row.Trade.NetPnl)).ToArray();
    }

    private static IEnumerable<Condition> BuildConditions()
    {
        return Categories(PatternFeature.Session, [
                (0, "Outside regular trading hours"), (1, "Entry from 9:30 to 10:00 a.m. ET"),
                (2, "Entry from 10:00 to 11:00 a.m. ET"), (3, "Entry from 11:00 a.m. to noon ET"),
                (4, "Entry from noon to 2:00 p.m. ET"), (5, "Entry from 2:00 to 4:00 p.m. ET")])
            .Concat(Categories(PatternFeature.Weekday, Enumerable.Range(1, 5)
                .Select(day => ((decimal)day, $"{(DayOfWeek)day} entry"))))
            .Concat(Categories(PatternFeature.Side, [(1, "Long entry"), (-1, "Short entry")]))
            .Concat(Categories(PatternFeature.InitialSize, [(1, "First fill was one contract"), (2, "First fill was two contracts")]))
            .Concat(Categories(PatternFeature.PreviousResult, [(-2, "Within 5 minutes after a net loss in the same instrument"),
                (-1, "At least 5 minutes after a net loss in the same instrument"), (1, "Previous trade in the same instrument that day was a net winner")]))
            .Concat(Threshold(PatternFeature.InitialSize, 3, true, "First fill was at least three contracts"))
            .Concat(Threshold(PatternFeature.GapPoints, .25m, true, "Regular session opened above the prior regular close"))
            .Concat(Threshold(PatternFeature.GapPoints, -.25m, false, "Regular session opened below the prior regular close"))
            .Concat(Categories(PatternFeature.OvernightAlignment, [(1, "Entry with the overnight move"), (-1, "Entry against the overnight move")]))
            .Concat(Threshold(PatternFeature.CandleRange, .67m, false, "Last 1-minute candle range at most 67% of the preceding 20-candle median"))
            .Concat(Threshold(PatternFeature.CandleRange, 1.5m, true, "Last 1-minute candle range at least 150% of the preceding 20-candle median"))
            .Concat(Threshold(PatternFeature.CandleBody, .65m, true, "Last 1-minute candle body was at least 65% of range with the entry"))
            .Concat(Threshold(PatternFeature.CandleBody, -.65m, false, "Last 1-minute candle body was at least 65% of range against the entry"))
            .Concat(Threshold(PatternFeature.RejectionWick, .5m, true, "Last 1-minute candle had a lower wick on longs or upper wick on shorts at least half its range"))
            .Concat(Threshold(PatternFeature.MovePoints, 2, true, "Previous five minutes moved at least 2 points with the entry"))
            .Concat(Threshold(PatternFeature.MovePoints, -2, false, "Previous five minutes moved at least 2 points against the entry"))
            .Concat(Threshold(PatternFeature.MoveAtr, 1, true, "Previous five minutes moved at least 1 ATR with the entry"))
            .Concat(Threshold(PatternFeature.MoveAtr, -1, false, "Previous five minutes moved at least 1 ATR against the entry"))
            .Concat(new[] { 1m, 2m, 4m, 6m }.SelectMany(points =>
                Threshold(PatternFeature.AtrPoints, points, true, $"Pre-entry ATR at least {points} points")
                    .Concat(Threshold(PatternFeature.AtrPoints, points, false, $"Pre-entry ATR at most {points} points"))))
            .Concat(Categories(PatternFeature.EmaTouch, [(1, "Last completed 1-minute candle touched the 21 EMA")]))
            .Concat(Threshold(PatternFeature.EmaDistance, 1, true, "Last 1-minute close at least 1 point on the entry side of the 21 EMA"))
            .Concat(Threshold(PatternFeature.EmaDistance, -1, false, "Last 1-minute close at least 1 point against the entry side of the 21 EMA"))
            .Concat(Threshold(PatternFeature.RelativeVolume, 1.5m, true, "Last 1-minute volume at least 150% of the preceding 20-candle median"))
            .Concat(Threshold(PatternFeature.RelativeVolume, .67m, false, "Last 1-minute volume at most 67% of the preceding 20-candle median"))
            .Concat(Threshold(PatternFeature.VolumeDelta, .2m, true, "Last 1-minute bid/ask volume delta at least 20% with the entry"))
            .Concat(Threshold(PatternFeature.VolumeDelta, -.2m, false, "Last 1-minute bid/ask volume delta at least 20% against the entry"))
            .Concat(new[] { .5m, 1m, 2m }.SelectMany(ratio =>
                Threshold(PatternFeature.StopAtr, ratio, false, $"Initial protective stop within {ratio:0.##} ATR")
                    .Concat(Threshold(PatternFeature.StopAtr, ratio, true, $"Initial protective stop at least {ratio:0.##} ATR away"))
                    .Concat(Threshold(PatternFeature.TargetAtr, ratio, false, $"Initial profit target within {ratio:0.##} ATR"))
                    .Concat(Threshold(PatternFeature.TargetAtr, ratio, true, $"Initial profit target at least {ratio:0.##} ATR away"))))
            .Concat(Categories(PatternFeature.BreakEven, [(1, "Working stop reached entry price within the first 3 minutes")]))
            .Concat(Categories(PatternFeature.Trailing, [(1, "At least two consecutive favorable stop adjustments in the first 5 minutes")]))
            .Concat(Categories(PatternFeature.NativeTrailing, [(1, "A Sierra trailing order was working at 5 minutes")]));
    }

    private static IEnumerable<Condition> Categories(PatternFeature feature, IEnumerable<(decimal Value, string Text)> categories) =>
        categories.Select(item => new Condition(feature, item.Value, 0, item.Text));

    private static IEnumerable<Condition> Threshold(PatternFeature feature, decimal value, bool above, string text) =>
        [new Condition(feature, value, above ? 1 : -1, text)];

    private sealed record Condition(PatternFeature Feature, decimal Value, int Comparison, string Text)
    {
        public string Key => $"{Feature}:{Comparison}:{Value.ToString(CultureInfo.InvariantCulture)}";
        public bool Management => Feature >= PatternFeature.StopAtr;
        public bool Matches(decimal value) => Comparison > 0 ? value >= Value : Comparison < 0 ? value <= Value : value == Value;
    }

    private sealed record Rule(Condition[] Conditions)
    {
        public string Key { get; } = string.Join('+', Conditions.OrderBy(condition => condition.Key, StringComparer.Ordinal).Select(condition => condition.Key));
        public bool Management => Conditions[0].Management;
        public bool Uses(PatternFeature feature) => Conditions.Any(condition => condition.Feature == feature);
        public bool Matches(PatternObservation row) => Conditions.All(condition => condition.Matches(row.Features[condition.Feature]));
    }

    private sealed record TradeGroup(string Account, string Side, int Session, int InitialSize, int Risk, int? Progress);
}

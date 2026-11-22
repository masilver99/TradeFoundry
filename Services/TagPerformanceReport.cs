using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services;

public enum TagStanding
{
    Ranked,
    TooFew,
    MissingRisk,
    ReviewFinding,
    NeedsReview
}

public enum TagStability
{
    Unknown,
    HeldUp,
    Changed,
    Negative
}

public sealed record TagPerformanceRow
{
    public required Tag Tag { get; init; }
    public required TagSample Sample { get; init; }
    public required TagSample Others { get; init; }
    public required TagStanding Standing { get; init; }
    public int? Rank { get; init; }
    public decimal? Earlier { get; init; }
    public decimal? Later { get; init; }
    public TagStability Stability { get; init; }
    public decimal? WinLift { get; init; }
    public decimal? ValueLift { get; init; }
}

/// <summary>A tag picked for the lookup. In the trade report it may be a tag on the trade's day.</summary>
public sealed record TagPick(Tag Tag, TagTarget On)
{
    public string Value => FormatValue(On, Tag.Id);

    public static string FormatValue(TagTarget on, Guid id) => $"{(on == TagTarget.Day ? "d" : "t")}:{id:D}";
}

public sealed record TagLookup
{
    public required IReadOnlyList<TagPick> Picks { get; init; }
    public required IReadOnlyList<TagObservation> Matches { get; init; }
    public required IReadOnlyList<TagObservation> Others { get; init; }
    public required TagSample Sample { get; init; }
    public required TagSample OtherSample { get; init; }
    /// <summary>False when a pick is only known during or after, or still needs review: then it describes, it doesn't predict.</summary>
    public required bool IsProbability { get; init; }
    public required bool EnoughData { get; init; }
}

/// <summary>Tag performance for one journal, filter and unit (trades or days).</summary>
public sealed class TagPerformanceReport
{
    public const int MinimumTrades = 25;
    public const int MinimumTradeDays = 10;
    public const int MinimumDays = 25;
    /// <summary>The smallest comparison group, and smallest sample to split in half, worth showing.</summary>
    public const int MinimumComparison = 10;
    public const int MaximumPicks = 3;
    public const decimal RiskCoverageForR = .8m;

    private readonly IReadOnlyList<TagObservation> _all;
    private readonly IReadOnlyList<TagObservation> _inScope;
    private readonly TagCatalog _catalog;

    private TagPerformanceReport(TagCatalog catalog, TagTarget target, IReadOnlyList<TagObservation> all, JournalFilterScope scope, string? metric)
    {
        _catalog = catalog;
        Target = target;
        _all = all;
        _inScope = all.Where(item => scope.IncludesDate(item.Date)).ToArray();
        var tagged = _inScope.Where(item => item.Tags.Count > 0).ToArray();
        Tagged = TagStatistics.Measure(tagged);
        TotalCount = _inScope.Count;
        UsesR = metric switch { "r" => true, "money" => false, _ => Tagged.Count > 0 && Tagged.RiskCoverage >= RiskCoverageForR };
        Rows = BuildRows(tagged);
        Options = BuildOptions(tagged);
    }

    public TagTarget Target { get; }
    public bool UsesR { get; }
    public TagSample Tagged { get; }
    public int TotalCount { get; }
    public IReadOnlyList<TagPerformanceRow> Rows { get; }
    /// <summary>The tags that can be combined in the probability lookup.</summary>
    public IReadOnlyList<TagPick> Options { get; }
    public int RequiredCount => Target == TagTarget.Day ? MinimumDays : MinimumTrades;
    public int RequiredDays => Target == TagTarget.Day ? MinimumDays : MinimumTradeDays;

    /// <summary>The chance that at least one of the ranked tags clears zero by luck, if none had an edge.</summary>
    public double ChanceOfLuckyTag => 1d - Math.Pow(.975d, Rows.Count(row => row.Standing == TagStanding.Ranked));

    public static TagPerformanceReport Load(TradeFoundryDb database, Journal journal, JournalFilterScope scope, TagTarget target, string? metric)
    {
        // History stays whole so "since the tag was first used" doesn't move with the date filter.
        // Direction applies throughout: a short-only day's P&L is its short trades.
        var trades = database.GetAllTrades(journal.Id).Where(scope.Filter.MatchesSide).ToArray();
        var observations = Observe(target, trades, database.GetTradeReviewAnnotations(journal.Id), database.GetTradeTags(journal.Id), database.GetDayTags(journal.Id), journal.TimeZone);
        return Create(database.GetTagCatalog(journal.Id), target, observations, scope, metric);
    }

    public static TagPerformanceReport Create(TagCatalog catalog, TagTarget target, IReadOnlyList<TagObservation> observations, JournalFilterScope scope, string? metric) =>
        new(catalog, target, observations, scope, metric);

    public static IReadOnlyList<TagObservation> Observe(
        TagTarget target,
        IReadOnlyList<Trade> trades,
        IReadOnlyDictionary<string, TradeReviewAnnotation> reviews,
        IReadOnlyDictionary<string, IReadOnlyList<Tag>> tradeTags,
        IReadOnlyDictionary<DateOnly, IReadOnlyList<Tag>> dayTags,
        string? timeZoneId)
    {
        var none = new HashSet<Guid>();
        var zone = TimeZoneCatalog.Resolve(timeZoneId);
        var tagsByDay = dayTags.ToDictionary(item => item.Key, item => (IReadOnlySet<Guid>)item.Value.Select(tag => tag.Id).ToHashSet());
        var items = new List<TagObservation>();
        foreach (var trade in trades)
        {
            if (!trade.ExitUtc.HasValue || !trade.Status.Equals("closed", StringComparison.OrdinalIgnoreCase)) continue;

            var review = reviews.GetValueOrDefault(trade.ReviewKey);
            // The same risk the MAE review uses: planned first, then the imported stop.
            var metrics = MaeReviewMetrics.Build(trade, new MaeTargetSettings(), review?.PlannedRiskCurrency, review?.PlannedRiskPoints);
            var risk = metrics.PlannedRiskCurrency is > 0m ? metrics.PlannedRiskCurrency : null;
            var pointValue = trade.PointValue > 0m ? trade.PointValue : InstrumentCatalog.Resolve(metrics.Instrument).PointValue;
            var mfe = trade.MfePoints.HasValue ? Math.Abs(trade.MfePoints.Value) * pointValue * metrics.ContractCount : (decimal?)null;
            var date = JournalFilter.TradeDate(trade, zone);
            items.Add(new TagObservation
            {
                Key = trade.ReviewKey,
                Date = date,
                Order = trade.ExitUtc.Value,
                NetPnl = trade.NetPnl,
                NetR = risk is { } amount ? trade.NetPnl / amount : null,
                ReachedR = risk is { } reachRisk && mfe is { } favorable ? Math.Max(favorable, trade.GrossPnl) / reachRisk : null,
                Mae = metrics.PositionMae,
                Mfe = mfe,
                Tags = tradeTags.TryGetValue(trade.ReviewKey, out var tags) ? tags.Select(tag => tag.Id).ToHashSet() : none,
                DayTags = tagsByDay.GetValueOrDefault(date) ?? none,
                Trade = trade
            });
        }
        if (target == TagTarget.Trade) return items;

        return items.GroupBy(item => item.Date).Select(day => new TagObservation
        {
            Key = day.Key.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            Date = day.Key,
            Order = day.Max(item => item.Order),
            NetPnl = day.Sum(item => item.NetPnl),
            NetR = day.All(item => item.NetR.HasValue) ? day.Sum(item => item.NetR!.Value) : null,
            Mae = day.Max(item => item.Mae),
            Tags = day.First().DayTags,
            DayTags = day.First().DayTags,
            TradeCount = day.Count()
        }).ToArray();
    }

    private IReadOnlyList<TagPerformanceRow> BuildRows(IReadOnlyList<TagObservation> tagged)
    {
        var firstUse = new Dictionary<Guid, DateOnly>();
        foreach (var item in _all)
            foreach (var id in item.Tags)
                if (!firstUse.TryGetValue(id, out var date) || item.Date < date) firstUse[id] = item.Date;

        var rows = new List<TagPerformanceRow>();
        foreach (var id in tagged.SelectMany(item => item.Tags).Distinct())
        {
            if (_catalog.Get(id) is not { } tag) continue;
            var with = tagged.Where(item => item.Tags.Contains(id)).ToArray();
            // Untagged items are left out: untagged doesn't mean the tag was absent.
            var without = tagged.Where(item => !item.Tags.Contains(id) && item.Date >= firstUse[id]).ToArray();
            var sample = TagStatistics.Measure(with);
            var others = TagStatistics.Measure(without);
            var (earlier, later) = Halves(with);
            var earlierMean = MeanOf(earlier);
            var laterMean = MeanOf(later);
            rows.Add(new TagPerformanceRow
            {
                Tag = tag,
                Sample = sample,
                Others = others,
                Standing = StandingOf(tag, sample),
                Earlier = earlierMean,
                Later = laterMean,
                Stability = earlierMean is not { } first || laterMean is not { } second ? TagStability.Unknown
                    : first > 0m && second > 0m ? TagStability.HeldUp
                    : first <= 0m && second <= 0m ? TagStability.Negative
                    : TagStability.Changed,
                WinLift = others.Count >= MinimumComparison ? sample.WinRate - others.WinRate : null,
                ValueLift = others.Value(UsesR).Count >= MinimumComparison ? sample.Value(UsesR).Mean - others.Value(UsesR).Mean : null
            });
        }

        // By the low end of the range, so a short lucky streak cannot top the list.
        var ordered = rows
            .OrderBy(row => row.Standing)
            .ThenByDescending(row => row.Standing == TagStanding.Ranked ? row.Sample.Value(UsesR).Lower : row.Sample.Count)
            .ThenBy(row => row.Tag.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return ordered.Select((row, index) => row.Standing == TagStanding.Ranked ? row with { Rank = index + 1 } : row).ToArray();
    }

    private TagStanding StandingOf(Tag tag, TagSample sample)
    {
        if (tag.NeedsReview) return TagStanding.NeedsReview;
        if (!tag.IsPredictive) return TagStanding.ReviewFinding;
        if (sample.Count < RequiredCount || sample.Days < RequiredDays) return TagStanding.TooFew;
        if (!UsesR) return TagStanding.Ranked;
        return sample.RiskCoverage >= RiskCoverageForR && sample.R.Count >= RequiredCount && sample.R.Days >= RequiredDays
            ? TagStanding.Ranked
            : TagStanding.MissingRisk;
    }

    private IReadOnlyList<TagPick> BuildOptions(IReadOnlyList<TagObservation> tagged)
    {
        var options = tagged.SelectMany(item => item.Tags).Distinct()
            .Select(id => (Id: id, On: Target));
        if (Target == TagTarget.Trade)
            options = options.Concat(_inScope.SelectMany(item => item.DayTags).Distinct().Select(id => (Id: id, On: TagTarget.Day)));
        return options
            .Select(option => (Tag: _catalog.Get(option.Id), option.On))
            .Where(option => option.Tag is { IsPredictive: true })
            .Select(option => new TagPick(option.Tag!, option.On))
            .OrderBy(pick => pick.On)
            .ThenBy(pick => pick.Tag.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Reads "t:id" and "d:id" values, keeping up to three.</summary>
    public IReadOnlyList<TagPick> ParsePicks(IEnumerable<string?> values)
    {
        var picks = new List<TagPick>();
        foreach (var value in values)
        {
            if (value is not { Length: > 2 } || value[1] != ':' || !Guid.TryParse(value.AsSpan(2), out var id)) continue;
            TagTarget on;
            if (value[0] == 'd') on = TagTarget.Day;
            else if (value[0] == 't' && Target == TagTarget.Trade) on = TagTarget.Trade;
            else continue;
            if (_catalog.Resolve(id) is not { } tag || picks.Any(pick => pick.Tag.Id == tag.Id && pick.On == on)) continue;
            picks.Add(new TagPick(tag, on));
            if (picks.Count == MaximumPicks) break;
        }
        return picks;
    }

    public TagLookup? Lookup(IReadOnlyList<TagPick> picks)
    {
        if (picks.Count == 0) return null;
        static IReadOnlySet<Guid> On(TagObservation item, TagTarget target) => target == TagTarget.Day ? item.DayTags : item.Tags;

        var matches = _inScope.Where(item => picks.All(pick => On(item, pick.On).Contains(pick.Tag.Id))).ToArray();

        // Compare only with items tagged the same way, since every picked tag was in use.
        var since = DateOnly.MinValue;
        foreach (var pick in picks)
        {
            var used = _all.Where(item => On(item, pick.On).Contains(pick.Tag.Id)).Select(item => item.Date).ToArray();
            var first = used.Length == 0 ? DateOnly.MaxValue : used.Min();
            if (first > since) since = first;
        }
        var kinds = picks.Select(pick => pick.On).Distinct().ToArray();
        var matched = matches.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var others = _inScope.Where(item => !matched.Contains(item.Key) && item.Date >= since && kinds.All(kind => On(item, kind).Count > 0)).ToArray();

        var sample = TagStatistics.Measure(matches);
        return new TagLookup
        {
            Picks = picks,
            Matches = matches,
            Others = others,
            Sample = sample,
            OtherSample = TagStatistics.Measure(others),
            IsProbability = picks.All(pick => pick.Tag.IsPredictive),
            EnoughData = sample.Count >= RequiredCount && sample.Days >= RequiredDays
        };
    }

    /// <summary>The exact items behind one of the numbers on the page.</summary>
    public IReadOnlyList<TagObservation> Show(TagLookup lookup, string? show) => show switch
    {
        "wins" => lookup.Matches.Where(item => item.NetPnl > 0m).ToArray(),
        "losses" => lookup.Matches.Where(item => item.NetPnl < 0m).ToArray(),
        "risk" => lookup.Matches.Where(item => item.NetR.HasValue).ToArray(),
        "value" => lookup.Matches.Where(HasValue).ToArray(),
        "closed-1r" => lookup.Matches.Where(item => item.NetR >= 1m).ToArray(),
        "reach" => lookup.Matches.Where(item => item.ReachedR.HasValue).ToArray(),
        "reached-1r" => lookup.Matches.Where(item => item.ReachedR >= 1m).ToArray(),
        "reached-2r" => lookup.Matches.Where(item => item.ReachedR >= 2m).ToArray(),
        "mae" => lookup.Matches.Where(item => item.Mae.HasValue).ToArray(),
        "mfe" => lookup.Matches.Where(item => item.Mfe.HasValue).ToArray(),
        "earlier" => Halves(lookup.Matches).Earlier,
        "later" => Halves(lookup.Matches).Later,
        "others" => lookup.Others,
        "others-wins" => lookup.Others.Where(item => item.NetPnl > 0m).ToArray(),
        "others-value" => lookup.Others.Where(HasValue).ToArray(),
        _ => lookup.Matches
    };

    private bool HasValue(TagObservation item) => !UsesR || item.NetR.HasValue;

    private (IReadOnlyList<TagObservation> Earlier, IReadOnlyList<TagObservation> Later) Halves(IReadOnlyList<TagObservation> items)
    {
        var ordered = items.Where(HasValue).OrderBy(item => item.Order).ThenBy(item => item.Key, StringComparer.Ordinal).ToArray();
        if (ordered.Length < MinimumComparison) return (Array.Empty<TagObservation>(), Array.Empty<TagObservation>());
        var middle = ordered.Length / 2;
        return (ordered[..middle], ordered[middle..]);
    }

    private decimal? MeanOf(IReadOnlyList<TagObservation> items) =>
        items.Count == 0 ? null : items.Average(item => UsesR ? item.NetR!.Value : item.NetPnl);
}

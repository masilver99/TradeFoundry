using System.Globalization;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services;

public enum MarketType
{
    StrongTrend,
    WeakTrend,
    Range
}

public enum MarketDirection
{
    Up,
    Down,
    Neutral
}

public enum VolatilityRegime
{
    Low,
    Normal,
    High,
    Extreme,
    Unknown
}

/// <summary>
/// Explicit first-pass assumptions for the market-regime classifier. The
/// classifier uses empirical ranks of the imported history rather than fixed
/// ES point thresholds, so the same code remains useful across volatility
/// eras. Changing these values changes the classifier version returned by the
/// service but never changes the imported bars.
/// </summary>
public sealed class MarketRegimeOptions
{
    public string SessionTimeZoneId { get; init; } = "America/New_York";
    public TimeOnly RthStart { get; init; } = new(9, 30);
    public TimeOnly RthEnd { get; init; } = new(16, 0);
    public int AtrPeriod { get; init; } = 20;
    public int MinimumRthBars { get; init; } = 5;
    public int MinimumVolatilityHistory { get; init; } = 5;

    public decimal StrongComponentPercentile { get; init; } = 0.67m;
    public decimal StrongScoreThreshold { get; init; } = 0.62m;
    public int StrongMinimumComponents { get; init; } = 4;
    public decimal WeakComponentPercentile { get; init; } = 0.40m;
    public decimal WeakScoreThreshold { get; init; } = 0.42m;
    public int WeakMinimumComponents { get; init; } = 2;

    public decimal LowVolatilityPercentile { get; init; } = 0.25m;
    public decimal HighVolatilityPercentile { get; init; } = 0.75m;
    public decimal ExtremeVolatilityPercentile { get; init; } = 0.90m;

    public decimal BayesianPriorStrength { get; init; } = 20m;
    public int FeatureWarmupCalendarDays { get; init; } = 60;
    public string FeatureVersion { get; init; } = "market-day-features-v1";
    public string ClassifierVersion { get; init; } = "market-regime-v1-percentile-components";

    public void Validate()
    {
        if (RthStart >= RthEnd)
            throw new ArgumentException("The RTH start must be earlier than the RTH end.");
        if (AtrPeriod < 2)
            throw new ArgumentOutOfRangeException(nameof(AtrPeriod), "ATR period must be at least two sessions.");
        if (MinimumRthBars < 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumRthBars));
        if (MinimumVolatilityHistory < 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumVolatilityHistory));
        if (FeatureWarmupCalendarDays < 30)
            throw new ArgumentOutOfRangeException(nameof(FeatureWarmupCalendarDays), "Feature warm-up must cover at least 30 calendar days.");
        if (StrongMinimumComponents < 1 || WeakMinimumComponents < 1)
            throw new ArgumentOutOfRangeException(nameof(StrongMinimumComponents));
        if (StrongComponentPercentile is < 0m or > 1m || WeakComponentPercentile is < 0m or > 1m ||
            StrongScoreThreshold is < 0m or > 1m || WeakScoreThreshold is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(StrongComponentPercentile), "Classifier percentiles must be between zero and one.");
        if (LowVolatilityPercentile < 0m || LowVolatilityPercentile >= HighVolatilityPercentile ||
            HighVolatilityPercentile >= ExtremeVolatilityPercentile || ExtremeVolatilityPercentile > 1m)
            throw new ArgumentException("Volatility percentile cut points must be ordered between zero and one.");
        if (BayesianPriorStrength <= 0m)
            throw new ArgumentOutOfRangeException(nameof(BayesianPriorStrength));
        if (string.IsNullOrWhiteSpace(ClassifierVersion))
            throw new ArgumentException("A classifier version is required.", nameof(ClassifierVersion));
        if (string.IsNullOrWhiteSpace(FeatureVersion))
            throw new ArgumentException("A feature version is required.", nameof(FeatureVersion));
    }
}

public sealed record MarketDayFeature
{
    public DateOnly TradeDate { get; init; }
    public string Symbol { get; init; } = string.Empty;
    public DayOfWeek DayOfWeek => TradeDate.DayOfWeek;
    public int Month => TradeDate.Month;
    public int Year => TradeDate.Year;

    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public decimal RthRangePoints { get; init; }
    public decimal? Atr20 { get; init; }
    public decimal? NormalizedRange { get; init; }
    public decimal OpenToClosePoints { get; init; }
    public decimal DirectionalEfficiency { get; init; }
    public decimal PathEfficiency { get; init; }
    public decimal? CloseLocation { get; init; }
    public int VwapCrossings { get; init; }
    public decimal? PercentSessionAboveVwap { get; init; }
    public decimal? PercentSessionBelowVwap { get; init; }
    public decimal? PercentHigherHighs { get; init; }
    public decimal? PercentHigherLows { get; init; }
    public decimal? PercentLowerHighs { get; init; }
    public decimal? PercentLowerLows { get; init; }
    public decimal? MaximumFavorableDirectionalExcursion { get; init; }
    public decimal? MaximumCountertrendExcursion { get; init; }

    public decimal? OvernightHigh { get; init; }
    public decimal? OvernightLow { get; init; }
    public decimal? OvernightRange { get; init; }
    public MarketDirection OvernightDirection { get; init; } = MarketDirection.Neutral;
    public decimal? GapFromPriorRthClose { get; init; }
    public MarketDirection GapDirection { get; init; } = MarketDirection.Neutral;

    public decimal? VolatilityMeasurePoints { get; init; }
    public VolatilityRegime VolatilityRegime { get; init; } = VolatilityRegime.Unknown;
    public MarketType MarketType { get; init; } = MarketType.Range;
    public MarketDirection Direction { get; init; } = MarketDirection.Neutral;
    public string ClassifierVersion { get; init; } = string.Empty;
    public int RthBarCount { get; init; }
    public int OvernightBarCount { get; init; }
    public bool HasVwapData => PercentSessionAboveVwap.HasValue && PercentSessionBelowVwap.HasValue;
}

public sealed record MarketProbabilityQuery
{
    public string? Symbol { get; init; }
    public int? Month { get; init; }
    public DayOfWeek? DayOfWeek { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public DateOnly? AsOfDate { get; init; }
    public VolatilityRegime? VolatilityRegime { get; init; }
    public MarketDirection? OvernightDirection { get; init; }
    public MarketDirection? GapDirection { get; init; }
    public decimal? RangeGreaterThanPoints { get; init; }
    public decimal? RangeLessThanPoints { get; init; }
    public decimal? NormalizedRangeGreaterThan { get; init; }
    public decimal? NormalizedRangeLessThan { get; init; }

    public void Validate()
    {
        if (Month is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(Month), "Month must be between 1 and 12.");
        if (StartDate.HasValue && EndDate.HasValue && StartDate > EndDate)
            throw new ArgumentException("StartDate must not be later than EndDate.");
        if (RangeGreaterThanPoints is < 0m || RangeLessThanPoints is < 0m ||
            NormalizedRangeGreaterThan is < 0m || NormalizedRangeLessThan is < 0m)
            throw new ArgumentOutOfRangeException(nameof(RangeGreaterThanPoints), "Range thresholds cannot be negative.");
        if (RangeGreaterThanPoints.HasValue && RangeLessThanPoints.HasValue && RangeGreaterThanPoints >= RangeLessThanPoints)
            throw new ArgumentException("RTH range lower and upper thresholds must leave a non-empty interval.");
        if (NormalizedRangeGreaterThan.HasValue && NormalizedRangeLessThan.HasValue && NormalizedRangeGreaterThan >= NormalizedRangeLessThan)
            throw new ArgumentException("Normalized range lower and upper thresholds must leave a non-empty interval.");
    }
}

public sealed record ProbabilityInterval(decimal? Lower, decimal? Upper, string Method);

public sealed record ProbabilityEstimate
{
    public string Outcome { get; init; } = string.Empty;
    public int Count { get; init; }
    public int SampleSize { get; init; }
    public decimal? RawProbability { get; init; }
    public decimal? AdjustedProbability { get; init; }
    public decimal? BaselineProbability { get; init; }
    public decimal? DifferenceFromBaseline { get; init; }
    public ProbabilityInterval ConfidenceInterval { get; init; } = new(null, null, "Wilson");
    public ProbabilityInterval CredibleInterval { get; init; } = new(null, null, "Beta posterior");
    public string ShrinkagePriorScope { get; init; } = string.Empty;
    public int ShrinkagePriorSampleSize { get; init; }
    public decimal? ShrinkagePriorProbability { get; init; }
}

public sealed record BaselineProbability
{
    public string Outcome { get; init; } = string.Empty;
    public int Count { get; init; }
    public int SampleSize { get; init; }
    public decimal? RawProbability { get; init; }
}

public sealed record MarketProbabilityBaseline
{
    public string Scope { get; init; } = string.Empty;
    public int SampleSize { get; init; }
    public IReadOnlyList<BaselineProbability> MarketTypes { get; init; } = Array.Empty<BaselineProbability>();
    public IReadOnlyList<BaselineProbability> Directions { get; init; } = Array.Empty<BaselineProbability>();
    public IReadOnlyList<BaselineProbability> DirectionsGivenTrend { get; init; } = Array.Empty<BaselineProbability>();
}

public sealed record RangeDistribution
{
    public int SampleSize { get; init; }
    public decimal? Mean { get; init; }
    public decimal? Median { get; init; }
    public decimal? StandardDeviation { get; init; }
    public decimal? P10 { get; init; }
    public decimal? P25 { get; init; }
    public decimal? P50 { get; init; }
    public decimal? P75 { get; init; }
    public decimal? P90 { get; init; }
}

public sealed record ThresholdProbability
{
    public string Metric { get; init; } = string.Empty;
    public string Operator { get; init; } = string.Empty;
    public decimal Threshold { get; init; }
    public int Count { get; init; }
    public int SampleSize { get; init; }
    public decimal? RawProbability { get; init; }
    public decimal? BaselineProbability { get; init; }
    public decimal? DifferenceFromBaseline { get; init; }
    public ProbabilityInterval ConfidenceInterval { get; init; } = new(null, null, "Wilson");
}

public sealed record ConditionalDirectionResult
{
    public string Condition { get; init; } = "MarketType is StrongTrend or WeakTrend";
    public int TrendSampleSize { get; init; }
    public IReadOnlyList<ProbabilityEstimate> Directions { get; init; } = Array.Empty<ProbabilityEstimate>();
}

public sealed record MarketProbabilityQueryResult
{
    public string PopulationSymbol { get; init; } = string.Empty;
    public string FilterDescription { get; init; } = string.Empty;
    public int SampleSize { get; init; }
    public int AvailableObservationCount { get; init; }
    public string ClassifierVersion { get; init; } = string.Empty;
    public string SessionDefinition { get; init; } = string.Empty;
    public IReadOnlyList<ProbabilityEstimate> MarketTypes { get; init; } = Array.Empty<ProbabilityEstimate>();
    public IReadOnlyList<ProbabilityEstimate> Directions { get; init; } = Array.Empty<ProbabilityEstimate>();
    public ConditionalDirectionResult DirectionGivenTrend { get; init; } = new();
    public RangeDistribution RthRange { get; init; } = new();
    public RangeDistribution NormalizedRange { get; init; } = new();
    public IReadOnlyList<ThresholdProbability> ThresholdProbabilities { get; init; } = Array.Empty<ThresholdProbability>();
    public IReadOnlyList<MarketProbabilityBaseline> Baselines { get; init; } = Array.Empty<MarketProbabilityBaseline>();
    public string ShrinkagePriorScope { get; init; } = string.Empty;
    public int ShrinkagePriorSampleSize { get; init; }
    public IReadOnlyList<string> DataQualityWarnings { get; init; } = Array.Empty<string>();
}

public sealed record MarketDaysResult
{
    public string PopulationSymbol { get; init; } = string.Empty;
    public int TotalMatches { get; init; }
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public IReadOnlyList<MarketDayFeature> Days { get; init; } = Array.Empty<MarketDayFeature>();
    public IReadOnlyList<string> DataQualityWarnings { get; init; } = Array.Empty<string>();
}

public sealed record MarketProbabilityPageSnapshot
{
    public MarketProbabilityQueryResult Probabilities { get; init; } = new();
    public MarketDaysResult RecentDays { get; init; } = new();
}

public sealed class MarketDayFeatureBuilder
{
    private readonly MarketRegimeOptions _options;
    private readonly TimeZoneInfo _sessionTimeZone;

    public MarketDayFeatureBuilder(MarketRegimeOptions? options = null)
    {
        _options = options ?? new MarketRegimeOptions();
        _options.Validate();
        _sessionTimeZone = TimeZoneCatalog.Resolve(_options.SessionTimeZoneId);
    }

    public IReadOnlyList<MarketDayFeature> Build(IEnumerable<Bar> source, string symbol)
    {
        ArgumentNullException.ThrowIfNull(source);
        var normalizedSymbol = InstrumentCatalog.ExtractRoot(symbol);
        if (string.IsNullOrWhiteSpace(normalizedSymbol))
            throw new ArgumentException("A market-data symbol is required.", nameof(symbol));

        var sessions = new SortedDictionary<DateOnly, SessionBars>();
        var uniqueBars = source
            .Where(bar => bar.High >= bar.Low && bar.Open >= bar.Low && bar.Open <= bar.High && bar.Close >= bar.Low && bar.Close <= bar.High)
            .OrderBy(bar => bar.EventUtc)
            .ThenBy(bar => bar.Id)
            .GroupBy(bar => bar.EventUtc)
            .Select(group => group.First());

        foreach (var bar in uniqueBars)
        {
            var local = TimeZoneInfo.ConvertTime(bar.EventUtc, _sessionTimeZone);
            var localDate = DateOnly.FromDateTime(local.DateTime);
            var localTime = TimeOnly.FromDateTime(local.DateTime);
            var sessionDate = localTime >= _options.RthEnd ? localDate.AddDays(1) : localDate;
            if (!sessions.TryGetValue(sessionDate, out var session))
            {
                session = new SessionBars();
                sessions[sessionDate] = session;
            }

            if (localTime >= _options.RthStart && localTime < _options.RthEnd)
                session.Rth.Add(bar);
            else
                session.Overnight.Add(bar);
        }

        var output = new List<MarketDayFeature>();
        var trueRanges = new List<decimal>();
        MarketDayFeature? previous = null;
        foreach (var entry in sessions)
        {
            var rth = entry.Value.Rth.OrderBy(bar => bar.EventUtc).ThenBy(bar => bar.Id).ToArray();
            if (rth.Length < _options.MinimumRthBars)
                continue;

            var overnight = entry.Value.Overnight.OrderBy(bar => bar.EventUtc).ThenBy(bar => bar.Id).ToArray();
            var open = rth[0].Open;
            var close = rth[^1].Close;
            var high = rth.Max(bar => bar.High);
            var low = rth.Min(bar => bar.Low);
            var range = high - low;
            var openToClose = close - open;
            var path = Math.Abs(rth[0].Close - open) + rth.Skip(1).Zip(rth, (current, prior) => Math.Abs(current.Close - prior.Close)).Sum();
            var direction = DirectionFor(openToClose);
            var previousClose = previous?.Close;
            var trueRange = previousClose.HasValue
                ? Math.Max(range, Math.Max(Math.Abs(high - previousClose.Value), Math.Abs(low - previousClose.Value)))
                : range;
            var atr20 = trueRanges.Count >= _options.AtrPeriod
                ? trueRanges.TakeLast(_options.AtrPeriod).Average()
                : (decimal?)null;
            var volatilityMeasure = trueRanges.Count >= _options.MinimumVolatilityHistory
                ? trueRanges.TakeLast(Math.Min(_options.AtrPeriod, trueRanges.Count)).Average()
                : (decimal?)null;

            var vwap = VwapSeries(rth);
            var aboveVwap = vwap.Length == 0 ? (decimal?)null : rth.Zip(vwap, (bar, value) => bar.Close > value).Count(value => value) / (decimal)rth.Length;
            var belowVwap = vwap.Length == 0 ? (decimal?)null : rth.Zip(vwap, (bar, value) => bar.Close < value).Count(value => value) / (decimal)rth.Length;
            var crossings = CountVwapCrossings(rth, vwap);
            var comparisons = rth.Skip(1).Zip(rth, (current, prior) => new
            {
                HigherHigh = current.High > prior.High,
                HigherLow = current.Low > prior.Low,
                LowerHigh = current.High < prior.High,
                LowerLow = current.Low < prior.Low
            }).ToArray();
            var comparisonCount = comparisons.Length;
            var favorable = direction switch
            {
                MarketDirection.Up => high - open,
                MarketDirection.Down => open - low,
                _ => Math.Max(high - open, open - low)
            };
            var countertrend = direction switch
            {
                MarketDirection.Up => open - low,
                MarketDirection.Down => high - open,
                _ => Math.Min(high - open, open - low)
            };
            var overnightOpen = overnight.Length == 0 ? (decimal?)null : overnight[0].Open;
            var overnightClose = overnight.Length == 0 ? (decimal?)null : overnight[^1].Close;
            var overnightHigh = overnight.Length == 0 ? (decimal?)null : overnight.Max(bar => bar.High);
            var overnightLow = overnight.Length == 0 ? (decimal?)null : overnight.Min(bar => bar.Low);
            var overnightRange = overnightHigh.HasValue && overnightLow.HasValue ? overnightHigh.Value - overnightLow.Value : (decimal?)null;
            var gap = previousClose.HasValue ? open - previousClose.Value : (decimal?)null;

            var feature = new MarketDayFeature
            {
                TradeDate = entry.Key,
                Symbol = normalizedSymbol,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                RthRangePoints = range,
                Atr20 = atr20,
                NormalizedRange = atr20 is > 0m ? range / atr20.Value : null,
                OpenToClosePoints = openToClose,
                DirectionalEfficiency = range > 0m ? Math.Abs(openToClose) / range : 0m,
                PathEfficiency = path > 0m ? Math.Abs(openToClose) / path : 0m,
                CloseLocation = range > 0m ? (close - low) / range : null,
                VwapCrossings = crossings,
                PercentSessionAboveVwap = aboveVwap,
                PercentSessionBelowVwap = belowVwap,
                PercentHigherHighs = comparisonCount == 0 ? null : comparisons.Count(item => item.HigherHigh) / (decimal)comparisonCount,
                PercentHigherLows = comparisonCount == 0 ? null : comparisons.Count(item => item.HigherLow) / (decimal)comparisonCount,
                PercentLowerHighs = comparisonCount == 0 ? null : comparisons.Count(item => item.LowerHigh) / (decimal)comparisonCount,
                PercentLowerLows = comparisonCount == 0 ? null : comparisons.Count(item => item.LowerLow) / (decimal)comparisonCount,
                MaximumFavorableDirectionalExcursion = favorable,
                MaximumCountertrendExcursion = countertrend,
                OvernightHigh = overnightHigh,
                OvernightLow = overnightLow,
                OvernightRange = overnightRange,
                OvernightDirection = overnightOpen.HasValue && overnightClose.HasValue
                    ? DirectionFor(overnightClose.Value - overnightOpen.Value)
                    : MarketDirection.Neutral,
                GapFromPriorRthClose = gap,
                GapDirection = gap.HasValue ? DirectionFor(gap.Value) : MarketDirection.Neutral,
                VolatilityMeasurePoints = volatilityMeasure,
                Direction = direction,
                RthBarCount = rth.Length,
                OvernightBarCount = overnight.Length
            };
            output.Add(feature);
            trueRanges.Add(trueRange);
            previous = feature;
        }

        return output;
    }

    private decimal[] VwapSeries(IReadOnlyList<Bar> bars)
    {
        if (bars.Count == 0) return Array.Empty<decimal>();
        var useVolume = bars.All(bar => bar.Volume.HasValue && bar.Volume.Value >= 0) && bars.Sum(bar => bar.Volume ?? 0) > 0;
        decimal cumulativePrice = 0m;
        decimal cumulativeWeight = 0m;
        var values = new decimal[bars.Count];
        for (var index = 0; index < bars.Count; index++)
        {
            var bar = bars[index];
            var typicalPrice = (bar.High + bar.Low + bar.Close) / 3m;
            var weight = useVolume ? bar.Volume!.Value : 1m;
            cumulativePrice += typicalPrice * weight;
            cumulativeWeight += weight;
            values[index] = cumulativeWeight > 0m ? cumulativePrice / cumulativeWeight : typicalPrice;
        }
        return values;
    }

    private static int CountVwapCrossings(IReadOnlyList<Bar> bars, IReadOnlyList<decimal> vwap)
    {
        if (bars.Count == 0 || bars.Count != vwap.Count) return 0;
        int? previousSign = null;
        var crossings = 0;
        for (var index = 0; index < bars.Count; index++)
        {
            var sign = bars[index].Close > vwap[index] ? 1 : bars[index].Close < vwap[index] ? -1 : 0;
            if (sign == 0) continue;
            if (previousSign.HasValue && previousSign.Value != sign) crossings++;
            previousSign = sign;
        }
        return crossings;
    }

    private static MarketDirection DirectionFor(decimal change) => change > 0m
        ? MarketDirection.Up
        : change < 0m ? MarketDirection.Down : MarketDirection.Neutral;

    private sealed class SessionBars
    {
        public List<Bar> Rth { get; } = new();
        public List<Bar> Overnight { get; } = new();
    }
}

public sealed class MarketRegimeClassifier
{
    private readonly MarketRegimeOptions _options;

    public MarketRegimeClassifier(MarketRegimeOptions? options = null)
    {
        _options = options ?? new MarketRegimeOptions();
        _options.Validate();
    }

    public IReadOnlyList<MarketDayFeature> Classify(IEnumerable<MarketDayFeature> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var days = source.OrderBy(day => day.TradeDate).ThenBy(day => day.Symbol, StringComparer.OrdinalIgnoreCase).ToArray();
        var volatility = new EmpiricalDistribution(days.Where(day => day.VolatilityMeasurePoints.HasValue).Select(day => day.VolatilityMeasurePoints!.Value));
        var efficiency = new EmpiricalDistribution(days.Select(day => day.DirectionalEfficiency));
        var path = new EmpiricalDistribution(days.Select(day => day.PathEfficiency));
        var closeExtreme = new EmpiricalDistribution(days.Where(day => day.CloseLocation.HasValue).Select(day => CloseExtreme(day.CloseLocation!.Value)));
        var vwapPersistence = new EmpiricalDistribution(days.Where(day => day.HasVwapData).Select(VwapPersistence));
        var countertrend = new EmpiricalDistribution(days
            .Where(day => day.RthRangePoints > 0m && day.MaximumCountertrendExcursion.HasValue)
            .Select(day => day.MaximumCountertrendExcursion!.Value / day.RthRangePoints));
        var structure = new EmpiricalDistribution(days
            .Where(day => day.Direction != MarketDirection.Neutral)
            .Select(StructureBias));

        return days.Select(day =>
        {
            var components = new List<decimal>
            {
                efficiency.Rank(day.DirectionalEfficiency),
                path.Rank(day.PathEfficiency)
            };
            if (day.CloseLocation.HasValue) components.Add(closeExtreme.Rank(CloseExtreme(day.CloseLocation.Value)));
            if (day.HasVwapData) components.Add(vwapPersistence.Rank(VwapPersistence(day)));
            if (day.RthRangePoints > 0m && day.MaximumCountertrendExcursion.HasValue)
                components.Add(1m - countertrend.Rank(day.MaximumCountertrendExcursion.Value / day.RthRangePoints));
            if (day.Direction != MarketDirection.Neutral) components.Add(structure.Rank(StructureBias(day)));

            var score = components.Count == 0 ? 0m : components.Average();
            var strongCount = components.Count(value => value >= _options.StrongComponentPercentile);
            var weakCount = components.Count(value => value >= _options.WeakComponentPercentile);
            var marketType = day.Direction != MarketDirection.Neutral && day.RthRangePoints > 0m &&
                strongCount >= _options.StrongMinimumComponents && score >= _options.StrongScoreThreshold
                ? MarketType.StrongTrend
                : day.Direction != MarketDirection.Neutral && weakCount >= _options.WeakMinimumComponents && score >= _options.WeakScoreThreshold
                    ? MarketType.WeakTrend
                    : MarketType.Range;
            var regime = day.VolatilityMeasurePoints.HasValue
                ? volatility.Classify(day.VolatilityMeasurePoints.Value, _options)
                : VolatilityRegime.Unknown;

            return day with
            {
                MarketType = marketType,
                VolatilityRegime = regime,
                ClassifierVersion = _options.ClassifierVersion
            };
        }).ToArray();
    }

    private static decimal CloseExtreme(decimal location) => Math.Max(location, 1m - location);

    private static decimal VwapPersistence(MarketDayFeature day) => Math.Max(day.PercentSessionAboveVwap!.Value, day.PercentSessionBelowVwap!.Value);

    private static decimal StructureBias(MarketDayFeature day) => day.Direction switch
    {
        MarketDirection.Up => ((day.PercentHigherHighs ?? 0m) + (day.PercentHigherLows ?? 0m)) / 2m,
        MarketDirection.Down => ((day.PercentLowerHighs ?? 0m) + (day.PercentLowerLows ?? 0m)) / 2m,
        _ => 0.5m
    };

    private sealed class EmpiricalDistribution
    {
        private readonly decimal[] _values;

        public EmpiricalDistribution(IEnumerable<decimal> values) => _values = values.OrderBy(value => value).ToArray();

        public decimal Rank(decimal value)
        {
            if (_values.Length == 0) return 0.5m;
            var lower = LowerBound(value);
            var upper = UpperBound(value);
            return (lower + (upper - lower) / 2m) / _values.Length;
        }

        public VolatilityRegime Classify(decimal value, MarketRegimeOptions options)
        {
            var rank = Rank(value);
            if (rank < options.LowVolatilityPercentile) return VolatilityRegime.Low;
            if (rank < options.HighVolatilityPercentile) return VolatilityRegime.Normal;
            if (rank < options.ExtremeVolatilityPercentile) return VolatilityRegime.High;
            return VolatilityRegime.Extreme;
        }

        private int LowerBound(decimal value)
        {
            var low = 0;
            var high = _values.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (_values[middle] < value) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private int UpperBound(decimal value)
        {
            var low = 0;
            var high = _values.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (_values[middle] <= value) low = middle + 1;
                else high = middle;
            }
            return low;
        }
    }
}

public sealed class MarketProbabilityService
{
    private static readonly string[] EsMesRoots = ["ES", "MES"];
    private readonly MarketRegimeOptions _options;
    private readonly MarketDayFeatureCacheService _featureCache;
    private readonly MarketRegimeClassifier _classifier;

    public MarketProbabilityService(TradeFoundryDb database)
        : this(database, new MarketRegimeOptions())
    {
    }

    public MarketProbabilityService(TradeFoundryDb database, MarketRegimeOptions options)
    {
        _ = database ?? throw new ArgumentNullException(nameof(database));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _featureCache = new MarketDayFeatureCacheService(database, _options);
        _classifier = new MarketRegimeClassifier(_options);
    }

    public MarketProbabilityService(TradeFoundryDb database, MarketDayFeatureCacheService featureCache)
    {
        _ = database ?? throw new ArgumentNullException(nameof(database));
        _featureCache = featureCache ?? throw new ArgumentNullException(nameof(featureCache));
        _options = featureCache.Options;
        _options.Validate();
        _classifier = new MarketRegimeClassifier(_options);
    }

    public MarketProbabilityQueryResult GetProbabilities(Guid journalId, MarketProbabilityQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        return BuildProbabilities(query, LoadAccessibleDays(journalId, query));
    }

    private MarketProbabilityQueryResult BuildProbabilities(MarketProbabilityQuery query, IReadOnlyList<MarketDayFeature> accessible)
    {
        var matches = accessible.Where(day => Matches(day, query)).ToArray();
        var overallQuery = query with
        {
            Month = null,
            DayOfWeek = null,
            VolatilityRegime = null,
            OvernightDirection = null,
            GapDirection = null,
            RangeGreaterThanPoints = null,
            RangeLessThanPoints = null,
            NormalizedRangeGreaterThan = null,
            NormalizedRangeLessThan = null
        };
        var overall = accessible.Where(day => Matches(day, overallQuery)).ToArray();
        var thresholdQuery = query with
        {
            RangeGreaterThanPoints = null,
            RangeLessThanPoints = null,
            NormalizedRangeGreaterThan = null,
            NormalizedRangeLessThan = null
        };
        var thresholdOverallQuery = overallQuery with
        {
            RangeGreaterThanPoints = null,
            RangeLessThanPoints = null,
            NormalizedRangeGreaterThan = null,
            NormalizedRangeLessThan = null
        };
        var thresholdMatches = accessible.Where(day => Matches(day, thresholdQuery)).ToArray();
        var thresholdOverall = accessible.Where(day => Matches(day, thresholdOverallQuery)).ToArray();
        var priorQuery = BuildPriorQuery(query);
        var prior = accessible.Where(day => Matches(day, priorQuery)).ToArray();
        var baselines = new List<MarketProbabilityBaseline>
        {
            BuildBaseline("overall", overall)
        };
        if (query.DayOfWeek.HasValue)
            baselines.Add(BuildBaseline("day_of_week", accessible.Where(day => Matches(day, overallQuery with { DayOfWeek = query.DayOfWeek })).ToArray()));
        if (query.Month.HasValue)
            baselines.Add(BuildBaseline("month", accessible.Where(day => Matches(day, overallQuery with { Month = query.Month })).ToArray()));
        if (query.Month.HasValue && query.DayOfWeek.HasValue)
            baselines.Add(BuildBaseline("shrinkage_prior", prior));

        var marketTypes = BuildEstimates(
            [MarketType.StrongTrend.ToString(), MarketType.WeakTrend.ToString(), MarketType.Range.ToString()],
            matches.Select(day => day.MarketType.ToString()),
            overall.Select(day => day.MarketType.ToString()),
            prior.Select(day => day.MarketType.ToString()),
            "market-type",
            _options.BayesianPriorStrength);
        var directions = BuildEstimates(
            [MarketDirection.Up.ToString(), MarketDirection.Down.ToString(), MarketDirection.Neutral.ToString()],
            matches.Select(day => day.Direction.ToString()),
            overall.Select(day => day.Direction.ToString()),
            prior.Select(day => day.Direction.ToString()),
            "direction",
            _options.BayesianPriorStrength);

        var targetTrend = matches.Where(IsTrend).ToArray();
        var overallTrend = overall.Where(IsTrend).ToArray();
        var priorTrend = prior.Where(IsTrend).ToArray();
        var conditional = new ConditionalDirectionResult
        {
            TrendSampleSize = targetTrend.Length,
            Directions = BuildEstimates(
                [MarketDirection.Up.ToString(), MarketDirection.Down.ToString(), MarketDirection.Neutral.ToString()],
                targetTrend.Select(day => day.Direction.ToString()),
                overallTrend.Select(day => day.Direction.ToString()),
                priorTrend.Select(day => day.Direction.ToString()),
                "direction-given-trend",
                _options.BayesianPriorStrength)
        };

        var warnings = new List<string>();
        if (accessible.Count == 0) warnings.Add("No qualifying intraday RTH sessions were found in the stored market data.");
        if (matches.Length == 0) warnings.Add("No sessions matched the requested filters.");
        if (accessible.Any(day => !day.Atr20.HasValue)) warnings.Add("ATR20 and normalized range are unavailable during the 20-session warm-up; those fields are omitted for early sessions.");
        if (accessible.Any(day => day.VolatilityRegime == VolatilityRegime.Unknown)) warnings.Add("Volatility regime is unknown during the minimum trailing-history warm-up.");

        return new MarketProbabilityQueryResult
        {
            PopulationSymbol = PopulationSymbol(query.Symbol),
            FilterDescription = Describe(query),
            SampleSize = matches.Length,
            AvailableObservationCount = accessible.Count,
            ClassifierVersion = _options.ClassifierVersion,
            SessionDefinition = $"RTH {_options.RthStart:HH\\:mm}–{_options.RthEnd:HH\\:mm} {_options.SessionTimeZoneId}; overnight is {_options.RthEnd:HH\\:mm}–{_options.RthStart:HH\\:mm} for the next RTH date.",
            MarketTypes = marketTypes,
            Directions = directions,
            DirectionGivenTrend = conditional,
            RthRange = BuildRangeDistribution(matches.Select(day => day.RthRangePoints)),
            NormalizedRange = BuildRangeDistribution(matches.Where(day => day.NormalizedRange.HasValue).Select(day => day.NormalizedRange!.Value)),
            ThresholdProbabilities = BuildThresholds(query, thresholdMatches, thresholdOverall),
            Baselines = baselines,
            ShrinkagePriorScope = DescribePriorScope(query),
            ShrinkagePriorSampleSize = prior.Length,
            DataQualityWarnings = warnings.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    public MarketDaysResult GetMarketDays(Guid journalId, MarketProbabilityQuery query, int limit = 100, string? cursor = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        if (limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(limit), "The market-day page size must be between 1 and 500.");

        return BuildMarketDays(query, LoadAccessibleDays(journalId, query), limit, cursor);
    }

    private MarketDaysResult BuildMarketDays(MarketProbabilityQuery query, IReadOnlyList<MarketDayFeature> accessible, int limit, string? cursor)
    {
        var matches = accessible.Where(day => Matches(day, query)).OrderBy(day => day.TradeDate).ThenBy(day => day.Symbol, StringComparer.OrdinalIgnoreCase).ToArray();
        var offset = DecodeCursor(cursor);
        if (offset > matches.Length) throw new ArgumentException("cursor is invalid.", nameof(cursor));
        var page = matches.Skip(offset).Take(limit).ToArray();
        var nextOffset = offset + page.Length;
        return new MarketDaysResult
        {
            PopulationSymbol = PopulationSymbol(query.Symbol),
            TotalMatches = matches.Length,
            Truncated = nextOffset < matches.Length,
            NextCursor = nextOffset < matches.Length ? EncodeCursor(nextOffset) : null,
            Days = page,
            DataQualityWarnings = accessible.Count == 0
                ? ["No qualifying intraday RTH sessions were found in the stored market data."]
                : query.AsOfDate.HasValue
                    ? [$"AsOfDate is exclusive: observations on or after {query.AsOfDate.Value:yyyy-MM-dd} were excluded."]
                    : Array.Empty<string>()
        };
    }

    public MarketProbabilityPageSnapshot GetPageSnapshot(Guid journalId, MarketProbabilityQuery query, int recentDayLimit = 100, string? cursor = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        if (recentDayLimit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(recentDayLimit), "The market-day page size must be between 1 and 500.");

        var accessible = LoadAccessibleDays(journalId, query);
        return new MarketProbabilityPageSnapshot
        {
            Probabilities = BuildProbabilities(query, accessible),
            RecentDays = BuildMarketDays(query, accessible, recentDayLimit, cursor)
        };
    }

    public IReadOnlyList<MarketDayFeature> GetClassifiedDays(Guid journalId, MarketProbabilityQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        return LoadAccessibleDays(journalId, query).Where(day => Matches(day, query)).ToArray();
    }

    private IReadOnlyList<MarketDayFeature> LoadAccessibleDays(Guid journalId, MarketProbabilityQuery query)
    {
        var roots = ResolveRoots(query.Symbol);
        var coverage = roots
            .Select(root => _featureCache.GetSourceSeries(journalId, root))
            .Where(series => series is not null)
            .Select(series => series!)
            .ToArray();
        if (coverage.Length == 0) return Array.Empty<MarketDayFeature>();

        var lastAvailableDate = coverage
            .Where(series => series.LastEventUtc.HasValue)
            .Select(series => SessionDate(series.LastEventUtc!.Value))
            .DefaultIfEmpty()
            .Max();
        var accessibleEnd = query.AsOfDate?.AddDays(-1) ?? lastAvailableDate;
        if (query.EndDate.HasValue && query.EndDate.Value < accessibleEnd)
            accessibleEnd = query.EndDate.Value;
        var firstAvailableDate = coverage
            .Where(series => series.FirstEventUtc.HasValue)
            .Select(series => SessionDate(series.FirstEventUtc!.Value))
            .DefaultIfEmpty()
            .Min();
        if (accessibleEnd < firstAvailableDate) return Array.Empty<MarketDayFeature>();

        var rawDays = _featureCache.EnsureFeatures(journalId, coverage.Select(series => series.Symbol), accessibleEnd);

        var canonicalDays = rawDays
            .Where(day => day.TradeDate <= accessibleEnd)
            .GroupBy(day => day.TradeDate)
            .Select(group => group
                .OrderByDescending(day => day.RthBarCount)
                .ThenBy(day => day.Symbol.Equals("ES", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(day => day.Symbol, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(day => day.TradeDate)
            .ToArray();
        return _classifier.Classify(canonicalDays);
    }

    private DateOnly SessionDate(DateTimeOffset timestamp)
    {
        var local = TimeZoneInfo.ConvertTime(timestamp, TimeZoneCatalog.Resolve(_options.SessionTimeZoneId));
        var date = DateOnly.FromDateTime(local.DateTime);
        return TimeOnly.FromDateTime(local.DateTime) >= _options.RthEnd ? date.AddDays(1) : date;
    }

    private static string[] ResolveRoots(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) || symbol.Trim().Equals("ES/MES", StringComparison.OrdinalIgnoreCase) || symbol.Trim().Equals("ESMES", StringComparison.OrdinalIgnoreCase))
            return EsMesRoots;
        return [InstrumentCatalog.ExtractRoot(symbol)];
    }

    private static string PopulationSymbol(string? symbol) => ResolveRoots(symbol).Length == 2 ? "ES/MES" : InstrumentCatalog.ExtractRoot(symbol ?? string.Empty);

    private static bool Matches(MarketDayFeature day, MarketProbabilityQuery query)
    {
        if (query.Month.HasValue && day.Month != query.Month.Value) return false;
        if (query.DayOfWeek.HasValue && day.DayOfWeek != query.DayOfWeek.Value) return false;
        if (query.StartDate.HasValue && day.TradeDate < query.StartDate.Value) return false;
        if (query.EndDate.HasValue && day.TradeDate > query.EndDate.Value) return false;
        if (query.VolatilityRegime.HasValue && day.VolatilityRegime != query.VolatilityRegime.Value) return false;
        if (query.OvernightDirection.HasValue && day.OvernightDirection != query.OvernightDirection.Value) return false;
        if (query.GapDirection.HasValue && day.GapDirection != query.GapDirection.Value) return false;
        if (query.RangeGreaterThanPoints.HasValue && day.RthRangePoints <= query.RangeGreaterThanPoints.Value) return false;
        if (query.RangeLessThanPoints.HasValue && day.RthRangePoints >= query.RangeLessThanPoints.Value) return false;
        if (query.NormalizedRangeGreaterThan.HasValue && (!day.NormalizedRange.HasValue || day.NormalizedRange.Value <= query.NormalizedRangeGreaterThan.Value)) return false;
        if (query.NormalizedRangeLessThan.HasValue && (!day.NormalizedRange.HasValue || day.NormalizedRange.Value >= query.NormalizedRangeLessThan.Value)) return false;
        return true;
    }

    private static MarketProbabilityQuery BuildPriorQuery(MarketProbabilityQuery query)
    {
        var prior = query with
        {
            VolatilityRegime = null,
            OvernightDirection = null,
            GapDirection = null,
            RangeGreaterThanPoints = null,
            RangeLessThanPoints = null,
            NormalizedRangeGreaterThan = null,
            NormalizedRangeLessThan = null
        };
        if (query.Month.HasValue && query.DayOfWeek.HasValue)
            return prior with { Month = null };
        if (query.Month.HasValue)
            return prior with { Month = null };
        if (query.DayOfWeek.HasValue)
            return prior with { DayOfWeek = null };
        return prior with { Month = null, DayOfWeek = null };
    }

    private static bool IsTrend(MarketDayFeature day) => day.MarketType is MarketType.StrongTrend or MarketType.WeakTrend;

    private IReadOnlyList<ProbabilityEstimate> BuildEstimates(
        IReadOnlyList<string> outcomes,
        IEnumerable<string> targetValues,
        IEnumerable<string> baselineValues,
        IEnumerable<string> priorValues,
        string priorScope,
        decimal priorStrength)
    {
        var target = targetValues.ToArray();
        var baseline = baselineValues.ToArray();
        var prior = priorValues.ToArray();
        var targetSample = target.Length;
        var baselineSample = baseline.Length;
        var priorSample = prior.Length;
        return outcomes.Select(outcome =>
        {
            var count = target.Count(value => value.Equals(outcome, StringComparison.Ordinal));
            var baselineCount = baseline.Count(value => value.Equals(outcome, StringComparison.Ordinal));
            var priorCount = prior.Count(value => value.Equals(outcome, StringComparison.Ordinal));
            if (targetSample == 0)
            {
                return new ProbabilityEstimate
                {
                    Outcome = outcome,
                    Count = 0,
                    SampleSize = 0,
                    ShrinkagePriorScope = priorScope,
                    ShrinkagePriorSampleSize = priorSample
                };
            }

            var raw = count / (decimal)targetSample;
            var baselineProbability = baselineSample == 0 ? (decimal?)null : baselineCount / (decimal)baselineSample;
            // Add one pseudo-observation to each category before deriving the
            // empirical prior. This keeps a prior category from becoming an
            // impossible outcome (alpha or beta equal to zero) when a small
            // broader cohort happens to contain only one observed category.
            var priorProbability = (priorCount + 1m) / (priorSample + outcomes.Count);
            var alpha = (double)(count + priorProbability * priorStrength);
            var beta = (double)((targetSample - count) + (1m - priorProbability) * priorStrength);
            var adjusted = (decimal)(alpha / (alpha + beta));
            var credible = ProbabilityMath.BetaInterval(alpha, beta);
            return new ProbabilityEstimate
            {
                Outcome = outcome,
                Count = count,
                SampleSize = targetSample,
                RawProbability = raw,
                AdjustedProbability = adjusted,
                BaselineProbability = baselineProbability,
                DifferenceFromBaseline = baselineProbability.HasValue ? adjusted - baselineProbability.Value : null,
                ConfidenceInterval = ProbabilityMath.WilsonInterval(count, targetSample),
                CredibleInterval = credible,
                ShrinkagePriorScope = priorScope,
                ShrinkagePriorSampleSize = priorSample,
                ShrinkagePriorProbability = priorProbability
            };
        }).ToArray();
    }

    private MarketProbabilityBaseline BuildBaseline(string scope, IReadOnlyList<MarketDayFeature> days)
    {
        var types = Enum.GetValues<MarketType>().Select(type =>
        {
            var count = days.Count(day => day.MarketType == type);
            return new BaselineProbability { Outcome = type.ToString(), Count = count, SampleSize = days.Count, RawProbability = days.Count == 0 ? null : count / (decimal)days.Count };
        }).ToArray();
        var directions = Enum.GetValues<MarketDirection>().Select(direction =>
        {
            var count = days.Count(day => day.Direction == direction);
            return new BaselineProbability { Outcome = direction.ToString(), Count = count, SampleSize = days.Count, RawProbability = days.Count == 0 ? null : count / (decimal)days.Count };
        }).ToArray();
        var trend = days.Where(IsTrend).ToArray();
        var trendDirections = Enum.GetValues<MarketDirection>().Select(direction =>
        {
            var count = trend.Count(day => day.Direction == direction);
            return new BaselineProbability { Outcome = direction.ToString(), Count = count, SampleSize = trend.Length, RawProbability = trend.Length == 0 ? null : count / (decimal)trend.Length };
        }).ToArray();
        return new MarketProbabilityBaseline
        {
            Scope = scope,
            SampleSize = days.Count,
            MarketTypes = types,
            Directions = directions,
            DirectionsGivenTrend = trendDirections
        };
    }

    private static IReadOnlyList<ThresholdProbability> BuildThresholds(MarketProbabilityQuery query, IReadOnlyList<MarketDayFeature> matches, IReadOnlyList<MarketDayFeature> overall)
    {
        var thresholds = new List<ThresholdProbability>();
        void Add(string metric, string op, decimal threshold, Func<MarketDayFeature, bool> predicate)
        {
            var count = matches.Count(predicate);
            var baselineCount = overall.Count(predicate);
            var raw = matches.Count == 0 ? (decimal?)null : count / (decimal)matches.Count;
            var baseline = overall.Count == 0 ? (decimal?)null : baselineCount / (decimal)overall.Count;
            thresholds.Add(new ThresholdProbability
            {
                Metric = metric,
                Operator = op,
                Threshold = threshold,
                Count = count,
                SampleSize = matches.Count,
                RawProbability = raw,
                BaselineProbability = baseline,
                DifferenceFromBaseline = raw.HasValue && baseline.HasValue ? raw - baseline : null,
                ConfidenceInterval = matches.Count == 0 ? new(null, null, "Wilson") : ProbabilityMath.WilsonInterval(count, matches.Count)
            });
        }

        if (query.RangeGreaterThanPoints.HasValue)
            Add("rth_range_points", ">", query.RangeGreaterThanPoints.Value, day => day.RthRangePoints > query.RangeGreaterThanPoints.Value);
        if (query.RangeLessThanPoints.HasValue)
            Add("rth_range_points", "<", query.RangeLessThanPoints.Value, day => day.RthRangePoints < query.RangeLessThanPoints.Value);
        if (query.NormalizedRangeGreaterThan.HasValue)
            Add("normalized_range", ">", query.NormalizedRangeGreaterThan.Value, day => day.NormalizedRange.HasValue && day.NormalizedRange.Value > query.NormalizedRangeGreaterThan.Value);
        if (query.NormalizedRangeLessThan.HasValue)
            Add("normalized_range", "<", query.NormalizedRangeLessThan.Value, day => day.NormalizedRange.HasValue && day.NormalizedRange.Value < query.NormalizedRangeLessThan.Value);
        return thresholds;
    }

    private static RangeDistribution BuildRangeDistribution(IEnumerable<decimal> source)
    {
        var values = source.OrderBy(value => value).ToArray();
        if (values.Length == 0) return new RangeDistribution();
        var mean = values.Average();
        var standardDeviation = values.Length < 2
            ? (decimal?)null
            : (decimal)Math.Sqrt((double)values.Sum(value => (value - mean) * (value - mean)) / (values.Length - 1));
        return new RangeDistribution
        {
            SampleSize = values.Length,
            Mean = mean,
            Median = Percentile(values, 0.50m),
            StandardDeviation = standardDeviation,
            P10 = Percentile(values, 0.10m),
            P25 = Percentile(values, 0.25m),
            P50 = Percentile(values, 0.50m),
            P75 = Percentile(values, 0.75m),
            P90 = Percentile(values, 0.90m)
        };
    }

    private static decimal Percentile(IReadOnlyList<decimal> values, decimal percentile)
    {
        if (values.Count == 0) return 0m;
        var position = (values.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return values[lower];
        var fraction = position - lower;
        return values[lower] + (values[upper] - values[lower]) * fraction;
    }

    private string Describe(MarketProbabilityQuery query)
    {
        var parts = new List<string> { PopulationSymbol(query.Symbol) };
        if (query.Month.HasValue) parts.Add(CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(query.Month.Value));
        if (query.DayOfWeek.HasValue) parts.Add(query.DayOfWeek.Value.ToString());
        if (query.VolatilityRegime.HasValue) parts.Add($"{query.VolatilityRegime.Value} volatility");
        if (query.OvernightDirection.HasValue) parts.Add($"overnight {query.OvernightDirection.Value}");
        if (query.GapDirection.HasValue) parts.Add($"gap {query.GapDirection.Value}");
        if (query.StartDate.HasValue) parts.Add($"from {query.StartDate.Value:yyyy-MM-dd}");
        if (query.EndDate.HasValue) parts.Add($"through {query.EndDate.Value:yyyy-MM-dd}");
        if (query.AsOfDate.HasValue) parts.Add($"as of {query.AsOfDate.Value:yyyy-MM-dd} exclusive");
        return string.Join(" · ", parts);
    }

    private static string DescribePriorScope(MarketProbabilityQuery query)
    {
        if (query.Month.HasValue && query.DayOfWeek.HasValue) return "day_of_week baseline with premarket and volatility conditioning removed";
        if (query.Month.HasValue) return "overall baseline with month conditioning removed";
        if (query.DayOfWeek.HasValue) return "overall baseline with day_of_week conditioning removed";
        return "overall baseline with conditioning removed";
    }

    private static string EncodeCursor(int offset) => "mdcur_" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        if (!cursor.StartsWith("mdcur_", StringComparison.Ordinal)) throw new ArgumentException("cursor is invalid.", nameof(cursor));
        var encoded = cursor[6..].Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
        try
        {
            var value = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset >= 0
                ? offset
                : throw new ArgumentException("cursor is invalid.", nameof(cursor));
        }
        catch (FormatException)
        {
            throw new ArgumentException("cursor is invalid.", nameof(cursor));
        }
    }
}

internal static class ProbabilityMath
{
    private const double Z95 = 1.959963984540054;

    public static ProbabilityInterval WilsonInterval(int successes, int sampleSize)
    {
        if (sampleSize <= 0) return new ProbabilityInterval(null, null, "Wilson");
        var n = sampleSize;
        var p = successes / (double)n;
        var denominator = 1d + Z95 * Z95 / n;
        var center = (p + Z95 * Z95 / (2d * n)) / denominator;
        var margin = Z95 * Math.Sqrt((p * (1d - p) + Z95 * Z95 / (4d * n)) / n) / denominator;
        return new ProbabilityInterval((decimal)Math.Max(0d, center - margin), (decimal)Math.Min(1d, center + margin), "Wilson 95%");
    }

    public static ProbabilityInterval BetaInterval(double alpha, double beta)
    {
        if (alpha <= 0d || beta <= 0d) return new ProbabilityInterval(null, null, "Beta posterior");
        var lower = BetaQuantile(0.025d, alpha, beta);
        var upper = BetaQuantile(0.975d, alpha, beta);
        return new ProbabilityInterval((decimal)lower, (decimal)upper, "Beta posterior 95% credible");
    }

    private static double BetaQuantile(double probability, double alpha, double beta)
    {
        var low = 0d;
        var high = 1d;
        for (var iteration = 0; iteration < 80; iteration++)
        {
            var middle = (low + high) / 2d;
            if (RegularizedIncompleteBeta(middle, alpha, beta) < probability) low = middle;
            else high = middle;
        }
        return (low + high) / 2d;
    }

    private static double RegularizedIncompleteBeta(double x, double a, double b)
    {
        if (x <= 0d) return 0d;
        if (x >= 1d) return 1d;
        var logBetaTerm = LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * Math.Log(x) + b * Math.Log(1d - x);
        var betaTerm = Math.Exp(logBetaTerm);
        return x < (a + 1d) / (a + b + 2d)
            ? betaTerm * ContinuedFraction(a, b, x) / a
            : 1d - betaTerm * ContinuedFraction(b, a, 1d - x) / b;
    }

    private static double ContinuedFraction(double a, double b, double x)
    {
        const int maxIterations = 200;
        const double epsilon = 3e-14;
        const double minimum = 1e-300;
        var qab = a + b;
        var qap = a + 1d;
        var qam = a - 1d;
        var c = 1d;
        var d = 1d - qab * x / qap;
        if (Math.Abs(d) < minimum) d = minimum;
        d = 1d / d;
        var h = d;
        for (var m = 1; m <= maxIterations; m++)
        {
            var m2 = 2d * m;
            var aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1d + aa * d;
            if (Math.Abs(d) < minimum) d = minimum;
            c = 1d + aa / c;
            if (Math.Abs(c) < minimum) c = minimum;
            d = 1d / d;
            h *= d * c;
            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1d + aa * d;
            if (Math.Abs(d) < minimum) d = minimum;
            c = 1d + aa / c;
            if (Math.Abs(c) < minimum) c = minimum;
            d = 1d / d;
            var delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1d) < epsilon) break;
        }
        return h;
    }

    private static double LogGamma(double value)
    {
        double[] coefficients =
        [
            676.5203681218851,
            -1259.1392167224028,
            771.32342877765313,
            -176.61502916214059,
            12.507343278686905,
            -0.13857109526572012,
            9.9843695780195716e-6,
            1.5056327351493116e-7
        ];
        if (value < 0.5d)
            return Math.Log(Math.PI) - Math.Log(Math.Sin(Math.PI * value)) - LogGamma(1d - value);
        value -= 1d;
        var x = 0.99999999999980993d;
        for (var index = 0; index < coefficients.Length; index++) x += coefficients[index] / (value + index + 1d);
        var t = value + coefficients.Length - 0.5d;
        return 0.5d * Math.Log(2d * Math.PI) + (value + 0.5d) * Math.Log(t) - t + Math.Log(x);
    }
}

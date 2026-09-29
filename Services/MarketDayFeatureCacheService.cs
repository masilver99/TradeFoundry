using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services;

/// <summary>
/// Owns the persisted raw-feature cache.  The cache is deliberately keyed by
/// feature version, source interval, symbol, and trade date; classifier output
/// remains an in-memory view of those raw rows so AsOfDate and classifier
/// version changes cannot silently reuse stale labels.
/// </summary>
public sealed class MarketDayFeatureCacheService
{
    private static readonly string[] EsMesRoots = ["ES", "MES"];
    private readonly TradeFoundryDb _database;
    private readonly MarketRegimeOptions _options;
    private readonly MarketDayFeatureBuilder _builder;
    private readonly TimeZoneInfo _sessionTimeZone;
    private readonly object _sync = new();
    private readonly Dictionary<string, BarSeriesInfo?> _sourceSeriesByRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sourceDirtySignatureByRoot = new(StringComparer.OrdinalIgnoreCase);

    public MarketDayFeatureCacheService(TradeFoundryDb database)
        : this(database, new MarketRegimeOptions())
    {
    }

    public MarketDayFeatureCacheService(TradeFoundryDb database, MarketRegimeOptions options)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _builder = new MarketDayFeatureBuilder(_options);
        _sessionTimeZone = TimeZoneCatalog.Resolve(_options.SessionTimeZoneId);
    }

    public MarketRegimeOptions Options => _options;

    public BarSeriesInfo? GetSourceSeries(Guid journalId, string root)
    {
        var normalizedRoot = InstrumentCatalog.ExtractRoot(root);
        lock (_sync)
        {
            var dirty = _database.GetMarketFeatureDirtyRanges(normalizedRoot);
            var signature = string.Join("|", dirty.Select(range => $"{range.StartDate:yyyy-MM-dd}:{range.EndDate:yyyy-MM-dd}"));
            if (_sourceSeriesByRoot.TryGetValue(normalizedRoot, out var cached) &&
                (dirty.Count == 0 || (_sourceDirtySignatureByRoot.TryGetValue(normalizedRoot, out var previousSignature) && previousSignature == signature)))
                return cached;
            var selected = SelectSourceSeries(journalId, normalizedRoot);
            _sourceSeriesByRoot[normalizedRoot] = selected;
            _sourceDirtySignatureByRoot[normalizedRoot] = signature;
            return selected;
        }
    }

    public IReadOnlyList<MarketDayFeature> EnsureFeatures(Guid journalId, IEnumerable<string> roots, DateOnly accessibleEnd)
    {
        ArgumentNullException.ThrowIfNull(roots);
        lock (_sync)
        {
            var output = new List<MarketDayFeature>();
            foreach (var root in roots
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Select(InstrumentCatalog.ExtractRoot)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var selected = GetSourceSeries(journalId, root);
                if (selected is null || !selected.FirstEventUtc.HasValue || !selected.LastEventUtc.HasValue)
                    continue;

                var firstDate = SessionDate(selected.FirstEventUtc.Value);
                var lastDate = SessionDate(selected.LastEventUtc.Value);
                var targetEnd = accessibleEnd < lastDate ? accessibleEnd : lastDate;
                if (targetEnd < firstDate) continue;
                output.AddRange(EnsureRoot(journalId, root, selected.Interval, firstDate, targetEnd));
            }
            return output;
        }
    }

    /// <summary>
    /// Idempotent backfill/rebuild entry point for startup jobs, diagnostics,
    /// and future admin UI.  The optional date window describes the rows to
    /// replace; feature warm-up is loaded before that window automatically.
    /// </summary>
    public int Rebuild(Guid journalId, string? symbol = null, DateOnly? startDate = null, DateOnly? endDate = null)
    {
        var roots = ResolveRoots(symbol);
        lock (_sync)
        {
            var rebuilt = 0;
            foreach (var root in roots)
            {
                var selected = GetSourceSeries(journalId, root);
                if (selected is null || !selected.FirstEventUtc.HasValue || !selected.LastEventUtc.HasValue)
                    continue;
                var firstDate = SessionDate(selected.FirstEventUtc.Value);
                var lastDate = SessionDate(selected.LastEventUtc.Value);
                var replaceStart = startDate ?? firstDate;
                var replaceEnd = endDate ?? lastDate;
                if (replaceStart < firstDate) replaceStart = firstDate;
                if (replaceEnd > lastDate) replaceEnd = lastDate;
                if (replaceStart > replaceEnd) continue;
                rebuilt += RebuildRoot(journalId, root, selected.Interval, replaceStart, replaceEnd);
            }
            return rebuilt;
        }
    }

    private IReadOnlyList<MarketDayFeature> EnsureRoot(Guid journalId, string root, string interval, DateOnly firstDate, DateOnly targetEnd)
    {
        var persisted = _database.GetMarketDayFeatureRows(root, interval, _options.FeatureVersion, endDate: targetEnd);
        var rebuildStart = (DateOnly?)null;
        var rebuildEnd = (DateOnly?)null;

        if (persisted.Count == 0)
        {
            rebuildStart = firstDate;
            rebuildEnd = targetEnd;
        }
        else
        {
            var minimum = persisted.Min(row => row.TradeDate);
            var maximum = persisted.Max(row => row.TradeDate);
            if (minimum > firstDate)
                MergeWindow(ref rebuildStart, ref rebuildEnd, firstDate, targetEnd);
            if (maximum < targetEnd)
                MergeWindow(ref rebuildStart, ref rebuildEnd, WarmupStart(maximum.AddDays(1), firstDate), targetEnd);
        }

        foreach (var dirty in _database.GetMarketFeatureDirtyRanges(root))
        {
            if (dirty.EndDate < firstDate || dirty.StartDate > targetEnd) continue;
            var start = WarmupStart(dirty.StartDate, firstDate);
            var end = dirty.EndDate.AddDays(_options.FeatureWarmupCalendarDays);
            if (end > targetEnd) end = targetEnd;
            MergeWindow(ref rebuildStart, ref rebuildEnd, start, end);
        }

        if (rebuildStart.HasValue && rebuildEnd.HasValue && rebuildStart <= rebuildEnd)
        {
            RebuildRoot(journalId, root, interval, rebuildStart.Value, rebuildEnd.Value);
            persisted = _database.GetMarketDayFeatureRows(root, interval, _options.FeatureVersion, endDate: targetEnd);
        }

        return persisted.Select(MarketFeaturePersistence.FromRow).ToArray();
    }

    private int RebuildRoot(Guid journalId, string root, string interval, DateOnly replaceStart, DateOnly replaceEnd)
    {
        var loadStart = WarmupStart(replaceStart, replaceStart.AddDays(-_options.FeatureWarmupCalendarDays));
        var rangeStart = LocalBoundary(loadStart.AddDays(-2), TimeOnly.MinValue);
        var rangeEnd = LocalBoundary(replaceEnd, _options.RthEnd);
        var bars = _database.GetBarsForTrade(journalId, root, rangeStart, rangeEnd, interval);
        var built = _builder.Build(bars, root)
            .Where(day => day.TradeDate >= replaceStart && day.TradeDate <= replaceEnd)
            .ToArray();
        var rows = built.Select(day => MarketFeaturePersistence.ToRow(day, interval, _options.FeatureVersion)).ToArray();
        _database.ReplaceMarketDayFeatureRows(root, interval, _options.FeatureVersion, replaceStart, replaceEnd, rows);
        _database.ClearMarketFeatureDirtyRanges(root, replaceStart, replaceEnd);
        return rows.Length;
    }

    private BarSeriesInfo? SelectSourceSeries(Guid journalId, string root) => _database
        .GetBarSeries(journalId, root)
        .Where(series => series.IntervalMinutes is > 0 and < 1440)
        .OrderBy(series => series.IntervalMinutes)
        .ThenBy(series => series.Interval, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();

    private DateOnly WarmupStart(DateOnly date, DateOnly minimum) => date.AddDays(-_options.FeatureWarmupCalendarDays) < minimum
        ? minimum
        : date.AddDays(-_options.FeatureWarmupCalendarDays);

    private static void MergeWindow(ref DateOnly? start, ref DateOnly? end, DateOnly candidateStart, DateOnly candidateEnd)
    {
        if (candidateStart > candidateEnd) return;
        start = !start.HasValue || candidateStart < start.Value ? candidateStart : start;
        end = !end.HasValue || candidateEnd > end.Value ? candidateEnd : end;
    }

    private DateOnly SessionDate(DateTimeOffset timestamp)
    {
        var local = TimeZoneInfo.ConvertTime(timestamp, _sessionTimeZone);
        var date = DateOnly.FromDateTime(local.DateTime);
        return TimeOnly.FromDateTime(local.DateTime) >= _options.RthEnd ? date.AddDays(1) : date;
    }

    private DateTimeOffset LocalBoundary(DateOnly date, TimeOnly time) => TimeZoneCatalog.FromLocal(date.ToDateTime(time), _options.SessionTimeZoneId);

    private static string[] ResolveRoots(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) || symbol.Trim().Equals("ES/MES", StringComparison.OrdinalIgnoreCase) || symbol.Trim().Equals("ESMES", StringComparison.OrdinalIgnoreCase))
            return EsMesRoots;
        return [InstrumentCatalog.ExtractRoot(symbol)];
    }
}

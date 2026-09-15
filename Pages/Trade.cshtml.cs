using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
public class TradeModel : PageModel
{
    private readonly TradeFoundryDb _database;

    public TradeModel(TradeFoundryDb database) => _database = database;

    public Trade? Trade { get; private set; }
    public ImportBatch? ImportBatch { get; private set; }
    public Journal? Journal { get; private set; }
    public BarQueryResult BarQuery { get; private set; } = new();
    public IReadOnlyList<BarSeriesInfo> AvailableBarSeries => BarQuery.AvailableSeries;
    public IReadOnlyList<string> AvailableBarIntervals { get; private set; } = Array.Empty<string>();
    public bool UsedDefaultBarInterval { get; private set; }
    public string SourceApplication => ImportBatch is null
        ? "Manual / source-defined"
        : TradeFoundryConstants.SourceApplicationName(ImportBatch.SourceApplication);
    public string CandleChart => Trade is null ? string.Empty : ChartRenderer.Candles(Trade, BarQuery, Journal?.TimeZone);

    [BindProperty(SupportsGet = true)] public Guid? JournalId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Interval { get; set; }

    public IActionResult OnGet(Guid id, Guid? journalId = null)
    {
        JournalId = journalId ?? JournalId;
        Trade = JournalId.HasValue ? _database.GetTrade(JournalId.Value, id) : _database.GetTrade(id);
        if (Trade is null) return NotFound();
        Journal = _database.GetJournal(Trade.JournalId);
        if (Journal is null) return NotFound();
        ImportBatch = Trade.ImportBatchId.HasValue ? _database.GetImport(Trade.ImportBatchId.Value) : null;
        UsedDefaultBarInterval = string.IsNullOrWhiteSpace(Interval);
        BarQuery = GetTradeBarQuery(Trade, Interval);
        AvailableBarIntervals = BuildAvailableBarIntervals(BarQuery.AvailableSeries);
        return Page();
    }

    public IActionResult OnGetCandleChart(Guid id, Guid? journalId, string? interval)
    {
        if (!journalId.HasValue)
            return NotFound();

        var trade = _database.GetTrade(journalId.Value, id);
        if (trade is null)
            return NotFound();

        var journal = _database.GetJournal(trade.JournalId);
        if (journal is null)
            return NotFound();

        var query = GetTradeBarQuery(trade, interval);
        return new JsonResult(new
        {
            requestedInterval = query.RequestedInterval,
            resolvedInterval = query.ResolvedInterval,
            timeZone = TimeZoneCatalog.CanonicalId(journal.TimeZone),
            barCount = query.Bars.Count,
            availabilityNote = query.AvailabilityNote,
            payload = query.Bars.Count == 0 ? null : ChartRenderer.CandlePayload(trade, query, journal.TimeZone)
        });
    }

    public IActionResult OnGetCandleBars(Guid id, Guid? journalId, string? interval, string? direction, string? cursor, int limit = 300)
    {
        if (!journalId.HasValue)
            return NotFound();

        var trade = _database.GetTrade(journalId.Value, id);
        if (trade is null)
            return NotFound();

        if (!string.Equals(direction, "before", StringComparison.OrdinalIgnoreCase) && !string.Equals(direction, "after", StringComparison.OrdinalIgnoreCase))
            return BadRequest("direction must be before or after.");
        if (string.IsNullOrWhiteSpace(cursor) || !DateTimeOffset.TryParse(cursor, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var cursorUtc) || cursorUtc.Offset != TimeSpan.Zero)
            return BadRequest("cursor must be a valid UTC timestamp.");
        if (limit is < 1 or > 500)
            return BadRequest("limit must be between 1 and 500.");
        if (!BarIntervals.TryNormalize(interval, out var normalizedInterval, allowSource: true))
            return BadRequest("interval must be source or a positive minute-based interval.");

        try
        {
            var page = _database.GetBarHistoryPage(trade.JournalId, trade.Symbol, normalizedInterval, cursorUtc.ToUniversalTime(), string.Equals(direction, "before", StringComparison.OrdinalIgnoreCase), limit);
            return new JsonResult(new
            {
                bars = page.Bars.Select(bar => new
                {
                    time = bar.EventUtc.ToUnixTimeSeconds(),
                    open = bar.Open,
                    high = bar.High,
                    low = bar.Low,
                    close = bar.Close,
                    volume = bar.Volume
                }),
                hasMore = page.HasMore,
                interval = page.ResolvedInterval,
                direction = direction!.ToLowerInvariant()
            });
        }
        catch (FormatException exception)
        {
            return BadRequest(exception.Message);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    private BarQueryResult GetTradeBarQuery(Trade trade, string? interval)
    {
        var end = (trade.ExitUtc ?? trade.EntryUtc).AddMinutes(30);
        var available = _database.GetBarSeries(trade.JournalId, trade.Symbol);
        var requestedInterval = BarIntervals.TryNormalize(interval, out var normalizedInterval, allowSource: false)
            ? normalizedInterval
            : available.Where(x => x.IntervalMinutes > 0).OrderBy(x => x.IntervalMinutes).Select(x => x.Interval).FirstOrDefault()
                ?? (available.Count > 0 ? available[0].Interval : "1m");
        return _database.GetBarWindow(trade.JournalId, trade.Symbol, trade.EntryUtc.AddMinutes(-30), end, requestedInterval);
    }

    private static IReadOnlyList<string> BuildAvailableBarIntervals(IReadOnlyList<BarSeriesInfo> series)
    {
        var intervals = series
            .Where(x => x.IntervalMinutes > 0)
            .Select(x => BarIntervals.Format(x.IntervalMinutes))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (series.Any(x => x.IntervalMinutes == 1 && x.BarCount > 0))
        {
            intervals.Add("2m");
            intervals.Add("5m");
        }

        return intervals
            .OrderBy(x => BarIntervals.TryGetMinutes(x, out var minutes) ? minutes : int.MaxValue)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

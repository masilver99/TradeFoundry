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
        var end = (Trade.ExitUtc ?? Trade.EntryUtc).AddMinutes(30);
        var available = _database.GetBarSeries(Trade.JournalId, Trade.Symbol);
        var requestedInterval = BarIntervals.TryNormalize(Interval, out var normalizedInterval, allowSource: false)
            ? normalizedInterval
            : available.Where(x => x.IntervalMinutes > 0).OrderBy(x => x.IntervalMinutes).Select(x => x.Interval).FirstOrDefault()
                ?? (available.Count > 0 ? available[0].Interval : "1m");
        UsedDefaultBarInterval = string.IsNullOrWhiteSpace(Interval);
        BarQuery = _database.GetBarWindow(Trade.JournalId, Trade.Symbol, Trade.EntryUtc.AddMinutes(-30), end, requestedInterval);
        AvailableBarIntervals = BuildAvailableBarIntervals(BarQuery.AvailableSeries);
        return Page();
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

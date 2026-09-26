using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
public sealed class CommandPaletteSearchModel : PageModel
{
    private const int MaximumQueryLength = 120;
    private const int ResultsPerKind = 6;
    private readonly TradeFoundryDb _database;

    public CommandPaletteSearchModel(TradeFoundryDb database) => _database = database;

    public IActionResult OnGet(Guid journalId, string? q)
    {
        var journal = _database.GetJournal(journalId);
        if (journal is null) return NotFound();

        var query = (q ?? string.Empty).Trim();
        if (query.Length > MaximumQueryLength) query = query[..MaximumQueryLength];
        if (query.Length < 2) return new JsonResult(new { results = Array.Empty<CommandPaletteRemoteResult>() });

        var results = new List<CommandPaletteRemoteResult>();
        foreach (var hit in _database.SearchTradeReviewNotes(journalId, query, ResultsPerKind))
        {
            var timestamp = hit.ExitUtc.HasValue && hit.Status.Equals("closed", StringComparison.OrdinalIgnoreCase)
                ? hit.ExitUtc.Value
                : hit.EntryUtc;
            var date = TimeZoneCatalog.Format(timestamp, journal.TimeZone, "yyyy-MM-dd");
            var title = string.IsNullOrWhiteSpace(hit.Instrument) ? hit.Symbol : hit.Instrument;
            var href = $"/journal/{journalId:D}/review?date={date}&focus={Uri.EscapeDataString(hit.ReviewKey)}&section=review";
            results.Add(new CommandPaletteRemoteResult(
                "Trade reviews",
                $"{title} {hit.Status} trade",
                $"{TimeZoneCatalog.Format(timestamp, journal.TimeZone, "MMM d, yyyy")} · {hit.Symbol}",
                Snippet(hit.SearchText, query),
                href));
        }

        foreach (var hit in _database.SearchDailyJournalEntries(journalId, query, ResultsPerKind))
        {
            var date = hit.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            results.Add(new CommandPaletteRemoteResult(
                "Daybook entries",
                $"Daybook · {hit.Date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}",
                "Daily journal note",
                Snippet(hit.SearchText, query),
                $"/journal/{journalId:D}/review?date={date}#daily-journal-editor"));
        }

        return new JsonResult(new { results });
    }

    private static string Snippet(string text, string query)
    {
        var clean = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var match = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => clean.IndexOf(term, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(0)
            .Min();
        var start = Math.Max(0, match - 48);
        var length = Math.Min(180, clean.Length - start);
        var snippet = clean.Substring(start, length);
        return (start > 0 ? "…" : string.Empty) + snippet + (start + length < clean.Length ? "…" : string.Empty);
    }
}

public sealed record CommandPaletteRemoteResult(string Category, string Title, string Subtitle, string Snippet, string Href);

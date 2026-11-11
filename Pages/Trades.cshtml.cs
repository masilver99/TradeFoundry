using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
public class TradesModel : PageModel
{
    private readonly TradeFoundryDb _database;

    public TradesModel(TradeFoundryDb database) => _database = database;

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public string Search { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public string Symbol { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public string Status { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "entry_desc";

    public Journal? Journal { get; private set; }
    public PagedResult<Trade> Results { get; private set; } = new();
    public JournalFilterScope Filter { get; private set; } = JournalFilter.None.Scope(null);
    public IReadOnlySet<DateOnly> DailyJournalEntryDates { get; private set; } = new HashSet<DateOnly>();

    public DateOnly GetReviewDate(Trade trade)
    {
        var timestamp = trade.ExitUtc.HasValue && trade.Status.Equals("closed", StringComparison.OrdinalIgnoreCase)
            ? trade.ExitUtc.Value : trade.EntryUtc;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, TimeZoneCatalog.Resolve(Journal?.TimeZone)).DateTime);
    }

    public IActionResult OnGet()
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return NotFound();
        Filter = JournalFilterRequest.Resolve(HttpContext, Journal);
        Results = _database.GetTrades(new TradeQuery
        {
            JournalId = JournalId,
            Page = PageNumber,
            PageSize = 50,
            Search = Search,
            Symbol = Symbol,
            Scope = Filter,
            Status = Status,
            Sort = Sort
        });
        DailyJournalEntryDates = _database.GetDailyJournalEntryDates(JournalId, Results.Items.Select(GetReviewDate));
        return Page();
    }
}

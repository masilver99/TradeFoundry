using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;

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
    [BindProperty(SupportsGet = true)] public string Direction { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public string Status { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "entry_desc";

    public Journal? Journal { get; private set; }
    public PagedResult<Trade> Results { get; private set; } = new();

    public IActionResult OnGet()
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return NotFound();
        Results = _database.GetTrades(new TradeQuery
        {
            JournalId = JournalId,
            Page = PageNumber,
            PageSize = 50,
            Search = Search,
            Symbol = Symbol,
            Direction = Direction,
            Status = Status,
            Sort = Sort
        });
        return Page();
    }
}

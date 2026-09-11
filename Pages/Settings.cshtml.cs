using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Pages;

[Authorize]
public class SettingsModel : PageModel
{
    private readonly TradeFoundryDb _database;

    public SettingsModel(TradeFoundryDb database)
    {
        _database = database;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    public Journal? Journal { get; private set; }

    public IActionResult OnGet()
    {
        Journal = _database.GetJournal(JournalId);
        return Journal is null ? NotFound() : Page();
    }
}

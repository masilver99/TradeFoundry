using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Data;

namespace TradeFoundry.Pages;

[Authorize]
public class ChartsModel : PageModel
{
    private readonly TradeFoundryDb _database;

    public ChartsModel(TradeFoundryDb database) => _database = database;

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }

    public IActionResult OnGet() => _database.GetJournal(JournalId) is null
        ? NotFound()
        : Redirect($"/journal/{JournalId:D}/analytics");
}

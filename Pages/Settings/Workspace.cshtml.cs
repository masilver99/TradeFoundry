using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Pages.Settings;

[Authorize]
public class WorkspaceModel : PageModel
{
    private readonly TradeFoundryDb _database;

    public WorkspaceModel(TradeFoundryDb database)
    {
        _database = database;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty] public string NewJournalName { get; set; } = string.Empty;
    [BindProperty] public string NewExecutionContext { get; set; } = "live";
    [BindProperty] public string NewLabels { get; set; } = string.Empty;
    public Journal? Journal { get; private set; }
    public IReadOnlyList<Journal> Journals { get; private set; } = Array.Empty<Journal>();
    public string? FlashMessage { get; private set; }
    public string? FlashKind { get; private set; }

    public IActionResult OnGet()
    {
        Load();
        if (Journal is null) return NotFound();
        FlashMessage = TempData["FlashMessage"] as string;
        FlashKind = TempData["FlashKind"] as string ?? "success";
        return Page();
    }

    public IActionResult OnPostCreateJournal()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var journal = _database.CreateJournal(NewJournalName, NewExecutionContext, NewLabels, "UTC", "USD", "flat_to_flat");
        TempData["FlashMessage"] = $"Created {journal.Name}.";
        TempData["FlashKind"] = "success";
        return Redirect($"/journal/{journal.Id:D}/settings/journal");
    }

    private void Load()
    {
        Journal = _database.GetJournal(JournalId);
        Journals = _database.GetJournals();
    }
}

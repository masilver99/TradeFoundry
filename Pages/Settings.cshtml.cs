using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Services;
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

    [BindProperty] public JournalImageSettings ImageSettings { get; set; } = new();
    [TempData] public string? ImageSettingsStatus { get; set; }
    private string DataRoot => Path.GetDirectoryName(_database.DatabasePath)!;

    public IActionResult OnPostImages()
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return NotFound();
        if (!ModelState.IsValid) return Page();
        ImageSettings.Save(DataRoot, JournalId);
        ImageSettingsStatus = "Image settings saved.";
        return RedirectToPage(new { journalId = JournalId });
    }

    public IActionResult OnGet()
    {
        Journal = _database.GetJournal(JournalId);
        ImageSettings = JournalImageSettings.Load(DataRoot, JournalId);
        return Journal is null ? NotFound() : Page();
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Pages.Settings;

[Authorize]
public class JournalModel : PageModel
{
    private const int MaxDescriptionLength = 20_000;
    private readonly TradeFoundryDb _database;

    public JournalModel(TradeFoundryDb database)
    {
        _database = database;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty] public string Name { get; set; } = string.Empty;
    [BindProperty] public string ExecutionContext { get; set; } = "live";
    [BindProperty] public string? Labels { get; set; }
    [BindProperty] public string DescriptionMarkdown { get; set; } = string.Empty;
    [BindProperty] public string TimeZone { get; set; } = "UTC";
    [BindProperty] public string Currency { get; set; } = "USD";
    [BindProperty] public string GroupingPolicy { get; set; } = "flat_to_flat";
    [BindProperty] public decimal? StartingEquity { get; set; }

    public Journal? Journal { get; private set; }
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

    public IActionResult OnPostSave()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        if ((DescriptionMarkdown?.Length ?? 0) > MaxDescriptionLength)
            ModelState.AddModelError(nameof(DescriptionMarkdown), $"Keep the description under {MaxDescriptionLength:N0} characters.");

        if (!ModelState.IsValid)
        {
            Load();
            return Page();
        }

        var saved = _database.UpdateJournal(JournalId, Name, ExecutionContext, Labels ?? string.Empty, TimeZone, Currency, GroupingPolicy, StartingEquity, DescriptionMarkdown ?? string.Empty);
        TempData["FlashMessage"] = saved ? "Journal settings saved." : "The journal could not be updated.";
        TempData["FlashKind"] = saved ? "success" : "error";
        return Redirect($"/journal/{JournalId:D}/settings/journal");
    }

    private void Load()
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return;
        Name = Journal.Name;
        ExecutionContext = Journal.ExecutionContext;
        Labels = Journal.Labels;
        DescriptionMarkdown = Journal.DescriptionMarkdown;
        TimeZone = Journal.TimeZone;
        Currency = Journal.Currency;
        GroupingPolicy = Journal.GroupingPolicy;
        StartingEquity = Journal.StartingEquity;
    }
}

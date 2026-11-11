using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Pages;

/// <summary>Saves and deletes named journal filters for the filter bar's Saved menu, then returns to the page it was used from.</summary>
[Authorize]
[ValidateAntiForgeryToken]
public sealed class JournalFiltersModel(TradeFoundryDb database) : PageModel
{
    public const string MessageKey = "JournalFilterMessage";
    public const string ErrorKey = "JournalFilterError";

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }

    public IActionResult OnGet() => Redirect($"/journal/{JournalId:D}/overview");

    public IActionResult OnPostSave(string? name, string? period, string? from, string? to, string? side, string? returnUrl)
    {
        if (database.GetJournal(JournalId) is null) return NotFound();
        try
        {
            var saved = database.SaveFilter(JournalId, name ?? string.Empty, JournalFilter.Parse(period, from, to, side));
            TempData[MessageKey] = $"Saved “{saved.Name}”.";
        }
        catch (InvalidOperationException exception)
        {
            TempData[ErrorKey] = exception.Message;
        }
        return Back(returnUrl);
    }

    public IActionResult OnPostDelete(Guid filterId, string? returnUrl)
    {
        if (database.GetJournal(JournalId) is null) return NotFound();
        var name = database.GetSavedFilters(JournalId).FirstOrDefault(item => item.Id == filterId)?.Name;
        if (database.DeleteSavedFilter(JournalId, filterId)) TempData[MessageKey] = $"Deleted “{name}”.";
        return Back(returnUrl);
    }

    private IActionResult Back(string? returnUrl)
    {
        var journalPrefix = $"/journal/{JournalId:D}/";
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) && returnUrl.StartsWith(journalPrefix, StringComparison.OrdinalIgnoreCase)
            ? LocalRedirect(returnUrl)
            : Redirect($"/journal/{JournalId:D}/overview");
    }
}

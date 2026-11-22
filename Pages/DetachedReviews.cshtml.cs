using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Pages;

[Authorize]
public sealed class DetachedReviewsModel : PageModel
{
    private readonly TradeFoundryDb _database;

    public DetachedReviewsModel(TradeFoundryDb database) => _database = database;

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }

    public Journal Journal { get; private set; } = new();
    public IReadOnlyList<DetachedReview> Reviews { get; private set; } = Array.Empty<DetachedReview>();
    public IReadOnlyList<Trade> Trades { get; private set; } = Array.Empty<Trade>();
    public string? ErrorMessage { get; private set; }
    public string? FlashMessage { get; private set; }

    public IActionResult OnGet()
    {
        if (!Load()) return NotFound();
        FlashMessage = TempData["DetachedFlash"] as string;
        return Page();
    }

    public IActionResult OnPostAttach(string? detachedKey, string? targetKey)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        try
        {
            if (string.IsNullOrWhiteSpace(detachedKey) || string.IsNullOrWhiteSpace(targetKey)) throw new InvalidOperationException("Choose the trade this review belongs to.");
            _database.AttachDetachedReview(JournalId, detachedKey, ResolveTarget(targetKey));
            TempData["DetachedFlash"] = "Review attached. The detached original is in that trade's revision history.";
            return RedirectToPage("/DetachedReviews", new { journalId = JournalId });
        }
        catch (InvalidOperationException exception)
        {
            if (!Load()) return NotFound();
            ErrorMessage = exception.Message;
            return Page();
        }
    }

    public static string ShortKey(string reviewKey) => reviewKey.Length <= 12 ? reviewKey : reviewKey[^12..];

    /// <summary>Accepts a key from a suggestion, or the searchable "date · instrument #short-key" label.</summary>
    private string ResolveTarget(string entered)
    {
        var mark = entered.LastIndexOf('#');
        if (mark < 0) return entered;
        var shortKey = entered[(mark + 1)..].Trim();
        var matches = _database.GetAllTrades(JournalId).Where(trade => ShortKey(trade.ReviewKey) == shortKey).ToArray();
        return matches.Length == 1 ? matches[0].ReviewKey : throw new InvalidOperationException("Choose a trade from the list.");
    }

    private bool Load()
    {
        var journal = _database.GetJournal(JournalId);
        if (journal is null) return false;
        Journal = journal;
        Reviews = _database.GetDetachedReviews(JournalId);
        if (Reviews.Count > 0) Trades = _database.GetAllTrades(JournalId).Reverse().ToArray();
        return true;
    }
}

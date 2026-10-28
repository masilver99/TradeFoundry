using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Publishing;

namespace TradeFoundry.PublicJournal.Pages;

public sealed class IndexModel(PublicationStore store) : PageModel
{
    public string Document { get; private set; } = "";
    public IActionResult OnGet(DateOnly? before, DateOnly? date, Guid? snapshot)
    {
        if (!ModelState.IsValid) return BadRequest();
        var current = store.Read();
        if (current is null)
        {
            Document = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Trading journal</title><link rel=\"stylesheet\" href=\"/publication.css\"></head><body><main><header><div class=\"eyebrow\">TRADING JOURNAL</div><h1>Nothing published yet.</h1><p>This journal is currently unavailable.</p></header></main></body></html>";
            return Page();
        }
        if (snapshot.HasValue && snapshot != current.Status.SnapshotId) return Redirect("/");
        Document = PublicationPresentation.Document(current.Snapshot, current.Status.PublishedUtc,
            id => $"/images/{current.Snapshot.SnapshotId:D}/{id}", "/", "/publication.css", before, date);
        return Page();
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Pages.Settings;

[Authorize]
public class InstanceModel : PageModel
{
    private readonly TradeFoundryDb _database;

    public InstanceModel(TradeFoundryDb database)
    {
        _database = database;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    public Journal? Journal { get; private set; }
    public IReadOnlyList<Journal> Journals { get; private set; } = Array.Empty<Journal>();
    public string DatabasePath => _database.DatabasePath;
    public string DatabaseSize => FormatBytes(_database.DatabaseSizeBytes);
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

    public IActionResult OnPostArchive()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        if (_database.GetJournals().Count <= 1)
        {
            TempData["FlashMessage"] = "Keep at least one active journal in this instance.";
            TempData["FlashKind"] = "error";
            return Redirect($"/journal/{JournalId:D}/settings/instance");
        }

        _database.ArchiveJournal(JournalId);
        var next = _database.GetJournals().FirstOrDefault();
        return Redirect(next is null ? "/" : $"/journal/{next.Id:D}/overview");
    }

    private void Load()
    {
        Journal = _database.GetJournal(JournalId);
        Journals = _database.GetJournals();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes:N0} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.#} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024d * 1024d):0.#} MB";
        return $"{bytes / (1024d * 1024d * 1024d):0.##} GB";
    }
}

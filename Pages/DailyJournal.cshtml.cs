using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Pages;

[Authorize]
public sealed class DailyJournalModel : PageModel
{
    private const int PageSize = 20;
    private readonly TradeFoundryDb _database;

    public DailyJournalModel(TradeFoundryDb database) => _database = database;

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Date { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Before { get; set; }

    public Journal Journal { get; private set; } = new();
    public DateOnly Today { get; private set; }
    public DailyJournalCardViewModel TodayCard { get; private set; } = new();
    public IReadOnlyList<DailyJournalCardViewModel> Entries { get; private set; } = Array.Empty<DailyJournalCardViewModel>();
    public bool HasMore { get; private set; }
    public DateOnly? NextBefore { get; private set; }
    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet()
    {
        if (!LoadPage()) return NotFound();
        return Page();
    }

    public IActionResult OnGetOlder(DateOnly before)
    {
        if (!LoadJournal()) return NotFound();
        var page = _database.GetDailyJournalEntries(JournalId, Today, before, PageSize);
        Response.Headers["X-Daily-Journal-Has-More"] = page.HasMore ? "true" : "false";
        Response.Headers["X-Daily-Journal-Next-Before"] = page.NextBefore?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        return Partial("_DailyJournalCards", ToCards(page.Entries));
    }

    public IActionResult OnGetEntry(DateOnly date)
    {
        if (!LoadJournal()) return NotFound();
        return Partial("_DailyJournalCard", BuildCard(_database.GetDailyJournal(JournalId, date), date == Today, false));
    }

    public IActionResult OnPostAutosave(DateOnly date, string? dailyJournalStateJson, int expectedRevision)
    {
        try
        {
            var result = _database.SaveDailyJournal(JournalId, date, dailyJournalStateJson, expectedRevision, "autosaved");
            if (result.Conflict)
            {
                Response.StatusCode = StatusCodes.Status409Conflict;
                return new JsonResult(new
                {
                    saved = false,
                    conflict = true,
                    currentRevision = result.Entry.Revision,
                    message = "This daily journal changed in another window. Reload the latest values before saving again."
                });
            }

            return new JsonResult(new { saved = true, revision = result.Entry.Revision, updatedUtc = result.Entry.UpdatedUtc });
        }
        catch (ArgumentException exception)
        {
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return new JsonResult(new { saved = false, message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return new JsonResult(new { saved = false, message = exception.Message });
        }
    }

    public IActionResult OnPostSave(DateOnly date, string? dailyJournalStateJson, int expectedRevision)
    {
        try
        {
            var result = _database.SaveDailyJournal(JournalId, date, dailyJournalStateJson, expectedRevision);
            if (result.Conflict)
            {
                Response.StatusCode = StatusCodes.Status409Conflict;
                ErrorMessage = "This daily journal changed in another window. Reload the latest values before saving again.";
                Date = date;
                LoadPage();
                return Page();
            }

            return RedirectToPage("/DailyJournal", new { journalId = JournalId, date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        }
        catch (ArgumentException exception)
        {
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            ErrorMessage = exception.Message;
            Date = date;
            LoadPage();
            return Page();
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    private bool LoadPage()
    {
        if (!LoadJournal()) return false;
        var entryDate = Date ?? Today;
        var page = _database.GetDailyJournalEntries(JournalId, Today, Before, PageSize);
        TodayCard = BuildCard(_database.GetDailyJournal(JournalId, Today), true, true);
        Entries = ToCards(page.Entries, entryDate);
        HasMore = page.HasMore;
        NextBefore = page.NextBefore;
        return true;
    }

    private bool LoadJournal()
    {
        var journal = _database.GetJournal(JournalId);
        if (journal is null) return false;
        Journal = journal;
        Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneCatalog.Resolve(journal.TimeZone)).DateTime);
        return true;
    }

    private IReadOnlyList<DailyJournalCardViewModel> ToCards(IReadOnlyList<DailyJournalEntry> entries, DateOnly? openDate = null) =>
        entries.Select(entry => BuildCard(entry, false, entry.Date == openDate)).ToArray();

    private DailyJournalCardViewModel BuildCard(DailyJournalEntry entry, bool isToday, bool openInitially)
    {
        var plainText = LexicalPlainText.Extract(entry.Text);
        var preview = plainText.Length > 360 ? plainText[..357].TrimEnd() + "…" : plainText;
        var dateText = entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var queryDate = Uri.EscapeDataString(dateText);
        var page = $"/journal/{JournalId:D}/daily-journal";
        return new DailyJournalCardViewModel
        {
            JournalId = JournalId,
            Entry = entry,
            IsToday = isToday,
            OpenInitially = openInitially,
            Preview = preview,
            SaveUrl = $"{page}?handler=Save&date={queryDate}",
            AutosaveUrl = $"{page}?handler=Autosave&date={queryDate}"
        };
    }
}

public sealed class DailyJournalCardViewModel
{
    public Guid JournalId { get; init; }
    public DailyJournalEntry Entry { get; init; } = new();
    public bool IsToday { get; init; }
    public bool OpenInitially { get; init; }
    public string Preview { get; init; } = string.Empty;
    public string SaveUrl { get; init; } = string.Empty;
    public string AutosaveUrl { get; init; } = string.Empty;
}

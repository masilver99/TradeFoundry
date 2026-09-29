using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
[RequestFormLimits(MultipartBodyLengthLimit = 12 * 1024 * 1024)]
public sealed class DailyJournalModel : PageModel
{
    private const int PageSize = 20;
    private readonly TradeFoundryDb _database;
    private readonly DailyJournalImageService _images;

    public DailyJournalModel(TradeFoundryDb database, DailyJournalImageService images)
    {
        _database = database;
        _images = images;
    }

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
    private IReadOnlyList<DailyTradeSummary> TradeDays { get; set; } = Array.Empty<DailyTradeSummary>();
    private IReadOnlyDictionary<DateOnly, DailyTradeSummary> TradeDaysByDate { get; set; } = new Dictionary<DateOnly, DailyTradeSummary>();

    public IActionResult OnGet()
    {
        if (!LoadPage()) return NotFound();
        return Page();
    }

    public IActionResult OnGetOlder(DateOnly before)
    {
        if (!LoadJournal()) return NotFound();
        var page = BuildEntriesPage(before);
        Response.Headers["X-Daily-Journal-Has-More"] = page.HasMore ? "true" : "false";
        Response.Headers["X-Daily-Journal-Next-Before"] = page.NextBefore?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        return Partial("_DailyJournalCards", ToCards(page.Days));
    }

    public IActionResult OnGetEntry(DateOnly date)
    {
        if (!LoadJournal()) return NotFound();
        return Partial("_DailyJournalCard", BuildCard(_database.GetDailyJournal(JournalId, date), date == Today, false, TradeDaysByDate.GetValueOrDefault(date)));
    }

    public IActionResult OnGetDayChart(DateOnly date)
    {
        if (!LoadJournal()) return NotFound();
        if (!TradeDaysByDate.TryGetValue(date, out var day) || day.TotalTradeCount == 0) return NotFound();

        var trades = day.CompletedTrades.Concat(day.OpenTrades)
            .OrderBy(trade => trade.EntryUtc)
            .ThenBy(trade => trade.Sequence)
            .Select(trade => new
            {
                label = $"{trade.Symbol} · {trade.Direction} · {trade.NetPnl.ToString("C2", CultureInfo.CurrentCulture)}",
                chartUrl = $"/journal/{JournalId:D}/review?reviewKey={Uri.EscapeDataString(trade.ReviewKey)}"
            })
            .ToArray();
        return new JsonResult(new { date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), trades });
    }

    public async Task<IActionResult> OnPostUploadImageAsync(DateOnly date, IFormFile? image, CancellationToken cancellationToken)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        if (image is not { Length: > 0 })
        {
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return new JsonResult(new { saved = false, message = "Choose or paste an image first." });
        }

        try
        {
            await using var content = image.OpenReadStream();
            var saved = await _images.SaveAsync(JournalId, date, content, image.FileName, image.ContentType, image.Length, cancellationToken);
            var imageUrl = $"/journal/{JournalId:D}/daily-journal?handler=Image&date={Uri.EscapeDataString(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}&imageId={saved.Id:D}";
            return new JsonResult(new
            {
                saved = true,
                image = new { id = saved.Id, src = imageUrl, altText = saved.OriginalFileName, fileName = saved.OriginalFileName, length = saved.Length }
            });
        }
        catch (InvalidOperationException exception)
        {
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return new JsonResult(new { saved = false, message = exception.Message });
        }
    }

    public IActionResult OnGetImage(DateOnly date, Guid imageId)
    {
        try
        {
            var image = _images.Get(JournalId, date, imageId);
            if (image is null) return NotFound();
            var path = _images.GetPath(JournalId, image);
            if (path is null) return NotFound();
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return PhysicalFile(path, image.ContentType);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
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
        var page = BuildEntriesPage(Before);
        var todayEntry = _database.GetDailyJournal(JournalId, Today);
        var todayHasText = !string.IsNullOrWhiteSpace(LexicalPlainText.Extract(todayEntry.Text));
        TodayCard = BuildCard(todayEntry, true, todayHasText || entryDate == Today, TradeDaysByDate.GetValueOrDefault(Today));
        Entries = ToCards(page.Days, entryDate);
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
        TradeDays = DailyTradeAggregation.Build(_database.GetAllTrades(JournalId), journal.TimeZone);
        TradeDaysByDate = TradeDays.ToDictionary(day => day.Date);
        return true;
    }

    private DailyJournalFeedPage BuildEntriesPage(DateOnly? beforeDate)
    {
        var journalPage = _database.GetDailyJournalEntries(JournalId, Today, beforeDate, PageSize + 1);
        return DailyJournalFeedBuilder.Build(JournalId, Today, beforeDate, PageSize, TradeDays, journalPage.Entries);
    }

    private IReadOnlyList<DailyJournalCardViewModel> ToCards(IReadOnlyList<DailyJournalFeedDay> days, DateOnly? openDate = null) =>
        days.Select(day => BuildCard(day.Entry, false, day.Entry.Date == openDate, day.Trading)).ToArray();

    private DailyJournalCardViewModel BuildCard(DailyJournalEntry entry, bool isToday, bool openInitially, DailyTradeSummary? trading)
    {
        var plainText = LexicalPlainText.Extract(entry.Text);
        var editorText = LexicalPlainText.ExtractVisibleText(entry.Text);
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
            HasJournalText = !string.IsNullOrWhiteSpace(plainText),
            Preview = preview,
            TextBytes = Encoding.UTF8.GetByteCount(editorText),
            TradeCount = trading?.TotalTradeCount ?? 0,
            RealizedNetPnl = trading?.RealizedNetPnl ?? 0m,
            ChartUrl = $"{page}?handler=DayChart&date={queryDate}",
            ImageUploadUrl = $"{page}?handler=UploadImage&date={queryDate}",
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
    public bool HasJournalText { get; init; }
    public string Preview { get; init; } = string.Empty;
    public int TextBytes { get; init; }
    public int TradeCount { get; init; }
    public decimal RealizedNetPnl { get; init; }
    public string ChartUrl { get; init; } = string.Empty;
    public string ImageUploadUrl { get; init; } = string.Empty;
    public string SaveUrl { get; init; } = string.Empty;
    public string AutosaveUrl { get; init; } = string.Empty;
}

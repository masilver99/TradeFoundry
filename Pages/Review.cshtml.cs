using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
public class ReviewModel : PageModel
{
    private readonly TradeReviewService _reviews;

    public ReviewModel(TradeReviewService reviews) => _reviews = reviews;

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Date { get; set; }
    [BindProperty(SupportsGet = true)] public string? Focus { get; set; }
    [BindProperty(SupportsGet = true)] public string? Section { get; set; }
    [BindProperty(SupportsGet = true)] public bool Saved { get; set; }
    [BindProperty(SupportsGet = true)] public bool JournalSaved { get; set; }
    [BindProperty] public TradeReviewPatch Edit { get; set; } = new();
    [BindProperty] public string? DailyJournalText { get; set; }
    [BindProperty] public IFormFile? Screenshot { get; set; }

    public DailyReviewModel Day { get; private set; } = new();
    public string? ErrorMessage { get; private set; }
    public string? NoticeMessage { get; private set; }

    public IActionResult OnGet()
    {
        try
        {
            var date = Date ?? _reviews.GetDefaultDate(JournalId);
            Day = _reviews.GetDay(JournalId, date);
            Date = date;
            DailyJournalText = Day.DailyJournal.Text;
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }

        if (JournalSaved) NoticeMessage = "Daily journal saved.";
        else if (Saved) NoticeMessage = "Review saved.";
        return Page();
    }

    public IActionResult OnPostSaveDailyJournal(string dailyJournalText, int expectedRevision, DateOnly date)
    {
        Date = date;
        try
        {
            var result = _reviews.SaveDailyJournal(JournalId, date, dailyJournalText, expectedRevision);
            if (result.Conflict)
            {
                LoadDay();
                DailyJournalText = result.Entry.Text;
                ErrorMessage = "This daily journal changed in another window. Reload the current values before saving again.";
                ModelState.AddModelError(string.Empty, ErrorMessage);
                return Page();
            }

            return RedirectToPage(new
            {
                journalId = JournalId,
                date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                journalSaved = true
            });
        }
        catch (ArgumentException exception)
        {
            LoadDay();
            DailyJournalText = dailyJournalText;
            ErrorMessage = exception.Message;
            ModelState.AddModelError(string.Empty, ErrorMessage);
            return Page();
        }
        catch (InvalidOperationException exception)
        {
            LoadDay();
            DailyJournalText = dailyJournalText;
            ErrorMessage = exception.Message;
            ModelState.AddModelError(string.Empty, ErrorMessage);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostSaveAsync(string reviewKey, int expectedRevision, DateOnly date, bool saveAndNext = false, CancellationToken cancellationToken = default)
    {
        Date = date;
        Edit.ExpectedRevision = expectedRevision;
        try
        {
            var result = _reviews.Save(JournalId, reviewKey, Edit);
            if (result.Conflict)
            {
                LoadDay();
                ErrorMessage = "This review changed in another window. Reload the current values before saving again.";
                ModelState.AddModelError(string.Empty, ErrorMessage);
                return Page();
            }

            if (Screenshot is { Length: > 0 })
            {
                try
                {
                    await using var content = Screenshot.OpenReadStream();
                    await _reviews.SaveAttachmentAsync(JournalId, reviewKey, content, Screenshot.FileName, Screenshot.ContentType, Screenshot.Length, cancellationToken);
                }
                catch (InvalidOperationException exception)
                {
                    NoticeMessage = $"Review saved, but the screenshot was not attached: {exception.Message}";
                }
            }

            if (!saveAndNext)
                return RedirectToPage(new { journalId = JournalId, date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), focus = reviewKey, saved = true });

            var next = NextTradeKey(_reviews.GetDay(JournalId, date), reviewKey);
            if (next is not null)
                return RedirectToPage(new { journalId = JournalId, date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), focus = next, saved = true });

            var currentDay = _reviews.GetDay(JournalId, date);
            if (currentDay.NextDate.HasValue)
            {
                var nextDay = _reviews.GetDay(JournalId, currentDay.NextDate.Value);
                return RedirectToPage(new { journalId = JournalId, date = currentDay.NextDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), focus = FirstTradeKey(nextDay), saved = true });
            }

            return RedirectToPage(new { journalId = JournalId, date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), saved = true });
        }
        catch (ArgumentException exception)
        {
            LoadDay();
            ErrorMessage = exception.Message;
            ModelState.AddModelError(string.Empty, ErrorMessage);
            return Page();
        }
        catch (InvalidOperationException exception)
        {
            LoadDay();
            ErrorMessage = exception.Message;
            ModelState.AddModelError(string.Empty, ErrorMessage);
            return Page();
        }
    }

    public IActionResult OnPostAutosave(string reviewKey, int expectedRevision, DateOnly date)
    {
        Date = date;
        Edit.ExpectedRevision = expectedRevision;
        try
        {
            var result = _reviews.Save(JournalId, reviewKey, Edit);
            if (result.Conflict)
            {
                Response.StatusCode = StatusCodes.Status409Conflict;
                return new JsonResult(new
                {
                    saved = false,
                    conflict = true,
                    currentRevision = result.Annotation.Revision,
                    message = "This review changed in another window. Reload the current values before saving again."
                });
            }

            var day = _reviews.GetDay(JournalId, date);
            var trade = day.CompletedTrades.Concat(day.OpenTrades).FirstOrDefault(item => item.Trade.ReviewKey == reviewKey)?.Trade;
            object? tradePayload = trade is null
                ? null
                : new { fees = trade.Fees, netPnl = trade.NetPnl, hasOverride = result.Annotation.AllInCommission.HasValue };

            return new JsonResult(new
            {
                saved = true,
                revision = result.Annotation.Revision,
                updatedUtc = result.Annotation.UpdatedUtc,
                trade = tradePayload,
                day = new { realizedNetPnl = day.RealizedNetPnl }
            });
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

    public async Task<IActionResult> OnPostAttachAsync(string reviewKey, DateOnly date, CancellationToken cancellationToken = default)
    {
        Date = date;
        Focus = reviewKey;
        Section = "media";
        try
        {
            if (Screenshot is not { Length: > 0 })
                throw new InvalidOperationException("Choose an image before attaching it.");

            await using var content = Screenshot.OpenReadStream();
            await _reviews.SaveAttachmentAsync(JournalId, reviewKey, content, Screenshot.FileName, Screenshot.ContentType, Screenshot.Length, cancellationToken);
            return RedirectToPage(new
            {
                journalId = JournalId,
                date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                focus = reviewKey,
                section = "media",
                saved = true
            });
        }
        catch (InvalidOperationException exception)
        {
            LoadDay();
            ErrorMessage = exception.Message;
            return Page();
        }
    }

    public IActionResult OnPostRevert(string reviewKey, int expectedRevision, int targetRevision, DateOnly date)
    {
        try
        {
            _reviews.Revert(JournalId, reviewKey, expectedRevision, targetRevision);
            return RedirectToPage(new { journalId = JournalId, date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), focus = reviewKey, saved = true });
        }
        catch (InvalidOperationException exception)
        {
            Date = date;
            LoadDay();
            ErrorMessage = exception.Message;
            return Page();
        }
    }

    public IActionResult OnPostRemoveAttachment(Guid attachmentId, DateOnly date, string? focus)
    {
        _reviews.RemoveAttachment(JournalId, attachmentId);
        return RedirectToPage(new { journalId = JournalId, date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), focus, section = "media", saved = true });
    }

    public IActionResult OnGetAttachment(Guid attachmentId)
    {
        var attachment = _reviews.GetAttachment(JournalId, attachmentId);
        if (attachment is null) return NotFound();
        var path = _reviews.GetAttachmentPath(JournalId, attachment);
        return path is null
            ? NotFound()
            : File(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), attachment.ContentType, attachment.OriginalFileName);
    }

    public string LocalTime(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, TimeZoneCatalog.Resolve(Day.Journal.TimeZone)).ToString("MMM d, yyyy · h:mm tt", CultureInfo.InvariantCulture);

    public string ReviewDate(Trade trade)
    {
        var value = trade.ExitUtc.HasValue && trade.Status.Equals("closed", StringComparison.OrdinalIgnoreCase) ? trade.ExitUtc.Value : trade.EntryUtc;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneCatalog.Resolve(Day.Journal.TimeZone)).DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public string DomId(string reviewKey) => string.Concat("review-", reviewKey.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_'));

    public string TagsText(TradeReviewAnnotation annotation) => string.Join(", ", annotation.Tags);

    public string DecimalInput(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private void LoadDay() => Day = _reviews.GetDay(JournalId, Date ?? _reviews.GetDefaultDate(JournalId));

    private static string? NextTradeKey(DailyReviewModel day, string reviewKey)
    {
        var keys = day.CompletedTrades.Concat(day.OpenTrades).Select(item => item.Trade.ReviewKey).ToArray();
        var index = Array.IndexOf(keys, reviewKey);
        return index >= 0 && index + 1 < keys.Length ? keys[index + 1] : null;
    }

    private static string? FirstTradeKey(DailyReviewModel day) => day.CompletedTrades.Concat(day.OpenTrades).Select(item => item.Trade.ReviewKey).FirstOrDefault();
}

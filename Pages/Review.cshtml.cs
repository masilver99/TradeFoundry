using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
public class ReviewModel : PageModel
{
    private readonly TradeReviewService _reviews;
    private readonly TradeFoundryDb _database;

    public ReviewModel(TradeReviewService reviews, TradeFoundryDb database)
    {
        _reviews = reviews;
        _database = database;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Date { get; set; }
    [BindProperty(SupportsGet = true)] public string? Focus { get; set; }
    [BindProperty(SupportsGet = true)] public string? Section { get; set; }
    [BindProperty(SupportsGet = true)] public string? Interval { get; set; }
    [BindProperty(SupportsGet = true)] public bool Saved { get; set; }
    [BindProperty(SupportsGet = true)] public bool JournalSaved { get; set; }
    [BindProperty] public TradeReviewPatch Edit { get; set; } = new();
    [BindProperty] public string? DailyJournalStateJson { get; set; }
    [BindProperty] public IFormFile? Screenshot { get; set; }
    [BindProperty] public string? AttachmentCaption { get; set; }

    public DailyReviewModel Day { get; private set; } = new();
    public MaeTargetSettings MaeTargets { get; private set; } = new();
    public string? ErrorMessage { get; private set; }
    public string? NoticeMessage { get; private set; }
    public DailyReviewTrade[] DayTrades => Day.CompletedTrades.Concat(Day.OpenTrades).ToArray();
    public DailyReviewTrade? ActiveTrade => DayTrades.FirstOrDefault(item => item.Trade.ReviewKey == Focus) ?? DayTrades.FirstOrDefault();

    public MaeTradeMetrics MaeReview(Trade trade, TradeReviewAnnotation? annotation = null)
        => MaeReviewMetrics.Build(trade, MaeTargets, annotation?.PlannedRiskCurrency, annotation?.PlannedRiskPoints);

    public IActionResult OnGet()
    {
        try
        {
            var date = Date ?? _reviews.GetDefaultDate(JournalId);
            Day = _reviews.GetDay(JournalId, date);
            MaeTargets = _database.GetMaeTargetSettings(JournalId);
            Date = date;
            DailyJournalStateJson = Day.DailyJournal.Text;
            Focus = ActiveTrade?.Trade.ReviewKey;
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }

        if (JournalSaved) NoticeMessage = "Daily journal saved.";
        else if (Saved) NoticeMessage = "Review saved.";
        return Page();
    }

    public IActionResult OnGetCandleChart(Guid journalId, string reviewKey, string? interval)
    {
        var trade = _database.GetTradeByReviewKey(journalId, reviewKey);
        if (trade is null)
            return NotFound();

        var journal = _database.GetJournal(trade.JournalId);
        if (journal is null)
            return NotFound();

        var query = GetTradeBarQuery(trade, interval);
        return new JsonResult(new
        {
            requestedInterval = query.RequestedInterval,
            resolvedInterval = query.ResolvedInterval,
            sourceTimeframe = trade.SourceTimeframe,
            timeZone = TimeZoneCatalog.CanonicalId(journal.TimeZone),
            barCount = query.Bars.Count,
            availabilityNote = query.AvailabilityNote,
            usedDefaultBarInterval = string.IsNullOrWhiteSpace(interval) && string.IsNullOrWhiteSpace(trade.SourceTimeframe),
            availableIntervals = BuildAvailableBarIntervals(query.AvailableSeries),
            payload = query.Bars.Count == 0 ? null : ChartRenderer.CandlePayload(trade, query, journal.TimeZone)
        });
    }

    public IActionResult OnGetCandleBars(Guid journalId, string reviewKey, string? interval, string? direction, string? cursor, int limit = 300)
    {
        var trade = _database.GetTradeByReviewKey(journalId, reviewKey);
        if (trade is null)
            return NotFound();

        if (!string.Equals(direction, "before", StringComparison.OrdinalIgnoreCase) && !string.Equals(direction, "after", StringComparison.OrdinalIgnoreCase))
            return BadRequest("direction must be before or after.");
        if (string.IsNullOrWhiteSpace(cursor) || !DateTimeOffset.TryParse(cursor, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var cursorUtc) || cursorUtc.Offset != TimeSpan.Zero)
            return BadRequest("cursor must be a valid UTC timestamp.");
        if (limit is < 1 or > 500)
            return BadRequest("limit must be between 1 and 500.");
        if (!BarIntervals.TryNormalize(interval, out var normalizedInterval, allowSource: true))
            return BadRequest("interval must be source or a positive minute-based interval.");

        try
        {
            var page = _database.GetBarHistoryPage(trade.JournalId, trade.Symbol, normalizedInterval, cursorUtc.ToUniversalTime(), string.Equals(direction, "before", StringComparison.OrdinalIgnoreCase), limit);
            return new JsonResult(new
            {
                bars = page.Bars.Select(bar => new
                {
                    time = bar.EventUtc.ToUnixTimeSeconds(),
                    open = bar.Open,
                    high = bar.High,
                    low = bar.Low,
                    close = bar.Close,
                    volume = bar.Volume
                }),
                hasMore = page.HasMore,
                interval = page.ResolvedInterval,
                direction = direction!.ToLowerInvariant()
            });
        }
        catch (FormatException exception)
        {
            return BadRequest(exception.Message);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    public IActionResult OnPostSaveDailyJournal(string dailyJournalStateJson, int expectedRevision, DateOnly date)
    {
        Date = date;
        try
        {
            var result = _reviews.SaveDailyJournal(JournalId, date, dailyJournalStateJson, expectedRevision);
            if (result.Conflict)
            {
                LoadDay();
                DailyJournalStateJson = result.Entry.Text;
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
            DailyJournalStateJson = dailyJournalStateJson;
            ErrorMessage = exception.Message;
            ModelState.AddModelError(string.Empty, ErrorMessage);
            return Page();
        }
        catch (InvalidOperationException exception)
        {
            LoadDay();
            DailyJournalStateJson = dailyJournalStateJson;
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
                    await _reviews.SaveAttachmentAsync(JournalId, reviewKey, content, Screenshot.FileName, Screenshot.ContentType, Screenshot.Length, cancellationToken: cancellationToken);
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
                : new { fees = trade.Fees, netPnl = trade.NetPnl, exchangeFees = trade.ExchangeFees, nfaFees = trade.NfaFees, clearingFees = trade.ClearingFees, hasAllInOverride = result.Annotation.AllInCommission.HasValue, hasComponentOverride = result.Annotation.ExchangeFees.HasValue || result.Annotation.NfaFees.HasValue || result.Annotation.ClearingFees.HasValue, hasReviewNotes = trade.HasReviewNotes, hasReviewImages = trade.HasReviewImages };

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

    public IActionResult OnPostAutosaveDailyJournal(string dailyJournalStateJson, int expectedRevision, DateOnly date)
    {
        Date = date;
        try
        {
            var result = _reviews.SaveDailyJournal(JournalId, date, dailyJournalStateJson, expectedRevision, "autosaved");
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

            return new JsonResult(new
            {
                saved = true,
                revision = result.Entry.Revision,
                updatedUtc = result.Entry.UpdatedUtc
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
            var attachment = await _reviews.SaveAttachmentAsync(JournalId, reviewKey, content, Screenshot.FileName, Screenshot.ContentType, Screenshot.Length, AttachmentCaption, cancellationToken);
            if (IsAjaxRequest())
            {
                return new JsonResult(new
                {
                    saved = true,
                    attachment = new
                    {
                        id = attachment.Id,
                        url = Url.Page("/Review", "Attachment", new { journalId = JournalId, attachmentId = attachment.Id }),
                        captionUrl = Url.Page("/Review", "UpdateAttachmentCaption", new { journalId = JournalId }),
                        originalFileName = attachment.OriginalFileName,
                        caption = attachment.Caption,
                        length = attachment.Length
                    }
                });
            }

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
            if (IsAjaxRequest())
            {
                Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                return new JsonResult(new { saved = false, message = exception.Message });
            }

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
        var removed = _reviews.RemoveAttachment(JournalId, attachmentId);
        if (IsAjaxRequest())
        {
            if (!removed)
            {
                return NotFound(new { removed = false, message = "The attachment could not be found." });
            }

            bool? hasReviewImages = string.IsNullOrWhiteSpace(focus) ? null : _reviews.GetAttachments(JournalId, focus).Count > 0;
            return new JsonResult(new { removed = true, attachmentId, hasReviewImages });
        }

        return RedirectToPage(new { journalId = JournalId, date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), focus, section = "media", saved = true });
    }

    public IActionResult OnPostUpdateAttachmentCaption(Guid attachmentId, string reviewKey, string? caption, DateOnly date, string? focus)
    {
        try
        {
            var updated = _reviews.UpdateAttachmentCaption(JournalId, reviewKey, attachmentId, caption);
            if (updated is null)
                return IsAjaxRequest() ? NotFound(new { saved = false, message = "The attachment could not be found." }) : NotFound();

            if (IsAjaxRequest())
                return new JsonResult(new { saved = true, caption = updated.Caption });

            return RedirectToPage(new
            {
                journalId = JournalId,
                date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                focus = focus ?? reviewKey,
                section = "media",
                saved = true
            });
        }
        catch (ArgumentException exception)
        {
            if (IsAjaxRequest())
            {
                Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                return new JsonResult(new { saved = false, message = exception.Message });
            }

            Date = date;
            Focus = focus ?? reviewKey;
            Section = "media";
            LoadDay();
            ErrorMessage = exception.Message;
            return Page();
        }
        catch (InvalidOperationException exception)
        {
            if (IsAjaxRequest())
            {
                Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                return new JsonResult(new { saved = false, message = exception.Message });
            }

            Date = date;
            Focus = focus ?? reviewKey;
            Section = "media";
            LoadDay();
            ErrorMessage = exception.Message;
            return Page();
        }
    }

    public IActionResult OnGetAttachment(Guid attachmentId)
    {
        var attachment = _reviews.GetAttachment(JournalId, attachmentId);
        if (attachment is null) return NotFound();
        var path = _reviews.GetAttachmentPath(JournalId, attachment);
        return path is null
            ? NotFound()
            : File(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), attachment.ContentType);
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

    private bool IsAjaxRequest() => string.Equals(Request.Headers["X-Requested-With"].ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

    private void LoadDay()
    {
        Day = _reviews.GetDay(JournalId, Date ?? _reviews.GetDefaultDate(JournalId));
        MaeTargets = _database.GetMaeTargetSettings(JournalId);
    }

    private BarQueryResult GetTradeBarQuery(Trade trade, string? interval)
    {
        var end = (trade.ExitUtc ?? trade.EntryUtc).AddMinutes(30);
        var available = _database.GetBarSeries(trade.JournalId, trade.Symbol);
        var requestedInterval = BarIntervals.TryNormalize(interval, out var normalizedInterval, allowSource: false)
            ? normalizedInterval
            : BarIntervals.TryNormalize(trade.SourceTimeframe, out var sourceTimeframe, allowSource: false)
                ? sourceTimeframe
            : available.Where(x => x.IntervalMinutes > 0).OrderBy(x => x.IntervalMinutes).Select(x => x.Interval).FirstOrDefault()
                ?? (available.Count > 0 ? available[0].Interval : "1m");
        return _database.GetBarWindow(trade.JournalId, trade.Symbol, trade.EntryUtc.AddMinutes(-30), end, requestedInterval);
    }

    private static IReadOnlyList<string> BuildAvailableBarIntervals(IReadOnlyList<BarSeriesInfo> series)
    {
        var intervals = series
            .Where(x => x.IntervalMinutes > 0)
            .Select(x => BarIntervals.Format(x.IntervalMinutes))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (series.Any(x => x.IntervalMinutes == 1 && x.BarCount > 0))
        {
            intervals.Add("2m");
            intervals.Add("5m");
        }

        return intervals
            .OrderBy(x => BarIntervals.TryGetMinutes(x, out var minutes) ? minutes : int.MaxValue)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? NextTradeKey(DailyReviewModel day, string reviewKey)
    {
        var keys = day.CompletedTrades.Concat(day.OpenTrades).Select(item => item.Trade.ReviewKey).ToArray();
        var index = Array.IndexOf(keys, reviewKey);
        return index >= 0 && index + 1 < keys.Length ? keys[index + 1] : null;
    }

    private static string? FirstTradeKey(DailyReviewModel day) => day.CompletedTrades.Concat(day.OpenTrades).Select(item => item.Trade.ReviewKey).FirstOrDefault();
}

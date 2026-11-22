using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
public sealed class TagsModel : PageModel
{
    public const int PageSize = 50;
    private readonly TradeFoundryDb _database;

    public TagsModel(TradeFoundryDb database) => _database = database;

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? TagId { get; set; }
    /// <summary>trades or days.</summary>
    [BindProperty(SupportsGet = true)] public string? Tab { get; set; }
    /// <summary>untagged, or empty for the tag list.</summary>
    [BindProperty(SupportsGet = true, Name = "view")] public string? ListView { get; set; }
    [BindProperty(SupportsGet = true, Name = "pageNumber")] public int PageNumber { get; set; } = 1;

    public Journal Journal { get; private set; } = new();
    public JournalFilterScope Filter { get; private set; } = JournalFilter.None.Scope(null);
    public TagCatalog Catalog { get; private set; } = new(Array.Empty<Tag>());
    public IReadOnlyDictionary<Guid, TagUsage> Usage { get; private set; } = new Dictionary<Guid, TagUsage>();
    public Tag? Selected { get; private set; }
    public TagDraft Editing { get; private set; } = new(null, null, TagKnown.Unset, TagScope.Trade);
    public IReadOnlyList<Tag> SimilarToConfirm { get; private set; } = Array.Empty<Tag>();
    public IReadOnlyList<Tag> LikelyDuplicates { get; private set; } = Array.Empty<Tag>();
    public bool CanDelete { get; private set; }
    public IReadOnlyList<Trade> TradeRows { get; private set; } = Array.Empty<Trade>();
    public IReadOnlyList<DayRow> DayRows { get; private set; } = Array.Empty<DayRow>();
    public int TradeTotal { get; private set; }
    public int DayTotal { get; private set; }
    public int DetachedCount { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? FlashMessage { get; private set; }

    public bool ShowsDays => Tab == "days";
    public bool ShowsUntagged => ListView == "untagged";
    public int PageCount => (int)Math.Ceiling((ShowsDays ? DayTotal : TradeTotal) / (double)PageSize);

    public sealed record TagUsage(int Trades, int Days, DateOnly? LastUsed)
    {
        public static readonly TagUsage None = new(0, 0, null);
    }

    public sealed record DayRow(DateOnly Date, int TradeCount, decimal NetPnl);

    public IActionResult OnGet()
    {
        if (!Load()) return NotFound();
        FlashMessage = TempData["TagFlash"] as string;
        return Page();
    }

    public IActionResult OnGetCatalog()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var catalog = _database.GetTagCatalog(JournalId);
        var counts = Counts();
        var otherNames = catalog.All.Select(tag => (Tag: tag, Current: catalog.Resolve(tag.Id)))
            .Where(item => item.Current is not null)
            .SelectMany(item => (item.Tag.Id == item.Current!.Id ? item.Tag.Aliases : item.Tag.Aliases.Prepend(item.Tag.Name)).Select(name => (item.Current.Id, Name: name)))
            .ToLookup(item => item.Id, item => item.Name);
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(new { tags = catalog.Current.Where(tag => !tag.Archived).Select(tag => Payload(tag, counts, otherNames[tag.Id])) });
    }

    public IActionResult OnGetCheck(string? name, Guid? exceptId)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var clean = TagNames.Clean(name);
        if (clean.Length is 0 or > Tag.NameMaxLength || TagNames.Key(clean).Length == 0)
            return new JsonResult(new { existing = (object?)null, similar = Array.Empty<object>() });
        var catalog = _database.GetTagCatalog(JournalId);
        var counts = Counts();
        var owner = catalog.Find(clean);
        var similar = owner is null ? catalog.Similar(clean, exceptId) : Array.Empty<Tag>();
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(new { existing = owner is null ? null : Payload(owner, counts), similar = similar.Select(tag => Payload(tag, counts)) });
    }

    public IActionResult OnPostCreate(string? name, string? description, TagKnown known, TagScope? appliesTo, List<Guid> confirmedDifferent)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        try
        {
            if (appliesTo is not { } scope) throw new ArgumentException("Choose what this tag applies to.");
            var tag = _database.CreateTag(JournalId, new TagDraft(name, description, known, scope), confirmedDifferent);
            return new JsonResult(new { tag = Payload(tag, Counts()) });
        }
        catch (TagNameException exception)
        {
            var counts = Counts();
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return new JsonResult(new
            {
                message = exception.Message,
                existing = exception.Existing is null ? null : Payload(exception.Existing, counts),
                similar = exception.Similar.Select(tag => Payload(tag, counts))
            });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return JsonError(exception.Message);
        }
    }

    public IActionResult OnPostAllowEverywhere(Guid id, int revision)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        try
        {
            return new JsonResult(new { tag = Payload(_database.AllowTagEverywhere(JournalId, id, revision), Counts()) });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return JsonError(exception.Message);
        }
    }

    public IActionResult OnPostSaveDay(DateOnly date, string? tagIds, int expectedRevision)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        if (date == default) return JsonError("Choose a day.");
        try
        {
            var ids = new List<Guid>();
            foreach (var part in (tagIds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Guid.TryParse(part, out var id)) return JsonError("The tag selection could not be read. Reload the page.");
                ids.Add(id);
            }
            var result = _database.SaveDailyTags(JournalId, date, ids, expectedRevision);
            if (!result.Saved) Response.StatusCode = StatusCodes.Status409Conflict;
            return new JsonResult(new
            {
                saved = result.Saved,
                revision = result.Set.Revision,
                tags = result.Set.Tags.Select(tag => new { id = tag.Id, name = tag.Name }),
                message = result.Saved ? null : "These tags changed in another window. The latest are shown; make your change again."
            });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return JsonError(exception.Message);
        }
    }

    public IActionResult OnPostUpdate(Guid id, int revision, string? name, string? description, TagKnown known, TagScope appliesTo, List<Guid> confirmedDifferent)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var draft = new TagDraft(name, description, known, appliesTo);
        try
        {
            _database.UpdateTag(JournalId, id, revision, draft, confirmedDifferent);
            return Done("Tag saved.", id);
        }
        catch (TagNameException exception)
        {
            return EditError(id, draft, exception.Message, exception.Similar);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return EditError(id, draft, exception.Message);
        }
    }

    public IActionResult OnPostArchive(Guid id, int revision, bool archived) =>
        Change(id, () => _database.SetTagArchived(JournalId, id, revision, archived), archived ? "Tag archived. Its history is unchanged." : "Tag restored.", id);

    public IActionResult OnPostMerge(Guid id, int revision, Guid targetId) =>
        Change(id, () => _database.MergeTag(JournalId, id, revision, targetId), "Tags merged. You can undo this under Merged tags.", targetId);

    public IActionResult OnPostUndoMerge(Guid id, int revision) =>
        Change(null, () => _database.UndoTagMerge(JournalId, id, revision), "Merge undone.", id);

    public IActionResult OnPostDelete(Guid id, int revision) =>
        Change(id, () => _database.DeleteTag(JournalId, id, revision), "Tag deleted.", null);

    private IActionResult Change(Guid? showOnError, Action action, string message, Guid? showAfter)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        try
        {
            action();
            return Done(message, showAfter);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            TagId = showOnError;
            if (!Load())
            {
                TagId = null;
                if (!Load()) return NotFound();
            }
            ErrorMessage = exception.Message;
            return Page();
        }
    }

    private IActionResult EditError(Guid id, TagDraft draft, string message, IReadOnlyList<Tag>? similar = null)
    {
        TagId = id;
        if (!Load()) return NotFound();
        Editing = draft;
        SimilarToConfirm = similar ?? Array.Empty<Tag>();
        ErrorMessage = message;
        return Page();
    }

    private IActionResult Done(string message, Guid? tagId)
    {
        TempData["TagFlash"] = message;
        return RedirectToPage("/Tags", new { journalId = JournalId, tagId });
    }

    private JsonResult JsonError(string message) => new(new { saved = false, message }) { StatusCode = StatusCodes.Status422UnprocessableEntity };

    private bool Load()
    {
        var journal = _database.GetJournal(JournalId);
        if (journal is null) return false;
        Journal = journal;
        Filter = JournalFilterRequest.Resolve(HttpContext, journal);
        Catalog = _database.GetTagCatalog(JournalId);
        if (TagId is { } requested)
        {
            Selected = Catalog.Resolve(requested);
            if (Selected is null) return false;
        }

        var timeZone = TimeZoneCatalog.Resolve(journal.TimeZone);
        var trades = Filter.Apply(_database.GetAllTrades(JournalId));
        var tradeTags = _database.GetTradeTags(JournalId);
        var allDayTags = _database.GetDayTags(JournalId);
        // Direction narrows trades, never which days carry a tag.
        var dayTags = allDayTags.Where(item => Filter.IncludesDate(item.Key)).ToArray();

        var tradeUses = trades.SelectMany(trade => (tradeTags.GetValueOrDefault(trade.ReviewKey) ?? Array.Empty<Tag>()).Select(tag => (tag.Id, Date: JournalFilter.TradeDate(trade, timeZone)))).ToLookup(item => item.Id, item => item.Date);
        var dayUses = dayTags.SelectMany(item => item.Value.Select(tag => (tag.Id, Date: item.Key))).ToLookup(item => item.Id, item => item.Date);
        Usage = Catalog.Current.ToDictionary(tag => tag.Id, tag =>
        {
            var used = tradeUses[tag.Id].Concat(dayUses[tag.Id]).ToArray();
            return new TagUsage(tradeUses[tag.Id].Count(), dayUses[tag.Id].Count(), used.Length == 0 ? null : used.Max());
        });

        if (Selected is not null || ShowsUntagged)
        {
            var summaries = DailyTradeAggregation.Build(trades, journal.TimeZone).ToDictionary(day => day.Date);
            var listedTrades = Selected is { } tag
                ? trades.Where(trade => tradeTags.GetValueOrDefault(trade.ReviewKey)?.Any(item => item.Id == tag.Id) == true).ToArray()
                : trades.Where(trade => !tradeTags.ContainsKey(trade.ReviewKey)).ToArray();
            var listedDays = Selected is { } dayTag
                ? dayTags.Where(item => item.Value.Any(value => value.Id == dayTag.Id)).Select(item => item.Key).ToArray()
                : summaries.Keys.Where(date => !allDayTags.ContainsKey(date)).ToArray();
            TradeTotal = listedTrades.Length;
            DayTotal = listedDays.Length;
            if (Tab is not ("trades" or "days")) Tab = TradeTotal == 0 && DayTotal > 0 ? "days" : "trades";
            PageNumber = Math.Clamp(PageNumber, 1, Math.Max(1, PageCount));
            var skip = (PageNumber - 1) * PageSize;
            if (ShowsDays)
            {
                DayRows = listedDays.OrderDescending().Skip(skip).Take(PageSize).Select(date =>
                    new DayRow(date, summaries.GetValueOrDefault(date)?.TotalTradeCount ?? 0, summaries.GetValueOrDefault(date)?.RealizedNetPnl ?? 0m)).ToArray();
            }
            else
            {
                TradeRows = listedTrades.OrderByDescending(trade => trade.EntryUtc).Skip(skip).Take(PageSize).ToArray();
            }
        }

        if (Selected is { } selected)
        {
            Editing = new TagDraft(selected.Name, selected.Description, selected.Known, selected.Scope);
            LikelyDuplicates = Catalog.Similar(selected.Name, selected.Id).Where(tag => !tag.Archived).ToArray();
            CanDelete = !Catalog.All.Any(tag => tag.MergedIntoId == selected.Id)
                && !tradeTags.Values.Any(list => list.Any(tag => tag.Id == selected.Id))
                && !allDayTags.Values.Any(list => list.Any(tag => tag.Id == selected.Id));
        }

        DetachedCount = _database.CountDetachedReviews(JournalId);
        return true;
    }

    private Dictionary<Guid, (int Trades, int Days)> Counts()
    {
        var trades = _database.GetTradeTags(JournalId).Values.SelectMany(tags => tags).ToLookup(tag => tag.Id);
        var days = _database.GetDayTags(JournalId).Values.SelectMany(tags => tags).ToLookup(tag => tag.Id);
        return trades.Select(group => group.Key).Union(days.Select(group => group.Key)).ToDictionary(id => id, id => (trades[id].Count(), days[id].Count()));
    }

    private static object Payload(Tag tag, IReadOnlyDictionary<Guid, (int Trades, int Days)> counts, IEnumerable<string>? otherNames = null) => new
    {
        id = tag.Id,
        name = tag.Name,
        description = tag.Description,
        revision = tag.Revision,
        archived = tag.Archived,
        needsReview = tag.NeedsReview,
        trade = tag.AppliesTo(TagTarget.Trade),
        day = tag.AppliesTo(TagTarget.Day),
        trades = counts.GetValueOrDefault(tag.Id).Trades,
        days = counts.GetValueOrDefault(tag.Id).Days,
        aliases = otherNames ?? tag.Aliases
    };

    public string TradeUrl(Trade trade) =>
        $"/journal/{JournalId:D}/review?date={JournalFilter.TradeDate(trade, Journal.TimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&focus={Uri.EscapeDataString(trade.ReviewKey)}&section=review";

    public string DayUrl(DateOnly date)
    {
        var text = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"/journal/{JournalId:D}/daily-journal?date={text}#daily-journal-{text}";
    }
}

public sealed record TagPickerViewModel(Guid JournalId, TagTarget Target, string InputId, IReadOnlyList<TagReference> Selected, DateOnly? Date = null, int Revision = 0)
{
    public static TagPickerViewModel ForTrade(Guid journalId, string inputId, TradeReviewAnnotation review) =>
        new(journalId, TagTarget.Trade, inputId, review.TagIds.Zip(review.Tags, (id, name) => new TagReference(id, name)).ToArray());

    public static TagPickerViewModel ForDay(Guid journalId, string inputId, DateOnly date, DailyTagSet set) =>
        new(journalId, TagTarget.Day, inputId, set.Tags, date, set.Revision);
}

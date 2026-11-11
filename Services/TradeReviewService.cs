using System.Globalization;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services;

public sealed class TradeReviewService
{
    public const long MaxAttachmentLength = JournalImageFileStore.MaxImageLength;
    public const int MaxAttachmentCaptionLength = 500;

    private readonly TradeFoundryDb _database;
    private readonly JournalImageFileStore _imageFiles;
    private readonly TradingSetupService? _setups;

    public TradeReviewService(TradeFoundryDb database)
        : this(database, null)
    {
    }

    public TradeReviewService(TradeFoundryDb database, TradingSetupService? setups)
    {
        _database = database;
        _setups = setups;
        _imageFiles = new JournalImageFileStore(Path.Combine(Path.GetDirectoryName(database.DatabasePath) ?? AppContext.BaseDirectory, "review-attachments"));
    }

    public DateOnly GetDefaultDate(Guid journalId) => GetDefaultDate(journalId, null);

    /// <summary>The latest trading day inside the filter; with no matching day, the end of the filter's range or today.</summary>
    public DateOnly GetDefaultDate(Guid journalId, JournalFilterScope? scope)
    {
        var journal = _database.GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        var trades = scope?.Apply(_database.GetAllTrades(journalId)) ?? _database.GetAllTrades(journalId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneCatalog.Resolve(journal.TimeZone)).DateTime);
        return DailyTradeAggregation.Build(trades, journal.TimeZone).LastOrDefault()?.Date
            ?? (scope?.To is { } to && to < today ? to : today);
    }

    public DailyReviewModel GetDay(Guid journalId, DateOnly date) => GetDay(journalId, date, null);

    /// <summary>
    /// One review day. With a journal filter, previous/next only step between days that have matching trades,
    /// and trades in the other direction are left out (counted in <see cref="DailyReviewModel.HiddenTradeCount"/>)
    /// unless <paramref name="showHidden"/> is set or <paramref name="focusReviewKey"/> names one of them.
    /// </summary>
    public DailyReviewModel GetDay(Guid journalId, DateOnly date, JournalFilterScope? scope, bool showHidden = false, string? focusReviewKey = null)
    {
        var journal = _database.GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        var allTrades = _database.GetAllTrades(journalId);
        var summaries = DailyTradeAggregation.Build(allTrades, journal.TimeZone);
        var navigation = scope is { IsActive: true } ? DailyTradeAggregation.Build(scope.Apply(allTrades), journal.TimeZone) : summaries;
        var summary = summaries.FirstOrDefault(item => item.Date == date);
        var completed = summary?.CompletedTrades ?? Array.Empty<Trade>();
        var open = summary?.OpenTrades ?? Array.Empty<Trade>();
        var hiddenCount = 0;
        if (scope is { Filter.HasSide: true })
        {
            var hidden = completed.Concat(open).Where(trade => !scope.Filter.MatchesSide(trade)).ToArray();
            hiddenCount = hidden.Length;
            if (focusReviewKey is not null && hidden.Any(trade => trade.ReviewKey == focusReviewKey)) showHidden = true;
            if (!showHidden && hiddenCount > 0)
            {
                completed = completed.Where(scope.Filter.MatchesSide).ToArray();
                open = open.Where(scope.Filter.MatchesSide).ToArray();
            }
        }
        var annotations = _database.GetTradeReviewAnnotations(journalId);
        var imports = new Dictionary<Guid, ImportBatch>();
        var setupDefinitions = _setups?.GetSetups(journalId, includeInactive: true) ?? Array.Empty<TradingSetupSummary>();

        DailyReviewTrade Map(Trade trade)
        {
            var annotation = annotations.TryGetValue(trade.ReviewKey, out var existing)
                ? existing
                : new TradeReviewAnnotation { JournalId = journalId, ReviewKey = trade.ReviewKey };
            ImportBatch? import = null;
            if (trade.ImportBatchId.HasValue)
            {
                if (!imports.TryGetValue(trade.ImportBatchId.Value, out import))
                {
                    import = _database.GetImport(trade.ImportBatchId.Value);
                    if (import is not null) imports[import.Id] = import;
                }
            }
            return new DailyReviewTrade
            {
                Trade = trade,
                ImportBatch = import,
                Annotation = annotation,
                Attachments = _database.GetTradeReviewAttachments(journalId, trade.ReviewKey),
                History = _database.GetTradeReviewHistory(journalId, trade.ReviewKey),
                SetupWorkspace = _setups?.GetTradeSetupWorkspace(journalId, trade.Id) ?? new TradeSetupWorkspace()
            };
        }

        var previous = navigation.Where(item => item.Date < date).Select(item => (DateOnly?)item.Date).LastOrDefault();
        var next = navigation.Where(item => item.Date > date).Select(item => (DateOnly?)item.Date).FirstOrDefault();
        return new DailyReviewModel
        {
            Journal = journal,
            Date = date,
            DailyJournal = _database.GetDailyJournal(journalId, date),
            CompletedTrades = completed.Select(Map).ToArray(),
            OpenTrades = open.Select(Map).ToArray(),
            HiddenTradeCount = hiddenCount,
            ShowingHiddenTrades = showHidden && hiddenCount > 0,
            OutsideFilterDates = scope is { HasDateBounds: true } && !scope.IncludesDate(date),
            SetupDefinitions = setupDefinitions,
            PreviousDate = previous,
            NextDate = next
        };
    }

    public TradeReviewSaveResult Save(Guid journalId, string reviewKey, TradeReviewPatch patch, string action = "saved") =>
        _database.SaveTradeReview(journalId, reviewKey, patch, action);

    public DailyJournalEntry GetDailyJournal(Guid journalId, DateOnly date) =>
        _database.GetDailyJournal(journalId, date);

    public DailyJournalSaveResult SaveDailyJournal(Guid journalId, DateOnly date, string? text, int expectedRevision, string action = "saved") =>
        _database.SaveDailyJournal(journalId, date, text, expectedRevision, action);

    public TradeReviewSaveResult Revert(Guid journalId, string reviewKey, int expectedRevision, int targetRevision, string reason = "Reverted review") =>
        _database.RevertTradeReview(journalId, reviewKey, expectedRevision, targetRevision, reason);

    public IReadOnlyList<TradeReviewHistoryEntry> GetHistory(Guid journalId, string reviewKey) =>
        _database.GetTradeReviewHistory(journalId, reviewKey);

    public IReadOnlyList<TradeReviewAttachment> GetAttachments(Guid journalId, string reviewKey) =>
        _database.GetTradeReviewAttachments(journalId, reviewKey);

    public TradeReviewAttachment? GetAttachment(Guid journalId, Guid attachmentId) =>
        _database.GetTradeReviewAttachment(journalId, attachmentId);

    public TradeReviewAttachment? UpdateAttachmentCaption(Guid journalId, string reviewKey, Guid attachmentId, string? caption)
    {
        if (string.IsNullOrWhiteSpace(reviewKey)) throw new ArgumentException("A review key is required.", nameof(reviewKey));
        _ = _database.GetTradeByReviewKey(journalId, reviewKey) ?? throw new InvalidOperationException("The trade for this review could not be found.");
        return _database.UpdateTradeReviewAttachmentCaption(journalId, reviewKey, attachmentId, TrimCaption(caption));
    }

    public Task<TradeReviewAttachment> SaveAttachmentAsync(Guid journalId, string reviewKey, Stream content, string originalFileName, string contentType, long length, CancellationToken cancellationToken = default) =>
        SaveAttachmentAsync(journalId, reviewKey, content, originalFileName, contentType, length, null, cancellationToken);

    public async Task<TradeReviewAttachment> SaveAttachmentAsync(Guid journalId, string reviewKey, Stream content, string originalFileName, string contentType, long length, string? caption, CancellationToken cancellationToken = default)
    {
        _ = _database.GetTradeByReviewKey(journalId, reviewKey) ?? throw new InvalidOperationException("The trade for this review could not be found.");
        var normalizedCaption = TrimCaption(caption);
        var stored = await _imageFiles.SaveAsync(journalId, content, contentType, length, "Screenshots", cancellationToken);
        try
        {
            var safeFileName = Path.GetFileName(string.IsNullOrWhiteSpace(originalFileName) ? $"screenshot{Path.GetExtension(stored.StorageKey)}" : originalFileName);
            return _database.AddTradeReviewAttachment(journalId, reviewKey, stored.StorageKey, TrimFileName(safeFileName), stored.ContentType, stored.Length, normalizedCaption);
        }
        catch
        {
            _imageFiles.Delete(journalId, stored.StorageKey);
            throw;
        }
    }

    public string? GetAttachmentPath(Guid journalId, TradeReviewAttachment attachment)
        => _imageFiles.GetPath(journalId, attachment.StorageKey);

    public bool RemoveAttachment(Guid journalId, Guid attachmentId)
    {
        var attachment = _database.RemoveTradeReviewAttachment(journalId, attachmentId);
        if (attachment is null) return false;
        _imageFiles.Delete(journalId, attachment.StorageKey);
        return true;
    }

    private static string TrimFileName(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 180 ? trimmed : trimmed[..180];
    }

    private static string TrimCaption(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= MaxAttachmentCaptionLength ? trimmed : trimmed[..MaxAttachmentCaptionLength];
    }
}

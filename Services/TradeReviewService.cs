using System.Globalization;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services;

public sealed class TradeReviewService
{
    public const long MaxAttachmentLength = 10 * 1024 * 1024;
    public const int MaxAttachmentCaptionLength = 500;

    private readonly TradeFoundryDb _database;
    private readonly string _attachmentRoot;

    public TradeReviewService(TradeFoundryDb database)
    {
        _database = database;
        _attachmentRoot = Path.Combine(Path.GetDirectoryName(database.DatabasePath) ?? AppContext.BaseDirectory, "review-attachments");
    }

    public DateOnly GetDefaultDate(Guid journalId)
    {
        var journal = _database.GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        return DailyTradeAggregation.Build(_database.GetAllTrades(journalId), journal.TimeZone).LastOrDefault()?.Date
            ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneCatalog.Resolve(journal.TimeZone)).DateTime);
    }

    public DailyReviewModel GetDay(Guid journalId, DateOnly date)
    {
        var journal = _database.GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        var summaries = DailyTradeAggregation.Build(_database.GetAllTrades(journalId), journal.TimeZone);
        var summary = summaries.FirstOrDefault(item => item.Date == date);
        var annotations = _database.GetTradeReviewAnnotations(journalId);
        var imports = new Dictionary<Guid, ImportBatch>();

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
                History = _database.GetTradeReviewHistory(journalId, trade.ReviewKey)
            };
        }

        var previous = summaries.Where(item => item.Date < date).Select(item => (DateOnly?)item.Date).LastOrDefault();
        var next = summaries.Where(item => item.Date > date).Select(item => (DateOnly?)item.Date).FirstOrDefault();
        return new DailyReviewModel
        {
            Journal = journal,
            Date = date,
            DailyJournal = _database.GetDailyJournal(journalId, date),
            CompletedTrades = summary?.CompletedTrades.Select(Map).ToArray() ?? Array.Empty<DailyReviewTrade>(),
            OpenTrades = summary?.OpenTrades.Select(Map).ToArray() ?? Array.Empty<DailyReviewTrade>(),
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
        if (length <= 0 || length > MaxAttachmentLength) throw new InvalidOperationException("Screenshots must be between 1 byte and 10 MB.");
        var extension = ExtensionFor(contentType);
        if (extension is null) throw new InvalidOperationException("Only PNG, JPEG, and WebP screenshots are supported.");
        _ = _database.GetTradeByReviewKey(journalId, reviewKey) ?? throw new InvalidOperationException("The trade for this review could not be found.");
        var normalizedCaption = TrimCaption(caption);

        var directory = Path.Combine(_attachmentRoot, journalId.ToString("D"));
        Directory.CreateDirectory(directory);
        var storageKey = $"{Guid.NewGuid():N}{extension}";
        var path = Path.Combine(directory, storageKey);
        try
        {
            long actualLength;
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                actualLength = await CopyBoundedAsync(content, output, cancellationToken);
            }

            if (actualLength <= 0 || actualLength > MaxAttachmentLength)
                throw new InvalidOperationException("Screenshots must be between 1 byte and 10 MB.");

            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (!await HasImageSignatureAsync(input, contentType, cancellationToken))
                    throw new InvalidOperationException("The screenshot content does not match its image type.");
            }

            var safeFileName = Path.GetFileName(string.IsNullOrWhiteSpace(originalFileName) ? $"screenshot{extension}" : originalFileName);
            return _database.AddTradeReviewAttachment(journalId, reviewKey, storageKey, TrimFileName(safeFileName), contentType, actualLength, normalizedCaption);
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    public string? GetAttachmentPath(Guid journalId, TradeReviewAttachment attachment)
    {
        var path = Path.GetFullPath(Path.Combine(_attachmentRoot, journalId.ToString("D"), attachment.StorageKey));
        var directory = Path.GetFullPath(Path.Combine(_attachmentRoot, journalId.ToString("D"))) + Path.DirectorySeparatorChar;
        return path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    public bool RemoveAttachment(Guid journalId, Guid attachmentId)
    {
        var attachment = _database.RemoveTradeReviewAttachment(journalId, attachmentId);
        if (attachment is null) return false;
        var path = GetAttachmentPath(journalId, attachment);
        if (path is not null && File.Exists(path)) File.Delete(path);
        return true;
    }

    private static string? ExtensionFor(string contentType) => contentType.Trim().ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/webp" => ".webp",
        _ => null
    };

    private static async Task<bool> HasImageSignatureAsync(Stream stream, string contentType, CancellationToken cancellationToken)
    {
        var header = new byte[12];
        var read = 0;
        while (read < header.Length)
        {
            var count = await stream.ReadAsync(header.AsMemory(read, header.Length - read), cancellationToken);
            if (count == 0) break;
            read += count;
        }

        return contentType.Trim().ToLowerInvariant() switch
        {
            "image/png" => read >= 8 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/jpeg" or "image/jpg" => read >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff,
            "image/webp" => read >= 12 && header[..4].SequenceEqual("RIFF"u8.ToArray()) && header[8..12].SequenceEqual("WEBP"u8.ToArray()),
            _ => false
        };
    }

    private static async Task<long> CopyBoundedAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return total;
            total += read;
            if (total > MaxAttachmentLength) throw new InvalidOperationException("Screenshots must be between 1 byte and 10 MB.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
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

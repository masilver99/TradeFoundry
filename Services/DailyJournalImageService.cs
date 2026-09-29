using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services;

public sealed class DailyJournalImageService
{
    private readonly TradeFoundryDb _database;
    private readonly JournalImageFileStore _files;

    public DailyJournalImageService(TradeFoundryDb database)
    {
        _database = database;
        _files = new JournalImageFileStore(Path.Combine(Path.GetDirectoryName(database.DatabasePath) ?? AppContext.BaseDirectory, "daily-journal-images"));
    }

    public async Task<DailyJournalImage> SaveAsync(Guid journalId, DateOnly date, Stream content, string originalFileName, string contentType, long length, CancellationToken cancellationToken = default)
    {
        _ = _database.GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        var stored = await _files.SaveAsync(journalId, content, contentType, length, "Images", cancellationToken);
        try
        {
            var suppliedName = Path.GetFileName(string.IsNullOrWhiteSpace(originalFileName)
                ? $"pasted-image{Path.GetExtension(stored.StorageKey)}"
                : originalFileName);
            var safeName = suppliedName.Length <= 180 ? suppliedName : suppliedName[..180];
            return _database.AddDailyJournalImage(journalId, date, stored.StorageKey, safeName, stored.ContentType, stored.Length);
        }
        catch
        {
            _files.Delete(journalId, stored.StorageKey);
            throw;
        }
    }

    public DailyJournalImage? Get(Guid journalId, DateOnly date, Guid imageId) =>
        _database.GetDailyJournalImage(journalId, date, imageId);

    public string? GetPath(Guid journalId, DailyJournalImage image) =>
        _files.GetPath(journalId, image.StorageKey);
}

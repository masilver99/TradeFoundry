using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class TradeReviewTests
{
    [Fact]
    public void DailyAggregationUsesExitDateForCompletedAndEntryDateForOpen()
    {
        var trades = new[]
        {
            new Trade
            {
                Id = Guid.NewGuid(), EntryUtc = new DateTimeOffset(2026, 9, 10, 3, 30, 0, TimeSpan.Zero),
                ExitUtc = new DateTimeOffset(2026, 9, 10, 4, 30, 0, TimeSpan.Zero), Status = "closed", NetPnl = 12m, Sequence = 1
            },
            new Trade
            {
                Id = Guid.NewGuid(), EntryUtc = new DateTimeOffset(2026, 9, 11, 5, 30, 0, TimeSpan.Zero),
                Status = "open", NetPnl = 0m, Sequence = 2
            }
        };

        var days = DailyTradeAggregation.Build(trades, "America/New_York");

        Assert.Equal(2, days.Count);
        Assert.Equal(new DateOnly(2026, 9, 10), days[0].Date);
        Assert.Single(days[0].CompletedTrades);
        Assert.Equal(new DateOnly(2026, 9, 11), days[1].Date);
        Assert.Single(days[1].OpenTrades);
        Assert.Equal(12m, days[0].RealizedNetPnl);
    }

    [Fact]
    public async Task ReviewAnnotationsAreRevisionCheckedAndAttachmentsStayOutsideEvidence()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var import = ImportRoundTrip(database, journal.Id);
            var trade = Assert.Single(database.GetAllTrades(journal.Id));
            var evidenceBefore = EvidenceCounts(database);

            var first = database.SaveTradeReview(journal.Id, trade.ReviewKey, new TradeReviewPatch
            {
                ReviewNote = "First pass",
                Setup = "Opening range",
                TagsText = "planned, planned, clean",
                ProcessRating = 4,
                ExpectedRevision = 0,
                Reason = "Initial review"
            });

            Assert.True(first.Saved);
            Assert.Equal(1, first.Annotation.Revision);
            Assert.Equal(["planned", "clean"], first.Annotation.Tags);
            Assert.Equal(evidenceBefore, EvidenceCounts(database));

            var conflict = database.SaveTradeReview(journal.Id, trade.ReviewKey, new TradeReviewPatch
            {
                ReviewNote = "Stale write",
                ExpectedRevision = 0
            });
            Assert.True(conflict.Conflict);
            Assert.Equal("First pass", conflict.Annotation.ReviewNote);

            var second = database.SaveTradeReview(journal.Id, trade.ReviewKey, new TradeReviewPatch
            {
                ReviewNote = "Second pass",
                ExpectedRevision = 1,
                Reason = "Refined review"
            });
            Assert.True(second.Saved);

            var reverted = database.RevertTradeReview(journal.Id, trade.ReviewKey, 2, 1);
            Assert.True(reverted.Saved);
            Assert.Equal("First pass", reverted.Annotation.ReviewNote);
            Assert.Equal(3, reverted.Annotation.Revision);

            var history = database.GetTradeReviewHistory(journal.Id, trade.ReviewKey);
            Assert.Contains(history, entry => entry.Action == "saved");
            Assert.Contains(history, entry => entry.Action == "conflict");
            Assert.Contains(history, entry => entry.Action == "reverted");

            var reviewService = new TradeReviewService(database);
            await using (var screenshot = new MemoryStream(PngSignature()))
            {
                var attachment = await reviewService.SaveAttachmentAsync(journal.Id, trade.ReviewKey, screenshot, "chart.png", "image/png", screenshot.Length);
                Assert.Single(reviewService.GetAttachments(journal.Id, trade.ReviewKey));
                Assert.NotNull(reviewService.GetAttachmentPath(journal.Id, attachment));
                Assert.True(reviewService.RemoveAttachment(journal.Id, attachment.Id));
                Assert.Empty(reviewService.GetAttachments(journal.Id, trade.ReviewKey));
            }

            database.RemoveImportBatch(import.Batch.Id);
            var removed = database.GetTradeReview(journal.Id, trade.ReviewKey);
            Assert.NotNull(removed);
            Assert.False(removed!.HasContent);
            Assert.Contains(database.GetTradeReviewHistory(journal.Id, trade.ReviewKey), entry => entry.Action == "source_removed");

            ImportRoundTrip(database, journal.Id);
            var reimported = database.GetTradeReview(journal.Id, trade.ReviewKey);
            Assert.NotNull(reimported);
            Assert.False(reimported!.HasContent);
            Assert.Equal(evidenceBefore, EvidenceCounts(database));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static TradeFoundryDb CreateDatabase(string directory) => new(Options.Create(new StorageOptions { DataDirectory = directory, DatabaseFileName = "journal.db" }));

    private static ImportResult ImportRoundTrip(TradeFoundryDb database, Guid journalId)
    {
        var entry = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var exit = entry.AddHours(1);
        var parsed = new ParsedImport { SourceType = TradeFoundryConstants.SierraFills, SourceApplication = TradeFoundryConstants.SierraChart };
        parsed.Records.Add(FillRecord("entry-key", "Buy", 5000m, entry, 1));
        parsed.Records.Add(FillRecord("exit-key", "Sell", 5001m, exit, 2));
        return database.CommitImport(journalId, "test.txt", parsed, "flat_to_flat", "source");
    }

    private static ParsedRecord FillRecord(string sourceKey, string side, decimal price, DateTimeOffset timestamp, int row) => new()
    {
        SourceType = TradeFoundryConstants.SierraFills,
        SourceKey = sourceKey,
        RowNumber = row,
        PayloadJson = "{}",
        Fill = new FillDraft
        {
            SourceType = TradeFoundryConstants.SierraFills, SourceKey = sourceKey, EventUtc = timestamp,
            SourceTimeText = timestamp.ToString("O"), Symbol = "MESZ26", Instrument = "MES", Account = "SIM",
            Side = side, Quantity = 1, Price = price, Fees = 0m, RowNumber = row, PointValue = 5m, TickSize = .25m
        }
    };

    private static IReadOnlyDictionary<string, long> EvidenceCounts(TradeFoundryDb database)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        using var connection = new SqliteConnection($"Data Source={database.DatabasePath}");
        connection.Open();
        foreach (var table in new[] { "journals", "import_batches", "raw_records", "fills", "order_events", "trades" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            counts[table] = Convert.ToInt64(command.ExecuteScalar());
        }
        return counts;
    }

    private static byte[] PngSignature() => [137, 80, 78, 71, 13, 10, 26, 10];

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "TradeFoundry.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string directory)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

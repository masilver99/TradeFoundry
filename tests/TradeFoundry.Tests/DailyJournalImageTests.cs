using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class DailyJournalImageTests
{
    [Fact]
    public async Task PastedImagesAreJournalAndDateScopedAndImageOnlyEntriesStaySearchable()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var otherJournal = database.CreateJournal("Paper", "paper", string.Empty, "UTC", "USD", "flat_to_flat");
            var date = new DateOnly(2026, 9, 28);
            var service = new DailyJournalImageService(database);

            await using var png = new MemoryStream(PngSignature());
            var image = await service.SaveAsync(journal.Id, date, png, "opening-chart.png", "image/png", png.Length);

            Assert.Equal("image/png", image.ContentType);
            Assert.Equal("opening-chart.png", image.OriginalFileName);
            Assert.True(File.Exists(service.GetPath(journal.Id, image)));
            Assert.Equal(image.Id, service.Get(journal.Id, date, image.Id)?.Id);
            Assert.Null(service.Get(journal.Id, date.AddDays(1), image.Id));
            Assert.Null(service.Get(otherJournal.Id, date, image.Id));

            var state = JsonSerializer.Serialize(new
            {
                root = new
                {
                    children = new[]
                    {
                        new
                        {
                            type = "daily-journal-image",
                            version = 1,
                            attachmentId = image.Id.ToString("D"),
                            src = $"/journal/{journal.Id:D}/daily-journal?handler=Image&date={date:yyyy-MM-dd}&imageId={image.Id:D}",
                            altText = image.OriginalFileName,
                            children = Array.Empty<object>()
                        }
                    }
                }
            });
            var saved = database.SaveDailyJournal(journal.Id, date, state, 0);
            Assert.True(saved.Saved);
            Assert.Equal($"[Image: {image.OriginalFileName}]", LexicalPlainText.Extract(saved.Entry.Text));
            Assert.Empty(LexicalPlainText.ExtractVisibleText(saved.Entry.Text));
            Assert.Single(database.SearchDailyJournalEntries(journal.Id, "opening-chart"));
            Assert.Single(DailyJournalFeedBuilder.Build(journal.Id, date.AddDays(1), null, 20, Array.Empty<DailyTradeSummary>(), [saved.Entry]).Days);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ImageUploadsRejectIncorrectSignaturesAndOversizedContent()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var service = new DailyJournalImageService(database);
            var date = new DateOnly(2026, 9, 28);

            await using (var invalid = new MemoryStream([1, 2, 3, 4]))
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(journal.Id, date, invalid, "fake.png", "image/png", invalid.Length));
                Assert.Contains("does not match", error.Message, StringComparison.OrdinalIgnoreCase);
            }

            await using (var tooLarge = new MemoryStream(new byte[JournalImageFileStore.MaxImageLength + 1]))
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(journal.Id, date, tooLarge, "large.png", "image/png", tooLarge.Length));
                Assert.Contains("10 MB", error.Message, StringComparison.Ordinal);
            }

            var journalImageDirectory = Path.Combine(directory, "daily-journal-images", journal.Id.ToString("D"));
            Assert.Empty(Directory.GetFiles(journalImageDirectory));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static TradeFoundryDb CreateDatabase(string directory) => new(Options.Create(new StorageOptions { DataDirectory = directory, DatabaseFileName = "journal.db" }));

    private static byte[] PngSignature() => [137, 80, 78, 71, 13, 10, 26, 10];

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tradefoundry-daily-journal-images-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

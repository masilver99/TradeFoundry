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
    public void MaeTargetsPersistPerJournalAndCanBeClearedWithoutTouchingAnotherJournal()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var live = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var paper = database.CreateJournal("Paper", "paper", string.Empty, "UTC", "USD", "flat_to_flat");

            database.SaveMaeTargetSettings(live.Id, 50m, new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["MESZ26_FUT_CME"] = 35m });

            var liveTargets = database.GetMaeTargetSettings(live.Id);
            Assert.Equal(50m, liveTargets.DefaultPerContract);
            Assert.Equal(35m, liveTargets.Resolve("MES"));
            Assert.Null(database.GetMaeTargetSettings(paper.Id).DefaultPerContract);
            Assert.Empty(database.GetMaeTargetSettings(paper.Id).InstrumentTargets);

            database.SaveMaeTargetSettings(live.Id, null, new Dictionary<string, decimal>());

            Assert.Null(database.GetMaeTargetSettings(live.Id).DefaultPerContract);
            Assert.Empty(database.GetMaeTargetSettings(live.Id).InstrumentTargets);
            Assert.Null(database.GetMaeTargetSettings(paper.Id).DefaultPerContract);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

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
                var attachment = await reviewService.SaveAttachmentAsync(journal.Id, trade.ReviewKey, screenshot, "chart.png", "image/png", screenshot.Length, "Entry and exit context");
                Assert.Equal("Entry and exit context", attachment.Caption);
                Assert.Equal("Entry and exit context", Assert.Single(reviewService.GetAttachments(journal.Id, trade.ReviewKey)).Caption);
                Assert.Single(reviewService.GetAttachments(journal.Id, trade.ReviewKey));
                Assert.NotNull(reviewService.GetAttachmentPath(journal.Id, attachment));
                var updatedAttachment = reviewService.UpdateAttachmentCaption(journal.Id, trade.ReviewKey, attachment.Id, "  Updated context  ");
                Assert.NotNull(updatedAttachment);
                Assert.Equal("Updated context", updatedAttachment!.Caption);
                Assert.Equal("Updated context", Assert.Single(reviewService.GetAttachments(journal.Id, trade.ReviewKey)).Caption);
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

    [Fact]
    public async Task LedgerExposesReviewIndicatorsAndDailyJournalIsRevisionChecked()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            ImportRoundTrip(database, journal.Id);
            var trade = Assert.Single(database.GetAllTrades(journal.Id));

            database.SaveTradeReview(journal.Id, trade.ReviewKey, new TradeReviewPatch { ReviewNote = "Reviewed", ExpectedRevision = 0 });
            var noteOnly = Assert.Single(database.GetTrades(new TradeQuery { JournalId = journal.Id }).Items);
            Assert.True(noteOnly.HasReviewNotes);
            Assert.False(noteOnly.HasReviewImages);

            var reviews = new TradeReviewService(database);
            await using (var image = new MemoryStream(PngSignature()))
                await reviews.SaveAttachmentAsync(journal.Id, trade.ReviewKey, image, "chart.png", "image/png", image.Length);

            var withImage = Assert.Single(database.GetTrades(new TradeQuery { JournalId = journal.Id }).Items);
            Assert.True(withImage.HasReviewNotes);
            Assert.True(withImage.HasReviewImages);

            var date = new DateOnly(2026, 9, 12);
            var empty = database.GetDailyJournal(journal.Id, date);
            Assert.Equal(0, empty.Revision);
            var saved = database.SaveDailyJournal(journal.Id, date, "Market was quiet.", empty.Revision);
            Assert.True(saved.Saved);
            Assert.Equal(1, saved.Entry.Revision);
            Assert.Equal("Market was quiet.", reviews.GetDay(journal.Id, date).DailyJournal.Text);

            var conflict = database.SaveDailyJournal(journal.Id, date, "Stale", expectedRevision: 0);
            Assert.True(conflict.Conflict);
            Assert.Equal("Market was quiet.", conflict.Entry.Text);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void AllInCommissionOverrideReplacesDerivedFeeAcrossTradeReads()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var parsed = new ParsedImport { SourceType = TradeFoundryConstants.SierraFills, SourceApplication = TradeFoundryConstants.SierraChart };
            parsed.Records.Add(FillRecord("entry-key", "Buy", 5000m, new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero), 1, .50m));
            parsed.Records.Add(FillRecord("exit-key", "Sell", 5001m, new DateTimeOffset(2026, 9, 10, 13, 0, 0, TimeSpan.Zero), 2, .50m));
            database.CommitImport(journal.Id, "test.txt", parsed, "flat_to_flat", "source");

            var imported = Assert.Single(database.GetAllTrades(journal.Id));
            Assert.Equal(1.00m, imported.Fees);
            Assert.Equal(4.00m, imported.NetPnl);

            var saved = database.SaveTradeReview(journal.Id, imported.ReviewKey, new TradeReviewPatch
            {
                ExchangeFees = 1.00m,
                NfaFees = .50m,
                ClearingFees = .75m,
                AllInCommission = 2.25m,
                ExpectedRevision = 0,
                Reason = "Corrected all-in round-trip commission"
            });

            Assert.True(saved.Saved);
            Assert.Equal(2.25m, saved.Annotation.AllInCommission);
            Assert.Equal(1.00m, saved.Annotation.ExchangeFees);
            Assert.Equal(.50m, saved.Annotation.NfaFees);
            Assert.Equal(.75m, saved.Annotation.ClearingFees);
            Assert.Equal(2.25m, database.GetTradeReview(journal.Id, imported.ReviewKey)!.AllInCommission);

            var fromLedger = Assert.Single(database.GetTrades(new TradeQuery { JournalId = journal.Id, PageSize = 50 }).Items);
            var fromDetail = database.GetTrade(journal.Id, imported.Id)!;
            var fromOverview = database.GetOverview(journal.Id);
            Assert.Equal(2.25m, fromLedger.Fees);
            Assert.Equal(2.75m, fromLedger.NetPnl);
            Assert.Equal(2.25m, fromDetail.Fees);
            Assert.Equal(2.75m, fromDetail.NetPnl);
            Assert.Equal(2.25m, fromOverview.Fees);
            Assert.Equal(1.00m, fromDetail.ExchangeFees);
            Assert.Equal(.50m, fromDetail.NfaFees);
            Assert.Equal(.75m, fromDetail.ClearingFees);
            Assert.Equal(2.75m, fromOverview.NetPnl);
            Assert.Equal(2.75m, Assert.Single(fromOverview.DailyPnl).NetPnl);
            Assert.Equal(2.75m, Assert.Single(fromOverview.Equity).CumulativePnl);
            Assert.Contains(database.GetTradeReviewHistory(journal.Id, imported.ReviewKey), entry => entry.AfterJson.Contains("AllInCommission", StringComparison.Ordinal));

            database.RebuildFlatTrades(journal.Id, "flat_to_flat");
            var rebuilt = Assert.Single(database.GetAllTrades(journal.Id));
            Assert.Equal(imported.ReviewKey, rebuilt.ReviewKey);
            Assert.Equal(2.25m, rebuilt.Fees);
            Assert.Equal(2.75m, rebuilt.NetPnl);

            var cleared = database.SaveTradeReview(journal.Id, imported.ReviewKey, new TradeReviewPatch
            {
                ExpectedRevision = 1,
                Reason = "Use imported commission again"
            });
            Assert.True(cleared.Saved);
            Assert.Equal(1.00m, Assert.Single(database.GetAllTrades(journal.Id)).Fees);
            Assert.Equal(4.00m, Assert.Single(database.GetAllTrades(journal.Id)).NetPnl);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void InstrumentFeeBreakdownIsAggregatedAcrossFillsAndSnapshottedOnTradeEvidence()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var instrument = Assert.Single(database.GetInstruments(), item => item.Code == "MES");
            Assert.True(database.SaveInstrument(instrument.Id, "MES", null, .35m, .01m, .13m, 5m, .25m));

            var csv = string.Join('\n',
                "Activity Type,DateTime,Symbol,BuySell,Quantity,Fill Price",
                "Fills,2026-09-10 12:00:00,MESZ26,Buy,1,5000",
                "Fills,2026-09-10 13:00:00,MESZ26,Sell,1,5001");
            var parsed = new ImportService(database).Parse(csv, "sierra", "source", "UTC", configuration: database.GetInstrumentConfiguration());
            Assert.Equal(.35m, parsed.Records[0].Fill!.ExchangeFeePerContract);
            database.CommitImport(journal.Id, "mes.txt", parsed, "flat_to_flat", "source");

            var trade = Assert.Single(database.GetAllTrades(journal.Id));
            Assert.Equal(.70m, trade.ExchangeFees);
            Assert.Equal(.02m, trade.NfaFees);
            Assert.Equal(.26m, trade.ClearingFees);
            Assert.Equal(.98m, trade.Fees);
            Assert.Equal(4.02m, trade.NetPnl);
            var overview = database.GetOverview(journal.Id);
            Assert.Equal(.70m, overview.ExchangeFees);
            Assert.Equal(.02m, overview.NfaFees);
            Assert.Equal(.26m, overview.ClearingFees);
            Assert.Equal(.98m, overview.Fees);

            var componentOverride = database.SaveTradeReview(journal.Id, trade.ReviewKey, new TradeReviewPatch
            {
                ExchangeFees = 1.10m,
                NfaFees = .03m,
                ClearingFees = .40m,
                ExpectedRevision = 0,
                Reason = "Corrected fee components"
            });
            Assert.True(componentOverride.Saved);
            var componentTrade = Assert.Single(database.GetAllTrades(journal.Id));
            Assert.Equal(1.10m, componentTrade.ExchangeFees);
            Assert.Equal(.03m, componentTrade.NfaFees);
            Assert.Equal(.40m, componentTrade.ClearingFees);
            Assert.Equal(1.53m, componentTrade.Fees);
            Assert.Equal(3.47m, componentTrade.NetPnl);

            var allInOverride = database.SaveTradeReview(journal.Id, trade.ReviewKey, new TradeReviewPatch
            {
                ExchangeFees = 9m,
                NfaFees = 9m,
                ClearingFees = 9m,
                AllInCommission = 2m,
                ExpectedRevision = 1,
                Reason = "Use all-in correction"
            });
            Assert.True(allInOverride.Saved);
            var allInTrade = Assert.Single(database.GetAllTrades(journal.Id));
            Assert.Equal(2m, allInTrade.Fees);
            Assert.Equal(3m, allInTrade.NetPnl);

            Assert.True(database.SaveInstrument(instrument.Id, "MES", null, 1m, 2m, 3m, 5m, .25m));
            database.RebuildFlatTrades(journal.Id, "flat_to_flat");
            var rebuilt = Assert.Single(database.GetAllTrades(journal.Id));
            Assert.Equal(9m, rebuilt.ExchangeFees);
            Assert.Equal(9m, rebuilt.NfaFees);
            Assert.Equal(9m, rebuilt.ClearingFees);
            Assert.Equal(2m, rebuilt.Fees);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void SierraImportUsesEmbeddedMinuteTimeframeForDerivedTrade()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var csv = string.Join('\n',
                "Activity Type,DateTime,Symbol,BuySell,Quantity,Fill Price,Order Action Source",
                "Fills,2026-09-10 12:00:00,MESZ26,Buy,1,5000,Chart Trade 5 min",
                "Fills,2026-09-10 13:00:00,MESZ26,Sell,1,5001,Chart Trade 5 min");

            var parsed = new ImportService(database).Parse(csv, "sierra", "source", "UTC");

            Assert.All(parsed.Records, record => Assert.Equal("5m", record.Fill?.SourceTimeframe));
            database.CommitImport(journal.Id, "sierra.csv", parsed, "flat_to_flat", "source");

            var trade = Assert.Single(database.GetAllTrades(journal.Id));
            Assert.Equal("5m", trade.SourceTimeframe);

            database.RebuildFlatTrades(journal.Id, "flat_to_flat");
            Assert.Equal("5m", Assert.Single(database.GetAllTrades(journal.Id)).SourceTimeframe);
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

    private static ParsedRecord FillRecord(string sourceKey, string side, decimal price, DateTimeOffset timestamp, int row, decimal fees = 0m) => new()
    {
        SourceType = TradeFoundryConstants.SierraFills,
        SourceKey = sourceKey,
        RowNumber = row,
        PayloadJson = "{}",
        Fill = new FillDraft
        {
            SourceType = TradeFoundryConstants.SierraFills, SourceKey = sourceKey, EventUtc = timestamp,
            SourceTimeText = timestamp.ToString("O"), Symbol = "MESZ26", Instrument = "MES", Account = "SIM",
            Side = side, Quantity = 1, Price = price, Fees = fees, RowNumber = row, PointValue = 5m, TickSize = .25m
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

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class ReviewIdentityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tradefoundry-identity-tests", Guid.NewGuid().ToString("N"));
    private readonly TradeFoundryDb _database;
    private readonly Guid _journal;

    public ReviewIdentityTests()
    {
        Directory.CreateDirectory(_directory);
        _database = new TradeFoundryDb(Options.Create(new StorageOptions { DataDirectory = _directory, DatabaseFileName = "journal.db" }));
        _database.CreateOwner("Owner", "test-hash");
        _journal = _database.CreateJournal("Identity", "live", "", "UTC", "USD", "flat_to_flat").Id;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ImportingWithAnotherGroupingKeepsTagsAndFeeOverrides()
    {
        Import("flat_to_flat", Fill("a", "Buy", 9), Fill("b", "Sell", 10));
        var key = Tag(Assert.Single(Trades()).ReviewKey, fee: 2m);
        var net = Assert.Single(Trades()).NetPnl;

        Import("fifo_lots", Fill("c", "Buy", 11), Fill("d", "Sell", 12));

        var first = Trades().Single(trade => trade.EntryUtc.Hour == 9);
        Assert.Equal(key, first.ReviewKey);
        Assert.Equal(net, first.NetPnl);
        AssertTagged(key);
        Assert.Equal(0, _database.CountDetachedReviews(_journal));
    }

    [Fact]
    public void BackfilledEarlierFillsCarryTheReviewToTheExtendedTrade()
    {
        Import("flat_to_flat", Fill("b", "Buy", 10), Fill("c", "Sell", 11));
        var key = Tag(Assert.Single(Trades()).ReviewKey, fee: 2m);

        Import("flat_to_flat", Fill("a", "Buy", 9), Fill("d", "Sell", 12));

        var extended = Assert.Single(Trades());
        Assert.Equal(key, extended.ReviewKey);
        Assert.Equal(2, extended.ClosedQuantity);
        Assert.Equal(2m, extended.Fees);
        AssertTagged(key);
    }

    [Fact]
    public void UndoingABackfillReturnsTheReviewToTheShorterTrade()
    {
        Import("flat_to_flat", Fill("b", "Buy", 10), Fill("c", "Sell", 11));
        var key = Tag(Assert.Single(Trades()).ReviewKey);
        var backfill = Import("flat_to_flat", Fill("a", "Buy", 9), Fill("d", "Sell", 12));

        _database.RemoveImportBatch(backfill.Batch.Id);

        Assert.Equal(key, Assert.Single(Trades()).ReviewKey);
        AssertTagged(key);
    }

    [Fact]
    public void UndoingTheExitKeepsTheReopenedTradesReview()
    {
        Import("flat_to_flat", Fill("a", "Buy", 9));
        var key = Tag(Assert.Single(Trades()).ReviewKey);
        var exit = Import("flat_to_flat", Fill("b", "Sell", 10));

        _database.RemoveImportBatch(exit.Batch.Id);

        var open = Assert.Single(Trades());
        Assert.Equal("open", open.Status);
        Assert.Equal(key, open.ReviewKey);
        AssertTagged(key);
    }

    [Fact]
    public void UndoingTheOpeningFillKeepsAnOpenTradeThatStillHasItsScaleIn()
    {
        var first = Import("flat_to_flat", Fill("a", "Buy", 9));
        var key = Tag(Assert.Single(Trades()).ReviewKey);
        Import("flat_to_flat", Fill("b", "Buy", 10));

        _database.RemoveImportBatch(first.Batch.Id);

        Assert.Equal(key, Assert.Single(Trades()).ReviewKey);
        AssertTagged(key);
    }

    [Fact]
    public void UndoingTheOpeningImportKeepsSetupsOnTheSurvivingTrade()
    {
        var first = Import("flat_to_flat", Fill("a", "Buy", 9));
        Import("flat_to_flat", Fill("b", "Buy", 10), Fill("c", "Sell", 11), Fill("d", "Sell", 12));
        var setups = new TradeFoundry.Services.TradingSetupService(_database);
        var setup = setups.CreateSetup(_journal, new TradingSetupDraft { Name = "Opening range" }, [new TradingSetupCriterionDraft { Name = "Range broke" }]);
        setups.AttachToTrade(_journal, Trades().Single(trade => trade.EntryUtc.Hour == 9).Id, setup.CurrentVersion!.Id, TradeSetupRole.Primary);
        var key = Tag(Trades().Single(trade => trade.EntryUtc.Hour == 9).ReviewKey);

        _database.RemoveImportBatch(first.Batch.Id);

        var survivor = Trades().Single(trade => trade.ReviewKey == key);
        Assert.Single(setups.GetTradeSetupWorkspace(_journal, survivor.Id).Evaluations);
    }

    [Fact]
    public void UndoingTheWholeTradeStillRemovesItsReviewAndTags()
    {
        var import = Import("flat_to_flat", Fill("a", "Buy", 9), Fill("b", "Sell", 10));
        var key = Tag(Assert.Single(Trades()).ReviewKey);

        _database.RemoveImportBatch(import.Batch.Id);

        Assert.Empty(Trades());
        Assert.Empty(_database.GetTradeReview(_journal, key)!.Tags);
        Assert.Contains(_database.GetTradeReviewHistory(_journal, key), entry => entry.Action == "source_removed");
        Assert.Equal(0, _database.CountDetachedReviews(_journal));
    }

    [Fact]
    public void ATradeLeftOnTheOtherSideIsNotGivenTheRemovedTradesReview()
    {
        var entry = Import("flat_to_flat", Fill("a", "Buy", 9));
        var key = Tag(Assert.Single(Trades()).ReviewKey);
        Import("flat_to_flat", Fill("b", "Sell", 10));

        _database.RemoveImportBatch(entry.Batch.Id);

        var remaining = Assert.Single(Trades());
        Assert.Equal("Short", remaining.Direction);
        Assert.NotEqual(key, remaining.ReviewKey);
        Assert.Null(_database.GetTradeReview(_journal, remaining.ReviewKey));
    }

    [Fact]
    public void AReviewedTradeKeepsItsKeyWhenABackfillSplitsItsSuccessorOff()
    {
        Import("flat_to_flat", Fill("a", "Buy", 8), Fill("b", "Buy", 10), Fill("c", "Sell", 13), Fill("d", "Sell", 14));
        var key = Tag(Assert.Single(Trades()).ReviewKey);

        Import("flat_to_flat", Fill("x", "Sell", 9), Fill("y", "Buy", 15));

        var trades = Trades();
        Assert.Equal(trades.Count, trades.Select(trade => trade.ReviewKey).Distinct().Count());
        Assert.Equal(key, trades.Single(trade => trade.EntryUtc.Hour == 8).ReviewKey);
    }

    [Fact]
    public void AKeyCarriedToAnEarlierFillIsNeverReusedByTheFillItCameFrom()
    {
        Import("flat_to_flat", Fill("b", "Buy", 10), Fill("c", "Sell", 13));
        var key = Tag(Assert.Single(Trades()).ReviewKey);
        Import("flat_to_flat", Fill("a", "Buy", 8), Fill("d", "Sell", 14));

        // The trade now splits, and "b" starts a trade again: its generated key would be the carried one.
        Import("flat_to_flat", Fill("x", "Sell", 9));

        var trades = Trades();
        Assert.Equal(trades.Count, trades.Select(trade => trade.ReviewKey).Distinct().Count());
        Assert.Equal(key, trades.Single(trade => trade.EntryUtc.Hour == 8).ReviewKey);
    }

    [Fact]
    public void AmbiguousMergesAreListedInsteadOfGuessed()
    {
        Import("flat_to_flat", Fill("b", "Buy", 10), Fill("c", "Sell", 11), Fill("e", "Buy", 12), Fill("f", "Sell", 13));
        foreach (var trade in Trades()) Tag(trade.ReviewKey);

        Import("flat_to_flat", Fill("a", "Buy", 9), Fill("z", "Sell", 14));

        var merged = Assert.Single(Trades());
        Assert.Null(_database.GetTradeReview(_journal, merged.ReviewKey));
        var detached = _database.GetDetachedReviews(_journal);
        Assert.Equal(2, detached.Count);
        Assert.All(detached, item => Assert.Equal([merged.ReviewKey], item.SuggestedKeys));
    }

    [Fact]
    public void ABackfillCarriesTheOnlyReviewAmongMergedTrades()
    {
        Import("flat_to_flat", Fill("b", "Buy", 10), Fill("c", "Sell", 11), Fill("e", "Buy", 12), Fill("f", "Sell", 13));
        var key = Tag(Trades().Single(trade => trade.EntryUtc.Hour == 12).ReviewKey);

        Import("flat_to_flat", Fill("a", "Buy", 9), Fill("z", "Sell", 14));

        Assert.Equal(key, Assert.Single(Trades()).ReviewKey);
        AssertTagged(key);
    }

    [Fact]
    public void AReviewDetachedByTheOldBugIsFoundAndCanBeAttached()
    {
        Import("flat_to_flat", Fill("a", "Buy", 9), Fill("b", "Sell", 10));
        var trade = Assert.Single(Trades());
        var key = Tag(trade.ReviewKey, fee: 2m);
        // What the old rebuild did: gave the same trade the key for a different grouping setting.
        var changed = TradeFoundryDb.CreateTradeReviewKey(_journal, "derived", "a", "fifo_lots", "SIM", "MESZ26", "Long");
        using (var connection = new SqliteConnection($"Data Source={_database.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE trades SET review_key = $key";
            command.Parameters.AddWithValue("$key", changed);
            command.ExecuteNonQuery();
        }
        Assert.Equal(0m, Assert.Single(Trades()).Fees);

        var detached = Assert.Single(_database.GetDetachedReviews(_journal));
        Assert.Equal(key, detached.Review.ReviewKey);
        Assert.Equal([changed], detached.SuggestedKeys);
        _database.SaveTradeReview(_journal, changed, new TradeReviewPatch { Lessons = "Written since" });

        var saved = _database.AttachDetachedReview(_journal, key, changed);

        Assert.Equal(["Breakout"], saved.Tags);
        Assert.Equal("Written since", saved.Lessons);
        Assert.Equal(2m, Assert.Single(Trades()).Fees);
        Assert.Equal(0, _database.CountDetachedReviews(_journal));
        Assert.Equal(["reattached", "detached original", "saved"], _database.GetTradeReviewHistory(_journal, changed).Select(entry => entry.Action));
    }

    private IReadOnlyList<Trade> Trades() => _database.GetAllTrades(_journal);

    private string Tag(string key, decimal? fee = null)
    {
        Assert.True(_database.SaveTradeReview(_journal, key, new TradeReviewPatch
        {
            TagIds = TestTags.Ids(_database, _journal, "Breakout"),
            AllInCommission = fee,
            ExpectedRevision = _database.GetTradeReview(_journal, key)?.Revision ?? 0
        }).Saved);
        return key;
    }

    private void AssertTagged(string key) => Assert.Equal(["Breakout"], _database.GetTradeReview(_journal, key)!.Tags);

    private ImportResult Import(string policy, params ParsedRecord[] records)
    {
        var parsed = new ParsedImport { SourceType = TradeFoundryConstants.SierraFills, SourceApplication = TradeFoundryConstants.SierraChart };
        parsed.Records.AddRange(records);
        return _database.CommitImport(_journal, "test.txt", parsed, policy, "source");
    }

    private static ParsedRecord Fill(string key, string side, int hour)
    {
        var time = new DateTimeOffset(2026, 9, 10, hour, 0, 0, TimeSpan.Zero);
        return new ParsedRecord
        {
            SourceType = TradeFoundryConstants.SierraFills, SourceKey = key, RowNumber = hour, PayloadJson = "{}",
            Fill = new FillDraft
            {
                SourceType = TradeFoundryConstants.SierraFills, SourceKey = key, EventUtc = time, SourceTimeText = time.ToString("O"),
                Symbol = "MESZ26", Instrument = "MES", Account = "SIM", Side = side, Quantity = 1, Price = 5000 + hour, RowNumber = hour, PointValue = 5m, TickSize = .25m
            }
        };
    }
}

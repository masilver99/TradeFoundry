using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class TagTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 9, 10);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tradefoundry-tag-tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _journal;
    private TradeFoundryDb _database;

    public TagTests()
    {
        Directory.CreateDirectory(_directory);
        _database = Open();
        _database.CreateOwner("Owner", "test-hash");
        _journal = _database.CreateJournal("Tags", "live", "", "UTC", "USD", "flat_to_flat").Id;
        var parsed = new ParsedImport { SourceType = TradeFoundryConstants.SierraFills, SourceApplication = TradeFoundryConstants.SierraChart };
        for (var index = 0; index < 8; index++)
        {
            var time = new DateTimeOffset(Day.ToDateTime(new TimeOnly(9 + index, 0)), TimeSpan.Zero);
            parsed.Records.Add(new ParsedRecord
            {
                SourceType = parsed.SourceType, SourceKey = $"f{index}", RowNumber = index + 1, PayloadJson = "{}",
                Fill = new FillDraft { SourceType = parsed.SourceType, SourceKey = $"f{index}", EventUtc = time, SourceTimeText = time.ToString("O"), Symbol = "MESZ26", Instrument = "MES", Account = "SIM", Side = index % 2 == 0 ? "Buy" : "Sell", Quantity = 1, Price = 5000 + index, RowNumber = index + 1, PointValue = 5m, TickSize = .25m }
            });
        }
        _database.CommitImport(_journal, "test.txt", parsed, "flat_to_flat", "source");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("Break out", "breakout")]
    [InlineData("Break-out", "BREAKOUT")]
    [InlineData("break_out", "Break out")]
    [InlineData("Break - out", "Breakout")]
    [InlineData("  Trend   Day ", "trend/day")]
    [InlineData("Trader's plan", "Traders plan")]
    [InlineData("ＶＷＡＰ", "vwap")]
    public void SpellingVariantsShareAKey(string first, string second) => Assert.Equal(TagNames.Key(first), TagNames.Key(second));

    [Theory]
    [InlineData("A+", "A-")]
    [InlineData("A+", "A")]
    [InlineData("Loss -1R", "Loss 1R")]
    [InlineData("-1R", "1R")]
    [InlineData("1.5R", "15R")]
    [InlineData("5 15", "515")]
    public void MeaningfulSymbolsKeepNamesApart(string first, string second) => Assert.NotEqual(TagNames.Key(first), TagNames.Key(second));

    [Theory]
    [InlineData("Brekout", "Breakout")]
    [InlineData("VWPA", "VWAP")]
    [InlineData("Breakouts", "Breakout")]
    [InlineData("Day trend", "Trend day")]
    public void LikelyTyposAreFlagged(string typed, string existing) => Assert.NotNull(TagNames.Distance(typed, existing));

    [Fact]
    public void DifferentWordsAreNotFlagged() => Assert.Null(TagNames.Distance("Breakout", "Reversal"));

    [Fact]
    public void ATypoCannotBecomeATagWithoutBeingConfirmed()
    {
        var breakout = Create("Breakout");

        var refused = Assert.Throws<TagNameException>(() => Create("Brekout"));
        Assert.Equal(breakout.Id, Assert.Single(refused.Similar).Id);
        Assert.Throws<TagNameException>(() => Create("Brekout", confirm: Guid.NewGuid()));
        Assert.Single(_database.GetTagCatalog(_journal).All);

        Create("Brekout", confirm: breakout.Id);
        Assert.Equal(2, _database.GetTagCatalog(_journal).All.Count);
    }

    [Fact]
    public void AnExactVariantCannotBeConfirmedAsDifferent()
    {
        var breakout = Create("Breakout");
        var refused = Assert.Throws<TagNameException>(() => Create("break-out", confirm: breakout.Id));
        Assert.Equal(breakout.Id, refused.Existing!.Id);
    }

    [Theory]
    [InlineData("", "Described", TagKnown.Beforehand)]
    [InlineData("Named", "  ", TagKnown.Beforehand)]
    [InlineData("Named", "Described", TagKnown.Unset)]
    [InlineData("One, two", "Described", TagKnown.Beforehand)]
    [InlineData("---", "Described", TagKnown.Unset)]
    public void NameDescriptionAndTimingAreRequired(string name, string description, TagKnown known) =>
        Assert.ThrowsAny<ArgumentException>(() => _database.CreateTag(_journal, new TagDraft(name, description, known, TagScope.Trade)));

    [Fact]
    public void TagsArePerJournal()
    {
        var tag = Create("Breakout");
        var paper = _database.CreateJournal("Paper", "paper", "", "UTC", "USD", "flat_to_flat").Id;

        Assert.Empty(_database.GetTagCatalog(paper).All);
        Assert.Throws<ArgumentException>(() => _database.SaveDailyTags(paper, Day, [tag.Id], 0));
        _database.CreateTag(paper, new TagDraft("Breakout", "Paper meaning", TagKnown.Beforehand, TagScope.Both));
    }

    [Fact]
    public void AMissingTagFieldLeavesTagsAloneAndAnEmptyOneClearsThem()
    {
        var key = Key(0);
        Save(key, tagIds: Create("Breakout").Id.ToString());

        Assert.Equal(["Breakout"], Save(key, tagIds: null, lessons: "From an old tab").Annotation.Tags);
        Assert.Empty(Save(key, tagIds: string.Empty).Annotation.Tags);
    }

    [Fact]
    public void TheFormKeepsAnEmptyTagFieldDistinctFromAMissingOne()
    {
        var property = typeof(TradeReviewPatch).GetProperty(nameof(TradeReviewPatch.TagIds))!;
        var format = Assert.Single(property.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.DisplayFormatAttribute), false));
        Assert.False(((System.ComponentModel.DataAnnotations.DisplayFormatAttribute)format).ConvertEmptyStringToNull);
    }

    [Fact]
    public void UnknownWrongScopeAndArchivedTagsCannotBeAdded()
    {
        var dayOnly = Create("CPI day", TagScope.Day);
        var archived = Create("Breakout");
        Save(Key(1), tagIds: archived.Id.ToString());
        _database.SetTagArchived(_journal, archived.Id, archived.Revision, true);

        Assert.Throws<ArgumentException>(() => Save(Key(0), Guid.NewGuid().ToString()));
        Assert.Throws<ArgumentException>(() => Save(Key(0), "not-an-id"));
        Assert.Throws<ArgumentException>(() => Save(Key(0), dayOnly.Id.ToString()));
        Assert.Throws<ArgumentException>(() => Save(Key(0), archived.Id.ToString()));
        // What is already on a trade can stay.
        Assert.Equal(["Breakout"], Save(Key(1), archived.Id.ToString(), "Still editable").Annotation.Tags);
    }

    [Fact]
    public void AStaleSaveDoesNotChangeTags()
    {
        var key = Key(0);
        Save(key, Create("Breakout").Id.ToString());

        var stale = _database.SaveTradeReview(_journal, key, new TradeReviewPatch { TagIds = string.Empty, ExpectedRevision = 0 });

        Assert.True(stale.Conflict);
        Assert.Equal(["Breakout"], _database.GetTradeReview(_journal, key)!.Tags);
    }

    [Fact]
    public void RenameShowsEverywhereAndTheOldNameStillFindsTheTag()
    {
        var tag = Create("Breakout");
        Save(Key(0), tag.Id.ToString());
        _database.SaveDailyTags(_journal, Day, [tag.Id], 0);

        _database.UpdateTag(_journal, tag.Id, tag.Revision, new TagDraft("Range break", "Broke the range", TagKnown.Beforehand, TagScope.Both));

        Assert.Equal(["Range break"], _database.GetTradeReview(_journal, Key(0))!.Tags);
        Assert.Equal("Range break", Assert.Single(_database.GetDailyTagSet(_journal, Day).Tags).Name);
        Assert.Equal(tag.Id, _database.GetTagCatalog(_journal).Find("breakout")!.Id);
        Assert.Throws<TagNameException>(() => Create("Breakout"));
        Assert.Single(_database.SearchTradeReviewNotes(_journal, "range break"));
    }

    [Fact]
    public void ARenameCanTakeBackTheFormerName()
    {
        var tag = Create("Breakout");
        var renamed = _database.UpdateTag(_journal, tag.Id, tag.Revision, new TagDraft("Range break", "Broke", TagKnown.Beforehand, TagScope.Both));
        _database.UpdateTag(_journal, tag.Id, renamed.Revision, new TagDraft("Breakout", "Broke", TagKnown.Beforehand, TagScope.Both));

        var current = Assert.Single(_database.GetTagCatalog(_journal).All);
        Assert.Equal("Breakout", current.Name);
        Assert.Equal(["Range break"], current.Aliases);
    }

    [Fact]
    public void ScopeCannotNarrowAwayFromExistingUse()
    {
        var tag = Create("Trend");
        _database.SaveDailyTags(_journal, Day, [tag.Id], 0);
        Assert.Throws<ArgumentException>(() => _database.UpdateTag(_journal, tag.Id, tag.Revision, new TagDraft("Trend", "Trending", TagKnown.Beforehand, TagScope.Trade)));
    }

    [Fact]
    public void MergeAndUndoKeepEveryLink()
    {
        var breakout = Create("Breakout");
        var typo = Create("Brekout", confirm: breakout.Id);
        Save(Key(0), typo.Id.ToString());
        Save(Key(1), $"{typo.Id},{breakout.Id}");
        Save(Key(2), breakout.Id.ToString());
        _database.SaveDailyTags(_journal, Day, [typo.Id], 0);

        var merged = _database.MergeTag(_journal, typo.Id, typo.Revision, breakout.Id);
        Assert.All(new[] { Key(0), Key(1), Key(2) }, key => Assert.Equal(["Breakout"], _database.GetTradeReview(_journal, key)!.Tags));
        Assert.Equal("Breakout", Assert.Single(_database.GetDailyTagSet(_journal, Day).Tags).Name);
        // An autosave during the merge must not rewrite the original link.
        Save(Key(0), breakout.Id.ToString(), "Edited while merged");
        var revisionBeforeUndo = _database.GetTradeReview(_journal, Key(0))!.Revision;

        _database.UndoTagMerge(_journal, typo.Id, merged.Revision);

        Assert.Equal(["Brekout"], _database.GetTradeReview(_journal, Key(0))!.Tags);
        Assert.Equal(["Breakout", "Brekout"], _database.GetTradeReview(_journal, Key(1))!.Tags.Order());
        Assert.Equal(["Breakout"], _database.GetTradeReview(_journal, Key(2))!.Tags);
        Assert.Equal(typo.Id, Assert.Single(_database.GetDailyTagSet(_journal, Day).Tags).Id);
        // A page left open from before the undo must conflict instead of saving the merged tag back.
        Assert.True(_database.SaveTradeReview(_journal, Key(0), new TradeReviewPatch { TagIds = breakout.Id.ToString(), ExpectedRevision = revisionBeforeUndo }).Conflict);
        Assert.False(_database.SaveDailyTags(_journal, Day, [breakout.Id], 1).Saved);
    }

    [Fact]
    public void UndoingAMergeInsideAChainMovesTheEarlierMergeBackToo()
    {
        var first = Create("Alpha");
        var second = Create("Momentum");
        var third = Create("Reversal");
        Save(Key(0), first.Id.ToString());
        _database.MergeTag(_journal, first.Id, first.Revision, second.Id);
        var moved = _database.MergeTag(_journal, second.Id, second.Revision, third.Id);
        Assert.Equal(["Reversal"], _database.GetTradeReview(_journal, Key(0))!.Tags);

        _database.UndoTagMerge(_journal, second.Id, moved.Revision);

        Assert.Equal(["Momentum"], _database.GetTradeReview(_journal, Key(0))!.Tags);
    }

    [Fact]
    public void OnlyUnusedTagsCanBeDeleted()
    {
        var used = Create("Breakout");
        var unused = Create("Reversal");
        Save(Key(0), used.Id.ToString());

        Assert.Throws<ArgumentException>(() => _database.DeleteTag(_journal, used.Id, used.Revision));
        _database.DeleteTag(_journal, unused.Id, unused.Revision);

        Assert.Equal(used.Id, Assert.Single(_database.GetTagCatalog(_journal).All).Id);
    }

    [Fact]
    public void RevertRestoresTagsIncludingOnesDeletedSince()
    {
        var tag = Create("Breakout");
        var first = Save(Key(0), tag.Id.ToString()).Annotation;
        var cleared = Save(Key(0), string.Empty).Annotation;
        _database.DeleteTag(_journal, tag.Id, tag.Revision);

        var reverted = _database.RevertTradeReview(_journal, Key(0), cleared.Revision, first.Revision);

        Assert.Equal(["Breakout"], reverted.Annotation.Tags);
        Assert.True(Assert.Single(_database.GetTagCatalog(_journal).All).NeedsReview);
    }

    [Fact]
    public void RevertToARevisionWithoutTagsClearsThem()
    {
        var first = Save(Key(0), null, "No tags yet").Annotation;
        var tagged = Save(Key(0), Create("Breakout").Id.ToString()).Annotation;
        Assert.Empty(_database.RevertTradeReview(_journal, Key(0), tagged.Revision, first.Revision).Annotation.Tags);
    }

    [Fact]
    public void DayTagsHaveTheirOwnRevision()
    {
        var tag = Create("CPI day", TagScope.Day);
        Assert.True(_database.SaveDailyJournal(_journal, Day, "Written", 0).Saved);

        Assert.True(_database.SaveDailyTags(_journal, Day, [tag.Id], 0).Saved);
        Assert.True(_database.SaveDailyJournal(_journal, Day, "Written more", 1).Saved);
        Assert.False(_database.SaveDailyTags(_journal, Day, [], 0).Saved);
        Assert.Single(_database.GetDailyTagSet(_journal, Day).Tags);
    }

    [Fact]
    public void TagOnlyDaysAppearInTheFeedAndInSearch()
    {
        var date = Day.AddDays(-3);
        _database.SaveDailyTags(_journal, date, [Create("Holiday", TagScope.Day).Id], 0);

        var feed = DailyJournalFeedBuilder.Build(_journal, Day, null, 20, [], [], [date]);

        Assert.Equal(date, Assert.Single(feed.Days).Entry.Date);
        Assert.Equal(date, Assert.Single(_database.SearchDailyJournalEntries(_journal, "holiday")).Date);
    }

    [Fact]
    public void ReportTotalsMatchTheOverviewIncludingFeeOverrides()
    {
        var tag = Create("Everything");
        var keys = new[] { Key(0), Key(1), Key(2), Key(3) };
        foreach (var key in keys) Save(key, tag.Id.ToString());
        // Each trade makes $5 gross, so this override turns one winner into a loser.
        _database.SaveTradeReview(_journal, keys[0], new TradeReviewPatch { AllInCommission = 9m, ExpectedRevision = 1 });

        var journal = _database.GetJournal(_journal)!;
        var sample = Assert.Single(TagPerformanceReport.Load(_database, journal, JournalFilter.None.Scope("UTC"), TagTarget.Trade, null).Rows).Sample;

        var overview = _database.GetOverview(_journal);
        Assert.Equal(overview.ClosedTradeCount, sample.Count);
        Assert.Equal(overview.WinningTrades, sample.Wins);
        Assert.Equal(overview.NetPnl, sample.TotalPnl);
        Assert.Equal(overview.ProfitFactor, sample.ProfitFactor!.Value, 8);
    }

    [Fact]
    public void AnOldDatabaseUpgradesWithoutLosingTags()
    {
        var keys = new[] { Key(0), Key(1), Key(2) };
        foreach (var key in keys) Save(key, null, "Written before tags");
        Sql($"""
            UPDATE trade_review_annotations SET tags_json = '["Breakout","A+"]' WHERE review_key = '{keys[0]}';
            UPDATE trade_review_annotations SET tags_json = '["break-out","A-"]' WHERE review_key = '{keys[1]}';
            UPDATE trade_review_annotations SET tags_json = '["Breakout","Brekout"]' WHERE review_key = '{keys[2]}';
            DELETE FROM app_settings WHERE settings_key = 'tags_v1_migrated';
            """);

        _database = Open();

        var catalog = _database.GetTagCatalog(_journal);
        Assert.Equal(["A+", "A-", "Breakout", "Brekout"], catalog.All.Select(tag => tag.Name).Order(StringComparer.Ordinal));
        Assert.All(catalog.All, tag => Assert.True(tag.NeedsReview && !tag.IsPredictive));
        Assert.Equal(["Breakout", "A+"], _database.GetTradeReview(_journal, keys[0])!.Tags);
        Assert.Equal(["Breakout", "A-"], _database.GetTradeReview(_journal, keys[1])!.Tags);
        Assert.Equal(["Breakout", "Brekout"], _database.GetTradeReview(_journal, keys[2])!.Tags);

        var backup = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "backups"), "tags-migration-*.db"));
        Assert.Equal(3L, Scalar(backup, "SELECT COUNT(*) FROM trade_review_annotations WHERE tags_json <> '[]'"));
        Assert.Equal(3L, Scalar(backup, "SELECT COUNT(*) FROM trade_review_history"));

        _database = Open();
        Assert.Equal(4, _database.GetTagCatalog(_journal).All.Count);
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "backups")));
    }

    [Fact]
    public void HistorySavedBeforeTagsHadIdsCanBeReverted()
    {
        var saved = Save(Key(0), null, "Old").Annotation;
        Sql($$"""UPDATE trade_review_history SET after_json = '{"Lessons":"Old","Tags":["Momentum"]}' WHERE review_key = '{{Key(0)}}'""");

        Assert.Equal(["Momentum"], _database.RevertTradeReview(_journal, Key(0), saved.Revision, saved.Revision).Annotation.Tags);
    }

    private TradeFoundryDb Open() => new(Options.Create(new StorageOptions { DataDirectory = _directory, DatabaseFileName = "journal.db" }));

    private string Key(int index) => _database.GetAllTrades(_journal).OrderBy(trade => trade.EntryUtc).ElementAt(index).ReviewKey;

    private Tag Create(string name, TagScope scope = TagScope.Both, Guid? confirm = null) =>
        _database.CreateTag(_journal, new TagDraft(name, $"When {name} applies.", TagKnown.Beforehand, scope), confirm is { } id ? [id] : null);

    private TradeReviewSaveResult Save(string key, string? tagIds, string lessons = "") =>
        _database.SaveTradeReview(_journal, key, new TradeReviewPatch { TagIds = tagIds, Lessons = lessons, ExpectedRevision = _database.GetTradeReview(_journal, key)?.Revision ?? 0 });

    private void Sql(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_database.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.PublicJournal;
using TradeFoundry.Publishing;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class PublicationTests
{
    [Fact]
    public void ExportAppliesJournalFilterToTradesEntriesAndStatistics()
    {
        using var data = new LocalData();
        var csv = string.Join('\n', "Activity Type,DateTime,Symbol,Account,BuySell,Quantity,Fill Price,Order Action Source",
            "Fills,2026-09-10 12:00:00,MESZ26,ACC,Buy,1,5000,SRC",
            "Fills,2026-09-10 13:00:00,MESZ26,ACC,Sell,1,5002,SRC",
            "Fills,2026-09-11 12:00:00,MESZ26,ACC,Sell,1,5000,SRC",
            "Fills,2026-09-11 13:00:00,MESZ26,ACC,Buy,1,5001,SRC",
            "Fills,2026-09-12 12:00:00,MESZ26,ACC,Buy,1,5000,SRC",
            "Fills,2026-09-12 13:00:00,MESZ26,ACC,Sell,1,5004,SRC");
        new ImportService(data.Database).ImportText(data.Journal.Id, "fixture.csv", csv, "sierra", "flat_to_flat", "source");
        Assert.Equal(3, data.Database.GetAllTrades(data.Journal.Id).Count);
        data.Database.SaveDailyJournal(data.Journal.Id, new DateOnly(2026, 9, 9), "Outside range", 0);
        data.Database.SaveDailyJournal(data.Journal.Id, new DateOnly(2026, 9, 11), "Inside range", 0);
        var exporter = new PublicationExportService(data.Database, new(data.Database));

        var all = exporter.Build(data.Journal.Id, new(Title: "Public"), JournalFilter.None.Scope("UTC"));
        Assert.Equal(3, all.Dashboard.CompletedTrades);
        Assert.Equal(4, all.Days.Count);

        var range = exporter.Build(data.Journal.Id, new(Title: "Public"),
            JournalFilter.Parse("custom", "2026-09-10", "2026-09-11", null).Scope("UTC"));
        Assert.Equal(2, range.Dashboard.CompletedTrades);
        Assert.Equal(new[] { new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 11) }, range.Days.Select(d => d.Date));
        Assert.DoesNotContain("Outside range", JsonSerializer.Serialize(range, PublicationJson.Options));

        var longOnly = exporter.Build(data.Journal.Id, new(Title: "Public"), JournalFilter.Parse("all", null, null, "long").Scope("UTC"));
        Assert.Equal(2, longOnly.Dashboard.CompletedTrades);
        Assert.All(longOnly.Days.SelectMany(d => d.Trades), t => Assert.Equal("long", t.Direction, ignoreCase: true));
        Assert.Equal(all.Dashboard.Balance, longOnly.Dashboard.Balance);
    }

    [Fact]
    public void TradeSetupsPublishAssignedVersionsAndResultsWithoutPrivateReviewData()
    {
        using var data = new LocalData();
        var csv = string.Join('\n', "Activity Type,DateTime,Symbol,Account,BuySell,Quantity,Fill Price,Order Action Source",
            "Fills,2026-09-10 12:00:00,MESZ26,PRIVATE-ACCOUNT,Buy,1,5000,PRIVATE-SOURCE",
            "Fills,2026-09-10 13:00:00,MESZ26,PRIVATE-ACCOUNT,Sell,1,5002,PRIVATE-SOURCE");
        new ImportService(data.Database).ImportText(data.Journal.Id, "fixture.csv", csv, "sierra", "flat_to_flat", "source");
        var trade = Assert.Single(data.Database.GetAllTrades(data.Journal.Id));
        data.Database.SaveTradeReview(data.Journal.Id, trade.ReviewKey, new TradeReviewPatch { Setup = "Opening range <label>", Mistakes = "PRIVATE-MISTAKE", TagsText = "PRIVATE-TAG" });
        var service = new TradingSetupService(data.Database);
        var setup = service.CreateSetup(data.Journal.Id, new TradingSetupDraft { Name = "Breakout <setup>", DetailedDescription = "PRIVATE-DESCRIPTION" },
        [
            new() { Name = "Trend <confirmed>", Stage = SetupCriterionStage.Context, RuleMetadataJson = "{\"private\":\"PRIVATE-RULE\"}" },
            new() { Name = "Volume", CriterionType = SetupCriterionType.Supporting },
            new() { Name = "Resistance", CriterionType = SetupCriterionType.Disqualifier },
            new() { Name = "Session", CriterionType = SetupCriterionType.Context }
        ]);
        var version = setup.CurrentVersion!;
        var attached = service.AttachToTrade(data.Journal.Id, trade.Id, version.Id, TradeSetupRole.Primary);
        service.SaveEvaluations(data.Journal.Id, attached.Association.Id,
        [
            new() { CriterionId = version.Criteria[0].Id, EvaluationState = CriterionEvaluationState.Met, Note = "PRIVATE-NOTE" },
            new() { CriterionId = version.Criteria[1].Id, EvaluationState = CriterionEvaluationState.NotApplicable },
            new() { CriterionId = version.Criteria[2].Id, EvaluationState = CriterionEvaluationState.NotMet }
        ]);
        var secondary = service.CreateSetup(data.Journal.Id, new TradingSetupDraft { Name = "Pullback" }, []);
        service.AttachToTrade(data.Journal.Id, trade.Id, secondary.CurrentVersion!.Id, TradeSetupRole.Secondary);
        service.CreateVersion(data.Journal.Id, setup.Setup.Id, "PRIVATE-VERSION-NOTE", [new() { Name = "PRIVATE-LATEST-CRITERION" }]);
        service.CreateSetup(data.Journal.Id, new TradingSetupDraft { Name = "PRIVATE-UNATTACHED" }, []);
        var exporter = new PublicationExportService(data.Database, new(data.Database));
        var snapshot = exporter.Build(data.Journal.Id, new(Title: "Public"));
        var published = Assert.Single(Assert.Single(snapshot.Days).Trades).Setup!;
        Assert.Equal("Opening range <label>", published.Label);
        Assert.Equal(2, published.Definitions.Count);
        Assert.Equal("Primary", published.Definitions[0].Role);
        Assert.Equal("Secondary", published.Definitions[1].Role);
        Assert.Equal(1, published.Definitions[0].Version);
        Assert.Equal(new[] { "Met", "NotApplicable", "NotMet", "Unknown" }, published.Definitions[0].Criteria.Select(c => c.State));
        var json = JsonSerializer.Serialize(snapshot, PublicationJson.Options);
        Assert.DoesNotContain("PRIVATE-", json);
        Assert.DoesNotContain(trade.ReviewKey, json);
        var html = PublicationPresentation.Document(snapshot, null, id => "/images/" + id, "/", "/style.css");
        Assert.Contains("Breakout &lt;setup&gt;", html);
        Assert.Contains("Trend &lt;confirmed&gt;", html);
        Assert.Contains("Opening range &lt;label&gt;", html);
        Assert.Contains("Primary · Version 1", html);
        Assert.Contains("N/A", html);
        Assert.Contains("Not evaluated", html);
        Assert.Contains("Not met", html);
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(snapshot with { SchemaVersion = 3 }));
        service.SaveEvaluations(data.Journal.Id, attached.Association.Id, [new() { CriterionId = version.Criteria[0].Id, EvaluationState = CriterionEvaluationState.NotMet }]);
        Assert.Equal(json, JsonSerializer.Serialize(snapshot, PublicationJson.Options));
    }

    [Fact]
    public void SetupPayloadValidatesStatesAndDistinguishesMissingFromOlderData()
    {
        var snapshot = Snapshot();
        var html = PublicationPresentation.Document(snapshot, null, id => "/images/" + id, "/", "/style.css");
        Assert.Contains("Setup not recorded.", html);
        var older = snapshot with { SchemaVersion = 3 };
        PublicationValidation.Validate(older);
        Assert.Contains("Setup information was not included", PublicationPresentation.Document(older, null, id => "/images/" + id, "/", "/style.css"));
        var day = snapshot.Days[0];
        var invalid = snapshot with { Days = [day with { Trades = [day.Trades[0] with { Setup = new("", [new("Example", 1, "Primary", [new("Criterion", "Required", "Setup", "Invalid")])]) }] }] };
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(invalid));
    }

    [Fact]
    public async Task TradeJournalsExportOnlyTheirReferencedImagesAndRenderSafeAccordions()
    {
        using var data = new LocalData();
        var csv = string.Join('\n', "Activity Type,DateTime,Symbol,Account,BuySell,Quantity,Fill Price,Order Action Source",
            "Fills,2026-09-10 12:00:00,MESZ26,PRIVATE-ACCOUNT,Buy,1,5000,PRIVATE-SOURCE",
            "Fills,2026-09-10 13:00:00,MESZ26,PRIVATE-ACCOUNT,Sell,1,5002,PRIVATE-SOURCE",
            "Fills,2026-09-10 14:00:00,MESZ26,PRIVATE-ACCOUNT,Buy,1,5000,PRIVATE-SOURCE",
            "Fills,2026-09-10 15:00:00,MESZ26,PRIVATE-ACCOUNT,Sell,1,5003,PRIVATE-SOURCE");
        new ImportService(data.Database).ImportText(data.Journal.Id, "fixture.csv", csv, "sierra", "flat_to_flat", "source");
        var trades = data.Database.GetAllTrades(data.Journal.Id).OrderBy(t => t.EntryUtc).ToArray();
        var reviews = new TradeReviewService(data.Database);
        await using var bytes = new MemoryStream(Png());
        var image = await reviews.SaveAttachmentAsync(data.Journal.Id, trades[0].ReviewKey, bytes, "chart.png", "image/png", bytes.Length);
        bytes.Position = 0;
        var unrelated = await reviews.SaveAttachmentAsync(data.Journal.Id, trades[0].ReviewKey, bytes, "private.png", "image/png", bytes.Length);
        var state = JsonSerializer.Serialize(new { root = new { children = new object[]
        {
            new { type = "paragraph", children = new[] { new { type = "text", text = "Journal <script>text</script>", format = 1 } } },
            new { type = "daily-journal-image", attachmentId = image.Id, src = "PRIVATE-LOCAL-URL", altText = "Trade chart" }
        } } });
        var saved = data.Database.SaveTradeReview(data.Journal.Id, trades[0].ReviewKey,
            new TradeReviewPatch { ReviewNote = state, Mistakes = "PRIVATE-MISTAKES", TagsText = "PRIVATE-TAGS" });
        var exporter = new PublicationExportService(data.Database, new(data.Database));
        var snapshot = exporter.Build(data.Journal.Id, new(Title: "Public"));
        var published = snapshot.Days.Single().Trades[0];
        Assert.Equal(image.Id.ToString("N"), Assert.Single(snapshot.Images).Id);
        Assert.DoesNotContain(snapshot.Images, x => x.Id == unrelated.Id.ToString("N"));
        Assert.Empty(snapshot.Days.Single().Trades[1].JournalContent);
        var json = JsonSerializer.Serialize(snapshot, PublicationJson.Options);
        Assert.DoesNotContain("PRIVATE-", json);
        string Render(bool preview) => PublicationPresentation.Document(snapshot, null, id => "/images/" + id, "/", "/style.css", preview: preview);
        var html = Render(false);
        Assert.Contains("<details class=\"published-trade\"><summary>", html);
        Assert.Contains("<strong>Journal &lt;script&gt;text&lt;/script&gt;</strong>", html);
        Assert.Contains("/images/" + image.Id.ToString("N"), html);
        Assert.Contains("No trade journal entry.", html);
        Assert.Contains("<details class=\"published-trade\" open>", Render(true));
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(snapshot with { SchemaVersion = 2 }));
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(snapshot with { Images = [] }));
        data.Database.SaveTradeReview(data.Journal.Id, trades[0].ReviewKey, new TradeReviewPatch { ExpectedRevision = saved.Annotation.Revision, ReviewNote = "Changed after preview" });
        Assert.Equal(json, JsonSerializer.Serialize(snapshot, PublicationJson.Options));
        data.Database.SaveTradeReview(data.Journal.Id, trades[1].ReviewKey, new TradeReviewPatch { ReviewNote = state });
        Assert.Throws<InvalidOperationException>(() => exporter.Build(data.Journal.Id, new(Title: "Public")));
        data.Database.SaveTradeReview(data.Journal.Id, trades[1].ReviewKey, new TradeReviewPatch { ExpectedRevision = 1 });
        data.Database.SaveTradeReview(data.Journal.Id, trades[0].ReviewKey, new TradeReviewPatch { ExpectedRevision = saved.Annotation.Revision + 1, ReviewNote = state });
        reviews.RemoveAttachment(data.Journal.Id, image.Id);
        Assert.Throws<InvalidOperationException>(() => exporter.Build(data.Journal.Id, new(Title: "Public")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OlderSnapshotsRemainReadableWithoutTradeJournalEntries(int version)
    {
        var snapshot = Snapshot() with { SchemaVersion = version };
        PublicationValidation.Validate(snapshot);
        var restored = JsonSerializer.Deserialize<PublicationSnapshot>(JsonSerializer.Serialize(snapshot, PublicationJson.Options).Replace("\"journalContent\":[],", "").Replace(",\"journalContent\":[]", ""), PublicationJson.Options)!;
        Assert.Empty(restored.Days[0].Trades[0].JournalContent);
        Assert.Contains("No trade journal entry.", PublicationPresentation.Document(restored, null, id => "/images/" + id, "/", "/style.css"));
    }

    [Fact]
    public void TradeChartsExportFiveMinuteContextAndExactPricesWithoutPrivateEvidence()
    {
        using var data = new LocalData();
        var start = new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
        var parsed = new ParsedImport { SourceType = TradeFoundryConstants.OhlcvBars };
        foreach (var index in Enumerable.Range(0, 500))
            parsed.Records.Add(new ParsedRecord { SourceType = TradeFoundryConstants.OhlcvBars, SourceKey = $"bar-{index}", RowNumber = index + 1, PayloadJson = "PRIVATE-BAR", Bar = new()
            {
                Symbol = "MES", Interval = "1m", EventUtc = start.AddMinutes(index),
                Open = 5000, High = 5002, Low = 4998, Close = 5001, Volume = 100
            } });
        parsed.Trades.Add(new ImportedTradeDraft
        {
            SourceType = TradeFoundryConstants.TradingViewStrategy, SourceKey = "PRIVATE-TRADE", Symbol = "MES", Instrument = "MES", Account = "PRIVATE-ACCOUNT",
            Direction = "Long", EntryUtc = start.AddMinutes(201), ExitUtc = start.AddMinutes(222),
            EntryPrice = 5000.25m, ExitPrice = 5001.75m, Quantity = 1, GrossPnl = 7.5m, NetPnl = 7.5m, PointValue = 5m, TickSize = .25m
        });
        data.Database.CommitImport(data.Journal.Id, "PRIVATE-IMPORT", parsed, "flat_to_flat", "1m");
        data.Database.UpdateJournal(data.Journal.Id, "Private name", "live", "", "UTC", "USD", "flat_to_flat", 10_000m);
        var snapshot = new PublicationExportService(data.Database, new(data.Database)).Build(data.Journal.Id, new(Title: "Public", IncludeTearSheet: true));
        Assert.Equal(10_000m, Assert.Single(snapshot.Months).OpeningBalance);
        Assert.Contains(snapshot.TearSheet!, group => group.Title == "Risk & stability");
        Assert.All(snapshot.TearSheet!.SelectMany(group => group.Metrics), metric => Assert.NotEqual("—", metric.Value));
        Assert.Null(new PublicationExportService(data.Database, new(data.Database)).Build(data.Journal.Id, new(Title: "Public")).TearSheet);
        var trade = Assert.Single(Assert.Single(snapshot.Days).Trades);
        var chart = Assert.IsType<PublicationTradeChart>(trade.Chart);
        Assert.Equal(5, snapshot.SchemaVersion);
        // Schema 5 carries the exact executions alongside the bar-anchored chart markers.
        Assert.Equal(start.AddMinutes(201), trade.EntryUtc);
        Assert.Equal(start.AddMinutes(222), trade.ExitUtc);
        Assert.Equal(5000.25m, trade.EntryPrice);
        Assert.Equal(5001.75m, trade.ExitPrice);
        Assert.Equal("5m", chart.Interval);
        Assert.Equal(50, chart.Bars.Count);
        Assert.Equal(5000.25m, chart.Entry.Price);
        Assert.Equal(5001.75m, chart.Exit.Price);
        Assert.Equal(start.AddMinutes(200).ToUnixTimeSeconds(), chart.Entry.Time);
        Assert.Equal(start.AddMinutes(220).ToUnixTimeSeconds(), chart.Exit.Time);
        Assert.All(chart.Bars, bar => Assert.Equal(500, bar.Volume));
        var json = JsonSerializer.Serialize(snapshot, PublicationJson.Options);
        Assert.DoesNotContain("PRIVATE-", json);
        var restored = JsonSerializer.Deserialize<PublicationSnapshot>(json, PublicationJson.Options)!;
        PublicationValidation.Validate(restored);
        foreach (var preview in new[] { true, false })
        {
            var html = PublicationPresentation.Document(restored, null, _ => "", "/", "/publication.css", preview: preview);
            Assert.Contains("View chart", html);
            Assert.Contains("data-public-chart=", html);
            Assert.Contains("trade-chart-dialog", html);
            Assert.Contains("/publication-assets/publication-charts.js", html);
            Assert.Contains("@ 5,000.25 → ", html);
            Assert.Contains("· held 21m</span>", html);
            Assert.Contains("By entry hour", html);
            Assert.Contains("Return by month", html);
            Assert.Contains("Detailed indicators", html);
            Assert.DoesNotContain("PRIVATE-", html);
        }
    }

    [Fact]
    public void ChartValidationRejectsMalformedCandlesAndUnmappedExecutionsAndKeepsLegacySnapshotsReadable()
    {
        var snapshot = Snapshot();
        var day = snapshot.Days[0];
        var chart = new PublicationTradeChart
        {
            Bars = [new(1000, 100, 102, 99, 101, 50)], Entry = new(1000, 100.25m), Exit = new(1000, 100.75m)
        };
        PublicationSnapshot WithChart(PublicationTradeChart value) => snapshot with { Days = [day with { Trades = [day.Trades[0] with { Chart = value }] }] };
        PublicationValidation.Validate(WithChart(chart));
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(WithChart(chart with { Bars = [chart.Bars[0], chart.Bars[0]] })));
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(WithChart(chart with { Exit = new(1300, 100) })));
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(WithChart(chart with { Bars = [new(1000, 100, 99, 98, 101, 50)] })));
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(WithChart(chart with { Interval = "1m" })));
        var legacy = snapshot with { SchemaVersion = 1 };
        PublicationValidation.Validate(legacy);
        var html = PublicationPresentation.Document(legacy, null, _ => "", "/", "/publication.css");
        Assert.Contains("Chart unavailable", html);
        Assert.DoesNotContain("data-public-chart=", html);
        Assert.DoesNotContain("publication-charts.js", html);
    }

    [Fact]
    public void IntroductionFormattingAndPublicTitleSurviveSnapshotSerializationAndRenderBelowTimezone()
    {
        const string state = """{"root":{"children":[{"type":"heading","tag":"h3","children":[{"type":"text","text":"Welcome","format":1}]},{"type":"paragraph","children":[{"type":"text","text":"<script>private</script>","format":2}]},{"type":"list","tag":"ul","children":[{"type":"listitem","children":[{"type":"text","text":"My process"}]}]}]}}""";
        var snapshot = PublicationExportService.BuildSnapshot(Journal(), [], new(),
            new Dictionary<DateOnly, List<PublicationNode>>(), [], new(Title: "My public title", Introduction: state), DateTimeOffset.UtcNow);
        var restored = JsonSerializer.Deserialize<PublicationSnapshot>(JsonSerializer.Serialize(snapshot, PublicationJson.Options), PublicationJson.Options)!;
        PublicationValidation.Validate(restored);
        foreach (var preview in new[] { false, true })
        {
            var html = PublicationPresentation.Document(restored, null, _ => "", "/", "/publication.css", preview: preview);
            Assert.Contains("<h1>My public title</h1>", html);
            Assert.Contains("PUBLIC TRADING JOURNAL", html);
            Assert.Contains("EQUITY CURVE", html);
            Assert.Contains("<h3><strong>Welcome</strong></h3>", html);
            Assert.Contains("<ul><li>My process</li></ul>", html);
            Assert.Contains("<em>&lt;script&gt;private&lt;/script&gt;</em>", html);
            Assert.DoesNotContain("<script>", html);
            Assert.DoesNotContain("PRIVATE-JOURNAL", html);
            Assert.True(html.IndexOf("class=\"metadata\"", StringComparison.Ordinal) < html.IndexOf("<h3><strong>Welcome", StringComparison.Ordinal));
            Assert.True(html.IndexOf("<h3><strong>Welcome", StringComparison.Ordinal) < html.IndexOf("class=\"metrics\"", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void OlderPlainTextIntroductionsStillPublishAndExistingSnapshotsStillRender()
    {
        var snapshot = PublicationExportService.BuildSnapshot(Journal(), [], new(),
            new Dictionary<DateOnly, List<PublicationNode>>(), [], new(Title: "Legacy", Introduction: "Hello <world>"), DateTimeOffset.UtcNow);
        Assert.Contains("<p>Hello &lt;world&gt;</p>", PublicationPresentation.Document(snapshot, null, _ => "", "/", "/publication.css"));
        var legacy = snapshot with { IntroductionContent = [], Introduction = "Earlier <introduction>" };
        Assert.Contains("Earlier &lt;introduction&gt;", PublicationPresentation.Document(legacy, null, _ => "", "/", "/publication.css"));
    }

    [Fact]
    public void IntroductionRejectsUnsafeLinksAndImages()
    {
        var snapshot = PublicationExportService.BuildSnapshot(Journal(), [], new(),
            new Dictionary<DateOnly, List<PublicationNode>>(), [], new(Title: "Safe"), DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(snapshot with
        {
            IntroductionContent = [new() { Type = "link", Url = "javascript:alert(1)" }]
        }));
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(snapshot with
        {
            IntroductionContent = [new() { Type = "image", ImageId = "missing" }]
        }));
    }

    [Fact]
    public void ExportUsesJournalDateBoundariesRealizedFeesAndCashFlowBalanceWithoutPrivateFields()
    {
        var journal = Journal("America/New_York");
        var first = Trade(journal.Id, 95, new(2026, 11, 1, 3, 30, 0, TimeSpan.Zero)); // Oct 31 local, before DST ends.
        var second = Trade(journal.Id, -45, new(2026, 11, 1, 6, 30, 0, TimeSpan.Zero)); // Nov 1 local, after DST ends.
        var even = Trade(journal.Id, 0, new(2026, 11, 2, 3, 0, 0, TimeSpan.Zero));
        var open = new Trade { JournalId = journal.Id, Status = "open", EntryUtc = first.EntryUtc, NetPnl = 9999, Symbol = "OPEN-PRIVATE" };
        var transactions = new[] { new AccountTransaction { Id = Guid.NewGuid(), JournalId = journal.Id, Type = AccountTransactionType.Deposit, Amount = 500, EffectiveUtc = first.EntryUtc.AddHours(-1), CreatedUtc = first.EntryUtc.AddHours(-1) } };
        var account = AccountLedgerService.Build(journal, transactions, [first, second, even, open], []).Summary;
        var snapshot = PublicationExportService.BuildSnapshot(journal, [first, second, even, open], account, new Dictionary<DateOnly, List<PublicationNode>>(), [], new(Title: "Public"), new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(1550m, snapshot.Dashboard.Balance);
        Assert.Equal(50m, snapshot.Dashboard.LifetimeNetPnl);
        Assert.Equal(-45m, snapshot.Dashboard.CurrentMonthNetPnl);
        Assert.Equal(3, snapshot.Dashboard.CompletedTrades);
        Assert.Equal(95m / 45m, snapshot.Dashboard.ProfitFactor);
        Assert.Equal(new DateOnly(2026, 10, 31), snapshot.Days[0].Date);
        Assert.Equal(new DateOnly(2026, 11, 1), snapshot.Days[1].Date);
        Assert.Equal(1, snapshot.Days[1].Breakevens);
        Assert.Equal([95m, 50m], snapshot.Equity.Select(p => p.CumulativePnl).ToArray());
        var json = JsonSerializer.Serialize(snapshot, PublicationJson.Options);
        foreach (var sentinel in new[] { "PRIVATE-ACCOUNT", "PRIVATE-NOTE", "PRIVATE-SOURCE", "OPEN-PRIVATE", "PRIVATE-JOURNAL" }) Assert.DoesNotContain(sentinel, json);
    }

    [Fact]
    public void EmptyAndNoLossMetricsStayUnavailableAndJournalOnlyDaysAreIncluded()
    {
        var journal = Journal();
        var notes = new Dictionary<DateOnly, List<PublicationNode>> { [new(2026, 8, 1)] = PublicationExportService.ConvertContent("123", _ => throw new Exception()) };
        var snapshot = PublicationExportService.BuildSnapshot(journal, [], new(), notes, [], new(Title: "Empty"), DateTimeOffset.UtcNow);
        Assert.Null(snapshot.Dashboard.Balance);
        Assert.Null(snapshot.Dashboard.WinRate);
        Assert.Null(snapshot.Dashboard.ProfitFactor);
        Assert.Empty(snapshot.Equity);
        Assert.Single(snapshot.Days);
        Assert.Equal("123", snapshot.Days[0].Content[0].Children[0].Text);
        Assert.Contains("Unavailable", PublicationPresentation.Document(snapshot, null, _ => "", "/", "/publication.css"));
        Assert.DoesNotContain("Infinity", JsonSerializer.Serialize(snapshot, PublicationJson.Options));
    }

    [Fact]
    public void InferredAccountBaselineMatchesExistingLedgerWithoutChangingLifetimeProfit()
    {
        var journal = new Journal { Id = Guid.NewGuid(), TimeZone = "UTC", Currency = "USD", StartingEquity = null };
        var before = Trade(journal.Id, 100, new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));
        var after = Trade(journal.Id, 50, new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero));
        var baseline = new AccountBalanceEvent { Id = Guid.NewGuid(), JournalId = journal.Id, EventUtc = new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero), Balance = 2000 };
        var account = AccountLedgerService.Build(journal, [], [before, after], [baseline]).Summary;
        var snapshot = PublicationExportService.BuildSnapshot(journal, [before, after], account, new Dictionary<DateOnly, List<PublicationNode>>(), [], new(Title: "Baseline"), DateTimeOffset.UtcNow);
        Assert.Equal(2050m, snapshot.Dashboard.Balance);
        Assert.Equal(150m, snapshot.Dashboard.LifetimeNetPnl);
        Assert.Contains("broker-reported", snapshot.Dashboard.BalanceNote);

    }

    [Fact]
    public void RichTextIsAllowlistedAndEscapedAndLocalImageUrlsAreNotExported()
    {
        var imageId = Guid.NewGuid().ToString("N");
        var state = """{"root":{"children":[{"type":"heading","tag":"h2","children":[{"type":"text","text":"<script>alert(1)</script>","format":3}]},{"type":"list","tag":"ol","children":[{"type":"listitem","children":[{"type":"text","text":"One"}]}]},{"type":"link","url":"https://example.com/?a=1&b=2","children":[{"type":"text","text":"Link"}]},{"type":"daily-journal-image","attachmentId":"source-id","src":"/private/local/path","altText":"Chart","width":480}]}}""";
        var nodes = PublicationExportService.ConvertContent(state, id => id == "source-id" ? imageId : throw new Exception());
        PublicationContent.Validate(nodes, new HashSet<string> { imageId });
        var html = PublicationContent.Render(nodes, id => "/images/" + id);
        Assert.Contains("<h2><em><strong>&lt;script&gt;", html);
        Assert.Contains("<ol><li>One</li></ol>", html);
        Assert.Contains("width=\"480\"", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("/private/local/path", JsonSerializer.Serialize(nodes));
        Assert.Throws<InvalidOperationException>(() => PublicationContent.Validate([new() { Type = "link", Url = "javascript:alert(1)" }], new HashSet<string>()));
        Assert.Throws<InvalidOperationException>(() => PublicationContent.Validate([new() { Type = "image", ImageId = "missing" }], new HashSet<string>()));
        Assert.Throws<InvalidOperationException>(() => PublicationExportService.ConvertContent("""{"root":{"children":[{"type":"unsupported"}]}}""", _ => ""));
    }

    [Fact]
    public void AnnotatedImagesPublishTheRenderedCopyWithoutAnnotationData()
    {
        var state = JsonSerializer.Serialize(new { root = new { children = new object[] {
            new
            {
                type = "daily-journal-image", attachmentId = "rendered-id", src = "/private/rendered", altText = "Chart-annotated.png", width = 480,
                originalAttachmentId = "original-id", originalSrc = "/private/original",
                annotation = new { version = 3, width = 800, height = 600, markers = new object[] { new { typeName = "ArrowMarker", x1 = 10, y1 = 10, x2 = 90, y2 = 90, notes = "secret-note" } } }
            }
        } } });
        var requested = new List<string>();
        var nodes = PublicationExportService.ConvertContent(state, id => { requested.Add(id); return "published"; });
        Assert.Equal(["rendered-id"], requested);
        Assert.Equal("published", Assert.Single(nodes).ImageId);
        var json = JsonSerializer.Serialize(nodes);
        Assert.DoesNotContain("original-id", json);
        Assert.DoesNotContain("secret-note", json);
        Assert.DoesNotContain("ArrowMarker", json);
    }

    [Fact]
    public async Task ExportIncludesTodayAndReferencedImagesOnlyAndReportsMissingImages()
    {
        using var data = new LocalData();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var images = new DailyJournalImageService(data.Database);
        await using var bytes = new MemoryStream(Png());
        var image = await images.SaveAsync(data.Journal.Id, today, bytes, "public-chart.png", "image/png", bytes.Length);
        var state = JsonSerializer.Serialize(new { root = new { children = new object[] {
            new { type = "paragraph", children = new[] { new { type = "text", text = "Today's public writing" } } },
            new { type = "daily-journal-image", attachmentId = image.Id, src = "/private/source", altText = "Chart" }
        } } });
        data.Database.SaveDailyJournal(data.Journal.Id, today, state, 0);
        var exporter = new PublicationExportService(data.Database, images);
        var snapshot = exporter.Build(data.Journal.Id, new(Title: "Public"));
        Assert.Equal(today, Assert.Single(snapshot.Days).Date);
        Assert.Equal(File.ReadAllBytes(images.GetPath(data.Journal.Id, image)!), Assert.Single(snapshot.Images).Content);
        Assert.DoesNotContain("/private/source", JsonSerializer.Serialize(snapshot));
        File.Delete(images.GetPath(data.Journal.Id, image)!);
        Assert.Contains("Missing journal image", Assert.Throws<InvalidOperationException>(() => exporter.Build(data.Journal.Id, new(Title: "Public"))).Message);
    }

    [Fact]
    public void ExportAgreesWithEffectiveJournalFeesAndDoesNotMutateImportedEvidence()
    {
        using var data = new LocalData();
        var csv = string.Join('\n', "Activity Type,DateTime,Symbol,Account,BuySell,Quantity,Fill Price,Order Action Source",
            "Fills,2026-09-10 12:00:00,MESZ26,PRIVATE-ACCOUNT,Buy,1,5000,PRIVATE-SOURCE",
            "Fills,2026-09-10 13:00:00,MESZ26,PRIVATE-ACCOUNT,Sell,1,5002,PRIVATE-SOURCE");
        new ImportService(data.Database).ImportText(data.Journal.Id, "PRIVATE-IMPORT.csv", csv, "sierra", "flat_to_flat", "source");
        var trade = Assert.Single(data.Database.GetAllTrades(data.Journal.Id));
        data.Database.SaveTradeReview(data.Journal.Id, trade.ReviewKey, new TradeReviewPatch { AllInCommission = 3.75m, ReviewNote = "PUBLIC-TRADE-JOURNAL", Mistakes = "PRIVATE-MISTAKES", Lessons = "PRIVATE-LESSONS", TagsText = "PRIVATE-TAG", ExpectedRevision = 0 });
        var overview = data.Database.GetOverview(data.Journal.Id);
        var evidenceBefore = Evidence();
        var snapshot = new PublicationExportService(data.Database, new(data.Database)).Build(data.Journal.Id, new(Title: "Public"));
        Assert.Equal(overview.NetPnl, snapshot.Dashboard.LifetimeNetPnl);
        Assert.Equal(overview.ClosedTradeCount, snapshot.Dashboard.CompletedTrades);
        Assert.Equal(overview.WinRate, snapshot.Dashboard.WinRate);
        Assert.Equal(6.25m, snapshot.Dashboard.LifetimeNetPnl);
        Assert.Equal(evidenceBefore, Evidence());
        var json = JsonSerializer.Serialize(snapshot, PublicationJson.Options);
        Assert.DoesNotContain("PRIVATE-", json);
        Assert.Contains("PUBLIC-TRADE-JOURNAL", json);

        string Evidence()
        {
            using var connection = new SqliteConnection($"Data Source={data.Database.DatabasePath};Pooling=False");
            connection.Open();
            return string.Join("|", new[] { "raw_records", "fills", "trades", "trade_review_annotations", "import_batches" }.Select(table =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM {table} ORDER BY rowid";
                using var reader = command.ExecuteReader();
                var values = new List<string>();
                while (reader.Read()) for (var i = 0; i < reader.FieldCount; i++) values.Add(reader.GetValue(i).ToString() ?? "");
                return string.Join(";", values);
            }));
        }
    }

    [Fact]
    public void StoreReplacementRetryConflictBindingAndUnpublishAreAtomicAndPersistAcrossRestart()
    {
        using var directory = new TestDirectory();
        var store = new PublicationStore(directory.Path);
        var first = Snapshot();
        first = WithImage(first);
        var published = store.Publish(new(0, first));
        Assert.Equal(1, published.Revision);
        Assert.Equal(published, store.Publish(new(0, first)));
        Assert.Equal(published, new PublicationStore(directory.Path).Status());
        Assert.NotNull(store.Image(first.SnapshotId, first.Images[0].Id));
        Assert.Throws<PublicationConflictException>(() => store.Publish(new(1, first with { Title = "Changed" })));
        Assert.Throws<PublicationConflictException>(() => store.Publish(new(0, first with { SnapshotId = Guid.NewGuid() })));
        Assert.Throws<PublicationConflictException>(() => store.Publish(new(1, Snapshot())));
        Assert.Throws<InvalidOperationException>(() => store.Publish(new(1, first with { SnapshotId = Guid.NewGuid(), SchemaVersion = 9 })));
        Assert.Equal(first.SnapshotId, store.Read()!.Snapshot.SnapshotId);
        var second = Snapshot(first.JournalId) with { Title = "Replaced" };
        var replaced = store.Publish(new(1, second));
        Assert.Equal(2, replaced.Revision);
        Assert.Null(store.Image(first.SnapshotId, first.Images[0].Id));
        Assert.Empty(store.Read()!.Snapshot.Days[0].Content);
        Assert.Throws<PublicationConflictException>(() => store.Unpublish(new(1, first.JournalId)));
        var removed = store.Unpublish(new(2, first.JournalId));
        Assert.Null(store.Read());
        Assert.Equal(first.JournalId, removed.BoundJournalId);
        Assert.Throws<PublicationConflictException>(() => store.Publish(new(3, Snapshot())));
        store.Unpublish(new(3, first.JournalId, ResetBinding: true));
        Assert.Null(store.Status().BoundJournalId);
        Assert.Equal(5, store.Publish(new(4, Snapshot())).Revision);
    }

    [Fact]
    public async Task ConcurrentUploadsWithSameRevisionHaveOneWinner()
    {
        using var directory = new TestDirectory();
        var store = new PublicationStore(directory.Path);
        var first = Snapshot();
        var snapshots = new[] { first, Snapshot(first.JournalId) };
        var outcomes = await Task.WhenAll(snapshots.Select(s => Task.Run(() =>
        {
            try { store.Publish(new(0, s)); return true; }
            catch (PublicationConflictException) { return false; }
        })));
        Assert.Single(outcomes, success => success);
        Assert.Equal(1, store.Status().Revision);
    }

    [Fact]
    public async Task HttpApiAndReaderValidateAuthenticationPrivacyImagesAndFailures()
    {
        await using var host = await PublicHost.Start();
        using var visitor = new HttpClient { BaseAddress = host.Client.BaseAddress };
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/health")).StatusCode);
        Assert.Contains("Nothing published", await visitor.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync("/api/v1/publication")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.PutAsync("/api/v1/publication", new StringContent("not json"))).StatusCode);
        var snapshot = WithImage(Snapshot());
        var response = await host.Client.PutAsJsonAsync("/api/v1/publication", new PublicationUpload(0, snapshot), PublicationJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await visitor.GetStringAsync("/");
        Assert.Contains(snapshot.Title, html);
        Assert.Contains("calendar-day positive", html);
        Assert.Contains("<strong>Public writing</strong>", html);
        Assert.DoesNotContain("PRIVATE", html);
        var imagePath = $"/images/{snapshot.SnapshotId:D}/{snapshot.Images[0].Id}";
        Assert.Equal(snapshot.Images[0].Content, await visitor.GetByteArrayAsync(imagePath));
        Assert.Equal("image/png", (await visitor.GetAsync(imagePath)).Content.Headers.ContentType!.MediaType);
        Assert.Contains(".calendar", await visitor.GetStringAsync("/publication.css"));
        var malformed = snapshot with { SnapshotId = Guid.NewGuid(), Images = [] };
        Assert.Contains("showModal", await visitor.GetStringAsync("/publication-assets/publication-charts.js"));
        Assert.Contains("class TradeExecutionDrawing", await visitor.GetStringAsync("/publication-assets/trade-execution-drawing.js"));
        Assert.Contains("5.2.0", await visitor.GetStringAsync("/publication-assets/lightweight-charts.standalone.production.js"));
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/publication-assets/private.js")).StatusCode);
        var reader = await visitor.GetStringAsync("/publication-assets/publication-reader.js");
        Assert.Contains("data-equity-points", reader);
        Assert.Contains(".journal-content img", reader);
        Assert.Contains("image-viewer", reader);
        Assert.Contains(".image-viewer-stage", await visitor.GetStringAsync("/publication.css"));
        Assert.Contains("<svg", await visitor.GetStringAsync("/favicon.svg"));
        using (var feedRequest = new HttpRequestMessage(HttpMethod.Get, "/feed.xml"))
        {
            feedRequest.Headers.Add("X-Forwarded-Proto", "https");
            var feed = await (await visitor.SendAsync(feedRequest)).Content.ReadAsStringAsync();
            Assert.Contains($"<id>https://{visitor.BaseAddress!.Authority}/</id>", feed);
            Assert.Contains(snapshot.Title, feed);
        }
        using (var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = visitor.BaseAddress })
        {
            var day = snapshot.Days[0].Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var stale = await noRedirect.GetAsync($"/?date={day}&snapshot={Guid.NewGuid():D}");
            Assert.Equal(HttpStatusCode.Redirect, stale.StatusCode);
            Assert.Equal($"/?date={day}", stale.Headers.Location!.OriginalString);
            Assert.Equal(HttpStatusCode.OK, (await noRedirect.GetAsync($"/?date={day}&snapshot={snapshot.SnapshotId:D}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PutAsJsonAsync("/api/v1/publication", new PublicationUpload(1, malformed), PublicationJson.Options)).StatusCode);
        var unknownJson = JsonSerializer.Serialize(new PublicationUpload(1, snapshot), PublicationJson.Options).Replace("\"schemaVersion\":", "\"privateField\":\"secret\",\"schemaVersion\":");
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PutAsync("/api/v1/publication", new StringContent(unknownJson, Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PutAsync("/api/v1/publication", new StringContent("{\"expectedRevision\":1,", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PutAsJsonAsync("/api/v1/publication", new PublicationUpload(0, snapshot with { SnapshotId = Guid.NewGuid() }), PublicationJson.Options)).StatusCode);
        Assert.Contains(snapshot.Title, await visitor.GetStringAsync("/"));
        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/publication") { Content = JsonContent.Create(new PublicationDelete(1, snapshot.JournalId), options: PublicationJson.Options) };
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(delete)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(imagePath)).StatusCode);
        Assert.Contains("Nothing published", await visitor.GetStringAsync("/"));
    }

    [Fact]
    public async Task InterruptedUploadLeavesCurrentPublicationIntact()
    {
        await using var host = await PublicHost.Start();
        var snapshot = Snapshot();
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PutAsJsonAsync("/api/v1/publication", new PublicationUpload(0, snapshot), PublicationJson.Options)).StatusCode);
        using var socket = new System.Net.Sockets.TcpClient();
        await socket.ConnectAsync(host.Client.BaseAddress!.Host, host.Client.BaseAddress.Port);
        var header = $"PUT /api/v1/publication HTTP/1.1\r\nHost: 127.0.0.1\r\nAuthorization: Bearer {host.Token}\r\nContent-Type: application/json\r\nContent-Length: 100000\r\nConnection: close\r\n\r\n{{\"expectedRevision\":1,";
        await socket.GetStream().WriteAsync(Encoding.UTF8.GetBytes(header));
        socket.Close();
        var status = await host.Client.GetFromJsonAsync<PublicationStatus>("/api/v1/publication");
        Assert.Equal(snapshot.SnapshotId, status!.SnapshotId);
        Assert.Equal(1, status.Revision);
        Assert.Contains(snapshot.Title, await host.Client.GetStringAsync("/"));
    }

    [Fact]
    public void InvalidImagesContentAndTotalsFailBeforeReplacement()
    {
        using var directory = new TestDirectory();
        var store = new PublicationStore(directory.Path);
        var first = Snapshot();
        store.Publish(new(0, first));
        var imageSnapshot = WithImage(first with { SnapshotId = Guid.NewGuid() });
        var invalids = new[]
        {
            imageSnapshot with { Images = [imageSnapshot.Images[0] with { Content = [1, 2, 3] }] },
            imageSnapshot with { Images = [imageSnapshot.Images[0] with { ContentType = "image/svg+xml" }] },
            imageSnapshot with { Days = [imageSnapshot.Days[0] with { Content = [new() { Type = "script", Text = "alert(1)" }] }] },
            first with { SnapshotId = Guid.NewGuid(), Dashboard = first.Dashboard with { LifetimeNetPnl = 1000 } },
            first with { SnapshotId = Guid.NewGuid(), Days = [first.Days[0], first.Days[0]] }
        };
        foreach (var invalid in invalids)
        {
            Assert.Throws<InvalidOperationException>(() => store.Publish(new(1, invalid)));
            Assert.Equal(first.SnapshotId, store.Read()!.Snapshot.SnapshotId);
            Assert.Equal(1, store.Status().Revision);
        }
    }

    [Fact]
    public async Task WorkflowPublishesFrozenPreviewAndProtectsTokenAndRejectsOldPreviewIds()
    {
        await using var host = await PublicHost.Start();
        using var data = new LocalData();
        var services = new ServiceCollection();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(System.IO.Path.Combine(data.Directory.Path, "keys")));
        services.AddHttpClient("Publication").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        using var provider = services.BuildServiceProvider();
        var workflow = new PublicationWorkflow(data.Database, new(data.Database, new(data.Database)), provider.GetRequiredService<IDataProtectionProvider>(), provider.GetRequiredService<IHttpClientFactory>());
        var date = new DateOnly(2026, 7, 1);
        data.Database.SaveDailyJournal(data.Journal.Id, date, "Before preview", 0);
        await workflow.SaveSettings(data.Journal.Id, new(host.Client.BaseAddress!.AbsoluteUri, "Frozen", "Introduction"), host.Token, default);
        Assert.DoesNotContain(host.Token, data.Database.GetPublishingState(data.Journal.Id).ProtectedToken);
        await workflow.Preview(data.Journal.Id, null, default);
        var frozen = data.Database.GetPublishingState(data.Journal.Id).Draft!;
        data.Database.SaveDailyJournal(data.Journal.Id, date, "Edited after preview", 1);
        await workflow.Publish(data.Journal.Id, frozen.Snapshot.SnapshotId, default);
        var html = await host.Client.GetStringAsync("/");
        Assert.Contains("Before preview", html);
        Assert.DoesNotContain("Edited after preview", html);
        await workflow.Publish(data.Journal.Id, frozen.Snapshot.SnapshotId, default); // Retry is safe.
        Assert.Equal(1, (await host.Client.GetFromJsonAsync<PublicationStatus>("/api/v1/publication"))!.Revision);
        await workflow.Preview(data.Journal.Id, null, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.Publish(data.Journal.Id, frozen.Snapshot.SnapshotId, default));
        await workflow.SaveSettings(data.Journal.Id, new(host.Client.BaseAddress.AbsoluteUri, "New title", ""), "", default);
        Assert.Null(data.Database.GetPublishingState(data.Journal.Id).Draft);
        Assert.NotNull(data.Database.GetPublishingState(data.Journal.Id).LastSuccessUtc);
    }

    [Theory]
    [InlineData("text/html", "<html>PRIVATE-RESPONSE</html>", "non-JSON")]
    [InlineData("application/json", "{PRIVATE-RESPONSE", "incompatible publication status JSON")]
    [InlineData("application/json", "{}", "incompatible publication status JSON")]
    [InlineData("application/json", "{\"revision\":0,\"boundJournalId\":null,\"snapshotId\":null,\"publishedUtc\":null,\"unexpected\":1}", "incompatible publication status JSON")]
    public async Task PreviewIdentifiesInvalidWebsiteResponsesWithoutBlamingLocalImages(string contentType, string content, string expected)
    {
        using var data = new LocalData();
        var services = new ServiceCollection();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(System.IO.Path.Combine(data.Directory.Path, "keys")));
        services.AddHttpClient("Publication").ConfigurePrimaryHttpMessageHandler(() => new PublicationResponseHandler(contentType, content));
        using var provider = services.BuildServiceProvider();
        var workflow = new PublicationWorkflow(data.Database, new(data.Database, new(data.Database)), provider.GetRequiredService<IDataProtectionProvider>(), provider.GetRequiredService<IHttpClientFactory>());
        await workflow.SaveSettings(data.Journal.Id, new("https://public.example/", "Public", ""), Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), default);
        var page = new TradeFoundry.Pages.PublishingModel(data.Database, workflow) { JournalId = data.Journal.Id };
        await page.OnPostPreviewAsync(default);
        Assert.Contains(expected, page.Error!);
        Assert.DoesNotContain("journal images", page.Error!);
        Assert.DoesNotContain("PRIVATE-RESPONSE", page.Error!);
        Assert.Null(data.Database.GetPublishingState(data.Journal.Id).Draft);
    }

    private sealed class PublicationResponseHandler(string contentType, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, contentType) });
    }

    [Fact]
    public void ReaderPaginatesSelectsCalendarDaysAndExpandsNewestMonthOnly()
    {
        var journal = Journal();
        var trades = Enumerable.Range(0, 36).Select(i => Trade(journal.Id, i - 10, new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero).AddDays(i))).ToArray();
        var snapshot = PublicationExportService.BuildSnapshot(journal, trades, new(), new Dictionary<DateOnly, List<PublicationNode>>(), [], new(Title: "History"), DateTimeOffset.UtcNow);
        var html = PublicationPresentation.Document(snapshot, DateTimeOffset.UtcNow, _ => "", "/", "/publication.css");
        Assert.Equal(10, Count(html, "class=\"panel day-entry\""));
        Assert.Equal(1, Count(html, "class=\"month panel current\""));
        Assert.Equal(snapshot.Months.Count, Count(html, "class=\"month panel"));
        Assert.Contains("Load older entries", html);
        Assert.DoesNotContain("Newer entries", html);
        Assert.Contains($"entries 1–10 of {snapshot.Days.Count}", html);
        // Day permalinks omit the snapshot so shared links survive republishing.
        Assert.Contains($"href=\"/?date={snapshot.Days[^1].Date:yyyy-MM-dd}#day-{snapshot.Days[^1].Date:yyyy-MM-dd}\"", html);
        Assert.DoesNotContain($"&amp;snapshot={snapshot.SnapshotId:D}#day-", html);
        Assert.Equal(36, Count(html, "<a class=\"pnl-column"));
        var older = PublicationPresentation.Document(snapshot, null, _ => "", "/", "/publication.css", before: snapshot.Days[^10].Date);
        Assert.Equal(10, Count(older, "class=\"panel day-entry\""));
        Assert.DoesNotContain("Newer entries", older);
        var third = PublicationPresentation.Document(snapshot, null, _ => "", "/", "/publication.css", before: snapshot.Days[^20].Date);
        Assert.Contains($"?before={snapshot.Days[^10].Date:yyyy-MM-dd}&amp;snapshot={snapshot.SnapshotId:D}#entries\">Newer entries", third);
        Assert.Contains($"entries 21–30 of {snapshot.Days.Count}", third);
        var middle = snapshot.Days[^5].Date;
        var selected = PublicationPresentation.Document(snapshot, null, _ => "", "/", "/publication.css", selected: middle);
        Assert.Equal(1, Count(selected, "class=\"panel day-entry\""));
        Assert.Contains($"id=\"day-{middle:yyyy-MM-dd}\"", selected);
        Assert.Contains($"rel=\"prev\" href=\"/?date={snapshot.Days[^6].Date:yyyy-MM-dd}", selected);
        Assert.Contains($"rel=\"next\" href=\"/?date={snapshot.Days[^4].Date:yyyy-MM-dd}", selected);
        Assert.Contains($"<a href=\"#month-{middle:yyyy-MM}\">", selected);
        Assert.Contains($"class=\"month panel current\" id=\"month-{middle:yyyy-MM}\"", selected);
        Assert.Contains($"<title>{middle.ToString("dddd, MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture)} &#183; History</title>", selected);
    }

    [Fact]
    public void ReaderRendersNavigationHeatmapWeeklyTotalsAndMetadata()
    {
        var journal = Journal();
        var trades = new[] { 30m, -10m, 20m }.Select((pnl, i) => Trade(journal.Id, pnl, new DateTimeOffset(2026, 3, 2 + i, 15, 0, 0, TimeSpan.Zero))).ToArray();
        var snapshot = PublicationExportService.BuildSnapshot(journal, trades, new(), new Dictionary<DateOnly, List<PublicationNode>>(), [], new(Title: "Meta <journal>"), DateTimeOffset.UtcNow);
        var html = PublicationPresentation.Document(snapshot, null, _ => "", "/", "/publication.css", siteUrl: "https://journal.example/");
        foreach (var section in new[] { "overview", "performance", "calendar", "entries" })
        {
            Assert.Contains($"href=\"#{section}\" data-section=\"{section}\"", html);
            Assert.Contains($"id=\"{section}\"", html);
        }
        Assert.Contains("<link rel=\"canonical\" href=\"https://journal.example/\">", html);
        Assert.Contains("property=\"og:title\" content=\"Meta &lt;journal&gt;\"", html);
        Assert.Contains("name=\"description\" content=\"Public trading journal: 3 completed trades", html);
        Assert.Contains("href=\"/favicon.svg\"", html);
        Assert.Contains("application/atom+xml", html);
        Assert.Contains("/publication-assets/publication-reader.js", html);
        Assert.Contains("data-equity-points=", html);
        Assert.Contains("class=\"drawdown-area\"", html);
        Assert.Contains("Maximum USD -10.00", html);
        Assert.Contains("aria-label=\"2026 daily results\"", html);
        Assert.Contains("href=\"#month-2026-03\"", html);
        // March 1-7 2026 is one Sunday-start week: 30 - 10 + 20.
        Assert.Contains("<div class=\"calendar-week positive\"><span>Week</span><strong>40.00</strong><small>3 trades</small></div>", html);
        Assert.Contains("class=\"calendar-day positive l4\"", html);
        Assert.Contains("<dt>Profit factor</dt><dd>5.00", html);
        Assert.DoesNotContain("<dt>Alpha</dt>", html);
        var preview = PublicationPresentation.Document(snapshot, null, _ => "", "/", "/publication.css", preview: true, siteUrl: "https://journal.example/");
        Assert.DoesNotContain("rel=\"canonical\"", preview);
        Assert.DoesNotContain("favicon.svg", preview);
        Assert.Contains("href=\"#day-2026-03-03\"", preview);
    }

    [Fact]
    public void ReaderComputesTradeStatisticsAndBreakdownsFromPublishedTrades()
    {
        var journal = Journal();
        var start = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        decimal[] pnl = [100, 100, 100, -40, -40, -40, -40, 60, 0, 80, .. Enumerable.Repeat(10m, 16)];
        var trades = pnl.Select((value, i) => i < 3 ? Trade(journal.Id, value, start.AddDays(i), "NQ", "Short") : Trade(journal.Id, value, start.AddDays(i))).ToArray();
        var snapshot = PublicationExportService.BuildSnapshot(journal, trades, new(), new Dictionary<DateOnly, List<PublicationNode>>(), [], new(Title: "Stats"), new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero));
        var html = PublicationPresentation.Document(snapshot, null, _ => "", "/", "/publication.css");
        string Stat(string label, string tone, string value) => $"<dt>{label}</dt><dd class=\"{tone}\">{value}";
        // Total 440 over 26 trades; 21 wins averaging 600/21, four 40 losses.
        Assert.Contains(Stat("Expectancy", "positive", "USD 16.92"), html);
        Assert.Contains(Stat("Average win", "positive", "USD 28.57"), html);
        Assert.Contains(Stat("Average loss", "negative", "USD -40.00"), html);
        Assert.Contains(Stat("Payoff ratio", "neutral", "0.71"), html);
        Assert.Contains(Stat("Largest win", "positive", "USD 100.00"), html);
        // Peak 300 on Jun 3, trough 140 on Jun 7, back to 300 on Jun 12.
        Assert.Contains(Stat("Max drawdown", "negative", "USD -160.00<small>Jun 3, 2026"), html);
        Assert.Contains(Stat("Current drawdown", "positive", "USD 0.00<small>At a cumulative high"), html);
        Assert.Contains(Stat("Recovery time", "neutral", "9 days"), html);
        Assert.Contains(Stat("Longest drawdown", "neutral", "9 days"), html);
        Assert.Contains(Stat("Longest losing streak", "neutral", "4 losses"), html);
        Assert.Contains(Stat("Green days", "neutral", "80.8%"), html);
        Assert.Contains(Stat("Best day", "positive", "USD 100.00<small>Jun 1, 2026"), html);
        Assert.Contains(Stat("Worst day", "negative", "USD -40.00<small>Jun 4, 2026"), html);
        // The breakeven trade ends the first run; 80 plus sixteen 10s is the longest.
        Assert.Contains(Stat("Current streak", "positive", "17 wins"), html);
        Assert.Contains(Stat("Longest winning streak", "neutral", "17 wins"), html);
        Assert.Contains("class=\"histogram\"", html);
        Assert.Contains("Rolling 20-trade win rate", html);
        Assert.Contains("href=\"#analysis\" data-section=\"analysis\"", html);
        Assert.Contains("<th scope=\"row\">NQ</th><td>3</td><td>100.0%</td><td class=\"positive\">USD 100.00</td>", html);
        Assert.Contains("<th scope=\"row\">MES</th><td>23</td>", html);
        Assert.Contains("<th scope=\"row\">Short</th><td>3</td>", html);
        Assert.Contains("<th scope=\"row\">Monday</th>", html);
        Assert.DoesNotContain("By entry hour", html);
        Assert.DoesNotContain("By setup", html);
        Assert.Contains("Net P&amp;L by month", html);
        Assert.Contains("<a href=\"#month-2026-06\">440</a>", html);
    }

    [Fact]
    public void ReaderRendersSchemaFiveSetupsMonthlyReturnsBenchmarkAndTearSheet()
    {
        var journal = Journal();
        PublicationTradeSetupInfo Setup(string name, string state) => new("", [new(name, 1, "Primary", [new("Trend", "Required", "Setup", state), new("News", "Disqualifier", "Context", "NotMet")])]);
        var setups = new Dictionary<decimal, PublicationTradeSetupInfo?> { [200] = Setup("Setup A", "Met"), [-100] = Setup("Setup A", "NotMet"), [300] = Setup("Setup B", "Unknown"), [100] = null };
        Trade Exact(decimal pnl, DateTimeOffset exit) => Trade(journal.Id, pnl, exit, exitPrice: 5000m + pnl / 5);
        var trades = new[] { Exact(200, new(2026, 3, 2, 22, 30, 0, TimeSpan.Zero)), Exact(-100, new(2026, 3, 3, 22, 30, 0, TimeSpan.Zero)),
            Exact(300, new(2026, 4, 1, 22, 30, 0, TimeSpan.Zero)), Exact(100, new(2026, 4, 2, 22, 30, 0, TimeSpan.Zero)) };
        var built = PublicationExportService.BuildSnapshot(journal, trades, new(), new Dictionary<DateOnly, List<PublicationNode>>(), [], new(Title: "Five"),
            new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero), setupForTrade: trade => setups[trade.NetPnl]);
        Assert.All(built.Days.SelectMany(d => d.Trades), trade => Assert.Equal(trade.ExitUtc!.Value.AddHours(-8), trade.EntryUtc));
        BenchmarkPoint Close(int month, int day, decimal value) => new() { Symbol = "SPY", EventUtc = new(2026, month, day, 21, 0, 0, TimeSpan.Zero), Value = value };
        var benchmark = PublicationExportService.Benchmark(new() { Ticker = "SPY", StrategyReturnPercent = 5, BenchmarkReturnPercent = 10, AlphaPercent = -5, AnnualizedAlpha = -.2m, Beta = .5m, Correlation = .3m },
            [Close(3, 1, 100), Close(3, 31, 105), Close(4, 2, 110), Close(4, 20, 200)], 10_000m, built);
        Assert.Equal(3, benchmark!.Points.Count);
        Assert.Null(PublicationExportService.Benchmark(new(), [Close(3, 1, 100)], 10_000m, built));
        var snapshot = built with
        {
            Months = built.Months.Select(m => m with { OpeningBalance = m.Month.Month == 3 ? 10_000m : 10_100m }).ToList(),
            Benchmark = benchmark,
            TearSheet = [new("Risk & stability", "Daily risk.", [new("Sharpe", "1.23", "Annualized", "positive")])]
        };
        PublicationValidation.Validate(snapshot);
        var restored = JsonSerializer.Deserialize<PublicationSnapshot>(JsonSerializer.Serialize(snapshot, PublicationJson.Options), PublicationJson.Options)!;
        PublicationValidation.Validate(restored);
        var html = PublicationPresentation.Document(restored, null, _ => "", "/", "/publication.css");
        Assert.Contains("<th scope=\"row\">Setup A</th><td>2</td><td>50.0%</td>", html);
        Assert.Contains("<th scope=\"row\">Setup B</th><td>1</td>", html);
        Assert.True(html.IndexOf("<th scope=\"row\">Setup B</th>") < html.IndexOf("<th scope=\"row\">No setup recorded</th>"));
        Assert.Contains("<th scope=\"row\">Rules followed</th><td>1</td><td>100.0%</td>", html);
        Assert.Contains("<th scope=\"row\">Rules broken</th><td>1</td><td>0.0%</td>", html);
        Assert.Contains("<th scope=\"row\">Not fully evaluated</th><td>1</td>", html);
        Assert.Contains("<th scope=\"row\">14:00–14:59</th><td>4</td>", html);
        Assert.Contains("14:30:00 @ 5,000.00 → 22:30:00 @ 5,040.00 · held 8h 00m", html);
        // March 100 / 10,000; April 400 / 10,100; the year compounds both.
        Assert.Contains("Return by month", html);
        Assert.Contains("<a href=\"#month-2026-03\">+1.0%</a>", html);
        Assert.Contains("<a href=\"#month-2026-04\">+4.0%</a>", html);
        Assert.Contains("\">+5.0%</td></tr>", html);
        Assert.Contains("Compared with SPY", html);
        Assert.Contains("<dt>Alpha</dt><dd class=\"negative\">-5.00%", html);
        Assert.Contains("<dt>Beta</dt><dd class=\"positive\">0.50", html);
        Assert.Contains("class=\"line-benchmark\"", html);
        Assert.Contains("Strategy 5.00%</span>", html);
        Assert.Contains("SPY 10.00%</span>", html);
        Assert.Contains("<dt>Sharpe</dt><dd class=\"positive\">1.23<small>Annualized</small>", html);
    }

    [Fact]
    public void SchemaFiveFieldsAreValidatedAndOlderSnapshotsStillRender()
    {
        var snapshot = Snapshot();
        var day = snapshot.Days[0];
        var exact = day.Trades[0] with { EntryUtc = new(2026, 7, 1, 4, 0, 0, TimeSpan.Zero), ExitUtc = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), EntryPrice = 1, ExitPrice = 2 };
        PublicationValidation.Validate(snapshot with { Days = [day with { Trades = [exact] }] });
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(snapshot with { SchemaVersion = 4, Days = [day with { Trades = [exact] }] }));
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(snapshot with { Days = [day with { Trades = [exact with { EntryUtc = exact.ExitUtc!.Value.AddMinutes(1) }] }] }));
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(snapshot with { Days = [day with { Trades = [exact with { ExitPrice = null }] }] }));
        var withBalance = snapshot.Months.Select(m => m with { OpeningBalance = 1000 }).ToList();
        PublicationValidation.Validate(snapshot with { Months = withBalance });
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(snapshot with { SchemaVersion = 4, Months = withBalance }));
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(snapshot with { Months = [snapshot.Months[0] with { NetPnl = 1, OpeningBalance = 1000 }] }));
        var benchmark = new PublicationBenchmark { Symbol = "SPY", Points = [new(new(2026, 6, 30), 100), new(new(2026, 7, 1), 101)] };
        PublicationValidation.Validate(snapshot with { Benchmark = benchmark });
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(snapshot with { SchemaVersion = 4, Benchmark = benchmark }));
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(snapshot with { Benchmark = benchmark with { Points = [.. benchmark.Points.AsEnumerable().Reverse()] } }));
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(snapshot with { Benchmark = benchmark with { Points = [new(new(2026, 7, 1), 0)] } }));
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(snapshot with { TearSheet = [new("Risk", "", [new("Sharpe", "—", "", "muted")])] }));
        Assert.Throws<InvalidOperationException>(() => PublicationValidation.Validate(snapshot with { SchemaVersion = 4, TearSheet = [new("Risk", "", [new("Sharpe", "1", "")])] }));
        var legacy = snapshot with { SchemaVersion = 4 };
        var json = JsonSerializer.Serialize(legacy, PublicationJson.Options);
        Assert.DoesNotContain("openingBalance", json);
        Assert.DoesNotContain("benchmark", json);
        Assert.DoesNotContain("entryUtc", json);
        var html = PublicationPresentation.Document(JsonSerializer.Deserialize<PublicationSnapshot>(json, PublicationJson.Options)!, null, _ => "", "/", "/publication.css");
        Assert.Contains("Where the results come from", html);
        Assert.Contains("Net P&amp;L by month", html);
        Assert.DoesNotContain("Compared with", html);
        Assert.DoesNotContain("Detailed indicators", html);
    }

    [Fact]
    public void FeedListsNewestDaysWithAbsoluteLinksAndEncodedContent()
    {
        var journal = Journal();
        var trades = Enumerable.Range(0, 40).Select(i => Trade(journal.Id, i, new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddDays(i))).ToArray();
        var notes = new Dictionary<DateOnly, List<PublicationNode>> { [new(2026, 2, 9)] = PublicationExportService.ConvertContent("Note <b>", _ => throw new Exception()) };
        var snapshot = PublicationExportService.BuildSnapshot(journal, trades, new(), notes, [], new(Title: "Feed"), DateTimeOffset.UtcNow);
        var feed = PublicationPresentation.Feed(snapshot, null, "https://journal.example/", id => "https://journal.example/images/" + id);
        var document = System.Xml.Linq.XDocument.Parse(feed);
        System.Xml.Linq.XNamespace atom = "http://www.w3.org/2005/Atom";
        var entries = document.Root!.Elements(atom + "entry").ToArray();
        Assert.Equal(PublicationPresentation.FeedSize, entries.Length);
        Assert.Equal("https://journal.example/?date=2026-02-09", entries[0].Element(atom + "id")!.Value);
        Assert.Contains("Note &lt;b&gt;", entries[0].Element(atom + "content")!.Value);
        Assert.Equal("https://journal.example/feed.xml", document.Root.Elements(atom + "link").Single(l => (string?)l.Attribute("rel") == "self").Attribute("href")!.Value);
    }

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("https://user:pass@example.com/")]
    [InlineData("https://example.com/?secret=x")]
    [InlineData("file:///C:/data")]
    public void PublishingRejectsUnsafeDestinations(string url) => Assert.Throws<InvalidOperationException>(() => PublicationWorkflow.Destination(url));

    private static int Count(string text, string fragment) => text.Split(fragment).Length - 1;
    private static byte[] TestImageBytes()
    {
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Transparent, 2, 2);
        return image.ToByteArray(ImageMagick.MagickFormat.Png);
    }

    // ImageMagick embeds creation timestamps, so encode once: separate encodings differ when they straddle a second.
    private static readonly byte[] PngBytes = TestImageBytes();
    private static byte[] Png() => PngBytes.ToArray();
    private static Journal Journal(string timeZone = "UTC") => new() { Id = Guid.NewGuid(), Name = "PRIVATE-JOURNAL", TimeZone = timeZone, Currency = "USD", StartingEquity = 1000, CreatedUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };
    private static Trade Trade(Guid journalId, decimal pnl, DateTimeOffset exit, string symbol = "MES", string direction = "Long", decimal? exitPrice = null) => new()
    {
        Id = Guid.NewGuid(), JournalId = journalId, Symbol = symbol, Direction = direction, Quantity = 1, ClosedQuantity = 1,
        EntryPrice = 5000m, ExitPrice = exitPrice,
        EntryUtc = exit.AddHours(-8), ExitUtc = exit, Status = "closed", NetPnl = pnl, GrossPnl = pnl + 5, Fees = 5,
        Account = "PRIVATE-ACCOUNT", Note = "PRIVATE-NOTE", SourceKey = "PRIVATE-SOURCE"
    };
    internal static PublicationSnapshot Snapshot(Guid? journalId = null)
    {
        var journal = new Journal { Id = journalId ?? Guid.NewGuid(), TimeZone = "UTC", Currency = "USD", StartingEquity = 1000 };
        return PublicationExportService.BuildSnapshot(journal, [Trade(journal.Id, 50, new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero))], new() { CurrentBalance = 1050 }, new Dictionary<DateOnly, List<PublicationNode>>(), [], new(Title: "Public journal"), new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero));
    }
    private static PublicationSnapshot WithImage(PublicationSnapshot snapshot)
    {
        var image = new PublicationImage(Guid.NewGuid().ToString("N"), "image/png", Png());
        return snapshot with { Images = [image], Days = [snapshot.Days[0] with { Content = [new() { Children = [new() { Type = "text", Format = 1, Text = "Public writing" }] }, new() { Type = "image", ImageId = image.Id, Alt = "Public chart" }] }] };
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(Environment.GetEnvironmentVariable("TRADEFOUNDRY_VERIFY_ROOT") ?? System.IO.Path.GetTempPath(), "publication-test-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => System.IO.Directory.CreateDirectory(Path);
        public void Dispose() { if (System.IO.Directory.Exists(Path)) System.IO.Directory.Delete(Path, true); }
    }

    private sealed class LocalData : IDisposable
    {
        public TestDirectory Directory { get; } = new();
        public TradeFoundryDb Database { get; }
        public Journal Journal { get; }
        public LocalData()
        {
            Database = new(Options.Create(new StorageOptions { DataDirectory = Directory.Path }));
            Database.CreateOwner("Owner", "test-hash");
            Journal = Database.CreateJournal("Private name", "live", "", "UTC", "USD", "flat_to_flat");
        }
        public void Dispose()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString());
            SqliteConnection.ClearPool(connection);
            Directory.Dispose();
        }
    }

    private sealed class PublicHost : IAsyncDisposable
    {
        private readonly TestDirectory _directory = new();
        private Microsoft.AspNetCore.Builder.WebApplication _app = null!;
        public string Token { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        public HttpClient Client { get; private set; } = null!;
        public static async Task<PublicHost> Start()
        {
            var host = new PublicHost();
            host._app = PublicJournalApplication.Build(["--urls", "http://127.0.0.1:0", "--Publishing:Token", host.Token,
                "--Storage:DataDirectory", host._directory.Path, "--Logging:LogLevel:Default", "Warning"]);
            await host._app.StartAsync();
            var address = host._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            host.Client = new HttpClient { BaseAddress = new(address) };
            host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.Token);
            return host;
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            _directory.Dispose();
        }
    }
}

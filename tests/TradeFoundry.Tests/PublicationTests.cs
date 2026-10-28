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
        var snapshot = new PublicationExportService(data.Database, new(data.Database)).Build(data.Journal.Id, new(Title: "Public"));
        var trade = Assert.Single(Assert.Single(snapshot.Days).Trades);
        var chart = Assert.IsType<PublicationTradeChart>(trade.Chart);
        Assert.Equal(2, snapshot.SchemaVersion);
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
        Assert.Contains("inferred", snapshot.Dashboard.BalanceNote);

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
        Assert.Equal(Png(), Assert.Single(snapshot.Images).Content);
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
        data.Database.SaveTradeReview(data.Journal.Id, trade.ReviewKey, new TradeReviewPatch { AllInCommission = 3.75m, ReviewNote = "PRIVATE-REVIEW", ExpectedRevision = 0 });
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
        Assert.Equal(Png(), await visitor.GetByteArrayAsync(imagePath));
        Assert.Equal("image/png", (await visitor.GetAsync(imagePath)).Content.Headers.ContentType!.MediaType);
        Assert.Contains(".calendar", await visitor.GetStringAsync("/publication.css"));
        var malformed = snapshot with { SnapshotId = Guid.NewGuid(), Images = [] };
        Assert.Contains("showModal", await visitor.GetStringAsync("/publication-assets/publication-charts.js"));
        Assert.Contains("class TradeExecutionDrawing", await visitor.GetStringAsync("/publication-assets/trade-execution-drawing.js"));
        Assert.Contains("5.2.0", await visitor.GetStringAsync("/publication-assets/lightweight-charts.standalone.production.js"));
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/publication-assets/private.js")).StatusCode);
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
        await workflow.Preview(data.Journal.Id, default);
        var frozen = data.Database.GetPublishingState(data.Journal.Id).Draft!;
        data.Database.SaveDailyJournal(data.Journal.Id, date, "Edited after preview", 1);
        await workflow.Publish(data.Journal.Id, frozen.Snapshot.SnapshotId, default);
        var html = await host.Client.GetStringAsync("/");
        Assert.Contains("Before preview", html);
        Assert.DoesNotContain("Edited after preview", html);
        await workflow.Publish(data.Journal.Id, frozen.Snapshot.SnapshotId, default); // Retry is safe.
        Assert.Equal(1, (await host.Client.GetFromJsonAsync<PublicationStatus>("/api/v1/publication"))!.Revision);
        await workflow.Preview(data.Journal.Id, default);
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
        Assert.Equal(1, Count(html, "class=\"month panel\" open"));
        Assert.Contains("Load older entries", html);
        var older = PublicationPresentation.Document(snapshot, null, _ => "", "/", "/publication.css", before: snapshot.Days[^10].Date);
        Assert.Equal(10, Count(older, "class=\"panel day-entry\""));
        var selected = PublicationPresentation.Document(snapshot, null, _ => "", "/", "/publication.css", selected: snapshot.Days[0].Date);
        Assert.Equal(1, Count(selected, "class=\"panel day-entry\""));
        Assert.Contains($"id=\"day-{snapshot.Days[0].Date:yyyy-MM-dd}\"", selected);
    }

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("https://user:pass@example.com/")]
    [InlineData("https://example.com/?secret=x")]
    [InlineData("file:///C:/data")]
    public void PublishingRejectsUnsafeDestinations(string url) => Assert.Throws<InvalidOperationException>(() => PublicationWorkflow.Destination(url));

    private static int Count(string text, string fragment) => text.Split(fragment).Length - 1;
    private static byte[] Png() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
    private static Journal Journal(string timeZone = "UTC") => new() { Id = Guid.NewGuid(), Name = "PRIVATE-JOURNAL", TimeZone = timeZone, Currency = "USD", StartingEquity = 1000, CreatedUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };
    private static Trade Trade(Guid journalId, decimal pnl, DateTimeOffset exit) => new()
    {
        Id = Guid.NewGuid(), JournalId = journalId, Symbol = "MES", Direction = "Long", Quantity = 1, ClosedQuantity = 1,
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

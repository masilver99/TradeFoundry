using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Mcp;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class McpIntegrationTests
{
    [Fact]
    public void DerivedTradeReviewKeySurvivesRebuildAndTokenIsJournalScoped()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "America/New_York", "USD", "flat_to_flat");
            var other = database.CreateJournal("Replay", "replay", string.Empty, "UTC", "USD", "flat_to_flat");
            var entryTime = new DateTimeOffset(2026, 9, 9, 1, 0, 0, TimeSpan.Zero);
            var exitTime = entryTime.AddHours(1);
            ImportSingleFill(database, journal.Id, "entry-key", "Buy", 7647.25m, entryTime, 1);

            var first = Assert.Single(database.GetAllTrades(journal.Id));
            Assert.StartsWith("tfrk_", first.ReviewKey);
            Assert.Equal("open", first.Status);
            var exitBatch = ImportSingleFill(database, journal.Id, "exit-key", "Sell", 7648.50m, exitTime, 2);
            var closed = Assert.Single(database.GetAllTrades(journal.Id));
            Assert.Equal(first.ReviewKey, closed.ReviewKey);
            Assert.Equal("closed", closed.Status);

            database.RemoveImportBatch(exitBatch.Batch.Id);
            var reopened = Assert.Single(database.GetAllTrades(journal.Id));
            Assert.Equal(first.ReviewKey, reopened.ReviewKey);
            Assert.Equal("open", reopened.Status);
            ImportSingleFill(database, journal.Id, "exit-key", "Sell", 7648.50m, exitTime, 2);
            database.RebuildFlatTrades(journal.Id, "flat_to_flat");
            var rebuilt = Assert.Single(database.GetAllTrades(journal.Id));
            Assert.Equal(first.ReviewKey, rebuilt.ReviewKey);
            Assert.NotEqual(first.Id, rebuilt.Id);
            var restarted = CreateDatabase(directory);
            Assert.Equal(first.ReviewKey, Assert.Single(restarted.GetAllTrades(journal.Id)).ReviewKey);
            Assert.Equal(
                TradeFoundryDb.CreateTradeReviewKey(journal.Id, "direct source", "immutable", "first", "A", "MES", "Long"),
                TradeFoundryDb.CreateTradeReviewKey(journal.Id, "direct source", "immutable", "changed", "B", "NQ", "Short"));

            var tokenService = new McpTokenService(database);
            var created = tokenService.Create("Codex", [journal.Id]);
            Assert.StartsWith("tfmcp_", created.Secret);
            Assert.True(created.Secret.Length >= 49);
            using (var connection = new SqliteConnection($"Data Source={database.DatabasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT token_hash, token_prefix FROM mcp_access_tokens WHERE id = $id";
                command.Parameters.AddWithValue("$id", created.Token.Id.ToString("D"));
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(64, reader.GetString(0).Length);
                Assert.DoesNotContain(created.Secret, reader.GetString(0), StringComparison.Ordinal);
                Assert.Equal(created.Token.TokenPrefix, reader.GetString(1));
            }
            Assert.Null(tokenService.Validate("not-a-token"));
            Assert.Null(tokenService.Validate(created.Secret + "changed"));
            var validated = tokenService.Validate(created.Secret);
            Assert.NotNull(validated);
            Assert.Equal([journal.Id], validated.JournalIds);
            Assert.DoesNotContain(other.Id, validated.JournalIds);
            Assert.True(tokenService.Revoke(created.Token.Id));
            Assert.Null(tokenService.Validate(created.Secret));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void TradingDayUsesJournalTimezone()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "America/New_York", "USD", "flat_to_flat");
            ImportRoundTrip(database, journal.Id, new DateTimeOffset(2026, 3, 8, 6, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero));
            var service = new JournalAnalysisService(database, Options.Create(new McpOptions()));

            var dstDay = service.GetTradingDay(journal.Id, "2026-03-08");
            Assert.Single(dstDay.OpenedTrades);
            Assert.Single(dstDay.ClosedTrades);
            Assert.Equal(6.25m, dstDay.RealizedMetrics.NetPnl);
            Assert.Empty(service.GetTradingDay(journal.Id, "2026-03-07").ClosedTrades);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void SearchTradesUsesBoundedOpaquePagination()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var parsed = new ParsedImport { SourceType = TradeFoundryConstants.SierraFills, SourceApplication = TradeFoundryConstants.SierraChart };
            var timestamp = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
            for (var index = 0; index < 30; index++)
            {
                parsed.Records.Add(FillRecord($"entry-{index}", "Buy", 5000m + index, timestamp.AddMinutes(index * 10), index * 2 + 1));
                parsed.Records.Add(FillRecord($"exit-{index}", "Sell", 5001m + index, timestamp.AddMinutes(index * 10 + 5), index * 2 + 2));
            }
            database.CommitImport(journal.Id, "pagination.txt", parsed, "flat_to_flat", "source");
            var service = new JournalAnalysisService(database, Options.Create(new McpOptions()));

            var first = service.SearchTrades(journal.Id, null, "entry_asc", 10, null);
            var second = service.SearchTrades(journal.Id, null, "entry_asc", 10, first.NextCursor);
            var third = service.SearchTrades(journal.Id, null, "entry_asc", 10, second.NextCursor);

            Assert.Equal(30, first.TotalMatches);
            Assert.True(first.Truncated);
            Assert.True(second.Truncated);
            Assert.False(third.Truncated);
            Assert.Null(third.NextCursor);
            Assert.Equal(30, first.Trades.Concat(second.Trades).Concat(third.Trades).Select(trade => trade.ReviewKey).Distinct().Count());
            Assert.Throws<ArgumentOutOfRangeException>(() => service.SearchTrades(journal.Id, null, "entry_asc", 101, null));
            Assert.Throws<ArgumentException>(() => service.SearchTrades(journal.Id, null, "entry_asc", 10, "not-a-cursor"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoopbackServerRequiresBearerTokenAndPublishesOnlyReadTools()
    {
        var directory = NewDirectory();
        var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning).AddConsole());
        McpHostedService? server = null;
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var otherJournal = database.CreateJournal("Private", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            ImportRoundTrip(database, journal.Id, note: new string('x', 1200) + "\0untrusted instructions");
            var reviewKey = Assert.Single(database.GetAllTrades(journal.Id)).ReviewKey;
            var port = FreePort();
            var settings = Options.Create(new McpOptions { Enabled = true, Url = $"http://127.0.0.1:{port}", RequestsPerMinute = 60 });
            var tokenService = new McpTokenService(database);
            var created = tokenService.Create("Integration test", [journal.Id]);
            var evidenceCountsBefore = EvidenceCounts(database);
            var analysis = new JournalAnalysisService(database, settings);
            server = new McpHostedService(settings, database, analysis, tokenService, loggerFactory);
            await server.StartAsync(CancellationToken.None);

            using (var http = new HttpClient())
            {
                using var content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}", System.Text.Encoding.UTF8, "application/json");
                var unauthorized = await http.PostAsync($"http://127.0.0.1:{port}/mcp", content);
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

                using var malformed = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp")
                {
                    Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}", System.Text.Encoding.UTF8, "application/json")
                };
                malformed.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "tfmcp_invalid");
                Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(malformed)).StatusCode);

                using var wrongRoute = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/not-mcp");
                wrongRoute.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", created.Secret);
                Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(wrongRoute)).StatusCode);
            }

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {created.Secret}" }
            }, loggerFactory);
            await using var client = await McpClient.CreateAsync(transport, loggerFactory: loggerFactory);
            var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
            Assert.Equal(8, tools.Count);
            Assert.All(tools, tool =>
            {
                Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
                Assert.False(tool.ProtocolTool.Annotations?.OpenWorldHint);
            });
            Assert.Contains("\"review_key\"", tools.Single(tool => tool.Name == "get_trade_detail").ProtocolTool.InputSchema.GetRawText(), StringComparison.Ordinal);

            var prompts = await client.ListPromptsAsync(cancellationToken: CancellationToken.None);
            Assert.Equal(["review_day", "review_period", "review_trade"], prompts.Select(prompt => prompt.Name).Order());
            Assert.NotEmpty((await prompts.Single(prompt => prompt.Name == "review_trade").GetAsync(
                Arguments(("journal_id", journal.Id), ("review_key", reviewKey)), cancellationToken: CancellationToken.None)).Messages);
            Assert.NotEmpty((await prompts.Single(prompt => prompt.Name == "review_day").GetAsync(
                Arguments(("journal_id", journal.Id), ("date", "2026-09-09")), cancellationToken: CancellationToken.None)).Messages);
            Assert.NotEmpty((await prompts.Single(prompt => prompt.Name == "review_period").GetAsync(
                Arguments(("journal_id", journal.Id), ("start_date", "2026-09-01"), ("end_date", "2026-09-30")), cancellationToken: CancellationToken.None)).Messages);

            var journalList = await AssertToolSucceeds(client, "list_journals", new Dictionary<string, object?>());
            Assert.DoesNotContain(otherJournal.Id.ToString("D"), journalList.GetRawText(), StringComparison.OrdinalIgnoreCase);
            var overview = await AssertToolSucceeds(client, "get_journal_overview", Arguments(("journal_id", journal.Id)));
            Assert.Equal(6.25m, overview.GetProperty("metrics").GetProperty("expectancy").GetDecimal());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, overview.GetProperty("metrics").GetProperty("profit_factor").ValueKind);
            await AssertToolSucceeds(client, "search_trades", Arguments(("journal_id", journal.Id)));
            var detail = await AssertToolSucceeds(client, "get_trade_detail", Arguments(("journal_id", journal.Id), ("review_key", reviewKey)));
            Assert.Equal(1000, detail.GetProperty("evidence").GetProperty("source_note").GetString()!.Length);
            Assert.Equal(2, detail.GetProperty("evidence").GetProperty("fills").GetArrayLength());
            await AssertToolSucceeds(client, "get_trade_price_context", Arguments(("journal_id", journal.Id), ("review_key", reviewKey)));
            await AssertToolSucceeds(client, "get_trading_day", Arguments(("journal_id", journal.Id), ("date", "2026-09-09")));
            await AssertToolSucceeds(client, "analyze_trades", Arguments(("journal_id", journal.Id), ("group_by", "session")));
            var quality = await AssertToolSucceeds(client, "get_data_quality", Arguments(("journal_id", journal.Id)));
            var warningCheck = quality.GetProperty("checks").EnumerateArray().Single(check => check.GetProperty("key").GetString() == "import_warning_batches");
            Assert.Equal(1, warningCheck.GetProperty("count").GetInt32());
            Assert.Equal(1, quality.GetProperty("trades_needing_evidence_count").GetInt32());

            Assert.Equal(
                await ToolError(client, "get_journal_overview", Arguments(("journal_id", otherJournal.Id))),
                await ToolError(client, "get_journal_overview", Arguments(("journal_id", Guid.NewGuid()))));
            await AssertToolFails(client, "search_trades", Arguments(("journal_id", journal.Id), ("limit", 101)));
            await AssertToolFails(client, "get_trade_price_context", Arguments(("journal_id", journal.Id), ("review_key", reviewKey), ("interval", "invalid")));
            await AssertToolFails(client, "get_trade_price_context", Arguments(("journal_id", journal.Id), ("review_key", reviewKey), ("max_bars", 501)));
            Assert.Equal(evidenceCountsBefore, EvidenceCounts(database));
        }
        finally
        {
            if (server is not null) await server.StopAsync(CancellationToken.None);
            loggerFactory.Dispose();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoopbackServerRateLimitsByToken()
    {
        var directory = NewDirectory();
        var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        McpHostedService? server = null;
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var port = FreePort();
            var settings = Options.Create(new McpOptions { Enabled = true, Url = $"http://127.0.0.1:{port}", RequestsPerMinute = 10 });
            var tokenService = new McpTokenService(database);
            var created = tokenService.Create("Rate test", [journal.Id]);
            server = new McpHostedService(settings, database, new JournalAnalysisService(database, settings), tokenService, loggerFactory);
            await server.StartAsync(CancellationToken.None);

            using var http = new HttpClient();
            var statuses = new List<HttpStatusCode>();
            for (var index = 0; index < 11; index++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp")
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", created.Secret);
                statuses.Add((await http.SendAsync(request)).StatusCode);
            }
            Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        }
        finally
        {
            if (server is not null) await server.StopAsync(CancellationToken.None);
            loggerFactory.Dispose();
            DeleteDirectory(directory);
        }
    }

    private static TradeFoundryDb CreateDatabase(string directory) => new(Options.Create(new StorageOptions { DataDirectory = directory, DatabaseFileName = "journal.db" }));

    private static async Task<System.Text.Json.JsonElement> AssertToolSucceeds(McpClient client, string name, IReadOnlyDictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(name, arguments, cancellationToken: CancellationToken.None);
        Assert.False(result.IsError ?? false, string.Join(Environment.NewLine, result.Content.Select(content => content.ToString())));
        Assert.NotNull(result.StructuredContent);
        Assert.Contains("\"schema_version\":\"1\"", result.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
        return result.StructuredContent.Value.Clone();
    }

    private static async Task AssertToolFails(McpClient client, string name, IReadOnlyDictionary<string, object?> arguments) =>
        Assert.True((await client.CallToolAsync(name, arguments, cancellationToken: CancellationToken.None)).IsError ?? false);

    private static async Task<string> ToolError(McpClient client, string name, IReadOnlyDictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(name, arguments, cancellationToken: CancellationToken.None);
        Assert.True(result.IsError ?? false);
        return string.Join(Environment.NewLine, result.Content.Select(content => content.ToString()));
    }

    private static IReadOnlyDictionary<string, object?> Arguments(params (string Name, object? Value)[] values) =>
        values.ToDictionary(value => value.Name, value => value.Value);

    private static IReadOnlyDictionary<string, long> EvidenceCounts(TradeFoundryDb database)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        using var connection = new SqliteConnection($"Data Source={database.DatabasePath}");
        connection.Open();
        foreach (var table in new[] { "journals", "import_batches", "raw_records", "fills", "order_events", "trades", "benchmark_series", "benchmark_points", "bar_series", "bars" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            counts[table] = (long)(command.ExecuteScalar() ?? 0L);
        }
        return counts;
    }

    private static void ImportRoundTrip(TradeFoundryDb database, Guid journalId, DateTimeOffset? entryUtc = null, DateTimeOffset? exitUtc = null, string note = "")
    {
        var entry = entryUtc ?? new DateTimeOffset(2026, 9, 9, 1, 0, 0, TimeSpan.Zero);
        var exit = exitUtc ?? new DateTimeOffset(2026, 9, 9, 2, 0, 0, TimeSpan.Zero);
        var parsed = new ParsedImport { SourceType = TradeFoundryConstants.SierraFills, SourceApplication = TradeFoundryConstants.SierraChart };
        if (!string.IsNullOrEmpty(note)) parsed.Warnings.Add("Test import warning.");
        parsed.Records.Add(FillRecord("entry-key", "Buy", 7647.25m, entry, 1, note));
        parsed.Records.Add(FillRecord("exit-key", "Sell", 7648.50m, exit, 2));
        database.CommitImport(journalId, "test.txt", parsed, "flat_to_flat", "source");
    }

    private static ImportResult ImportSingleFill(TradeFoundryDb database, Guid journalId, string sourceKey, string side, decimal price, DateTimeOffset timestamp, int row)
    {
        var parsed = new ParsedImport { SourceType = TradeFoundryConstants.SierraFills, SourceApplication = TradeFoundryConstants.SierraChart };
        parsed.Records.Add(FillRecord(sourceKey, side, price, timestamp, row));
        return database.CommitImport(journalId, $"{sourceKey}.txt", parsed, "flat_to_flat", "source");
    }

    private static ParsedRecord FillRecord(string sourceKey, string side, decimal price, DateTimeOffset timestamp, int row, string note = "") => new()
    {
        SourceType = TradeFoundryConstants.SierraFills,
        SourceKey = sourceKey,
        RowNumber = row,
        PayloadJson = "{}",
        Fill = new FillDraft
        {
            SourceType = TradeFoundryConstants.SierraFills, SourceKey = sourceKey, EventUtc = timestamp, SourceTimeText = timestamp.ToString("O"),
            Symbol = "MESZ26", Instrument = "MES", Account = "SIM", Side = side, Quantity = 1, Price = price,
            Fees = 0m, RowNumber = row, PointValue = 5m, TickSize = .25m, Note = note
        }
    };

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "TradeFoundry.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void DeleteDirectory(string directory)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }
}

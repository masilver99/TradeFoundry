using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Pages;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class TradeCandleChartTests
{
    [Fact]
    public void CandlesEmitLightweightPayloadWithExactPricesAndNoPlotlyMarkup()
    {
        var start = Utc(2026, 9, 14, 13, 0);
        var bars = Enumerable.Range(0, 20)
            .Select(index => new Bar
            {
                Id = Guid.NewGuid(),
                SeriesId = Guid.NewGuid(),
                Symbol = "MES",
                Interval = "1m",
                EventUtc = start.AddMinutes(index),
                Open = 5000m + index,
                High = 5001m + index,
                Low = 4999m + index,
                Close = 5000.5m + index,
                Volume = 100 + index
            })
            .ToArray();
        var trade = new Trade
        {
            Id = Guid.NewGuid(),
            JournalId = Guid.NewGuid(),
            Symbol = "MES",
            Instrument = "MES",
            Direction = "Long",
            EntryUtc = start.AddMinutes(8).AddSeconds(20),
            ExitUtc = start.AddMinutes(12).AddSeconds(10),
            EntryPrice = 5008.25m,
            ExitPrice = 5012.75m,
            NetPnl = 22.50m,
            TickSize = .25m
        };

        var html = ChartRenderer.Candles(trade, new BarQueryResult { Bars = bars, ResolvedInterval = "1m" }, "America/New_York");

        Assert.Contains("data-lightweight-chart", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-plotly-chart", html, StringComparison.Ordinal);
        Assert.DoesNotContain("TradingView", html, StringComparison.Ordinal);

        var match = Regex.Match(html, "data-lightweight-chart=\"(?<payload>[^\"]+)\"", RegexOptions.CultureInvariant);
        Assert.True(match.Success);
        using var payload = JsonDocument.Parse(WebUtility.HtmlDecode(match.Groups["payload"].Value));
        var root = payload.RootElement;
        Assert.Equal("MES", root.GetProperty("symbol").GetString());
        Assert.Equal("1m", root.GetProperty("interval").GetString());
        Assert.Equal("America/New_York", root.GetProperty("timeZone").GetString());
        Assert.Equal(20, root.GetProperty("bars").GetArrayLength());
        Assert.Equal(102, root.GetProperty("bars")[2].GetProperty("volume").GetInt64());
        var entry = root.GetProperty("trade").GetProperty("entry");
        Assert.Equal(5008.25m, entry.GetProperty("price").GetDecimal());
        Assert.Equal(trade.EntryUtc.ToUnixTimeSeconds(), entry.GetProperty("exactTime").GetInt64());
        Assert.Equal(start.AddMinutes(8).ToUnixTimeSeconds(), entry.GetProperty("time").GetInt64());
        Assert.Equal("belowBar", entry.GetProperty("barPosition").GetString());
        Assert.Equal("arrowUp", entry.GetProperty("shape").GetString());
        Assert.Equal("Long", entry.GetProperty("label").GetString());
        var exit = root.GetProperty("trade").GetProperty("exit");
        Assert.Equal(5012.75m, exit.GetProperty("price").GetDecimal());
        Assert.Equal(trade.ExitUtc.Value.ToUnixTimeSeconds(), exit.GetProperty("exactTime").GetInt64());
        Assert.Equal("aboveBar", exit.GetProperty("barPosition").GetString());
        Assert.Equal("arrowDown", exit.GetProperty("shape").GetString());
        Assert.Equal("Exit", exit.GetProperty("label").GetString());
        var path = root.GetProperty("trade").GetProperty("path");
        Assert.Equal(2, path.GetArrayLength());
        Assert.Equal(start.AddMinutes(8).ToUnixTimeSeconds(), path[0].GetProperty("time").GetInt64());
        Assert.Equal(start.AddMinutes(12).ToUnixTimeSeconds(), path[1].GetProperty("time").GetInt64());
        Assert.DoesNotContain(path.EnumerateArray(), point => point.GetProperty("time").GetInt64() == trade.EntryUtc.ToUnixTimeSeconds());
        Assert.DoesNotContain(path.EnumerateArray(), point => point.GetProperty("time").GetInt64() == trade.ExitUtc.Value.ToUnixTimeSeconds());
        Assert.Contains("handler=CandleBars", root.GetProperty("historyUrl").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CandlesUseOppositeBarMarkerPositionsForShortTrades()
    {
        var start = Utc(2026, 9, 14, 13, 0);
        var bars = Enumerable.Range(0, 8)
            .Select(index => new Bar
            {
                Id = Guid.NewGuid(),
                SeriesId = Guid.NewGuid(),
                Symbol = "MES",
                Interval = "1m",
                EventUtc = start.AddMinutes(index),
                Open = 5000m + index,
                High = 5001m + index,
                Low = 4999m + index,
                Close = 5000.5m + index
            })
            .ToArray();
        var trade = new Trade
        {
            Id = Guid.NewGuid(),
            JournalId = Guid.NewGuid(),
            Symbol = "MES",
            Instrument = "MES",
            Direction = "Short",
            EntryUtc = start.AddMinutes(2).AddSeconds(10),
            ExitUtc = start.AddMinutes(5).AddSeconds(20),
            EntryPrice = 5002.25m,
            ExitPrice = 4999.75m,
            NetPnl = -12.50m,
            TickSize = .25m
        };

        using var payload = ReadPayload(ChartRenderer.Candles(trade, new BarQueryResult { Bars = bars, ResolvedInterval = "1m" }));
        var entry = payload.RootElement.GetProperty("trade").GetProperty("entry");
        var exit = payload.RootElement.GetProperty("trade").GetProperty("exit");

        Assert.Equal("aboveBar", entry.GetProperty("barPosition").GetString());
        Assert.Equal("arrowDown", entry.GetProperty("shape").GetString());
        Assert.Equal("Short", entry.GetProperty("label").GetString());
        Assert.Equal("belowBar", exit.GetProperty("barPosition").GetString());
        Assert.Equal("arrowUp", exit.GetProperty("shape").GetString());
        Assert.Equal("Exit", exit.GetProperty("label").GetString());
    }

    [Fact]
    public void CandlesDoNotEmitDuplicatePathTimesWhenTradeFitsInsideOneBar()
    {
        var start = Utc(2026, 9, 14, 13, 0);
        var bars = Enumerable.Range(0, 5)
            .Select(index => new Bar
            {
                Id = Guid.NewGuid(),
                SeriesId = Guid.NewGuid(),
                Symbol = "MES",
                Interval = "1m",
                EventUtc = start.AddMinutes(index),
                Open = 5000m + index,
                High = 5001m + index,
                Low = 4999m + index,
                Close = 5000.5m + index
            })
            .ToArray();
        var trade = new Trade
        {
            Id = Guid.NewGuid(),
            JournalId = Guid.NewGuid(),
            Symbol = "MES",
            Instrument = "MES",
            Direction = "Long",
            EntryUtc = start.AddMinutes(2).AddSeconds(10),
            ExitUtc = start.AddMinutes(2).AddSeconds(40),
            EntryPrice = 5002.25m,
            ExitPrice = 5002.75m,
            NetPnl = 2.50m,
            TickSize = .25m
        };

        using var payload = ReadPayload(ChartRenderer.Candles(trade, new BarQueryResult { Bars = bars, ResolvedInterval = "1m" }));

        Assert.Empty(payload.RootElement.GetProperty("trade").GetProperty("path").EnumerateArray());
    }

    private static JsonDocument ReadPayload(string html)
    {
        var match = Regex.Match(html, "data-lightweight-chart=\"(?<payload>[^\"]+)\"", RegexOptions.CultureInvariant);
        Assert.True(match.Success);
        return JsonDocument.Parse(WebUtility.HtmlDecode(match.Groups["payload"].Value));
    }

    [Fact]
    public void BarHistoryPagesAreExclusiveOrderedAndBounded()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var start = Utc(2026, 9, 14, 13, 0);
            ImportBars(database, journal.Id, start, 20, "1m");

            var before = database.GetBarHistoryPage(journal.Id, "MES", "1m", start.AddMinutes(10), before: true, limit: 3);
            Assert.Equal([start.AddMinutes(7), start.AddMinutes(8), start.AddMinutes(9)], before.Bars.Select(x => x.EventUtc));
            Assert.True(before.HasMore);

            var after = database.GetBarHistoryPage(journal.Id, "MES", "1m", start.AddMinutes(10), before: false, limit: 3);
            Assert.Equal([start.AddMinutes(11), start.AddMinutes(12), start.AddMinutes(13)], after.Bars.Select(x => x.EventUtc));
            Assert.True(after.HasMore);

            var last = database.GetBarHistoryPage(journal.Id, "MES", "1m", start.AddMinutes(18), before: false, limit: 3);
            Assert.Single(last.Bars);
            Assert.Equal(start.AddMinutes(19), last.Bars[0].EventUtc);
            Assert.False(last.HasMore);

            var first = database.GetBarHistoryPage(journal.Id, "MES", "1m", start.AddMinutes(3), before: true, limit: 3);
            Assert.Equal([start, start.AddMinutes(1), start.AddMinutes(2)], first.Bars.Select(x => x.EventUtc));
            Assert.False(first.HasMore);
            Assert.Throws<ArgumentOutOfRangeException>(() => database.GetBarHistoryPage(journal.Id, "MES", "1m", start, before: true, limit: 501));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void DerivedHistoryUsesCompleteUtcAlignedBucketsAcrossPageBoundaries()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var start = Utc(2026, 9, 14, 13, 0);
            ImportBars(database, journal.Id, start, 30, "1m");

            var first = database.GetBarHistoryPage(journal.Id, "MES", "5m", start, before: false, limit: 2);
            Assert.Equal([start.AddMinutes(5), start.AddMinutes(10)], first.Bars.Select(x => x.EventUtc));
            Assert.True(first.HasMore);
            Assert.Equal(105m, first.Bars[0].Open);
            Assert.Equal(111m, first.Bars[0].High);
            Assert.Equal(104m, first.Bars[0].Low);
            Assert.Equal(110m, first.Bars[0].Close);
            Assert.Equal(535L, first.Bars[0].Volume);

            var second = database.GetBarHistoryPage(journal.Id, "MES", "5m", first.Bars[^1].EventUtc, before: false, limit: 2);
            Assert.Equal([start.AddMinutes(15), start.AddMinutes(20)], second.Bars.Select(x => x.EventUtc));
            Assert.DoesNotContain(first.Bars.Select(x => x.EventUtc), value => second.Bars.Any(x => x.EventUtc == value));

            var beforeUnaligned = database.GetBarHistoryPage(journal.Id, "MES", "5m", start.AddMinutes(7), before: true, limit: 5);
            Assert.Equal([start], beforeUnaligned.Bars.Select(x => x.EventUtc));
            var afterUnaligned = database.GetBarHistoryPage(journal.Id, "MES", "5m", start.AddMinutes(7), before: false, limit: 2);
            Assert.Equal([start.AddMinutes(10), start.AddMinutes(15)], afterUnaligned.Bars.Select(x => x.EventUtc));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void CandleBarsHandlerValidatesInputsAndJournalOwnership()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var start = Utc(2026, 9, 14, 13, 0);
            ImportBars(database, journal.Id, start, 5, "1m", includeTrade: true);
            var trade = Assert.Single(database.GetAllTrades(journal.Id));
            var page = new TradeModel(database);

            var json = Assert.IsType<JsonResult>(page.OnGetCandleBars(trade.Id, journal.Id, "1m", "after", start.AddMinutes(1).ToString("O"), 2));
            using (var document = JsonDocument.Parse(JsonSerializer.Serialize(json.Value)))
            {
                Assert.Equal("1m", document.RootElement.GetProperty("interval").GetString());
                Assert.Equal(2, document.RootElement.GetProperty("bars").GetArrayLength());
            }

            Assert.IsType<BadRequestObjectResult>(page.OnGetCandleBars(trade.Id, journal.Id, "1m", "sideways", start.ToString("O"), 2));
            Assert.IsType<BadRequestObjectResult>(page.OnGetCandleBars(trade.Id, journal.Id, "1m", "after", start.ToString("O"), 501));
            Assert.IsType<BadRequestObjectResult>(page.OnGetCandleBars(trade.Id, journal.Id, "bogus", "after", start.ToString("O"), 2));
            Assert.IsType<BadRequestObjectResult>(page.OnGetCandleBars(trade.Id, journal.Id, "1m", "after", start.AddHours(1).ToString("Ozzz"), 2));
            Assert.IsType<NotFoundResult>(page.OnGetCandleBars(trade.Id, Guid.NewGuid(), "1m", "after", start.ToString("O"), 2));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void CandleChartHandlerReturnsPayloadForRequestedTimeframe()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var start = Utc(2026, 9, 14, 13, 0);
            ImportBars(database, journal.Id, start, 10, "1m", includeTrade: true);
            var trade = Assert.Single(database.GetAllTrades(journal.Id));
            var page = new TradeModel(database);

            var json = Assert.IsType<JsonResult>(page.OnGetCandleChart(trade.Id, journal.Id, "2m"));
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(json.Value));
            var root = document.RootElement;
            Assert.Equal("2m", root.GetProperty("requestedInterval").GetString());
            Assert.Equal("2m", root.GetProperty("resolvedInterval").GetString());
            Assert.Equal(5, root.GetProperty("barCount").GetInt32());
            Assert.Equal(5, root.GetProperty("payload").GetProperty("bars").GetArrayLength());
            Assert.Contains("handler=CandleBars", root.GetProperty("payload").GetProperty("historyUrl").GetString(), StringComparison.Ordinal);
            Assert.Contains("interval=2m", root.GetProperty("payload").GetProperty("historyUrl").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static void ImportBars(TradeFoundryDb database, Guid journalId, DateTimeOffset start, int count, string interval, bool includeTrade = false)
    {
        var parsed = new ParsedImport { SourceType = TradeFoundryConstants.OhlcvBars, SourceApplication = TradeFoundryConstants.Ohlcv };
        for (var index = 0; index < count; index++)
        {
            var eventUtc = start.AddMinutes(index);
            parsed.Records.Add(new ParsedRecord
            {
                SourceType = TradeFoundryConstants.OhlcvBars,
                SourceKey = $"MES\u001f{interval}\u001f{eventUtc:O}",
                RowNumber = index + 1,
                PayloadJson = "{}",
                Bar = new BarDraft
                {
                    Symbol = "MES",
                    Interval = interval,
                    EventUtc = eventUtc,
                    Open = 100m + index,
                    High = 102m + index,
                    Low = 99m + index,
                    Close = 101m + index,
                    Volume = 100 + index
                }
            });
        }

        if (includeTrade)
        {
            parsed.Trades.Add(new ImportedTradeDraft
            {
                SourceType = TradeFoundryConstants.TradingViewStrategy,
                SourceKey = "trade-1",
                Symbol = "MES",
                Instrument = "MES",
                Account = "SIM",
                Direction = "Long",
                EntryUtc = start.AddMinutes(1),
                ExitUtc = start.AddMinutes(3),
                EntryPrice = 101m,
                ExitPrice = 103m,
                Quantity = 1,
                GrossPoints = 2m,
                GrossPnl = 10m,
                NetPnl = 10m,
                PointValue = 5m,
                TickSize = .25m
            });
        }

        database.CommitImport(journalId, $"bars-{interval}.csv", parsed, "flat_to_flat", interval);
    }

    private static TradeFoundryDb CreateDatabase(string directory) => new(Options.Create(new StorageOptions { DataDirectory = directory, DatabaseFileName = "journal.db" }));

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "TradeFoundry.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string directory)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

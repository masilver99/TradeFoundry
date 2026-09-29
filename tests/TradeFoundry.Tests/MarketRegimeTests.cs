using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class MarketRegimeTests
{
    [Fact]
    public void FeatureBuilderCalculatesEfficiencyAndGuardsZeroRange()
    {
        var builder = new MarketDayFeatureBuilder(new MarketRegimeOptions { MinimumRthBars = 3 });
        var bars = new List<Bar>();
        var firstStart = Utc(2026, 9, 1, 13, 30);
        bars.AddRange(new[]
        {
            BarAt(firstStart, 100m, 101m, 99m, 100.5m),
            BarAt(firstStart.AddMinutes(5), 100.5m, 102m, 100m, 101.5m),
            BarAt(firstStart.AddMinutes(10), 101.5m, 103m, 101m, 102.5m)
        });
        var zeroRangeStart = Utc(2026, 9, 2, 13, 30);
        bars.AddRange(new[]
        {
            BarAt(zeroRangeStart, 200m, 200m, 200m, 200m),
            BarAt(zeroRangeStart.AddMinutes(5), 200m, 200m, 200m, 200m),
            BarAt(zeroRangeStart.AddMinutes(10), 200m, 200m, 200m, 200m)
        });

        var days = builder.Build(bars, "MES");
        var directional = Assert.Single(days, day => day.TradeDate == new DateOnly(2026, 9, 1));
        Assert.Equal(4m, directional.RthRangePoints);
        Assert.Equal(2.5m / 4m, directional.DirectionalEfficiency, 8);
        Assert.Equal(1m, directional.PathEfficiency, 8);
        Assert.Equal(.875m, directional.CloseLocation);
        Assert.Equal(MarketDirection.Up, directional.Direction);

        var zeroRange = Assert.Single(days, day => day.TradeDate == new DateOnly(2026, 9, 2));
        Assert.Equal(0m, zeroRange.RthRangePoints);
        Assert.Equal(0m, zeroRange.DirectionalEfficiency);
        Assert.Equal(0m, zeroRange.PathEfficiency);
        Assert.Null(zeroRange.CloseLocation);
        Assert.Null(zeroRange.NormalizedRange);
    }

    [Fact]
    public void ClassifierKeepsMarketTypeAndDirectionSeparate()
    {
        var classifier = new MarketRegimeClassifier();
        var days = new[]
        {
            DirectFeature(new DateOnly(2026, 1, 1), MarketDirection.Up, .95m, .95m, .95m, .95m, .1m, .9m, 30m),
            DirectFeature(new DateOnly(2026, 1, 2), MarketDirection.Down, .45m, .45m, .6m, .65m, 4m, .55m, 35m),
            DirectFeature(new DateOnly(2026, 1, 3), MarketDirection.Neutral, .1m, .1m, .5m, .5m, 5m, .5m, 40m)
        };

        var classified = classifier.Classify(days);
        var strongUp = Assert.Single(classified, day => day.TradeDate == new DateOnly(2026, 1, 1));
        Assert.Equal(MarketType.StrongTrend, strongUp.MarketType);
        Assert.Equal(MarketDirection.Up, strongUp.Direction);
        Assert.NotEqual("StrongTrendUp", strongUp.MarketType.ToString());
        Assert.Equal(MarketType.Range, classified.Single(day => day.Direction == MarketDirection.Neutral).MarketType);
    }

    [Fact]
    public void VolatilityClassifierUsesLowNormalHighAndExtremeRanks()
    {
        var classifier = new MarketRegimeClassifier();
        var days = Enumerable.Range(1, 10)
            .Select(index => DirectFeature(new DateOnly(2026, 1, index), MarketDirection.Neutral, .1m, .1m, .5m, .5m, 5m, .5m, index))
            .ToArray();

        var classified = classifier.Classify(days);
        Assert.Equal(VolatilityRegime.Low, classified[0].VolatilityRegime);
        Assert.Equal(VolatilityRegime.Normal, classified[2].VolatilityRegime);
        Assert.Equal(VolatilityRegime.High, classified[7].VolatilityRegime);
        Assert.Equal(VolatilityRegime.Extreme, classified[9].VolatilityRegime);
    }

    [Fact]
    public void ProbabilityServiceFiltersShrinksAndExcludesAsOfDateWithoutDoubleCountingEsMes()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Market data", "replay", string.Empty, "America/New_York", "USD", "flat_to_flat");
            ImportSyntheticBars(database, journal.Id, 25, includeEs: true, includeMes: true);
            var service = new MarketProbabilityService(database);

            var all = service.GetMarketDays(journal.Id, new MarketProbabilityQuery { Symbol = "ES/MES" }, limit: 500);
            Assert.Equal(25, all.TotalMatches);
            Assert.Equal(25, all.Days.Count);
            Assert.Equal(25, all.Days.Select(day => day.TradeDate).Distinct().Count());
            Assert.Contains(all.Days, day => day.Symbol == "ES");

            var monday = service.GetProbabilities(journal.Id, new MarketProbabilityQuery
            {
                Symbol = "ES/MES",
                Month = 8,
                DayOfWeek = DayOfWeek.Monday
            });
            Assert.True(monday.SampleSize > 0);
            Assert.Equal(monday.SampleSize, monday.MarketTypes.First().SampleSize);
            Assert.All(monday.MarketTypes, estimate =>
            {
                Assert.NotNull(estimate.RawProbability);
                Assert.NotNull(estimate.AdjustedProbability);
                Assert.NotNull(estimate.ConfidenceInterval.Lower);
                Assert.NotNull(estimate.CredibleInterval.Upper);
            });
            Assert.Equal("day_of_week baseline with premarket and volatility conditioning removed", monday.ShrinkagePriorScope);
            Assert.True(monday.ShrinkagePriorSampleSize >= monday.SampleSize);
            Assert.NotEmpty(monday.Baselines);
            Assert.True(monday.RthRange.P25 <= monday.RthRange.P50);
            Assert.True(monday.RthRange.P50 <= monday.RthRange.P75);
            var weakTrend = monday.MarketTypes.Single(estimate => estimate.Outcome == nameof(MarketType.WeakTrend));
            Assert.True(weakTrend.AdjustedProbability < weakTrend.RawProbability);
            Assert.Equal(weakTrend.AdjustedProbability - weakTrend.BaselineProbability, weakTrend.DifferenceFromBaseline);

            var windowStart = all.Days[5].TradeDate;
            var windowEnd = all.Days[9].TradeDate;
            var window = service.GetProbabilities(journal.Id, new MarketProbabilityQuery
            {
                Symbol = "MES",
                StartDate = windowStart,
                EndDate = windowEnd,
                RangeLessThanPoints = 8m
            });
            Assert.Equal(5, window.SampleSize);
            var rangeThreshold = Assert.Single(window.ThresholdProbabilities);
            Assert.Equal("rth_range_points", rangeThreshold.Metric);
            Assert.Equal(1m, rangeThreshold.RawProbability);
            Assert.Equal(5, rangeThreshold.SampleSize);

            var cutoff = all.Days[10].TradeDate;
            var asOf = service.GetProbabilities(journal.Id, new MarketProbabilityQuery
            {
                Symbol = "MES",
                AsOfDate = cutoff
            });
            Assert.Equal(10, asOf.SampleSize);
            Assert.DoesNotContain(service.GetClassifiedDays(journal.Id, new MarketProbabilityQuery { Symbol = "MES", AsOfDate = cutoff }), day => day.TradeDate >= cutoff);

            var noResults = service.GetProbabilities(journal.Id, new MarketProbabilityQuery { Symbol = "MES", Month = 12 });
            Assert.Equal(0, noResults.SampleSize);
            Assert.All(noResults.MarketTypes, estimate => Assert.Null(estimate.RawProbability));
            Assert.Contains(noResults.DataQualityWarnings, warning => warning.Contains("No sessions matched", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void MarketDaysArePaginatedAndCombinedFiltersRemainDeterministic()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Market data", "replay", string.Empty, "America/New_York", "USD", "flat_to_flat");
            ImportSyntheticBars(database, journal.Id, 25, includeEs: false, includeMes: true);
            var service = new MarketProbabilityService(database);

            var first = service.GetMarketDays(journal.Id, new MarketProbabilityQuery { Symbol = "MES" }, limit: 7);
            Assert.True(first.Truncated);
            Assert.NotNull(first.NextCursor);
            var second = service.GetMarketDays(journal.Id, new MarketProbabilityQuery { Symbol = "MES" }, limit: 7, first.NextCursor);
            Assert.Equal(7, second.Days.Count);
            Assert.NotEqual(first.Days[0].TradeDate, second.Days[0].TradeDate);

            var filtered = service.GetMarketDays(journal.Id, new MarketProbabilityQuery
            {
                Symbol = "MES",
                Month = 8,
                DayOfWeek = DayOfWeek.Monday,
                OvernightDirection = MarketDirection.Up
            }, 500);
            Assert.All(filtered.Days, day =>
            {
                Assert.Equal(8, day.Month);
                Assert.Equal(DayOfWeek.Monday, day.DayOfWeek);
                Assert.Equal(MarketDirection.Up, day.OvernightDirection);
            });
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void FeatureCachePersistsDailyRowsAndRefreshesAfterALaterBarImport()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Market data", "replay", string.Empty, "America/New_York", "USD", "flat_to_flat");
            ImportSyntheticBars(database, journal.Id, 25, includeEs: false, includeMes: true);
            var service = new MarketProbabilityService(database);

            Assert.Equal(25, service.GetClassifiedDays(journal.Id, new MarketProbabilityQuery { Symbol = "MES" }).Count);
            var persisted = database.GetMarketDayFeatureRows("MES", "5m", "market-day-features-v1");
            Assert.Equal(25, persisted.Count);

            var nextDate = new DateOnly(2026, 9, 7);
            var nextImport = new ParsedImport { SourceType = TradeFoundryConstants.OhlcvBars, SourceApplication = TradeFoundryConstants.Ohlcv };
            AddSession(nextImport, nextDate, 25, "MES");
            database.CommitImport(journal.Id, "synthetic-market-append.csv", nextImport, "flat_to_flat", "5m", "UTC");

            Assert.Equal(26, service.GetClassifiedDays(journal.Id, new MarketProbabilityQuery { Symbol = "MES" }).Count);
            Assert.Equal(26, database.GetMarketDayFeatureRows("MES", "5m", "market-day-features-v1").Count);
            Assert.Empty(database.GetMarketFeatureDirtyRanges("MES"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static MarketDayFeature DirectFeature(DateOnly date, MarketDirection direction, decimal efficiency, decimal path, decimal closeLocation, decimal vwapPersistence, decimal countertrend, decimal structure, decimal volatility) => new()
    {
        TradeDate = date,
        Symbol = "MES",
        Open = 100m,
        High = 110m,
        Low = 100m,
        Close = direction == MarketDirection.Up ? 109m : direction == MarketDirection.Down ? 101m : 105m,
        RthRangePoints = 10m,
        VolatilityMeasurePoints = volatility,
        DirectionalEfficiency = efficiency,
        PathEfficiency = path,
        CloseLocation = closeLocation,
        PercentSessionAboveVwap = vwapPersistence,
        PercentSessionBelowVwap = 1m - vwapPersistence,
        PercentHigherHighs = direction == MarketDirection.Up ? structure : .5m,
        PercentHigherLows = direction == MarketDirection.Up ? structure : .5m,
        PercentLowerHighs = direction == MarketDirection.Down ? structure : .5m,
        PercentLowerLows = direction == MarketDirection.Down ? structure : .5m,
        MaximumCountertrendExcursion = countertrend,
        Direction = direction,
        RthBarCount = 10
    };

    private static Bar BarAt(DateTimeOffset timestamp, decimal open, decimal high, decimal low, decimal close) => new()
    {
        Id = Guid.NewGuid(),
        SeriesId = Guid.NewGuid(),
        Symbol = "MES",
        Interval = "5m",
        EventUtc = timestamp,
        Open = open,
        High = high,
        Low = low,
        Close = close,
        Volume = 10
    };

    private static void ImportSyntheticBars(TradeFoundryDb database, Guid journalId, int sessionCount, bool includeEs, bool includeMes)
    {
        var parsed = new ParsedImport { SourceType = TradeFoundryConstants.OhlcvBars, SourceApplication = TradeFoundryConstants.Ohlcv };
        var date = new DateOnly(2026, 8, 3);
        var sessionIndex = 0;
        while (sessionIndex < sessionCount)
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                date = date.AddDays(1);
                continue;
            }

            if (includeEs) AddSession(parsed, date, sessionIndex, "ES");
            if (includeMes) AddSession(parsed, date, sessionIndex, "MES");
            sessionIndex++;
            date = date.AddDays(1);
        }

        database.CommitImport(journalId, "synthetic-market.csv", parsed, "flat_to_flat", "5m", "UTC");
    }

    private static void AddSession(ParsedImport parsed, DateOnly date, int sessionIndex, string symbol)
    {
        var rthStart = Utc(date.Year, date.Month, date.Day, 13, 30);
        var basePrice = 100m + sessionIndex * .5m;
        var up = sessionIndex % 2 == 0;
        for (var index = 0; index < 6; index++)
        {
            var open = basePrice + (up ? index : -index);
            var close = open + (up ? 1m : -1m);
            var high = Math.Max(open, close) + .5m;
            var low = Math.Min(open, close) - .5m;
            var timestamp = rthStart.AddMinutes(index * 5);
            parsed.Records.Add(new ParsedRecord
            {
                SourceType = TradeFoundryConstants.OhlcvBars,
                SourceKey = $"{symbol}\u001f5m\u001f{timestamp:O}",
                RowNumber = parsed.Records.Count + 1,
                Bar = new BarDraft { Symbol = symbol, Interval = "5m", EventUtc = timestamp, Open = open, High = high, Low = low, Close = close, Volume = 100 }
            });
        }

        var overnightStart = rthStart.AddMinutes(-60);
        var overnightOpen = basePrice + (up ? -.5m : .5m);
        var overnightClose = basePrice + (up ? 0m : 1m);
        parsed.Records.Add(new ParsedRecord
        {
            SourceType = TradeFoundryConstants.OhlcvBars,
            SourceKey = $"{symbol}\u001f5m\u001f{overnightStart:O}",
            RowNumber = parsed.Records.Count + 1,
                Bar = new BarDraft { Symbol = symbol, Interval = "5m", EventUtc = overnightStart, Open = overnightOpen, High = Math.Max(overnightOpen, overnightClose) + .25m, Low = Math.Min(overnightOpen, overnightClose) - .25m, Close = overnightClose, Volume = 50 }
            });

        var overnightContinuation = overnightStart.AddMinutes(5);
        var continuationClose = overnightClose + (up ? .5m : -.5m);
        parsed.Records.Add(new ParsedRecord
        {
            SourceType = TradeFoundryConstants.OhlcvBars,
            SourceKey = $"{symbol}\u001f5m\u001f{overnightContinuation:O}",
            RowNumber = parsed.Records.Count + 1,
            Bar = new BarDraft
            {
                Symbol = symbol,
                Interval = "5m",
                EventUtc = overnightContinuation,
                Open = overnightClose,
                High = Math.Max(overnightClose, continuationClose) + .25m,
                Low = Math.Min(overnightClose, continuationClose) - .25m,
                Close = continuationClose,
                Volume = 50
            }
        });
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

using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class JournalFilterTests
{
    private const string NewYork = "America/New_York";

    [Fact]
    public void Parse_falls_back_to_all_and_repairs_inputs()
    {
        Assert.Equal(JournalFilter.None, JournalFilter.Parse("someday", "not-a-date", null, "sideways"));

        var reversed = JournalFilter.Parse("custom", "2026-03-31", "2026-01-01", "LONG");
        Assert.Equal(new DateOnly(2026, 1, 1), reversed.CustomFrom);
        Assert.Equal(new DateOnly(2026, 3, 31), reversed.CustomTo);
        Assert.Equal("long", reversed.Side);

        var datesOnly = JournalFilter.Parse(null, "2026-02-01", null, null);
        Assert.Equal("custom", datesOnly.Period);
        Assert.Null(datesOnly.CustomTo);

        var presetIgnoresDates = JournalFilter.Parse("this-year", "2026-02-01", "2026-02-02", null);
        Assert.Null(presetIgnoresDates.CustomFrom);
        Assert.Equal("period=this-year", presetIgnoresDates.ToQueryString());

        Assert.False(JournalFilter.Parse("custom", null, null, "all").IsActive);
    }

    [Theory]
    [InlineData("this-month", "2026-12-03", "2026-12-01", "2026-12-31")]
    [InlineData("this-month", "2027-01-02", "2027-01-01", "2027-01-31")]
    [InlineData("last-month", "2026-01-15", "2025-12-01", "2025-12-31")]
    [InlineData("last-month", "2026-03-31", "2026-02-01", "2026-02-28")]
    [InlineData("this-quarter", "2026-11-11", "2026-10-01", "2026-12-31")]
    [InlineData("this-quarter", "2026-01-01", "2026-01-01", "2026-03-31")]
    [InlineData("this-year", "2026-07-04", "2026-01-01", "2026-12-31")]
    public void Presets_resolve_against_the_current_day(string period, string today, string from, string to)
    {
        var (start, end) = JournalFilter.Parse(period, null, null, null).DateRange(DateOnly.Parse(today));
        Assert.Equal(DateOnly.Parse(from), start);
        Assert.Equal(DateOnly.Parse(to), end);
    }

    [Fact]
    public void Scope_uses_local_midnights_across_a_daylight_saving_change()
    {
        var scope = JournalFilter.Parse("custom", "2026-11-01", "2026-11-01", null).Scope(NewYork);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), scope.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), scope.EndUtcExclusive);
        Assert.Equal("Nov 1, 2026", scope.PeriodLabel);
        Assert.Equal("Oct 1–31, 2026", JournalFilterScope.RangeText(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        Assert.Equal("From Jan 5, 2026", JournalFilterScope.RangeText(new DateOnly(2026, 1, 5), null));
    }

    [Fact]
    public void Database_and_in_memory_filters_agree_on_dates_direction_and_open_trades()
    {
        var directory = NewDirectory();
        try
        {
            var (database, journal) = CreateFixture(directory);
            var all = database.GetAllTrades(journal.Id);
            Assert.Equal(5, all.Count);

            // Nov 1–3 local: the overnight short (exits Nov 1), the losing long, and the late long that exits Nov 4 UTC but Nov 3 locally.
            AssertFilter(database, journal, JournalFilter.Parse("custom", "2026-11-01", "2026-11-03", null), expectedCount: 3, expectedNet: 10m);
            AssertFilter(database, journal, JournalFilter.Parse("custom", "2026-11-01", "2026-11-03", "long"), expectedCount: 2, expectedNet: 0m);
            // The overnight short entered Oct 31 but belongs to its exit day.
            AssertFilter(database, journal, JournalFilter.Parse("custom", "2026-10-31", "2026-10-31", null), expectedCount: 0, expectedNet: 0m);
            // An open trade is dated by its entry.
            AssertFilter(database, journal, JournalFilter.Parse("custom", "2026-11-05", null, null), expectedCount: 1, expectedNet: 0m, expectedOpen: 1);
            AssertFilter(database, journal, JournalFilter.Parse(null, null, null, "short"), expectedCount: 2, expectedNet: 10m, expectedOpen: 1);
            AssertFilter(database, journal, JournalFilter.None, expectedCount: 5, expectedNet: 20m, expectedOpen: 1);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public void Review_navigation_stays_inside_the_filter_and_collapses_the_other_direction()
    {
        var directory = NewDirectory();
        try
        {
            var (database, journal) = CreateFixture(directory);
            var reviews = new TradeReviewService(database);
            var longOnly = JournalFilter.Parse(null, null, null, "long").Scope(NewYork);

            var nov1 = reviews.GetDay(journal.Id, new DateOnly(2026, 11, 1), longOnly);
            Assert.Empty(nov1.CompletedTrades);
            Assert.Equal(1, nov1.HiddenTradeCount);
            Assert.False(nov1.ShowingHiddenTrades);
            Assert.Equal(new DateOnly(2026, 10, 30), nov1.PreviousDate);
            Assert.Equal(new DateOnly(2026, 11, 2), nov1.NextDate);

            // Nov 2 steps back to Oct 30, skipping the short-only Nov 1.
            Assert.Equal(new DateOnly(2026, 10, 30), reviews.GetDay(journal.Id, new DateOnly(2026, 11, 2), longOnly).PreviousDate);

            var shortKey = database.GetAllTrades(journal.Id).Single(trade => trade.Direction == "Short" && trade.Status == "closed").ReviewKey;
            var linked = reviews.GetDay(journal.Id, new DateOnly(2026, 11, 1), longOnly, focusReviewKey: shortKey);
            Assert.True(linked.ShowingHiddenTrades);
            Assert.Single(linked.CompletedTrades);

            var november = JournalFilter.Parse("custom", "2026-11-01", "2026-11-03", null).Scope(NewYork);
            var outside = reviews.GetDay(journal.Id, new DateOnly(2026, 10, 30), november);
            Assert.True(outside.OutsideFilterDates);
            Assert.Null(outside.PreviousDate);
            Assert.Equal(new DateOnly(2026, 11, 1), outside.NextDate);
            Assert.Equal(new DateOnly(2026, 11, 3), reviews.GetDefaultDate(journal.Id, november));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public void Url_filter_is_remembered_and_cookie_is_used_without_one()
    {
        var journal = new Journal { Id = Guid.NewGuid(), TimeZone = NewYork };
        var withQuery = new DefaultHttpContext();
        withQuery.Request.QueryString = new QueryString("?period=last-month&side=short");
        var scope = JournalFilterRequest.Resolve(withQuery, journal);
        Assert.Equal("last-month", scope.Filter.Period);
        var setCookie = withQuery.Response.Headers.SetCookie.ToString();
        Assert.Contains(JournalFilterRequest.CookieName(journal.Id), setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);

        var fromCookie = new DefaultHttpContext();
        fromCookie.Request.Headers.Cookie = $"{JournalFilterRequest.CookieName(journal.Id)}={Uri.EscapeDataString("period=last-month&side=short")}";
        var remembered = JournalFilterRequest.Resolve(fromCookie, journal);
        Assert.Equal("short", remembered.Filter.Side);
        Assert.False(JournalFilterRequest.Current(fromCookie, journal)!.FromUrl);
        Assert.Empty(fromCookie.Response.Headers.SetCookie.ToString());

        // An explicit "all" in the URL wins over the cookie and forgets it.
        var cleared = new DefaultHttpContext();
        cleared.Request.Headers.Cookie = fromCookie.Request.Headers.Cookie;
        cleared.Request.QueryString = new QueryString("?period=all&side=all");
        Assert.False(JournalFilterRequest.Resolve(cleared, journal).IsActive);
        Assert.Contains("expires=Thu, 01 Jan 1970", cleared.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);

        // Another journal's cookie doesn't leak.
        var other = new Journal { Id = Guid.NewGuid(), TimeZone = NewYork };
        Assert.False(JournalFilterRequest.Resolve(fromCookie, other).IsActive);
    }

    private static void AssertFilter(TradeFoundryDb database, Journal journal, JournalFilter filter, int expectedCount, decimal expectedNet, int expectedOpen = 0)
    {
        var scope = filter.Scope(journal.TimeZone, new DateOnly(2026, 11, 11));
        var inMemory = scope.Apply(database.GetAllTrades(journal.Id));
        Assert.Equal(expectedCount, inMemory.Count);
        Assert.Equal(expectedNet, inMemory.Where(trade => trade.Status == "closed").Sum(trade => trade.NetPnl));

        var overview = database.GetOverview(journal.Id, filter);
        Assert.Equal(expectedCount - expectedOpen, overview.ClosedTradeCount);
        Assert.Equal(expectedOpen, overview.OpenTradeCount);
        Assert.Equal(expectedNet, overview.Equity.LastOrDefault()?.CumulativePnl ?? 0m);

        var ledger = database.GetTrades(new TradeQuery { JournalId = journal.Id, Scope = scope.IsActive ? scope : null });
        Assert.Equal(expectedCount, ledger.TotalCount);
    }

    private static (TradeFoundryDb Database, Journal Journal) CreateFixture(string directory)
    {
        var database = new TradeFoundryDb(Options.Create(new StorageOptions { DataDirectory = directory, DatabaseFileName = "journal.db" }));
        database.CreateOwner("Owner", "test-hash");
        var journal = database.CreateJournal("Live", "live", string.Empty, NewYork, "USD", "flat_to_flat");
        var parsed = new ParsedImport { SourceType = TradeFoundryConstants.SierraFills, SourceApplication = TradeFoundryConstants.SierraChart };
        var row = 0;
        void Fill(string side, decimal price, DateTimeOffset at) => parsed.Records.Add(FillRecord($"fill-{++row}", side, price, at, row));
        static DateTimeOffset Utc(int month, int day, int hour, int minute = 0, int second = 0) => new(2026, month, day, hour, minute, second, TimeSpan.Zero);

        Fill("Buy", 5000m, Utc(10, 30, 14)); Fill("Sell", 5002m, Utc(10, 30, 15));               // Long +10, Oct 30
        Fill("Sell", 5005m, Utc(10, 31, 19)); Fill("Buy", 5003m, Utc(11, 1, 15));                // Short +10, overnight, exits Nov 1 (after the DST change)
        Fill("Buy", 5000m, Utc(11, 2, 15)); Fill("Sell", 4999m, Utc(11, 2, 16));                 // Long -5, Nov 2
        Fill("Buy", 5000m, Utc(11, 4, 4)); Fill("Sell", 5001m, Utc(11, 4, 4, 59, 30));           // Long +5, Nov 3 23:59 local
        Fill("Sell", 5000m, Utc(11, 5, 15));                                                      // Open short, Nov 5
        database.CommitImport(journal.Id, "fixture.txt", parsed, "flat_to_flat", "source");
        return (database, database.GetJournal(journal.Id)!);
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

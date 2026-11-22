using TradeFoundry.Core;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class TagReportTests
{
    private static readonly DateOnly Start = new(2026, 3, 2);
    private static readonly JournalFilterScope AllDates = JournalFilter.None.Scope("UTC");

    [Fact]
    public void SampleMatchesHandCalculation()
    {
        // Six trades on three days: +300, +100, -100, -100, +200, 0.
        var items = new[]
        {
            Item(0, 300m, 3m), Item(0, 100m, 1m), Item(1, -100m, -1m),
            Item(1, -100m, -1m), Item(2, 200m, 2m), Item(2, 0m, 0m)
        };

        var sample = TagStatistics.Measure(items);

        Assert.Equal(6, sample.Count);
        Assert.Equal(3, sample.Days);
        Assert.Equal(3, sample.Wins);
        Assert.Equal(.5m, sample.WinRate);                         // Breakeven trades count, as in Analytics.
        Assert.Equal(400m, sample.TotalPnl);
        Assert.Equal(3m, sample.ProfitFactor);                     // 600 / 200
        Assert.Equal(2m, sample.Payoff);                           // 200 / 100
        Assert.Equal(.333333m, sample.BreakevenWinRate!.Value, 6); // 1 / (1 + 2)
        Assert.Equal(66.666667m, sample.Pnl.Mean!.Value, 6);

        // Day residuals 800/3, -1000/3, 200/3 give SE = sqrt(3/2 * 1680000/9) / 6 = 88.1917 and t(2) = 4.303.
        Assert.Equal(379.4890, (double)(sample.Pnl.Upper!.Value - sample.Pnl.Mean.Value), 2);
        Assert.Equal(3, sample.ClosedAtOneR);
        Assert.Equal(.1876, (double)sample.WinRange.Lower!.Value, 3); // Wilson, 3 of 6
        Assert.Equal(.8124, (double)sample.WinRange.Upper!.Value, 3);
    }

    [Fact]
    public void OneDayGivesNoExpectancyRange()
    {
        var estimate = TagStatistics.Measure([Item(0, 10m), Item(0, 30m), Item(0, 50m)]).Pnl;
        Assert.Equal(30m, estimate.Mean);
        Assert.Null(estimate.Lower);
    }

    [Fact]
    public void ManyTradesOnOneDayAreWeakerEvidenceThanTheSameTradesOnSeparateDays()
    {
        var clustered = Enumerable.Range(0, 24).Select(index => Item(index / 12, index < 12 ? 100m : -50m)).ToArray();
        var separate = Enumerable.Range(0, 24).Select(index => Item(index, index < 12 ? 100m : -50m)).ToArray();
        Assert.True(TagStatistics.Measure(clustered).Pnl.Lower < TagStatistics.Measure(separate).Pnl.Lower);
    }

    [Theory]
    [InlineData(1, 12.706)]
    [InlineData(30, 2.042)]
    [InlineData(40, 2.021)]
    [InlineData(120, 1.980)]
    public void CriticalValuesMatchTheStudentTTable(int degrees, double expected) => Assert.Equal(expected, TagStatistics.Critical(degrees), 3);

    [Fact]
    public void AShortLuckyStreakCannotTopTheList()
    {
        var lucky = Tag("Lucky");
        var steady = Tag("Steady");
        var items = Enumerable.Range(0, 10).Select(index => Item(index, index == 0 ? -100m : 900m, null, lucky))
            .Concat(Enumerable.Range(0, 40).Select(index => Item(index + 20, index % 4 == 0 ? -100m : 120m, null, steady)))
            .ToArray();

        var report = Report(items, lucky, steady);

        Assert.Equal(steady.Id, report.Rows[0].Tag.Id);
        Assert.Equal(1, report.Rows[0].Rank);
        Assert.Equal(TagStanding.TooFew, report.Rows[1].Standing);
        Assert.Null(report.Rows[1].Rank);
    }

    [Fact]
    public void RankedTagsAreOrderedByTheLowEndOfTheirRangeNotTheAverage()
    {
        var wild = Tag("Wild");
        var calm = Tag("Calm");
        var items = Enumerable.Range(0, 30).Select(index => Item(index, index < 3 ? 3000m : -50m, null, wild))
            .Concat(Enumerable.Range(0, 30).Select(index => Item(index, index % 2 == 0 ? 120m : 80m, null, calm)))
            .ToArray();

        var rows = Report(items, wild, calm).Rows;

        Assert.Equal(calm.Id, rows[0].Tag.Id);
        Assert.True(rows[1].Sample.Pnl.Mean > rows[0].Sample.Pnl.Mean);
    }

    [Fact]
    public void RankingNeedsSeparateDaysAsWellAsTrades()
    {
        var tag = Tag("Opening drive");
        var items = Enumerable.Range(0, 40).Select(index => Item(index % 9, 50m + index, null, tag)).ToArray();
        Assert.Equal(TagStanding.TooFew, Assert.Single(Report(items, tag).Rows).Standing);
    }

    [Fact]
    public void OnlyBeforehandTagsWithADescriptionAreProbabilities()
    {
        var after = Tag("Runner", TagKnown.After);
        var during = Tag("Chased", TagKnown.During);
        var unreviewed = Tag("Old tag", TagKnown.Unset);
        var before = Tag("Opening drive");
        var items = Enumerable.Range(0, 30).Select(index => Item(index, 100m + index, null, after, during, unreviewed, before)).ToArray();

        var report = Report(items, after, during, unreviewed, before);

        TagStanding StandingOf(Tag tag) => report.Rows.Single(row => row.Tag.Id == tag.Id).Standing;
        Assert.Equal(TagStanding.Ranked, StandingOf(before));
        Assert.Equal(TagStanding.ReviewFinding, StandingOf(after));
        Assert.Equal(TagStanding.ReviewFinding, StandingOf(during));
        Assert.Equal(TagStanding.NeedsReview, StandingOf(unreviewed));
        Assert.Equal(before.Id, Assert.Single(report.Options).Tag.Id);
        Assert.False(report.Lookup([new TagPick(after, TagTarget.Trade)])!.IsProbability);
    }

    [Fact]
    public void RIsTheDefaultOnlyWithEnoughRiskData()
    {
        var tag = Tag("Opening drive");
        TagObservation[] WithRiskOn(int count) => Enumerable.Range(0, 30).Select(index => Item(index, 100m + index, index < count ? 1m : null, tag)).ToArray();

        Assert.True(Report(WithRiskOn(24), tag).UsesR);
        Assert.False(Report(WithRiskOn(23), tag).UsesR);
        var forced = Report(WithRiskOn(23), "r", tag);
        Assert.Equal(TagStanding.MissingRisk, Assert.Single(forced.Rows).Standing);
        Assert.False(Report(WithRiskOn(30), "money", tag).UsesR);
    }

    [Fact]
    public void LiftIgnoresUntaggedTradesAndThoseBeforeTheTagExisted()
    {
        var tag = Tag("New tag");
        var other = Tag("Established");
        var items = new List<TagObservation>();
        items.AddRange(Enumerable.Range(0, 15).Select(index => Item(index, -1000m, null, other)));       // Before first use.
        items.AddRange(Enumerable.Range(0, 12).Select(index => Item(20 + index, 100m, null, tag)));
        items.AddRange(Enumerable.Range(0, 12).Select(index => Item(20 + index, 40m, null, other)));
        items.AddRange(Enumerable.Range(0, 30).Select(index => Item(20 + index, -500m)));                // Untagged.

        var row = Report(items.ToArray(), tag, other).Rows.Single(item => item.Tag.Id == tag.Id);

        Assert.Equal(12, row.Others.Count);
        Assert.Equal(60m, row.ValueLift);
        Assert.Equal(0m, row.WinLift);
    }

    [Fact]
    public void StabilityComparesTheEarlierHalfWithTheLater()
    {
        var tag = Tag("Fading edge");
        var items = Enumerable.Range(0, 20).Select(index => Item(index, index < 10 ? 100m : -40m, null, tag)).ToArray();

        var row = Assert.Single(Report(items, tag).Rows);

        Assert.Equal(100m, row.Earlier);
        Assert.Equal(-40m, row.Later);
        Assert.Equal(TagStability.Changed, row.Stability);
    }

    [Fact]
    public void LookupCombinesTradeAndDayTagsAndEveryNumberOpensItsTrades()
    {
        var trend = Tag("Trend day");
        var drive = Tag("Opening drive");
        var location = Tag("A+ location");
        var items = new[]
        {
            Item(0, 300m, 3m, [trend], drive, location),
            Item(0, -100m, -1m, [trend], drive, location),
            Item(0, 50m, .5m, [trend], drive),
            Item(1, 80m, .8m, [], drive, location),
            Item(0, 20m, .2m, [trend])
        };
        var report = Report(items, trend, drive, location);

        var picks = report.ParsePicks([$"d:{trend.Id}", $"t:{drive.Id}", $"t:{location.Id}"]);
        var lookup = report.Lookup(picks)!;

        Assert.Equal(2, lookup.Sample.Count);
        Assert.Equal(.5m, lookup.Sample.WinRate);
        Assert.True(lookup.IsProbability);
        Assert.False(lookup.EnoughData);
        Assert.Equal(300m, Assert.Single(report.Show(lookup, "wins")).NetPnl);
        Assert.Equal(-100m, Assert.Single(report.Show(lookup, "losses")).NetPnl);
        Assert.Equal(lookup.Sample.ClosedAtOneR, report.Show(lookup, "closed-1r").Count);
        // Compared with trades tagged the same way: a trade tag and a tagged day.
        Assert.Equal(50m, Assert.Single(report.Show(lookup, "others")).NetPnl);
    }

    [Fact]
    public void ADayIsOneObservationHoweverManyTradesItHas()
    {
        var cpi = Tag("CPI day");
        var trades = Enumerable.Range(0, 12).Select(index => ClosedTrade(index, 10m)).ToArray();
        var dayTags = new Dictionary<DateOnly, IReadOnlyList<Tag>> { [Start] = [cpi] };

        var observations = TagPerformanceReport.Observe(TagTarget.Day, trades, new Dictionary<string, TradeReviewAnnotation>(), new Dictionary<string, IReadOnlyList<Tag>>(), dayTags, "UTC");
        var report = TagPerformanceReport.Create(new TagCatalog([cpi]), TagTarget.Day, observations, AllDates, null);

        var row = Assert.Single(report.Rows);
        Assert.Equal(1, row.Sample.Count);
        Assert.Equal(120m, row.Sample.TotalPnl);
        Assert.Equal(12, Assert.Single(observations).TradeCount);
    }

    [Fact]
    public void NetRUsesFeesAndPlannedRiskTakesPrecedence()
    {
        var trade = ClosedTrade(0, net: 90m, gross: 100m, importedRisk: 200m, mfePoints: 36m);
        var reviews = new Dictionary<string, TradeReviewAnnotation> { [trade.ReviewKey] = new() { PlannedRiskCurrency = 90m } };
        var none = new Dictionary<string, IReadOnlyList<Tag>>();
        var noDays = new Dictionary<DateOnly, IReadOnlyList<Tag>>();

        var planned = Assert.Single(TagPerformanceReport.Observe(TagTarget.Trade, [trade], reviews, none, noDays, "UTC"));
        var imported = Assert.Single(TagPerformanceReport.Observe(TagTarget.Trade, [trade], new Dictionary<string, TradeReviewAnnotation>(), none, noDays, "UTC"));

        Assert.Equal(1m, planned.NetR);
        Assert.Equal(2m, planned.ReachedR); // 36 points * $5 / $90
        Assert.Equal(.45m, imported.NetR);
    }

    private static TagPerformanceReport Report(TagObservation[] items, params Tag[] tags) => Report(items, null, tags);

    private static TagPerformanceReport Report(TagObservation[] items, string? metric, params Tag[] tags) =>
        TagPerformanceReport.Create(new TagCatalog(tags), TagTarget.Trade, items, AllDates, metric);

    private static Tag Tag(string name, TagKnown known = TagKnown.Beforehand) =>
        new() { Id = Guid.NewGuid(), Name = name, Known = known, Scope = TagScope.Both, Description = known == TagKnown.Unset ? string.Empty : $"When {name} applies." };

    private static TagObservation Item(int day, decimal pnl, decimal? r = null, params Tag[] tags) => Item(day, pnl, r, [], tags);

    private static TagObservation Item(int day, decimal pnl, decimal? r, Tag[] dayTags, params Tag[] tags) => new()
    {
        Key = Guid.NewGuid().ToString("N"),
        Date = Start.AddDays(day),
        Order = new DateTimeOffset(Start.AddDays(day).ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero),
        NetPnl = pnl,
        NetR = r,
        Tags = tags.Select(tag => tag.Id).ToHashSet(),
        DayTags = dayTags.Select(tag => tag.Id).ToHashSet()
    };

    private static Trade ClosedTrade(int index, decimal net, decimal? gross = null, decimal? importedRisk = null, decimal? mfePoints = null)
    {
        var entry = new DateTimeOffset(Start.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero).AddMinutes(index * 20);
        return new Trade
        {
            Id = Guid.NewGuid(), ReviewKey = $"key-{index}", Status = "closed", Symbol = "MESZ26", Instrument = "MES", Direction = "Long",
            EntryUtc = entry, ExitUtc = entry.AddMinutes(10), Quantity = 1, ClosedQuantity = 1, PointValue = 5m,
            NetPnl = net, GrossPnl = gross ?? net, InitialRiskCurrency = importedRisk, MfePoints = mfePoints
        };
    }
}

using TradeFoundry.Core;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class MaeReviewMetricsTests
{
    [Fact]
    public void TargetUsesUnroundedPerContractMaeAndShowsPositionTotal()
    {
        var settings = new MaeTargetSettings { DefaultPerContract = 50m };
        var trade = new Trade
        {
            Instrument = "MES",
            Symbol = "MESZ26_FUT_CME",
            Quantity = 2,
            ClosedQuantity = 2,
            PointValue = 5m,
            MaePoints = -10.00002m,
            InitialRiskCurrency = 50m
        };

        var metrics = MaeReviewMetrics.Build(trade, settings);

        Assert.Equal(50.00010m, metrics.MaePerContract);
        Assert.Equal(100.00020m, metrics.PositionMae);
        Assert.Equal(50m, metrics.PlannedRiskCurrency);
        Assert.Equal(2.000004m, metrics.MaeRiskMultiple);
        Assert.True(metrics.IsBreach);
    }

    [Fact]
    public void InstrumentRootOverrideReplacesJournalDefaultAtInclusiveBoundary()
    {
        var settings = new MaeTargetSettings
        {
            DefaultPerContract = 50m,
            InstrumentTargets = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["MNQ"] = 35m }
        };
        var trade = new Trade
        {
            Instrument = "MNQZ26_FUT_CME",
            Symbol = "MNQZ26_FUT_CME",
            Quantity = 1,
            ClosedQuantity = 1,
            PointValue = 2m,
            MaePoints = 17.5m
        };

        var metrics = MaeReviewMetrics.Build(trade, settings);

        Assert.Equal("MNQ", metrics.Instrument);
        Assert.Equal(35m, metrics.TargetPerContract);
        Assert.True(metrics.IsBreach);
    }

    [Fact]
    public void MissingMaeOrRiskRemainsUnavailable()
    {
        var metrics = MaeReviewMetrics.Build(
            new Trade { Instrument = "MES", Symbol = "MESZ26", Quantity = 1, PointValue = 5m },
            new MaeTargetSettings { DefaultPerContract = 50m });

        Assert.Null(metrics.MaePerContract);
        Assert.Null(metrics.PositionMae);
        Assert.Null(metrics.MaeRiskMultiple);
        Assert.Null(metrics.IsBreach);
    }

    [Fact]
    public void CalendarUsesLocalExitDateAndShowsProfitableBreachesAndCoverage()
    {
        var tradeDate = new DateTimeOffset(2026, 9, 16, 1, 5, 0, TimeSpan.Zero);
        var boundaryTrade = new Trade
        {
            Instrument = "MES",
            Symbol = "MESZ26",
            EntryUtc = tradeDate.AddMinutes(-15),
            ExitUtc = tradeDate,
            Status = "closed",
            ReviewKey = "boundary",
            Quantity = 2,
            ClosedQuantity = 2,
            PointValue = 5m,
            MaePoints = 10m,
            NetPnl = 25m,
            InitialRiskCurrency = 200m
        };
        var overrideTrade = new Trade
        {
            Instrument = "MNQ",
            Symbol = "MNQZ26",
            EntryUtc = tradeDate.AddMinutes(-10),
            ExitUtc = tradeDate.AddMinutes(1),
            Status = "closed",
            ReviewKey = "override",
            Quantity = 1,
            ClosedQuantity = 1,
            PointValue = 2m,
            MaePoints = 18m,
            NetPnl = -36m
        };
        var withinTargetTrade = new Trade
        {
            Instrument = "MES",
            Symbol = "MESZ26",
            EntryUtc = tradeDate.AddMinutes(-5),
            ExitUtc = tradeDate.AddMinutes(2),
            Status = "closed",
            ReviewKey = "within",
            Quantity = 1,
            ClosedQuantity = 1,
            PointValue = 5m,
            MaePoints = 8m,
            NetPnl = 40m
        };
        var missingMaeTrade = new Trade
        {
            Instrument = "MES",
            Symbol = "MESZ26",
            EntryUtc = tradeDate.AddDays(1),
            ExitUtc = tradeDate.AddDays(1).AddMinutes(2),
            Status = "closed",
            ReviewKey = "missing",
            Quantity = 1,
            PointValue = 5m,
            NetPnl = 1m
        };
        var annotations = new Dictionary<string, TradeReviewAnnotation>(StringComparer.Ordinal)
        {
            ["boundary"] = new() { ReviewKey = "boundary", Setup = "Opening range", PlannedRiskCurrency = 50m }
        };
        var targets = new MaeTargetSettings
        {
            DefaultPerContract = 50m,
            InstrumentTargets = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["MNQ"] = 35m }
        };

        var month = Assert.Single(MaeCalendarBuilder.Build(
            [boundaryTrade, overrideTrade, withinTargetTrade, missingMaeTrade],
            "America/New_York",
            targets,
            annotations));
        var day = month.Days.Single(item => item.Date == "2026-09-15");
        var missingMaeDay = month.Days.Single(item => item.Date == "2026-09-16");

        Assert.Equal("2026-09", month.Key);
        Assert.Equal(4, month.TradeCount);
        Assert.Equal(3, month.MaeObservedTradeCount);
        Assert.Equal(4, month.TargetConfiguredTradeCount);
        Assert.Equal(3, month.TargetEvaluatedTradeCount);
        Assert.Equal(2, month.BreachCount);
        Assert.Equal(50m, month.WorstMaePerContract);
        Assert.Equal(2, day.BreachCount);
        Assert.Equal(3, day.TargetConfiguredTradeCount);
        Assert.Equal("boundary", day.WorstTradeReviewKey);
        Assert.Contains(month.Breaches, item => item.ReviewKey == "boundary" && item.Date == "2026-09-15" && item.NetPnl > 0m && item.PlannedRiskCurrency == 50m && item.MaeRiskMultiple == 2m && item.Setup == "Opening range");
        Assert.Contains(month.Breaches, item => item.ReviewKey == "override" && item.TargetPerContract == 35m);
        Assert.Equal(100m, day.WorstPositionMae);
        Assert.Equal(1, missingMaeDay.TradeCount);
        Assert.Equal(0, missingMaeDay.MaeObservedTradeCount);
        Assert.Equal(1, missingMaeDay.TargetConfiguredTradeCount);
        Assert.Equal(0, missingMaeDay.TargetEvaluatedTradeCount);
    }

    [Fact]
    public void MissingTargetDoesNotTurnObservedMaeIntoACompliantTrade()
    {
        var trade = new Trade
        {
            Instrument = "MES",
            Symbol = "MESZ26",
            EntryUtc = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero),
            ExitUtc = new DateTimeOffset(2026, 9, 10, 12, 1, 0, TimeSpan.Zero),
            Status = "closed",
            ReviewKey = "no-target",
            Quantity = 1,
            PointValue = 5m,
            MaePoints = 12m
        };

        var month = Assert.Single(MaeCalendarBuilder.Build([trade], "UTC", new MaeTargetSettings()));

        Assert.Equal(60m, month.WorstMaePerContract);
        Assert.Equal(0, month.TargetEvaluatedTradeCount);
        Assert.Equal(0, month.TargetConfiguredTradeCount);
        Assert.Equal(0, month.BreachCount);
        Assert.Empty(month.Breaches);
    }
}

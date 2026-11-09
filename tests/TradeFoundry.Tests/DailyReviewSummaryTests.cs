using TradeFoundry.Core;
using Xunit;

namespace TradeFoundry.Tests;

public class DailyReviewSummaryTests
{
    private static DailyReviewTrade Trade(decimal pnl, int sequence, int exitMinute, decimal? mae = null) => new()
    {
        Trade = new Trade { Id = Guid.NewGuid(), NetPnl = pnl, Sequence = sequence,
            ExitUtc = new DateTimeOffset(2026, 11, 8, 15, exitMinute, 0, TimeSpan.Zero),
            Quantity = 2, ClosedQuantity = 2, PointValue = 5, MaePoints = mae }
    };

    [Fact]
    public void EquityUsesExitOrderAndDrawdownIncludesInitialZero()
    {
        var day = new DailyReviewModel { CompletedTrades = new[] { Trade(-40, 3, 3), Trade(100, 2, 2), Trade(-20, 1, 1, 3) } };
        var summary = day.Summary;
        Assert.Equal(new decimal[] { 0, -20, 80, 40 }, summary.Equity);
        Assert.Equal(40, summary.RealizedDrawdown);
        Assert.Equal(40, summary.RealizedNetPnl);
        Assert.Equal(30, summary.WorstMae);
        Assert.Equal(1, summary.MaeCoverage);
    }

    [Fact]
    public void EquityBreaksExitTiesBySequenceThenId()
    {
        var first = new DailyReviewTrade { Trade = new Trade { Id = Guid.Empty, Sequence = 1, NetPnl = -10 } };
        var second = new DailyReviewTrade { Trade = new Trade { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Sequence = 1, NetPnl = 20 } };
        var third = new DailyReviewTrade { Trade = new Trade { Sequence = 2, NetPnl = -5 } };
        Assert.Equal(new decimal[] { 0, -10, 10, 5 }, new DailyReviewModel { CompletedTrades = new[] { third, second, first } }.Summary.Equity);
    }

    [Fact]
    public void ProgressCountsContentOnceAndClearingRemovesQualification()
    {
        var annotated = new DailyReviewTrade { Annotation = new TradeReviewAnnotation { AllInCommission = 0, Tags = new[] { "tag" } }, Attachments = new[] { new TradeReviewAttachment() } };
        var assessed = new DailyReviewTrade { SetupWorkspace = new TradeSetupWorkspace { Evaluations = new[] { new TradeSetupEvaluation { Criteria = new[] { new TradeSetupCriterionEvaluationView { EvaluationState = CriterionEvaluationState.NotApplicable } } } } } };
        var note = new DailyReviewTrade { SetupWorkspace = new TradeSetupWorkspace { Evaluations = new[] { new TradeSetupEvaluation { Criteria = new[] { new TradeSetupCriterionEvaluationView { Note = "Context" } } } } } };
        var untouched = new DailyReviewTrade { SetupWorkspace = new TradeSetupWorkspace { Evaluations = new[] { new TradeSetupEvaluation { Criteria = new[] { new TradeSetupCriterionEvaluationView() } } } } };
        var day = new DailyReviewModel { CompletedTrades = new[] { annotated, assessed, note, untouched }, OpenTrades = new[] { new DailyReviewTrade { Annotation = new TradeReviewAnnotation { ReviewNote = "Reviewed" } } } };
        Assert.Equal(4, day.Summary.ReviewedCount);
        Assert.Equal(5, day.Summary.TotalCount);
        Assert.Equal(0, new DailyReviewModel { CompletedTrades = new[] { untouched } }.Summary.ReviewedCount);
    }

    [Fact]
    public void EmptyAndOpenOnlyDaysHaveNoRealizedMetricsAndJournalImagesCount()
    {
        var empty = new DailyReviewModel().Summary;
        Assert.Equal(0, empty.TotalCount);
        Assert.Null(empty.WorstMae);
        Assert.Equal(new decimal[] { 0 }, empty.Equity);
        var open = new DailyReviewModel { OpenTrades = new[] { Trade(100, 1, 1, 5) }, DailyJournal = new DailyJournalEntry { Text = """{"root":{"children":[{"type":"daily-journal-image"}]}}""" } }.Summary;
        Assert.Equal(0, open.RealizedNetPnl);
        Assert.Equal(0, open.RealizedDrawdown);
        Assert.Null(open.WorstMae);
        Assert.True(open.HasDailyJournal);
        Assert.False(new DailyReviewModel { DailyJournal = new DailyJournalEntry { Text = """{"root":{"children":[{"type":"paragraph","children":[]}]}}""" } }.Summary.HasDailyJournal);
    }
}

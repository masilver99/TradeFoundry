namespace TradeFoundry.Core;

public sealed class DailyReviewSummary
{
    public decimal RealizedNetPnl { get; init; }
    public decimal RealizedDrawdown { get; init; }
    public decimal? WorstMae { get; init; }
    public int MaeCoverage { get; init; }
    public int CompletedCount { get; init; }
    public int OpenCount { get; init; }
    public int ReviewedCount { get; init; }
    public int TotalCount => CompletedCount + OpenCount;
    public bool HasDailyJournal { get; init; }
    public IReadOnlyList<decimal> Equity { get; init; } = Array.Empty<decimal>();

    public static DailyReviewSummary Build(DailyReviewModel day)
    {
        var equity = new List<decimal> { 0m };
        decimal peak = 0m, drawdown = 0m;
        foreach (var item in day.CompletedTrades.OrderBy(x => x.Trade.ExitUtc).ThenBy(x => x.Trade.Sequence).ThenBy(x => x.Trade.Id))
        {
            var value = equity[^1] + item.Trade.NetPnl;
            equity.Add(value);
            peak = Math.Max(peak, value);
            drawdown = Math.Max(drawdown, peak - value);
        }
        var trades = day.CompletedTrades.Select(x => x.Trade).ToArray();
        return new DailyReviewSummary
        {
            RealizedNetPnl = day.RealizedNetPnl,
            RealizedDrawdown = drawdown,
            WorstMae = new DailyTradeSummary { CompletedTrades = trades }.RealizedMaeCurrency,
            MaeCoverage = trades.Count(x => x.MaePoints.HasValue),
            CompletedCount = day.CompletedTradeCount,
            OpenCount = day.OpenTradeCount,
            ReviewedCount = day.CompletedTrades.Concat(day.OpenTrades).Count(x => x.Annotation.HasContent
                || x.Attachments.Count > 0
                || x.SetupWorkspace.Evaluations.Any(e => e.Criteria.Any(c => c.EvaluationState != CriterionEvaluationState.Unknown || !string.IsNullOrWhiteSpace(c.Note)))),
            HasDailyJournal = !string.IsNullOrWhiteSpace(LexicalPlainText.Extract(day.DailyJournal.Text)),
            Equity = equity
        };
    }
}

namespace TradeFoundry.Core;

public sealed record PatternDiscoverySnapshot(
    DateTimeOffset ScannedUtc,
    IReadOnlyList<PatternInstrumentReport> Instruments);

public sealed record PatternInstrumentReport(
    string Instrument, int TradeCount, int BarCount, int AtrCount, int StopCount,
    DateOnly FirstDate, DateOnly LastDate, DateOnly? ValidationStart,
    decimal? MedianAtrPoints, decimal? MedianMfeAtr, int MfeCount, PatternManagementCoverage Management,
    IReadOnlyList<PatternFinding> Findings);

public sealed record PatternFinding(
    string Key, string Instrument, IReadOnlyList<string> Criteria, bool Management,
    int Direction, bool Weakened, PatternCohort Discovery, PatternCohort Validation,
    PatternCohort Recent, IReadOnlyList<PatternTradeExample> Examples);

public sealed record PatternCohort(
    int Trades, int Days, int ComparisonTrades, decimal? NetPointsPerContract,
    decimal? MatchedLift, decimal? NetPnl, decimal? LiftWithoutStrongestDay = null, decimal? LiftWithoutWeakestDay = null);

public sealed record PatternManagementCoverage(
    int Targets, int BreakEvenEligible, int BreakEvenReached, int TrailingEligible, int TrailingAdjusted, int NativeTrailing);

public sealed record PatternTradeExample(string ReviewKey, DateOnly ReviewDate, decimal NetPnl);

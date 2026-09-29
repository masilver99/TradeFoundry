namespace TradeFoundry.Core;

/// <summary>
/// Raw, versioned daily market features persisted independently from the
/// classifier.  Keeping the raw feature row separate means classifier rules
/// can evolve without rewriting imported bars or making old rows ambiguous.
/// </summary>
public sealed record PersistedMarketDayFeature
{
    public string FeatureVersion { get; init; } = string.Empty;
    public string SourceInterval { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public DateOnly TradeDate { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public decimal RthRangePoints { get; init; }
    public decimal? Atr20 { get; init; }
    public decimal? NormalizedRange { get; init; }
    public decimal OpenToClosePoints { get; init; }
    public decimal DirectionalEfficiency { get; init; }
    public decimal PathEfficiency { get; init; }
    public decimal? CloseLocation { get; init; }
    public int VwapCrossings { get; init; }
    public decimal? PercentSessionAboveVwap { get; init; }
    public decimal? PercentSessionBelowVwap { get; init; }
    public decimal? PercentHigherHighs { get; init; }
    public decimal? PercentHigherLows { get; init; }
    public decimal? PercentLowerHighs { get; init; }
    public decimal? PercentLowerLows { get; init; }
    public decimal? MaximumFavorableDirectionalExcursion { get; init; }
    public decimal? MaximumCountertrendExcursion { get; init; }
    public decimal? OvernightHigh { get; init; }
    public decimal? OvernightLow { get; init; }
    public decimal? OvernightRange { get; init; }
    public string OvernightDirection { get; init; } = "Neutral";
    public decimal? GapFromPriorRthClose { get; init; }
    public string GapDirection { get; init; } = "Neutral";
    public decimal? VolatilityMeasurePoints { get; init; }
    public string Direction { get; init; } = "Neutral";
    public int RthBarCount { get; init; }
    public int OvernightBarCount { get; init; }
}

/// <summary>
/// A bar import can affect more than the exact imported session: ATR and
/// trailing volatility are intentionally rebuilt through a warm-up/propagation
/// window.  These ranges are derived-cache metadata, not imported evidence.
/// </summary>
public sealed record MarketFeatureDirtyRange
{
    public string Symbol { get; init; } = string.Empty;
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
}

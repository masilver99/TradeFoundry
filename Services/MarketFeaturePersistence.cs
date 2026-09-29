using TradeFoundry.Core;

namespace TradeFoundry.Services;

internal static class MarketFeaturePersistence
{
    public static PersistedMarketDayFeature ToRow(MarketDayFeature feature, string sourceInterval, string featureVersion) => new()
    {
        FeatureVersion = featureVersion,
        SourceInterval = sourceInterval,
        Symbol = feature.Symbol,
        TradeDate = feature.TradeDate,
        Open = feature.Open,
        High = feature.High,
        Low = feature.Low,
        Close = feature.Close,
        RthRangePoints = feature.RthRangePoints,
        Atr20 = feature.Atr20,
        NormalizedRange = feature.NormalizedRange,
        OpenToClosePoints = feature.OpenToClosePoints,
        DirectionalEfficiency = feature.DirectionalEfficiency,
        PathEfficiency = feature.PathEfficiency,
        CloseLocation = feature.CloseLocation,
        VwapCrossings = feature.VwapCrossings,
        PercentSessionAboveVwap = feature.PercentSessionAboveVwap,
        PercentSessionBelowVwap = feature.PercentSessionBelowVwap,
        PercentHigherHighs = feature.PercentHigherHighs,
        PercentHigherLows = feature.PercentHigherLows,
        PercentLowerHighs = feature.PercentLowerHighs,
        PercentLowerLows = feature.PercentLowerLows,
        MaximumFavorableDirectionalExcursion = feature.MaximumFavorableDirectionalExcursion,
        MaximumCountertrendExcursion = feature.MaximumCountertrendExcursion,
        OvernightHigh = feature.OvernightHigh,
        OvernightLow = feature.OvernightLow,
        OvernightRange = feature.OvernightRange,
        OvernightDirection = feature.OvernightDirection.ToString(),
        GapFromPriorRthClose = feature.GapFromPriorRthClose,
        GapDirection = feature.GapDirection.ToString(),
        VolatilityMeasurePoints = feature.VolatilityMeasurePoints,
        Direction = feature.Direction.ToString(),
        RthBarCount = feature.RthBarCount,
        OvernightBarCount = feature.OvernightBarCount
    };

    public static MarketDayFeature FromRow(PersistedMarketDayFeature row) => new()
    {
        TradeDate = row.TradeDate,
        Symbol = row.Symbol,
        Open = row.Open,
        High = row.High,
        Low = row.Low,
        Close = row.Close,
        RthRangePoints = row.RthRangePoints,
        Atr20 = row.Atr20,
        NormalizedRange = row.NormalizedRange,
        OpenToClosePoints = row.OpenToClosePoints,
        DirectionalEfficiency = row.DirectionalEfficiency,
        PathEfficiency = row.PathEfficiency,
        CloseLocation = row.CloseLocation,
        VwapCrossings = row.VwapCrossings,
        PercentSessionAboveVwap = row.PercentSessionAboveVwap,
        PercentSessionBelowVwap = row.PercentSessionBelowVwap,
        PercentHigherHighs = row.PercentHigherHighs,
        PercentHigherLows = row.PercentHigherLows,
        PercentLowerHighs = row.PercentLowerHighs,
        PercentLowerLows = row.PercentLowerLows,
        MaximumFavorableDirectionalExcursion = row.MaximumFavorableDirectionalExcursion,
        MaximumCountertrendExcursion = row.MaximumCountertrendExcursion,
        OvernightHigh = row.OvernightHigh,
        OvernightLow = row.OvernightLow,
        OvernightRange = row.OvernightRange,
        OvernightDirection = ParseDirection(row.OvernightDirection),
        GapFromPriorRthClose = row.GapFromPriorRthClose,
        GapDirection = ParseDirection(row.GapDirection),
        VolatilityMeasurePoints = row.VolatilityMeasurePoints,
        Direction = ParseDirection(row.Direction),
        RthBarCount = row.RthBarCount,
        OvernightBarCount = row.OvernightBarCount
    };

    private static MarketDirection ParseDirection(string value) =>
        Enum.TryParse<MarketDirection>(value, ignoreCase: true, out var direction)
            ? direction
            : MarketDirection.Neutral;
}

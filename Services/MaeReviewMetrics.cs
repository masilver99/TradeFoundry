using TradeFoundry.Core;

namespace TradeFoundry.Services;

public sealed record MaeTradeMetrics(
    string Instrument,
    int ContractCount,
    decimal? MaePerContract,
    decimal? PositionMae,
    decimal? TargetPerContract,
    decimal? PlannedRiskCurrency,
    decimal? MaeRiskMultiple)
{
    public bool? IsBreach => MaePerContract.HasValue && TargetPerContract.HasValue
        ? MaePerContract.Value >= TargetPerContract.Value
        : null;
}

public static class MaeReviewMetrics
{
    public static MaeTradeMetrics Build(
        Trade trade,
        MaeTargetSettings settings,
        decimal? plannedRiskCurrency = null,
        decimal? plannedRiskPoints = null)
    {
        var instrument = string.IsNullOrWhiteSpace(trade.Instrument)
            ? InstrumentCatalog.ExtractRoot(trade.Symbol)
            : InstrumentCatalog.ExtractRoot(trade.Instrument);
        var quantity = Math.Max(1, trade.ClosedQuantity > 0 ? trade.ClosedQuantity : trade.Quantity);
        var pointValue = trade.PointValue > 0m ? trade.PointValue : InstrumentCatalog.Resolve(instrument).PointValue;
        decimal? maePerContract = trade.MaePoints.HasValue
            ? Math.Abs(trade.MaePoints.Value) * pointValue
            : null;
        decimal? positionMae = maePerContract.HasValue ? maePerContract.Value * quantity : null;

        decimal? initialRisk = null;
        if (plannedRiskCurrency is > 0m) initialRisk = plannedRiskCurrency.Value;
        else if (plannedRiskPoints is > 0m) initialRisk = plannedRiskPoints.Value * quantity * pointValue;
        else if (trade.InitialRiskCurrency is > 0m) initialRisk = trade.InitialRiskCurrency.Value;
        else if (trade.InitialRiskPoints is > 0m) initialRisk = trade.InitialRiskPoints.Value * quantity * pointValue;

        decimal? riskMultiple = positionMae.HasValue && initialRisk is > 0m
            ? positionMae.Value / initialRisk.GetValueOrDefault()
            : null;

        return new MaeTradeMetrics(
            instrument,
            quantity,
            maePerContract,
            positionMae,
            settings.Resolve(instrument),
            initialRisk,
            riskMultiple);
    }
}

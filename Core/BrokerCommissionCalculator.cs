namespace TradeFoundry.Core;

/// <summary>
/// A broker's variable fee schedule for one contract on one side of a trade.
/// Fixed charges are monthly amounts and are intentionally kept separate from
/// per-contract costs so the comparison can show both kinds of friction.
/// </summary>
public sealed class BrokerCommissionRate
{
    public decimal? CommissionPerContractSide { get; init; }
    public decimal? ExchangePerContractSide { get; init; }
    public decimal? NfaPerContractSide { get; init; }
    public decimal? ClearingPerContractSide { get; init; }
    public decimal? PlatformMonthly { get; init; }
    public decimal? DataMonthly { get; init; }
    public decimal? OtherMonthly { get; init; }

    public bool HasInput => CommissionPerContractSide.HasValue
        || ExchangePerContractSide.HasValue
        || NfaPerContractSide.HasValue
        || ClearingPerContractSide.HasValue
        || PlatformMonthly.HasValue
        || DataMonthly.HasValue
        || OtherMonthly.HasValue;

    public decimal VariablePerContractSide => (CommissionPerContractSide ?? 0m)
        + (ExchangePerContractSide ?? 0m)
        + (NfaPerContractSide ?? 0m)
        + (ClearingPerContractSide ?? 0m);

    public decimal FixedMonthly => (PlatformMonthly ?? 0m)
        + (DataMonthly ?? 0m)
        + (OtherMonthly ?? 0m);
}

public sealed class BrokerCommissionEstimate
{
    public decimal ContractSidesPerMonth { get; init; }
    public decimal VariablePerContractSide { get; init; }
    public decimal FixedMonthly { get; init; }
    public decimal MonthlyCost { get; init; }
    public decimal AnnualCost => MonthlyCost * 12m;
}

public static class BrokerCommissionCalculator
{
    /// <summary>
    /// Estimates cost from completed round turns and average contracts per
    /// trade. A round turn has two contract sides: entry and exit.
    /// </summary>
    public static BrokerCommissionEstimate Estimate(
        BrokerCommissionRate rate,
        decimal roundTurnsPerMonth,
        decimal contractsPerRoundTurn)
    {
        ArgumentNullException.ThrowIfNull(rate);

        var normalizedRoundTurns = Math.Max(0m, roundTurnsPerMonth);
        var normalizedContracts = Math.Max(0m, contractsPerRoundTurn);
        var contractSides = normalizedRoundTurns * normalizedContracts * 2m;
        var variablePerSide = rate.VariablePerContractSide;
        var fixedMonthly = rate.FixedMonthly;

        return new BrokerCommissionEstimate
        {
            ContractSidesPerMonth = contractSides,
            VariablePerContractSide = variablePerSide,
            FixedMonthly = fixedMonthly,
            MonthlyCost = contractSides * variablePerSide + fixedMonthly
        };
    }
}

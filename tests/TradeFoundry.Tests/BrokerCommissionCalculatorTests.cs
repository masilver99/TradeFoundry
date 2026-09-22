using TradeFoundry.Core;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class BrokerCommissionCalculatorTests
{
    [Fact]
    public void EstimateAppliesPerSideRatesToBothSidesAndAddsMonthlyCharges()
    {
        var rate = new BrokerCommissionRate
        {
            CommissionPerContractSide = 0.45m,
            ExchangePerContractSide = 0.12m,
            NfaPerContractSide = 0.02m,
            ClearingPerContractSide = 0.08m,
            PlatformMonthly = 35m,
            DataMonthly = 15m,
            OtherMonthly = 5m
        };

        var estimate = BrokerCommissionCalculator.Estimate(rate, roundTurnsPerMonth: 20m, contractsPerRoundTurn: 2m);

        Assert.Equal(80m, estimate.ContractSidesPerMonth);
        Assert.Equal(0.67m, estimate.VariablePerContractSide);
        Assert.Equal(55m, estimate.FixedMonthly);
        Assert.Equal(108.60m, estimate.MonthlyCost);
        Assert.Equal(1_303.20m, estimate.AnnualCost);
    }

    [Fact]
    public void NegativeActivityIsClampedToZeroButFixedChargesRemain()
    {
        var rate = new BrokerCommissionRate
        {
            CommissionPerContractSide = 1m,
            PlatformMonthly = 25m
        };

        var estimate = BrokerCommissionCalculator.Estimate(rate, roundTurnsPerMonth: -4m, contractsPerRoundTurn: 2m);

        Assert.Equal(0m, estimate.ContractSidesPerMonth);
        Assert.Equal(25m, estimate.MonthlyCost);
    }
}

namespace TradeFoundry.Core;

public sealed class BrokerFeeProfile
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Instrument { get; init; } = BrokerFeeProfileDraft.AllInstruments;
    public string Notes { get; init; } = string.Empty;
    public decimal? CommissionPerContractSide { get; init; }
    public decimal? ExchangePerContractSide { get; init; }
    public decimal? NfaPerContractSide { get; init; }
    public decimal? ClearingPerContractSide { get; init; }
    public decimal? PlatformMonthly { get; init; }
    public decimal? DataMonthly { get; init; }
    public decimal? OtherMonthly { get; init; }
    public int Revision { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }

    public BrokerCommissionRate ToRate() => new()
    {
        CommissionPerContractSide = CommissionPerContractSide,
        ExchangePerContractSide = ExchangePerContractSide,
        NfaPerContractSide = NfaPerContractSide,
        ClearingPerContractSide = ClearingPerContractSide,
        PlatformMonthly = PlatformMonthly,
        DataMonthly = DataMonthly,
        OtherMonthly = OtherMonthly
    };
}

public sealed class BrokerFeeProfileDraft
{
    public const string AllInstruments = "*";

    public string Name { get; init; } = string.Empty;
    public string Instrument { get; init; } = AllInstruments;
    public string Notes { get; init; } = string.Empty;
    public decimal? CommissionPerContractSide { get; init; }
    public decimal? ExchangePerContractSide { get; init; }
    public decimal? NfaPerContractSide { get; init; }
    public decimal? ClearingPerContractSide { get; init; }
    public decimal? PlatformMonthly { get; init; }
    public decimal? DataMonthly { get; init; }
    public decimal? OtherMonthly { get; init; }

    public BrokerCommissionRate ToRate() => new()
    {
        CommissionPerContractSide = CommissionPerContractSide,
        ExchangePerContractSide = ExchangePerContractSide,
        NfaPerContractSide = NfaPerContractSide,
        ClearingPerContractSide = ClearingPerContractSide,
        PlatformMonthly = PlatformMonthly,
        DataMonthly = DataMonthly,
        OtherMonthly = OtherMonthly
    };
}

public sealed class BrokerFeeProfileMutationResult
{
    public bool Saved { get; init; }
    public bool Conflict { get; init; }
    public bool NotFound { get; init; }
    public BrokerFeeProfile? Profile { get; init; }
}

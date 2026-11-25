namespace TradeFoundry.Core;

/// <summary>Which statement a broker produced: one trading day, or a calendar month.</summary>
public enum BrokerStatementKind
{
    Daily,
    Monthly
}

/// <summary>The fee buckets a statement fee line is reconciled into, matching the trade fee components.</summary>
public static class StatementFeeCategory
{
    public const string Exchange = "exchange";
    public const string Nfa = "nfa";
    /// <summary>Clearing and commission: everything that is not an exchange or NFA fee.</summary>
    public const string Clearing = "clearing";

    public static string Classify(string name)
    {
        var upper = name.ToUpperInvariant();
        if (upper.Contains("EXCHANGE", StringComparison.Ordinal) || upper.Contains("EXCH", StringComparison.Ordinal)) return Exchange;
        if (upper.Contains("NFA", StringComparison.Ordinal)) return Nfa;
        return Clearing;
    }
}

/// <summary>One fill from a statement's trade confirmations.</summary>
public sealed record StatementFill(DateOnly TradeDate, string TradeNumber, string Market, string Side, int Quantity, string Contract, decimal Price, string Currency);

/// <summary>One fee line as the statement printed it. A credit is stored as a negative amount.</summary>
public sealed record StatementFee(string Name, string Category, decimal Amount, string Currency);

/// <summary>Everything a statement says about one trading day.</summary>
public sealed class StatementDay
{
    public DateOnly TradeDate { get; init; }
    public int BuyContracts { get; set; }
    public int SellContracts { get; set; }
    /// <summary>Realized P&amp;L before fees from the purchase &amp; sale section; positive is a credit.</summary>
    public decimal RealizedPnl { get; set; }
    public bool HasRealizedPnl { get; set; }
    public List<StatementFee> Fees { get; init; } = [];
    public List<StatementFill> Fills { get; init; } = [];

    public int ContractSides => BuyContracts + SellContracts;
    public decimal ExchangeFees => Fees.Where(f => f.Category == StatementFeeCategory.Exchange).Sum(f => f.Amount);
    public decimal NfaFees => Fees.Where(f => f.Category == StatementFeeCategory.Nfa).Sum(f => f.Amount);
    public decimal ClearingFees => Fees.Where(f => f.Category == StatementFeeCategory.Clearing).Sum(f => f.Amount);
    public decimal TotalFees => Fees.Sum(f => f.Amount);
}

/// <summary>The account summary block. Values are signed: credits positive, debits negative, except fees which are a positive cost.</summary>
public sealed record StatementAccountSummary(decimal? OpeningCashBalance, decimal? RealizedPnl, decimal? TotalFees, decimal? NewCashBalance, decimal? NetLiquidatingValue);

/// <summary>A broker statement read from its PDF, before it is stored.</summary>
public sealed class ParsedBrokerStatement
{
    public string Broker { get; init; } = "AMP";
    public BrokerStatementKind Kind { get; init; }
    public string Account { get; init; } = string.Empty;
    public DateOnly PeriodStart { get; init; }
    public DateOnly PeriodEnd { get; init; }
    public IReadOnlyList<StatementDay> Days { get; init; } = [];
    public StatementAccountSummary Summary { get; init; } = new(null, null, null, null, null);
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>A stored statement, as listed on the reconciliation page.</summary>
public sealed record BrokerStatement(
    Guid Id,
    Guid JournalId,
    string Broker,
    BrokerStatementKind Kind,
    string Account,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string FileName,
    string ContentHash,
    StatementAccountSummary Summary,
    IReadOnlyList<string> Warnings,
    int DayCount,
    DateTimeOffset ImportedUtc);

/// <summary>A stored statement day, with the statement it came from.</summary>
/// <param name="StatementHasWarnings">The statement did not read cleanly, so its figures are shown but never applied.</param>
public sealed record BrokerStatementDayRecord(Guid StatementId, BrokerStatementKind Kind, string FileName, string Account, bool StatementHasWarnings, DateTimeOffset ImportedUtc, StatementDay Day);

/// <summary>
/// New fee components for one trade's review. <paramref name="ExpectedRevision"/> is the review revision the
/// fees were calculated against; if the review has changed since, nothing is written.
/// </summary>
public sealed record TradeFeeUpdate(string ReviewKey, int ExpectedRevision, decimal ExchangeFees, decimal NfaFees, decimal ClearingFees, string Reason);

/// <summary>A trade review changed after statement fees were calculated for it, so none of the batch was saved.</summary>
public sealed class StatementFeeConflictException(string message) : InvalidOperationException(message);

/// <summary>The statement could not be read as a supported broker statement.</summary>
public sealed class StatementParseException(string message) : InvalidOperationException(message);

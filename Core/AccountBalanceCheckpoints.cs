using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TradeFoundry.Core;

/// <summary>
/// Chooses the broker balance evidence the account views compare against. Imported broker statements are
/// the authority: each one gives the account's cash balance at the close of its last trading day, and the
/// earliest one also gives the cash balance it opened with. Balances read from a trading-platform export
/// (Sierra Chart's account-balance rows) are estimates that can include open-position value, so they are
/// used only when no statement gives a usable balance.
/// </summary>
public static class AccountBalanceCheckpoints
{
    public const string StatementSourceType = "broker_statement";

    private static readonly TimeZoneInfo ExchangeTimeZone = TimeZoneCatalog.Resolve("America/Chicago");

    public static bool IsStatement(AccountBalanceEvent checkpoint) => checkpoint.SourceType == StatementSourceType;

    /// <summary>Statement checkpoints when there are any, otherwise the imported platform balances.</summary>
    public static IReadOnlyList<AccountBalanceEvent> Select(IEnumerable<BrokerStatement> statements, IEnumerable<AccountBalanceEvent> imported)
    {
        var fromStatements = FromStatements(statements);
        return fromStatements.Count > 0 ? fromStatements : imported.ToArray();
    }

    /// <summary>
    /// One checkpoint per statement close (its new cash balance), plus the opening cash balance of the earliest
    /// statement. A cash balance is per account, so statements for more than one account give no journal-wide
    /// balance and produce nothing. Where a daily and a monthly statement close on the same day, the monthly one
    /// wins, because it is issued after the month closes and carries any corrections.
    /// </summary>
    public static IReadOnlyList<AccountBalanceEvent> FromStatements(IEnumerable<BrokerStatement> statements)
    {
        var all = statements.ToArray();
        if (all.Select(s => s.Account).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1) return [];

        var checkpoints = new List<AccountBalanceEvent>();
        if (all.OrderBy(s => s.PeriodStart).ThenByDescending(s => s.Kind == BrokerStatementKind.Monthly).ThenByDescending(s => s.ImportedUtc).FirstOrDefault() is { Summary.OpeningCashBalance: { } opening } first)
        {
            checkpoints.Add(Checkpoint(first, "opening", SessionStartUtc(first.PeriodStart), opening, 0,
                $"Opening cash balance on {Describe(first)}"));
        }

        var closes = all
            .Where(s => s.Summary.NewCashBalance.HasValue)
            .GroupBy(s => s.PeriodEnd)
            .Select(g => g.OrderByDescending(s => s.Kind == BrokerStatementKind.Monthly).ThenByDescending(s => s.ImportedUtc).First());
        foreach (var statement in closes)
        {
            checkpoints.Add(Checkpoint(statement, "close", SessionCloseUtc(statement.PeriodEnd), statement.Summary.NewCashBalance!.Value, 1,
                $"New cash balance on {Describe(statement)}"));
        }

        return checkpoints.OrderBy(c => c.EventUtc).ThenBy(c => c.RowNumber).ToArray();
    }

    /// <summary>
    /// The end of a CME trade date: 5 p.m. Chicago, when the next day's session opens. Trades that exit
    /// before it belong to that trade date, matching how statements are reconciled.
    /// </summary>
    public static DateTimeOffset SessionCloseUtc(DateOnly tradeDate)
    {
        var local = tradeDate.ToDateTime(new TimeOnly(17, 0));
        return new DateTimeOffset(local, ExchangeTimeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>The start of a CME trade date: the previous calendar day's session close.</summary>
    public static DateTimeOffset SessionStartUtc(DateOnly tradeDate) => SessionCloseUtc(tradeDate.AddDays(-1));

    private static string Describe(BrokerStatement statement)
    {
        var kind = statement.Kind == BrokerStatementKind.Daily ? "daily" : "monthly";
        var period = statement.Kind == BrokerStatementKind.Daily
            ? statement.PeriodStart.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
            : $"{statement.PeriodStart.ToString("MMM d", CultureInfo.InvariantCulture)} – {statement.PeriodEnd.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}";
        return $"the {statement.Broker} {kind} statement for {period}";
    }

    private static AccountBalanceEvent Checkpoint(BrokerStatement statement, string point, DateTimeOffset eventUtc, decimal balance, int rowNumber, string note) => new()
    {
        Id = StableId(statement.Id, point),
        JournalId = statement.JournalId,
        SourceType = StatementSourceType,
        SourceKey = $"{statement.Id:D}:{point}",
        EventUtc = eventUtc,
        SourceTimeText = statement.FileName,
        Account = statement.Account,
        Balance = balance,
        Note = note,
        RowNumber = rowNumber
    };

    private static Guid StableId(Guid statementId, string point) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{statementId:D}:{point}")).AsSpan(0, 16));
}

using System.Globalization;
using System.Security.Cryptography;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services.Statements;

public enum ReconciliationStatus
{
    /// <summary>Contracts, realized P&amp;L and fees all agree.</summary>
    Matched,
    /// <summary>Contracts and P&amp;L agree; only the fees differ, so the statement fees can be applied.</summary>
    FeesDiffer,
    /// <summary>The contract count or realized P&amp;L differs: a trade is missing, extra, or priced differently.</summary>
    TradesDiffer,
    /// <summary>The statement shows trading the journal does not have.</summary>
    MissingFromJournal,
    /// <summary>The journal has trades on a day the statement covers but shows no trading.</summary>
    NotOnStatement,
    /// <summary>Two imported statements give different figures for the same day.</summary>
    StatementsDisagree
}

/// <summary>A trade's share of a statement day's fees, split by contract sides.</summary>
public sealed record ReconciliationTradeShare(Trade Trade, int ContractSides, decimal ExchangeFees, decimal NfaFees, decimal ClearingFees)
{
    public decimal TotalFees => ExchangeFees + NfaFees + ClearingFees;
}

public sealed class ReconciliationDay
{
    public DateOnly Date { get; init; }
    public BrokerStatementDayRecord? Source { get; init; }
    public IReadOnlyList<BrokerStatementDayRecord> OtherSources { get; init; } = [];
    public IReadOnlyList<Trade> Trades { get; init; } = [];
    public IReadOnlyList<ReconciliationTradeShare> Shares { get; init; } = [];
    public int JournalContractSides { get; init; }
    public decimal JournalGrossPnl { get; init; }
    public decimal JournalExchangeFees { get; init; }
    public decimal JournalNfaFees { get; init; }
    public decimal JournalClearingFees { get; init; }
    public decimal JournalFees => JournalExchangeFees + JournalNfaFees + JournalClearingFees;
    public ReconciliationStatus Status { get; init; }
    public bool CanApplyFees { get; init; }

    public StatementDay? Statement => Source?.Day;
    public int StatementContractSides => Statement?.ContractSides ?? 0;
    public decimal StatementGrossPnl => Statement?.RealizedPnl ?? 0m;
    public decimal StatementFees => Statement?.TotalFees ?? 0m;
    /// <summary>Journal fees minus statement fees: positive means the journal over-counts fees.</summary>
    public decimal FeeDifference => JournalFees - StatementFees;
    public decimal PnlDifference => JournalGrossPnl - StatementGrossPnl;
    public decimal NetDifference => (JournalGrossPnl - JournalFees) - (StatementGrossPnl - StatementFees);
}

public sealed class ReconciliationReport
{
    public Journal Journal { get; init; } = null!;
    public IReadOnlyList<BrokerStatement> Statements { get; init; } = [];
    public IReadOnlyList<ReconciliationDay> Days { get; init; } = [];
    /// <summary>Journal trading days that no imported statement covers.</summary>
    public IReadOnlyList<DateOnly> UncoveredTradeDates { get; init; } = [];

    public int Count(ReconciliationStatus status) => Days.Count(d => d.Status == status);
    public IEnumerable<ReconciliationDay> FeeOnlyDifferences => Days.Where(d => d.Status == ReconciliationStatus.FeesDiffer && d.CanApplyFees);
    public decimal TotalFeeDifference => Days.Where(d => d.Source is not null && d.Trades.Count > 0).Sum(d => d.FeeDifference);
}

public sealed record StatementImportOutcome(string FileName, bool Imported, bool Duplicate, string Message, BrokerStatement? Statement);

/// <summary>
/// Compares imported broker statements with the journal one trading day at a time, and writes the
/// statement's fees onto the day's trades through the review layer when the trades themselves agree.
/// </summary>
public sealed class StatementReconciliationService(TradeFoundryDb database)
{
    public const long MaxStatementLength = 10 * 1024 * 1024;
    private const decimal Tolerance = 0.005m;
    private static readonly TimeZoneInfo ExchangeTimeZone = TimeZoneCatalog.Resolve("America/Chicago");

    public async Task<StatementImportOutcome> ImportAsync(Guid journalId, Stream content, string fileName, long declaredLength, CancellationToken cancellationToken = default)
    {
        var safeName = SafeFileName(fileName);
        if (declaredLength <= 0 || declaredLength > MaxStatementLength)
            return new(safeName, false, false, "Statements must be between 1 byte and 10 MB.", null);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > MaxStatementLength) return new(safeName, false, false, "Statements must be between 1 byte and 10 MB.", null);
        var bytes = buffer.ToArray();
        if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            return new(safeName, false, false, "This file is not a PDF.", null);

        ParsedBrokerStatement parsed;
        try
        {
            parsed = AmpStatementParser.Parse(StatementText.ReadPdf(bytes));
        }
        catch (StatementParseException e)
        {
            return new(safeName, false, false, e.Message, null);
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var (statement, duplicate) = database.ImportBrokerStatement(journalId, parsed, safeName, hash);
        var period = statement.Kind == BrokerStatementKind.Daily
            ? statement.PeriodStart.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
            : $"{statement.PeriodStart:MMM d} – {statement.PeriodEnd:MMM d, yyyy}";
        return duplicate
            ? new(safeName, false, true, $"Already imported ({statement.Kind.ToString().ToLowerInvariant()} statement, {period}).", statement)
            : new(safeName, true, false, $"Imported {statement.Kind.ToString().ToLowerInvariant()} statement for {period}: {statement.DayCount} trading day(s).", statement);
    }

    /// <summary>The CME trade date of a trade's exit: the session that opens at 5 p.m. Chicago belongs to the next day.</summary>
    public static DateOnly ExchangeTradeDate(Trade trade)
    {
        var local = TimeZoneInfo.ConvertTime(trade.ExitUtc ?? trade.EntryUtc, ExchangeTimeZone);
        var date = DateOnly.FromDateTime(local.DateTime);
        return local.Hour >= 17 ? date.AddDays(1) : date;
    }

    public ReconciliationReport BuildReport(Guid journalId)
    {
        var journal = database.GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        var statements = database.GetBrokerStatements(journalId);
        var statementDays = database.GetBrokerStatementDays(journalId).GroupBy(d => d.Day.TradeDate).ToDictionary(g => g.Key, g => g.ToArray());
        var trades = database.GetAllTrades(journalId)
            .Where(t => t.ExitUtc.HasValue && t.Status.Equals("closed", StringComparison.OrdinalIgnoreCase))
            .GroupBy(ExchangeTradeDate)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.ExitUtc).ThenBy(t => t.Sequence).ThenBy(t => t.Id).ToArray());

        bool Covered(DateOnly date) => statements.Any(s => s.PeriodStart <= date && date <= s.PeriodEnd);

        var dates = statementDays.Keys.Concat(trades.Keys.Where(Covered)).Distinct().Order();
        var days = new List<ReconciliationDay>();
        foreach (var date in dates)
        {
            statementDays.TryGetValue(date, out var records);
            trades.TryGetValue(date, out var dayTrades);
            days.Add(BuildDay(date, records ?? [], dayTrades ?? []));
        }

        return new ReconciliationReport
        {
            Journal = journal,
            Statements = statements,
            Days = days,
            UncoveredTradeDates = trades.Keys.Where(d => !Covered(d)).Order().ToArray()
        };
    }

    /// <summary>Contracts bought plus sold. A completed trade closes everything it opened, so that is twice the closed quantity.</summary>
    public static int SidesOf(Trade trade) => 2 * (trade.ClosedQuantity > 0 ? trade.ClosedQuantity : trade.Quantity);

    private static ReconciliationDay BuildDay(DateOnly date, IReadOnlyList<BrokerStatementDayRecord> records, IReadOnlyList<Trade> trades)
    {
        // A monthly statement is issued after the month closes and carries any corrections, so it wins over a daily one.
        var ordered = records.OrderByDescending(r => r.Kind == BrokerStatementKind.Monthly).ThenByDescending(r => r.ImportedUtc).ToArray();
        var source = ordered.FirstOrDefault(r => r.Day.ContractSides > 0 || r.Day.Fees.Count > 0 || r.Day.HasRealizedPnl);
        var others = ordered.Where(r => r != source).ToArray();
        var journalSides = trades.Sum(SidesOf);
        var gross = trades.Sum(t => t.GrossPnl);

        var disagree = source is not null && others.Any(o => o.Day.ContractSides != source.Day.ContractSides
            || Math.Abs(o.Day.TotalFees - source.Day.TotalFees) > Tolerance
            || Math.Abs(o.Day.RealizedPnl - source.Day.RealizedPnl) > Tolerance);

        ReconciliationStatus status;
        var canApply = false;
        var shares = Array.Empty<ReconciliationTradeShare>();
        if (source is null) status = ReconciliationStatus.NotOnStatement;
        else if (trades.Count == 0) status = ReconciliationStatus.MissingFromJournal;
        else
        {
            var statement = source.Day;
            var sidesMatch = statement.ContractSides == journalSides;
            var pnlMatch = Math.Abs(statement.RealizedPnl - gross) <= Tolerance;
            var feesMatch = Math.Abs(statement.TotalFees - trades.Sum(t => t.Fees)) <= Tolerance;
            if (sidesMatch) shares = Allocate(statement, trades);
            status = disagree ? ReconciliationStatus.StatementsDisagree
                : !sidesMatch || !pnlMatch ? ReconciliationStatus.TradesDiffer
                : !feesMatch ? ReconciliationStatus.FeesDiffer
                : ReconciliationStatus.Matched;
            canApply = status is ReconciliationStatus.FeesDiffer or ReconciliationStatus.TradesDiffer
                && sidesMatch
                && trades.All(t => t.ReviewKey.Length > 0)
                && shares.Any(s => !SameFees(s));
        }

        return new ReconciliationDay
        {
            Date = date,
            Source = source,
            OtherSources = others,
            Trades = trades,
            Shares = shares,
            JournalContractSides = journalSides,
            JournalGrossPnl = gross,
            JournalExchangeFees = trades.Sum(t => t.ExchangeFees),
            JournalNfaFees = trades.Sum(t => t.NfaFees),
            JournalClearingFees = trades.Sum(t => t.Fees - t.ExchangeFees - t.NfaFees),
            Status = status,
            CanApplyFees = canApply
        };
    }

    private static bool SameFees(ReconciliationTradeShare share) =>
        Math.Abs(share.Trade.ExchangeFees - share.ExchangeFees) <= Tolerance
        && Math.Abs(share.Trade.NfaFees - share.NfaFees) <= Tolerance
        && Math.Abs(share.Trade.Fees - share.TotalFees) <= Tolerance;

    /// <summary>
    /// Splits each fee component across the day's trades by contract sides, rounded to cents, with the
    /// rounding remainder on the trade with the most sides so the trades add up to the statement exactly.
    /// </summary>
    internal static ReconciliationTradeShare[] Allocate(StatementDay statement, IReadOnlyList<Trade> trades)
    {
        var counts = trades.Select(SidesOf).ToArray();
        var total = counts.Sum();
        if (total <= 0) return [];
        var largest = Array.IndexOf(counts, counts.Max());

        decimal[] Split(decimal amount)
        {
            var parts = counts.Select(c => Math.Round(amount * c / total, 2, MidpointRounding.AwayFromZero)).ToArray();
            parts[largest] += amount - parts.Sum();
            return parts;
        }

        var exchange = Split(statement.ExchangeFees);
        var nfa = Split(statement.NfaFees);
        var clearing = Split(statement.ClearingFees);
        return trades.Select((t, i) => new ReconciliationTradeShare(t, counts[i], exchange[i], nfa[i], clearing[i])).ToArray();
    }

    /// <summary>Writes the statement's fees onto one day's trades. Returns how many trades changed.</summary>
    public int ApplyFees(Guid journalId, DateOnly date)
    {
        var day = BuildReport(journalId).Days.FirstOrDefault(d => d.Date == date)
            ?? throw new InvalidOperationException("That day is not on any imported statement.");
        if (!day.CanApplyFees)
            throw new InvalidOperationException(day.Status == ReconciliationStatus.Matched
                ? "The fees for that day already match the statement."
                : "Statement fees can only be applied when the day's contract count matches the statement and the statements agree.");
        return Apply(journalId, day);
    }

    /// <summary>Applies statement fees to every day whose only difference is the fees.</summary>
    public (int Days, int Trades) ApplyAllFeeDifferences(Guid journalId)
    {
        var days = BuildReport(journalId).FeeOnlyDifferences.ToArray();
        return (days.Length, days.Sum(day => Apply(journalId, day)));
    }

    private int Apply(Guid journalId, ReconciliationDay day)
    {
        var source = day.Source!;
        var reason = $"Fees from {source.Kind.ToString().ToLowerInvariant()} statement {source.FileName} for {day.Date:yyyy-MM-dd}";
        var changed = 0;
        foreach (var share in day.Shares.Where(s => !SameFees(s)))
        {
            var result = database.SetTradeReviewFees(journalId, share.Trade.ReviewKey, share.ExchangeFees, share.NfaFees, share.ClearingFees, reason);
            if (result.Conflict) throw new InvalidOperationException($"A review for a trade on {day.Date:MMM d, yyyy} changed while fees were being applied. Reload and try again.");
            changed++;
        }
        return changed;
    }

    private static string SafeFileName(string fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) name = "statement.pdf";
        return name.Length > 180 ? name[..180] : name;
    }
}

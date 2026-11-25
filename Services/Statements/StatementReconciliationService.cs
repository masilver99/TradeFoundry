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
    /// <summary>The contract count, realized P&amp;L or instruments differ: a trade is missing, extra, or priced differently.</summary>
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
    /// <summary>Why the statement fees cannot be applied to a day whose fees differ; null when they can.</summary>
    public string? ApplyBlocker { get; init; }
    /// <summary>Journal instruments on this day that do not appear in the statement's fills.</summary>
    public IReadOnlyList<string> UnmatchedInstruments { get; init; } = [];
    /// <summary>The review revision of each trade when the day was built, keyed by review key.</summary>
    public IReadOnlyDictionary<string, int> ReviewRevisions { get; init; } = new Dictionary<string, int>();

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
    /// <summary>Statement account numbers found in journal trade accounts; only those trades are compared.</summary>
    public IReadOnlyList<string> MatchedAccounts { get; init; } = [];
    /// <summary>Statement account numbers that no journal trade account contains, so every trade had to be compared.</summary>
    public IReadOnlyList<string> UnmatchedAccounts { get; init; } = [];
    /// <summary>Closed journal trades left out because their account is not one the statements are for.</summary>
    public int OtherAccountTradeCount { get; init; }

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
        var revisions = database.GetTradeReviewRevisions(journalId);
        var closed = database.GetAllTrades(journalId)
            .Where(t => t.ExitUtc.HasValue && t.Status.Equals("closed", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // A journal can hold several accounts. When trade account names carry a statement's account
        // number, only those trades are compared; otherwise every trade is, and the page says so.
        var accounts = statements.Select(s => s.Account).Where(a => a.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var matchedAccounts = accounts.Where(a => closed.Any(t => AccountMatches(t, a))).ToArray();
        var scoped = matchedAccounts.Length > 0
            ? closed.Where(t => matchedAccounts.Any(a => AccountMatches(t, a))).ToArray()
            : closed;
        var trades = scoped
            .GroupBy(ExchangeTradeDate)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.ExitUtc).ThenBy(t => t.Sequence).ThenBy(t => t.Id).ToArray());

        bool Covered(DateOnly date) => statements.Any(s => s.PeriodStart <= date && date <= s.PeriodEnd);

        var dates = statementDays.Keys.Concat(trades.Keys.Where(Covered)).Distinct().Order();
        var days = new List<ReconciliationDay>();
        foreach (var date in dates)
        {
            statementDays.TryGetValue(date, out var records);
            trades.TryGetValue(date, out var dayTrades);
            days.Add(BuildDay(date, records ?? [], dayTrades ?? [], revisions, matchedAccounts.Length > 0));
        }

        return new ReconciliationReport
        {
            Journal = journal,
            Statements = statements,
            Days = days,
            UncoveredTradeDates = trades.Keys.Where(d => !Covered(d)).Order().ToArray(),
            MatchedAccounts = matchedAccounts,
            UnmatchedAccounts = accounts.Except(matchedAccounts, StringComparer.Ordinal).ToArray(),
            OtherAccountTradeCount = closed.Length - scoped.Length
        };
    }

    /// <summary>Whether a trade's account name contains the statement account number, ignoring spaces and punctuation.</summary>
    public static bool AccountMatches(Trade trade, string statementAccount)
    {
        if (statementAccount.Length == 0) return false;
        var normalized = new string(trade.Account.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Contains(statementAccount, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Contracts bought plus sold. A completed trade closes everything it opened, so that is twice the closed quantity.</summary>
    public static int SidesOf(Trade trade) => 2 * (trade.ClosedQuantity > 0 ? trade.ClosedQuantity : trade.Quantity);

    /// <summary>The product root of a statement contract, e.g. "MES" from "MES Future SEP 26".</summary>
    private static string ContractRoot(string contract) => contract.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant() ?? string.Empty;

    private static bool InstrumentOnStatement(Trade trade, IReadOnlySet<string> roots) =>
        (trade.Instrument.Length > 0 && roots.Contains(trade.Instrument.ToUpperInvariant()))
        || roots.Any(root => trade.Symbol.StartsWith(root, StringComparison.OrdinalIgnoreCase));

    private static bool Same(decimal left, decimal right) => Math.Abs(left - right) <= Tolerance;

    private static bool SameFigures(StatementDay left, StatementDay right) =>
        left.BuyContracts == right.BuyContracts && left.SellContracts == right.SellContracts
        && Same(left.RealizedPnl, right.RealizedPnl)
        && Same(left.ExchangeFees, right.ExchangeFees) && Same(left.NfaFees, right.NfaFees) && Same(left.ClearingFees, right.ClearingFees);

    private static ReconciliationDay BuildDay(DateOnly date, IReadOnlyList<BrokerStatementDayRecord> records, IReadOnlyList<Trade> trades, IReadOnlyDictionary<string, int> revisions, bool accountScoped)
    {
        // A monthly statement is issued after the month closes and carries any corrections, so it wins over a daily one.
        var ordered = records.OrderByDescending(r => r.Kind == BrokerStatementKind.Monthly).ThenByDescending(r => r.ImportedUtc).ToArray();
        var source = ordered.FirstOrDefault(r => r.Day.ContractSides > 0 || r.Day.Fees.Count > 0 || r.Day.HasRealizedPnl);
        var others = ordered.Where(r => r != source).ToArray();
        // With several statement accounts, a day is compared only with trades from its own account.
        if (accountScoped && source is { Account.Length: > 0 }) trades = trades.Where(t => AccountMatches(t, source.Account)).ToArray();

        var journalSides = trades.Sum(SidesOf);
        var gross = trades.Sum(t => t.GrossPnl);
        var exchange = trades.Sum(t => t.ExchangeFees);
        var nfa = trades.Sum(t => t.NfaFees);
        var clearing = trades.Sum(t => t.Fees - t.ExchangeFees - t.NfaFees);
        var disagree = source is not null && others.Any(o => !SameFigures(o.Day, source.Day));

        ReconciliationStatus status;
        string? blocker = null;
        var unmatched = Array.Empty<string>();
        var shares = Array.Empty<ReconciliationTradeShare>();
        if (source is null) status = ReconciliationStatus.NotOnStatement;
        else if (trades.Count == 0) status = ReconciliationStatus.MissingFromJournal;
        else
        {
            var statement = source.Day;
            var roots = statement.Fills.Select(f => ContractRoot(f.Contract)).Where(r => r.Length > 0).ToHashSet(StringComparer.Ordinal);
            unmatched = roots.Count == 0 ? [] : trades.Where(t => !InstrumentOnStatement(t, roots)).Select(t => t.Instrument.Length > 0 ? t.Instrument : t.Symbol).Distinct().ToArray();
            var tradesMatch = statement.ContractSides == journalSides && Same(statement.RealizedPnl, gross) && unmatched.Length == 0;
            var feesMatch = Same(statement.ExchangeFees, exchange) && Same(statement.NfaFees, nfa) && Same(statement.ClearingFees, clearing);
            status = disagree ? ReconciliationStatus.StatementsDisagree
                : !tradesMatch ? ReconciliationStatus.TradesDiffer
                : !feesMatch ? ReconciliationStatus.FeesDiffer
                : ReconciliationStatus.Matched;

            if (status == ReconciliationStatus.FeesDiffer)
            {
                // Fees are written only from complete, clean statement evidence.
                blocker = source.StatementHasWarnings ? "The statement did not read cleanly; see its warnings under Imported statements."
                    : statement.Fills.Count == 0 ? "The statement's fills for this day could not be read."
                    : statement.Fees.Count == 0 ? "No fee lines were read for this day."
                    : !statement.HasRealizedPnl ? "No purchase & sale total was read for this day."
                    : statement.ExchangeFees < 0m || statement.NfaFees < 0m || statement.ClearingFees < 0m ? "The statement shows a net fee credit, which trade fees cannot record."
                    : trades.Any(t => t.ReviewKey.Length == 0) ? "A trade on this day has no review to record fees in."
                    : null;
                if (blocker is null) shares = Allocate(statement, trades);
            }
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
            JournalExchangeFees = exchange,
            JournalNfaFees = nfa,
            JournalClearingFees = clearing,
            Status = status,
            CanApplyFees = status == ReconciliationStatus.FeesDiffer && blocker is null && shares.Any(s => !SameFees(s)),
            ApplyBlocker = blocker,
            UnmatchedInstruments = unmatched,
            ReviewRevisions = trades.Where(t => t.ReviewKey.Length > 0).GroupBy(t => t.ReviewKey, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => revisions.GetValueOrDefault(g.Key), StringComparer.Ordinal)
        };
    }

    private static bool SameFees(ReconciliationTradeShare share) =>
        Same(share.Trade.ExchangeFees, share.ExchangeFees)
        && Same(share.Trade.NfaFees, share.NfaFees)
        && Same(share.Trade.Fees - share.Trade.ExchangeFees - share.Trade.NfaFees, share.ClearingFees);

    /// <summary>
    /// Splits each fee component across the day's trades by contract sides in whole cents. Each trade gets
    /// its floor share, and the leftover cents go one at a time to the largest remainders (then the most
    /// sides), so a non-negative fee never produces a negative share and the trades add up exactly.
    /// </summary>
    internal static ReconciliationTradeShare[] Allocate(StatementDay statement, IReadOnlyList<Trade> trades)
    {
        var counts = trades.Select(SidesOf).ToArray();
        var total = counts.Sum();
        if (total <= 0) return [];

        decimal[] Split(decimal amount)
        {
            if (amount < 0m) throw new InvalidOperationException("Fee credits cannot be split across trades.");
            var cents = (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
            var parts = counts.Select(c => cents * c / total).ToArray();
            var leftover = cents - parts.Sum();
            foreach (var index in Enumerable.Range(0, counts.Length)
                .OrderByDescending(i => cents * counts[i] % total)
                .ThenByDescending(i => counts[i])
                .ThenBy(i => i)
                .Take((int)leftover))
            {
                parts[index]++;
            }
            return parts.Select(p => p / 100m).ToArray();
        }

        var exchange = Split(statement.ExchangeFees);
        var nfa = Split(statement.NfaFees);
        var clearing = Split(statement.ClearingFees);
        return trades.Select((t, i) => new ReconciliationTradeShare(t, counts[i], exchange[i], nfa[i], clearing[i])).ToArray();
    }

    /// <summary>
    /// Writes the statement's fees onto one day's trades in one transaction. <paramref name="expectedJournalFees"/>,
    /// when given, is the day's journal fee total the person saw; if it has changed since, nothing is written.
    /// Returns how many trades changed.
    /// </summary>
    public int ApplyFees(Guid journalId, DateOnly date, decimal? expectedJournalFees = null)
    {
        var day = BuildReport(journalId).Days.FirstOrDefault(d => d.Date == date)
            ?? throw new InvalidOperationException("That day is not on any imported statement.");
        if (expectedJournalFees is { } expected && !Same(expected, day.JournalFees))
            throw new StatementFeeConflictException("The fees for that day changed after the page loaded, so nothing was saved. Review the day again.");
        if (!day.CanApplyFees)
            throw new InvalidOperationException(day.Status == ReconciliationStatus.Matched ? "The fees for that day already match the statement."
                : day.ApplyBlocker ?? "Statement fees can only be applied when the day's contracts, realized P&L and instruments match the statement and the statements agree.");
        return database.ApplyTradeReviewFees(journalId, Updates(day).ToArray());
    }

    /// <summary>Applies statement fees to every day whose only difference is the fees, all in one transaction.</summary>
    public (int Days, int Trades) ApplyAllFeeDifferences(Guid journalId)
    {
        var days = BuildReport(journalId).FeeOnlyDifferences.ToArray();
        var updates = days.SelectMany(Updates).ToArray();
        return (days.Length, updates.Length == 0 ? 0 : database.ApplyTradeReviewFees(journalId, updates));
    }

    private static IEnumerable<TradeFeeUpdate> Updates(ReconciliationDay day)
    {
        var source = day.Source!;
        var reason = $"Fees from {source.Kind.ToString().ToLowerInvariant()} statement {source.FileName} for {day.Date:yyyy-MM-dd}";
        return day.Shares.Where(s => !SameFees(s)).Select(s => new TradeFeeUpdate(s.Trade.ReviewKey, day.ReviewRevisions[s.Trade.ReviewKey], s.ExchangeFees, s.NfaFees, s.ClearingFees, reason));
    }

    private static string SafeFileName(string fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) name = "statement.pdf";
        return name.Length > 180 ? name[..180] : name;
    }
}

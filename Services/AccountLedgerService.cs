using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services;

/// <summary>
/// Builds the journal-scoped account view from the journal opening balance,
/// manual cash flows, realized trades, and imported balance evidence. Imported
/// checkpoints are deliberately observations: after the opening fallback they
/// never change the calculated balance.
/// </summary>
public sealed class AccountLedgerService
{
    public const int DefaultPageSize = 50;

    private readonly TradeFoundryDb _database;

    public AccountLedgerService(TradeFoundryDb database)
    {
        _database = database;
    }

    public AccountSummary GetSummary(Guid journalId)
    {
        var journal = _database.GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        return Build(
            journal,
            _database.GetAccountTransactions(journalId),
            _database.GetAllTrades(journalId),
            _database.GetAccountBalanceEvents(journalId),
            1,
            DefaultPageSize).Summary;
    }

    public AccountLedgerPage GetLedger(Guid journalId, int page = 1, int pageSize = DefaultPageSize)
    {
        var journal = _database.GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        return Build(
            journal,
            _database.GetAccountTransactions(journalId),
            _database.GetAllTrades(journalId),
            _database.GetAccountBalanceEvents(journalId),
            page,
            pageSize);
    }

    /// <summary>
    /// Pure account calculation entry point. Keeping the calculation separate
    /// from persistence makes balance, TWR, ordering, and baseline behavior
    /// directly testable without a browser or a live database.
    /// </summary>
    public static AccountLedgerPage Build(
        Journal journal,
        IEnumerable<AccountTransaction> transactions,
        IEnumerable<Trade> trades,
        IEnumerable<AccountBalanceEvent> snapshots,
        int page = 1,
        int pageSize = DefaultPageSize)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(trades);
        ArgumentNullException.ThrowIfNull(snapshots);

        var manualTransactions = transactions
            .Where(transaction => transaction.JournalId == journal.Id && !transaction.DeletedUtc.HasValue)
            .ToArray();
        var closedTrades = trades
            .Where(trade => trade.JournalId == journal.Id
                && trade.ExitUtc.HasValue
                && trade.Status.Equals("closed", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var importedSnapshots = snapshots
            .Where(snapshot => snapshot.JournalId == journal.Id)
            .ToArray();

        var events = new List<LedgerEvent>(manualTransactions.Length + closedTrades.Length + importedSnapshots.Length + 1);

        foreach (var transaction in manualTransactions)
        {
            var signedAmount = transaction.Type switch
            {
                AccountTransactionType.Deposit => transaction.Amount,
                AccountTransactionType.Withdrawal => -transaction.Amount,
                _ => throw new InvalidOperationException("An account transaction has an invalid type.")
            };

            events.Add(new LedgerEvent
            {
                EffectiveUtc = transaction.EffectiveUtc.ToUniversalTime(),
                Priority = 1,
                TieUtc = transaction.CreatedUtc,
                TieId = transaction.Id,
                SignedAmount = signedAmount,
                Kind = transaction.Type == AccountTransactionType.Deposit ? "deposit" : "withdrawal",
                Entry = new AccountLedgerEntry
                {
                    TransactionId = transaction.Id,
                    EffectiveUtc = transaction.EffectiveUtc.ToUniversalTime(),
                    Kind = transaction.Type == AccountTransactionType.Deposit ? "deposit" : "withdrawal",
                    Label = transaction.Type == AccountTransactionType.Deposit ? "Deposit" : "Withdrawal",
                    Description = string.IsNullOrWhiteSpace(transaction.Note) ? "Manual account entry" : transaction.Note.Trim(),
                    Source = "Manual",
                    Amount = signedAmount,
                    Revision = transaction.Revision,
                    IsReadOnly = false,
                    Note = transaction.Note.Trim()
                }
            });
        }

        foreach (var trade in closedTrades)
        {
            var exitUtc = trade.ExitUtc!.Value.ToUniversalTime();
            events.Add(new LedgerEvent
            {
                EffectiveUtc = exitUtc,
                Priority = 2,
                TieNumber = trade.Sequence,
                TieId = trade.Id,
                SignedAmount = trade.NetPnl,
                Kind = "realized_trade_pnl",
                IsTradePnl = true,
                Entry = new AccountLedgerEntry
                {
                    TradeId = trade.Id,
                    ImportBatchId = trade.ImportBatchId,
                    TradeReviewKey = trade.ReviewKey,
                    EffectiveUtc = exitUtc,
                    Kind = "realized_trade_pnl",
                    Label = "Realized trade P&L",
                    Description = TradeDescription(trade),
                    Source = "Derived trade",
                    Account = trade.Account,
                    Amount = trade.NetPnl,
                    IsReadOnly = true,
                    Note = trade.Note
                }
            });
        }

        var baselineSnapshot = journal.StartingEquity.HasValue
            ? null
            : importedSnapshots
                .Where(snapshot => snapshot.Balance.HasValue)
                .OrderBy(snapshot => snapshot.EventUtc)
                .ThenBy(snapshot => snapshot.RowNumber)
                .ThenBy(snapshot => snapshot.Id)
                .FirstOrDefault();
        var hasInferredBaseline = baselineSnapshot is not null;
        var openingBalance = journal.StartingEquity ?? baselineSnapshot?.Balance ?? 0m;
        var openingSource = journal.StartingEquity.HasValue
            ? "Journal starting equity"
            : hasInferredBaseline
                ? "First imported account balance"
                : "Zero-based account";
        var inferredBaselineUtc = baselineSnapshot?.EventUtc.ToUniversalTime();

        foreach (var snapshot in importedSnapshots)
        {
            var eventUtc = snapshot.EventUtc.ToUniversalTime();
            var isOpeningSnapshot = hasInferredBaseline && ReferenceEquals(snapshot, baselineSnapshot);
            events.Add(new LedgerEvent
            {
                EffectiveUtc = eventUtc,
                Priority = isOpeningSnapshot ? 0 : 3,
                TieNumber = snapshot.RowNumber,
                TieId = snapshot.Id,
                IsOpening = isOpeningSnapshot,
                IsSnapshot = true,
                ReportedBalance = snapshot.Balance,
                Kind = isOpeningSnapshot ? "opening_balance" : "imported_balance",
                Entry = new AccountLedgerEntry
                {
                    SnapshotId = snapshot.Id,
                    ImportBatchId = snapshot.ImportBatchId,
                    EffectiveUtc = eventUtc,
                    Kind = isOpeningSnapshot ? "opening_balance" : "imported_balance",
                    Label = isOpeningSnapshot ? "Opening balance (imported)" : "Imported balance",
                    Description = SnapshotDescription(snapshot),
                    Source = "Imported account balance",
                    Account = snapshot.Account,
                    Amount = isOpeningSnapshot ? snapshot.Balance : null,
                    ReportedBalance = snapshot.Balance,
                    IsReadOnly = true,
                    IsOpeningBaseline = isOpeningSnapshot,
                    Note = snapshot.Note
                }
            });
        }

        var actualEvents = events.ToArray();
        var firstActivityUtc = actualEvents.Length == 0
            ? (DateTimeOffset?)null
            : actualEvents.Min(item => item.EffectiveUtc);

        if (!hasInferredBaseline)
        {
            var openingUtc = firstActivityUtc.HasValue
                ? Before(firstActivityUtc.Value)
                : journal.CreatedUtc == default ? DateTimeOffset.UnixEpoch : journal.CreatedUtc.ToUniversalTime();
            events.Add(new LedgerEvent
            {
                EffectiveUtc = openingUtc,
                Priority = 0,
                TieId = Guid.Empty,
                IsOpening = true,
                Kind = "opening_balance",
                Entry = new AccountLedgerEntry
                {
                    EffectiveUtc = openingUtc,
                    Kind = "opening_balance",
                    Label = "Opening balance",
                    Description = openingSource,
                    Source = openingSource,
                    Amount = openingBalance,
                    IsReadOnly = true,
                    IsOpeningBaseline = true
                }
            });
        }

        var ordered = events
            .OrderBy(item => item.EffectiveUtc)
            .ThenBy(item => item.Priority)
            .ThenBy(item => item.TieUtc)
            .ThenBy(item => item.TieNumber)
            .ThenBy(item => item.TieId)
            .ToArray();

        var calculatedBalance = openingBalance;
        var realizedProfit = 0m;
        var totalDeposits = 0m;
        var totalWithdrawals = 0m;
        var latestReportedBalance = (decimal?)null;
        var latestReportedVariance = (decimal?)null;
        var hasPreBaselineActivity = false;
        var hasPositiveCapitalBase = calculatedBalance > 0m;
        var twrFactor = 1m;
        var twrUnavailable = false;
        var realizedTradeCount = 0;

        foreach (var ledgerEvent in ordered)
        {
            var isBeforeBaseline = inferredBaselineUtc.HasValue
                && !ledgerEvent.IsOpening
                && ledgerEvent.EffectiveUtc < inferredBaselineUtc.Value;
            if (isBeforeBaseline)
            {
                hasPreBaselineActivity = true;
                ledgerEvent.Entry = WithCalculation(ledgerEvent.Entry, null, null, true);
                continue;
            }

            if (ledgerEvent.IsOpening)
            {
                calculatedBalance = ledgerEvent.Entry.Amount ?? openingBalance;
                hasPositiveCapitalBase |= calculatedBalance > 0m;
                var openingVariance = ledgerEvent.IsSnapshot && ledgerEvent.ReportedBalance.HasValue
                    ? ledgerEvent.ReportedBalance.Value - calculatedBalance
                    : (decimal?)null;
                if (ledgerEvent.ReportedBalance.HasValue)
                {
                    latestReportedBalance = ledgerEvent.ReportedBalance;
                    latestReportedVariance = openingVariance;
                }
                ledgerEvent.Entry = WithCalculation(ledgerEvent.Entry, calculatedBalance, openingVariance, false);
                continue;
            }

            if (ledgerEvent.SignedAmount.HasValue)
            {
                var beforeTrade = calculatedBalance;
                calculatedBalance += ledgerEvent.SignedAmount.Value;

                if (ledgerEvent.Kind == "deposit")
                {
                    totalDeposits += ledgerEvent.SignedAmount.Value;
                }
                else if (ledgerEvent.Kind == "withdrawal")
                {
                    totalWithdrawals += -ledgerEvent.SignedAmount.Value;
                }
                else if (ledgerEvent.IsTradePnl)
                {
                    realizedProfit += ledgerEvent.SignedAmount.Value;
                    realizedTradeCount++;
                    if (beforeTrade <= 0m || calculatedBalance <= 0m)
                    {
                        twrUnavailable = true;
                    }
                    else
                    {
                        twrFactor *= calculatedBalance / beforeTrade;
                    }
                }

                hasPositiveCapitalBase |= calculatedBalance > 0m;
                ledgerEvent.Entry = WithCalculation(ledgerEvent.Entry, calculatedBalance, null, false);
            }
            else
            {
                var variance = ledgerEvent.ReportedBalance.HasValue
                    ? ledgerEvent.ReportedBalance.Value - calculatedBalance
                    : (decimal?)null;
                if (ledgerEvent.ReportedBalance.HasValue)
                {
                    latestReportedBalance = ledgerEvent.ReportedBalance;
                    latestReportedVariance = variance;
                }
                ledgerEvent.Entry = WithCalculation(ledgerEvent.Entry, calculatedBalance, variance, false);
            }
        }

        var latestActivityUtc = actualEvents.Length == 0
            ? (DateTimeOffset?)null
            : actualEvents.Max(item => item.EffectiveUtc);
        var realizedTwr = hasPositiveCapitalBase && !twrUnavailable
            ? (decimal?)((twrFactor - 1m) * 100m)
            : null;
        var statusMessages = new List<string>();
        if (hasInferredBaseline)
            statusMessages.Add("Opening balance inferred from the earliest imported account-balance snapshot.");
        if (hasPreBaselineActivity)
            statusMessages.Add("Some activity predates the inferred account opening balance and is outside the calculated account period.");
        if (!hasPositiveCapitalBase)
            statusMessages.Add("Realized TWR is unavailable until a positive capital base is recorded.");
        else if (twrUnavailable)
            statusMessages.Add("Realized TWR is unavailable for a trade that started or ended with a non-positive balance.");
        if (calculatedBalance < 0m)
            statusMessages.Add("Calculated balance is negative; withdrawals remain recorded and can be reviewed.");

        var summary = new AccountSummary
        {
            JournalId = journal.Id,
            CurrentBalance = calculatedBalance,
            OpeningBalance = openingBalance,
            OpeningBalanceSource = openingSource,
            RealizedProfit = realizedProfit,
            TotalDeposits = totalDeposits,
            TotalWithdrawals = totalWithdrawals,
            RealizedTwrPercent = realizedTwr,
            LatestReportedBalance = latestReportedBalance,
            LatestReportedVariance = latestReportedVariance,
            LatestActivityUtc = latestActivityUtc,
            HasActivity = actualEvents.Length > 0,
            HasInferredBaseline = hasInferredBaseline,
            HasPreBaselineActivity = hasPreBaselineActivity,
            StatusMessage = string.Join(" ", statusMessages)
        };

        var safePageSize = Math.Clamp(pageSize, 1, 200);
        var safePage = Math.Max(1, page);
        var newestFirst = ordered
            .OrderByDescending(item => item.EffectiveUtc)
            .ThenByDescending(item => item.Priority)
            .ThenByDescending(item => item.TieUtc)
            .ThenByDescending(item => item.TieNumber)
            .ThenByDescending(item => item.TieId)
            .Select(item => item.Entry)
            .ToArray();

        return new AccountLedgerPage
        {
            Summary = summary,
            Entries = newestFirst.Skip((safePage - 1) * safePageSize).Take(safePageSize).ToArray(),
            Page = safePage,
            PageSize = safePageSize,
            TotalCount = newestFirst.Length
        };
    }

    private static AccountLedgerEntry WithCalculation(AccountLedgerEntry entry, decimal? calculatedBalance, decimal? variance, bool beforeBaseline) => new()
    {
        TransactionId = entry.TransactionId,
        TradeId = entry.TradeId,
        ImportBatchId = entry.ImportBatchId,
        SnapshotId = entry.SnapshotId,
        TradeReviewKey = entry.TradeReviewKey,
        EffectiveUtc = entry.EffectiveUtc,
        Kind = entry.Kind,
        Label = entry.Label,
        Description = entry.Description,
        Source = entry.Source,
        Account = entry.Account,
        Amount = entry.Amount,
        CalculatedBalance = calculatedBalance,
        ReportedBalance = entry.ReportedBalance,
        Variance = variance,
        Revision = entry.Revision,
        IsReadOnly = entry.IsReadOnly,
        IsOpeningBaseline = entry.IsOpeningBaseline,
        IsBeforeBaseline = beforeBaseline,
        Note = entry.Note
    };

    private static DateTimeOffset Before(DateTimeOffset value) => value <= DateTimeOffset.MinValue
        ? DateTimeOffset.MinValue
        : value.AddTicks(-1);

    private static string SnapshotDescription(AccountBalanceEvent snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Note)) return snapshot.Note.Trim();
        return string.IsNullOrWhiteSpace(snapshot.Account) ? "Reported balance checkpoint" : $"Reported balance for {snapshot.Account}";
    }

    private static string TradeDescription(Trade trade)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(trade.Instrument)) parts.Add(trade.Instrument);
        if (!string.IsNullOrWhiteSpace(trade.Symbol) && !trade.Symbol.Equals(trade.Instrument, StringComparison.OrdinalIgnoreCase)) parts.Add(trade.Symbol);
        if (!string.IsNullOrWhiteSpace(trade.Direction)) parts.Add(trade.Direction);
        if (trade.ClosedQuantity > 0) parts.Add($"{trade.ClosedQuantity} qty");
        return parts.Count == 0 ? "Closed trade" : string.Join(" · ", parts);
    }

    private sealed class LedgerEvent
    {
        public DateTimeOffset EffectiveUtc { get; init; }
        public int Priority { get; init; }
        public DateTimeOffset TieUtc { get; init; }
        public int TieNumber { get; init; }
        public Guid TieId { get; init; }
        public string Kind { get; init; } = string.Empty;
        public decimal? SignedAmount { get; init; }
        public decimal? ReportedBalance { get; init; }
        public bool IsTradePnl { get; init; }
        public bool IsSnapshot { get; init; }
        public bool IsOpening { get; init; }
        public AccountLedgerEntry Entry { get; set; } = new();
    }
}

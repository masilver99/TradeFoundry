using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services;

/// <summary>
/// Builds the journal-scoped account view from the journal opening balance,
/// manual cash flows, realized trades, and imported balance evidence. Imported
/// checkpoints are deliberately observations: after the opening fallback they
/// never change the calculated balance.
/// Broker statements strike their cash balance at the CME session close (5 p.m. Chicago),
/// not at the journal's midnight, so each journal day that ends after a statement balance
/// also gets a derived midnight balance: the statement balance plus the activity recorded
/// between the close and midnight, checked against the next statement.
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
            _database.GetAccountBalanceCheckpoints(journalId),
            1,
            DefaultPageSize).Summary;
    }

    public AccountLedgerPage GetLedger(Guid journalId, int page = 1, int pageSize = DefaultPageSize)
        => GetLedger(journalId, page, pageSize, null, null);

    /// <summary>
    /// The ledger with rows limited to a half-open UTC window. Balances are still calculated over the
    /// whole history, so each row shows the real running balance; <see cref="AccountLedgerPage.Period"/> sums the window.
    /// </summary>
    public AccountLedgerPage GetLedger(Guid journalId, int page, int pageSize, DateTimeOffset? windowStartUtc, DateTimeOffset? windowEndUtcExclusive, string? show = null)
    {
        var journal = _database.GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        return Build(
            journal,
            _database.GetAccountTransactions(journalId),
            _database.GetAllTrades(journalId),
            _database.GetAccountBalanceCheckpoints(journalId),
            page,
            pageSize,
            windowStartUtc,
            windowEndUtcExclusive,
            show);
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
        int pageSize = DefaultPageSize,
        DateTimeOffset? windowStartUtc = null,
        DateTimeOffset? windowEndUtcExclusive = null,
        string? show = null,
        DateTimeOffset? nowUtc = null)
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
                AccountTransactionType.Correction => transaction.Amount,
                _ => throw new InvalidOperationException("An account transaction has an invalid type.")
            };

            var (kind, label) = transaction.Type switch
            {
                AccountTransactionType.Deposit => ("deposit", "Deposit"),
                AccountTransactionType.Withdrawal => ("withdrawal", "Withdrawal"),
                _ => ("correction", "Correction")
            };

            events.Add(new LedgerEvent
            {
                EffectiveUtc = transaction.EffectiveUtc.ToUniversalTime(),
                Priority = 1,
                TieUtc = transaction.CreatedUtc,
                TieId = transaction.Id,
                SignedAmount = signedAmount,
                Kind = kind,
                Entry = new AccountLedgerEntry
                {
                    TransactionId = transaction.Id,
                    EffectiveUtc = transaction.EffectiveUtc.ToUniversalTime(),
                    Kind = kind,
                    Label = label,
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
                ? AccountBalanceCheckpoints.IsStatement(baselineSnapshot!) ? "First broker statement cash balance" : "First imported account balance"
                : "Zero-based account";
        var inferredBaselineUtc = baselineSnapshot?.EventUtc.ToUniversalTime();

        foreach (var snapshot in importedSnapshots)
        {
            var eventUtc = snapshot.EventUtc.ToUniversalTime();
            var isOpeningSnapshot = hasInferredBaseline && ReferenceEquals(snapshot, baselineSnapshot);
            var fromStatement = AccountBalanceCheckpoints.IsStatement(snapshot);
            events.Add(new LedgerEvent
            {
                EffectiveUtc = eventUtc,
                // A statement balance is struck at the session close, before anything at that instant, which belongs to the next trade date.
                Priority = isOpeningSnapshot ? 0 : fromStatement ? -1 : 3,
                TieNumber = snapshot.RowNumber,
                TieId = snapshot.Id,
                IsOpening = isOpeningSnapshot,
                IsSnapshot = true,
                IsStatement = fromStatement,
                ReportedBalance = snapshot.Balance,
                Kind = isOpeningSnapshot ? "opening_balance" : "imported_balance",
                Entry = new AccountLedgerEntry
                {
                    SnapshotId = snapshot.Id,
                    ImportBatchId = snapshot.ImportBatchId,
                    EffectiveUtc = eventUtc,
                    Kind = isOpeningSnapshot ? "opening_balance" : "imported_balance",
                    Label = isOpeningSnapshot ? (fromStatement ? "Opening balance (statement)" : "Opening balance (imported)") : fromStatement ? "Statement balance" : "Imported balance",
                    Description = SnapshotDescription(snapshot),
                    Source = SnapshotSource(snapshot),
                    Account = snapshot.Account,
                    Amount = isOpeningSnapshot ? snapshot.Balance : null,
                    ReportedBalance = snapshot.Balance,
                    IsReadOnly = true,
                    IsOpeningBaseline = isOpeningSnapshot,
                    // The description already carries a statement checkpoint's note; name the file it came from instead.
                    Note = fromStatement ? snapshot.SourceTimeText : snapshot.Note
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

        var ordered = InLedgerOrder(events);
        var midnights = MidnightBalances(journal, ordered, inferredBaselineUtc, nowUtc ?? DateTimeOffset.UtcNow);
        if (midnights.Count > 0) ordered = InLedgerOrder(ordered.Concat(midnights));

        var calculatedBalance = openingBalance;
        var realizedProfit = 0m;
        var totalDeposits = 0m;
        var totalWithdrawals = 0m;
        var netCorrections = 0m;
        var latestReportedBalance = (decimal?)null;
        var latestReportedVariance = (decimal?)null;
        var latestReportedSource = (string?)null;
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
                    latestReportedSource = ledgerEvent.Entry.Source;
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
                else if (ledgerEvent.Kind == "correction")
                {
                    netCorrections += ledgerEvent.SignedAmount.Value;
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
                // A derived midnight balance is not broker evidence of its own, so the latest reported balance stays the statement's.
                if (ledgerEvent.ReportedBalance.HasValue && !ledgerEvent.IsDerived)
                {
                    latestReportedBalance = ledgerEvent.ReportedBalance;
                    latestReportedVariance = variance;
                    latestReportedSource = ledgerEvent.Entry.Source;
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
            statusMessages.Add(AccountBalanceCheckpoints.IsStatement(baselineSnapshot!)
                ? "Opening balance taken from the earliest broker statement's opening cash balance."
                : "Opening balance inferred from the earliest imported account-balance snapshot.");
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
            ReportedCurrentBalance = ReportedBalanceBefore(ordered, _ => true),
            OpeningBalance = openingBalance,
            OpeningBalanceSource = openingSource,
            RealizedProfit = realizedProfit,
            TotalDeposits = totalDeposits,
            TotalWithdrawals = totalWithdrawals,
            NetCorrections = netCorrections,
            RealizedTwrPercent = realizedTwr,
            LatestReportedBalance = latestReportedBalance,
            LatestReportedVariance = latestReportedVariance,
            LatestReportedSource = latestReportedSource,
            LatestActivityUtc = latestActivityUtc,
            HasActivity = actualEvents.Length > 0,
            HasInferredBaseline = hasInferredBaseline,
            HasPreBaselineActivity = hasPreBaselineActivity,
            StatusMessage = string.Join(" ", statusMessages)
        };

        bool InWindow(LedgerEvent item) =>
            (!windowStartUtc.HasValue || item.EffectiveUtc >= windowStartUtc.Value)
            && (!windowEndUtcExclusive.HasValue || item.EffectiveUtc < windowEndUtcExclusive.Value);
        var hasWindow = windowStartUtc.HasValue || windowEndUtcExclusive.HasValue;
        var period = hasWindow ? BuildPeriod(ordered, InWindow, windowStartUtc, openingBalance) : null;

        var safeShow = AccountLedgerShow.Normalize(show);
        var safePageSize = Math.Clamp(pageSize, 1, 200);
        var newestFirst = ordered
            .Where(item => !hasWindow || InWindow(item))
            .Where(item => AccountLedgerShow.Matches(safeShow, item.Entry))
            .OrderByDescending(item => item.EffectiveUtc)
            .ThenByDescending(item => item.Priority)
            .ThenByDescending(item => item.TieUtc)
            .ThenByDescending(item => item.TieNumber)
            .ThenByDescending(item => item.TieId)
            .Select(item => item.Entry)
            .ToArray();
        // A page past the end (e.g. after narrowing the filter) shows the last page instead of nothing.
        var pageCount = Math.Max(1, (int)Math.Ceiling((double)newestFirst.Length / safePageSize));
        var safePage = Math.Clamp(page, 1, pageCount);

        return new AccountLedgerPage
        {
            Summary = summary,
            Period = period,
            Entries = newestFirst.Skip((safePage - 1) * safePageSize).Take(safePageSize).ToArray(),
            Show = safeShow,
            Page = safePage,
            PageSize = safePageSize,
            TotalCount = newestFirst.Length
        };
    }

    /// <summary>Opening and closing calculated balances around the window, and the cash flows and realized profit inside it.</summary>
    private static AccountPeriodSummary BuildPeriod(IReadOnlyList<LedgerEvent> ordered, Func<LedgerEvent, bool> inWindow, DateTimeOffset? windowStartUtc, decimal openingBalance)
    {
        var counted = ordered.Where(item => !item.Entry.IsBeforeBaseline).ToArray();
        var before = windowStartUtc.HasValue ? counted.LastOrDefault(item => item.EffectiveUtc < windowStartUtc.Value) : null;
        var inside = counted.Where(inWindow).ToArray();
        // A window with a start opens at the balance just before it (nothing recorded yet → unknown);
        // a window open at the start begins with the account's opening baseline.
        var opening = windowStartUtc.HasValue
            ? before?.Entry.CalculatedBalance
            : inside.FirstOrDefault(item => item.IsOpening)?.Entry.CalculatedBalance ?? openingBalance;
        var closing = inside.LastOrDefault(item => item.Entry.CalculatedBalance.HasValue)?.Entry.CalculatedBalance ?? opening;
        return new AccountPeriodSummary
        {
            OpeningBalance = opening,
            ClosingBalance = closing,
            ReportedOpeningBalance = windowStartUtc.HasValue ? ReportedBalanceBefore(ordered, item => item.EffectiveUtc < windowStartUtc.Value) : null,
            ReportedClosingBalance = ReportedBalanceBefore(ordered, item => item.EffectiveUtc < windowStartUtc.GetValueOrDefault(DateTimeOffset.MinValue) || inWindow(item)),
            Deposits = inside.Where(item => item.Kind == "deposit").Sum(item => item.SignedAmount ?? 0m),
            Withdrawals = -inside.Where(item => item.Kind == "withdrawal").Sum(item => item.SignedAmount ?? 0m),
            Corrections = inside.Where(item => item.Kind == "correction").Sum(item => item.SignedAmount ?? 0m),
            RealizedProfit = inside.Where(item => item.IsTradePnl).Sum(item => item.SignedAmount ?? 0m),
            RealizedTradeCount = inside.Count(item => item.IsTradePnl),
            RowCount = ordered.Count(inWindow)
        };
    }

    /// <summary>
    /// The broker's own balance at the last point matching <paramref name="upTo"/>: the latest imported balance there,
    /// plus the cash flows and realized trades recorded after it. Expenses never enter the ledger, so they are not deducted.
    /// </summary>
    private static decimal? ReportedBalanceBefore(IReadOnlyList<LedgerEvent> ordered, Func<LedgerEvent, bool> upTo)
    {
        decimal? balance = null;
        foreach (var item in ordered)
        {
            if (item.IsDerived || !upTo(item)) continue;
            if (item.IsSnapshot && item.ReportedBalance.HasValue) balance = item.ReportedBalance;
            else if (balance.HasValue && !item.IsOpening && item.SignedAmount.HasValue) balance += item.SignedAmount.Value;
        }
        return balance;
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
        Note = entry.Note,
        NextStatementDifference = entry.NextStatementDifference,
        NextStatementDescription = entry.NextStatementDescription
    };

    private static LedgerEvent[] InLedgerOrder(IEnumerable<LedgerEvent> events) => events
        .OrderBy(item => item.EffectiveUtc)
        .ThenBy(item => item.Priority)
        .ThenBy(item => item.TieUtc)
        .ThenBy(item => item.TieNumber)
        .ThenBy(item => item.TieId)
        .ToArray();

    /// <summary>
    /// One balance at each journal midnight that follows a statement balance on the same journal day: the latest such
    /// statement balance plus the cash flows and realized trades recorded after it and before midnight. Each is then
    /// rolled forward to the next statement balance, and the difference from it is the check. Midnights that have not
    /// happened yet, and statement balances before the account's opening baseline, give none.
    /// </summary>
    private static List<LedgerEvent> MidnightBalances(Journal journal, IReadOnlyList<LedgerEvent> ordered, DateTimeOffset? baselineUtc, DateTimeOffset nowUtc)
    {
        var zone = TimeZoneCatalog.Resolve(journal.TimeZone);
        DateTimeOffset MidnightAfter(DateTimeOffset utc) =>
            TimeZoneCatalog.FromLocal(TimeZoneInfo.ConvertTime(utc, zone).Date.AddDays(1), journal.TimeZone).ToUniversalTime();
        static bool IsStatementBalance(LedgerEvent item) => item.IsStatement && item.ReportedBalance.HasValue;
        static bool MovesBalance(LedgerEvent item) => !item.IsOpening && item.SignedAmount.HasValue;

        var anchors = ordered
            .Select((item, index) => (Item: item, Index: index))
            .Where(x => IsStatementBalance(x.Item) && !(baselineUtc.HasValue && x.Item.EffectiveUtc < baselineUtc.Value))
            .Select(x => (x.Item, x.Index, Midnight: MidnightAfter(x.Item.EffectiveUtc)))
            .Where(x => x.Midnight <= nowUtc)
            .GroupBy(x => x.Midnight)
            .Select(g => g.Last());

        var midnights = new List<LedgerEvent>();
        foreach (var (anchor, anchorIndex, midnight) in anchors)
        {
            var balance = anchor.ReportedBalance!.Value;
            var trades = 0;
            var cashFlows = 0;
            var next = anchorIndex + 1;
            for (; next < ordered.Count && ordered[next].EffectiveUtc < midnight; next++)
            {
                var item = ordered[next];
                if (!MovesBalance(item)) continue;
                balance += item.SignedAmount!.Value;
                if (item.IsTradePnl) trades++; else cashFlows++;
            }

            // Everything at or after midnight belongs to the next day; the next statement balance confirms the roll-forward.
            decimal? difference = null;
            var checkedAgainst = string.Empty;
            var rolled = balance;
            for (; next < ordered.Count; next++)
            {
                var item = ordered[next];
                if (IsStatementBalance(item))
                {
                    difference = item.ReportedBalance!.Value - rolled;
                    checkedAgainst = item.Entry.Description;
                    break;
                }
                if (MovesBalance(item)) rolled += item.SignedAmount!.Value;
            }

            midnights.Add(new LedgerEvent
            {
                EffectiveUtc = midnight,
                // Before anything at midnight itself, which starts the next journal day.
                Priority = -2,
                TieId = anchor.TieId,
                IsSnapshot = true,
                IsDerived = true,
                ReportedBalance = balance,
                Kind = "midnight_balance",
                Entry = new AccountLedgerEntry
                {
                    EffectiveUtc = midnight,
                    Kind = "midnight_balance",
                    Label = "Midnight balance",
                    Description = $"{anchor.Entry.Description}{ActivitySince(trades, cashFlows)}",
                    Source = "Broker statement + journal",
                    Account = anchor.Entry.Account,
                    ReportedBalance = balance,
                    IsReadOnly = true,
                    Note = anchor.Entry.Note,
                    NextStatementDifference = difference,
                    NextStatementDescription = checkedAgainst
                }
            });
        }
        return midnights;
    }

    private static string ActivitySince(int trades, int cashFlows)
    {
        var parts = new List<string>();
        if (trades > 0) parts.Add(trades == 1 ? "1 trade" : $"{trades} trades");
        if (cashFlows > 0) parts.Add(cashFlows == 1 ? "1 account entry" : $"{cashFlows} account entries");
        return parts.Count == 0 ? ", with no activity after it" : $", plus {string.Join(" and ", parts)} after it";
    }

    private static DateTimeOffset Before(DateTimeOffset value) => value <= DateTimeOffset.MinValue
        ? DateTimeOffset.MinValue
        : value.AddTicks(-1);

    private static string SnapshotDescription(AccountBalanceEvent snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Note)) return snapshot.Note.Trim();
        return string.IsNullOrWhiteSpace(snapshot.Account) ? "Reported balance checkpoint" : $"Reported balance for {snapshot.Account}";
    }

    private static string SnapshotSource(AccountBalanceEvent snapshot) =>
        AccountBalanceCheckpoints.IsStatement(snapshot) ? "Broker statement" : "Imported account balance";

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
        public bool IsStatement { get; init; }
        /// <summary>A midnight balance worked out from a statement balance, shown for comparison but never broker evidence itself.</summary>
        public bool IsDerived { get; init; }
        public bool IsOpening { get; init; }
        public AccountLedgerEntry Entry { get; set; } = new();
    }
}

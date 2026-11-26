using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class AccountLedgerTests
{
    [Fact]
    public void BalanceIncludesCashFlowsAndTwrCompoundsRealizedTradeReturns()
    {
        var journalId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var depositTime = start.AddDays(1);
        var secondTradeTime = start.AddDays(2);
        var journal = Journal(journalId, 1_000m, start);
        var deposit = Transaction(journalId, AccountTransactionType.Deposit, 500m, depositTime, 1);
        var withdrawal = Transaction(journalId, AccountTransactionType.Withdrawal, 100m, secondTradeTime, 2);
        var firstTrade = Trade(journalId, 150m, depositTime, 1);
        var secondTrade = Trade(journalId, -50m, secondTradeTime, 2);

        var ledger = AccountLedgerService.Build(journal, [deposit, withdrawal], [firstTrade, secondTrade], []);

        Assert.Equal(1_500m, ledger.Summary.CurrentBalance);
        Assert.Equal(100m, ledger.Summary.RealizedProfit);
        Assert.Equal(500m, ledger.Summary.TotalDeposits);
        Assert.Equal(100m, ledger.Summary.TotalWithdrawals);
        Assert.Equal((1.1m * (1_500m / 1_550m) - 1m) * 100m, ledger.Summary.RealizedTwrPercent);

        var sameTime = ledger.Entries
            .Where(entry => entry.EffectiveUtc == depositTime)
            .Reverse()
            .Select(entry => entry.Kind)
            .ToArray();
        Assert.Equal(["deposit", "realized_trade_pnl"], sameTime);
        Assert.Equal(1_500m, ledger.Entries.First(entry => entry.Kind == "realized_trade_pnl" && entry.EffectiveUtc == secondTradeTime).CalculatedBalance);
    }

    [Fact]
    public void DateWindowKeepsRunningBalancesFromFullHistoryAndSumsOnlyTheWindow()
    {
        var journalId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var journal = Journal(journalId, 1_000m, start);
        var earlyDeposit = Transaction(journalId, AccountTransactionType.Deposit, 500m, start.AddDays(1), 1);
        var earlyTrade = Trade(journalId, 200m, start.AddDays(2), 1);
        var windowTrade = Trade(journalId, -50m, start.AddDays(10), 2);
        var windowWithdrawal = Transaction(journalId, AccountTransactionType.Withdrawal, 100m, start.AddDays(11), 2);
        var lateTrade = Trade(journalId, 75m, start.AddDays(30), 3);

        var windowStart = start.AddDays(5);
        var windowEnd = start.AddDays(20);
        var ledger = AccountLedgerService.Build(journal, [earlyDeposit, windowWithdrawal], [earlyTrade, windowTrade, lateTrade], [], 1, 50, windowStart, windowEnd);

        // Rows are limited to the window, but each keeps the real running balance.
        Assert.Equal(2, ledger.TotalCount);
        Assert.Equal(1_650m, ledger.Entries.Single(entry => entry.Kind == "realized_trade_pnl").CalculatedBalance);
        Assert.Equal(1_550m, ledger.Entries.Single(entry => entry.Kind == "withdrawal").CalculatedBalance);
        // The whole-account summary is unchanged by the window.
        Assert.Equal(1_625m, ledger.Summary.CurrentBalance);

        var period = Assert.IsType<AccountPeriodSummary>(ledger.Period);
        Assert.Equal(1_700m, period.OpeningBalance);
        Assert.Equal(1_550m, period.ClosingBalance);
        Assert.Equal(-50m, period.RealizedProfit);
        Assert.Equal(1, period.RealizedTradeCount);
        Assert.Equal(0m, period.Deposits);
        Assert.Equal(100m, period.Withdrawals);

        // A window before any activity has no opening balance and an empty ledger.
        var empty = AccountLedgerService.Build(journal, [earlyDeposit], [earlyTrade], [], 1, 50, start.AddYears(-1), start.AddYears(-1).AddDays(7));
        Assert.Equal(0, empty.TotalCount);
        Assert.Null(empty.Period!.OpeningBalance);
        Assert.Null(AccountLedgerService.Build(journal, [earlyDeposit], [earlyTrade], []).Period);
    }

    [Fact]
    public void DateWindowBalancesFollowTheBrokerReportedBalanceWhenImported()
    {
        var journalId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        // A wrong starting equity and an unrecorded deposit put the calculated balance far from the broker's.
        var journal = Journal(journalId, 2_605.30m, start);
        var trades = new[] { Trade(journalId, -20m, start.AddDays(2), 1), Trade(journalId, 30m, start.AddDays(10), 2), Trade(journalId, 5m, start.AddDays(12), 3) };
        var snapshots = new[] { Snapshot(journalId, 61.77m, start.AddDays(1), 1), Snapshot(journalId, 1_541.77m, start.AddDays(3), 2), Snapshot(journalId, 1_570m, start.AddDays(11), 3) };

        var period = AccountLedgerService.Build(journal, [], trades, snapshots, 1, 50, start.AddDays(5), start.AddDays(20)).Period!;

        Assert.Equal(1_541.77m, period.DisplayOpeningBalance);
        // The latest broker balance, plus the trade recorded after it.
        Assert.Equal(1_575m, period.DisplayClosingBalance);
        Assert.True(period.IsBrokerAnchored);
        Assert.Equal(2_620.30m, period.ClosingBalance);

        // The whole-account balance shown on the overview and published site uses the same broker anchor.
        var summary = AccountLedgerService.Build(journal, [], trades, snapshots).Summary;
        Assert.Equal(1_575m, summary.DisplayBalance);
        Assert.Equal(2_620.30m, summary.CurrentBalance);
        var published = PublicationExportService.BuildSnapshot(journal, trades, summary, new Dictionary<DateOnly, List<TradeFoundry.Publishing.PublicationNode>>(), [], new(Title: "Public"), start.AddDays(30));
        Assert.Equal(1_575m, published.Dashboard.Balance);

        // Without imported balances the calculated figures are shown.
        var calculated = AccountLedgerService.Build(journal, [], trades, [], 1, 50, start.AddDays(5), start.AddDays(20)).Period!;
        Assert.False(calculated.IsBrokerAnchored);
        Assert.Equal(calculated.ClosingBalance, calculated.DisplayClosingBalance);
    }

    [Fact]
    public void EarliestImportedSnapshotIsFallbackBaselineAndLaterSnapshotsReconcile()
    {
        var journalId = Guid.NewGuid();
        var preBaselineTime = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var baselineTime = preBaselineTime.AddDays(1);
        var laterTime = baselineTime.AddDays(1);
        var baseline = Snapshot(journalId, 1_000m, baselineTime, 1);
        var later = Snapshot(journalId, 1_200m, laterTime.AddHours(2), 2);
        var journal = Journal(journalId, null, baselineTime);
        var preBaselineDeposit = Transaction(journalId, AccountTransactionType.Deposit, 50m, preBaselineTime, 1);
        var preBaselineTrade = Trade(journalId, 100m, preBaselineTime.AddHours(1), 1);
        var deposit = Transaction(journalId, AccountTransactionType.Deposit, 100m, laterTime, 2);
        var trade = Trade(journalId, 50m, laterTime.AddHours(1), 2);

        var ledger = AccountLedgerService.Build(journal, [preBaselineDeposit, deposit], [preBaselineTrade, trade], [baseline, later]);

        Assert.Equal(1_150m, ledger.Summary.CurrentBalance);
        Assert.Equal(50m, ledger.Summary.RealizedProfit);
        Assert.Equal(100m, ledger.Summary.TotalDeposits);
        Assert.Equal("First imported account balance", ledger.Summary.OpeningBalanceSource);
        Assert.True(ledger.Summary.HasInferredBaseline);
        Assert.True(ledger.Summary.HasPreBaselineActivity);
        Assert.Equal(1_200m, ledger.Summary.LatestReportedBalance);
        Assert.Equal(50m, ledger.Summary.LatestReportedVariance);
        Assert.Equal((1_150m / 1_100m - 1m) * 100m, ledger.Summary.RealizedTwrPercent);
        Assert.Contains("outside the calculated account period", ledger.Summary.StatusMessage, StringComparison.Ordinal);

        var opening = Assert.Single(ledger.Entries, entry => entry.IsOpeningBaseline);
        Assert.Equal("opening_balance", opening.Kind);
        Assert.Equal(1_000m, opening.CalculatedBalance);
        Assert.Equal(0m, opening.Variance);
        Assert.Null(Assert.Single(ledger.Entries, entry => entry.TradeId == preBaselineTrade.Id).CalculatedBalance);
        Assert.Equal(50m, Assert.Single(ledger.Entries, entry => entry.SnapshotId == later.Id).Variance);
    }

    [Fact]
    public void DepositCanEstablishCapitalAndNonPositiveTradeBasesMakeTwrUnavailable()
    {
        var journalId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var zeroJournal = Journal(journalId, null, start);
        var withdrawal = Transaction(journalId, AccountTransactionType.Withdrawal, 25m, start.AddDays(1), 1);
        var negativeTrade = Trade(journalId, 10m, start.AddDays(2), 1);

        var negative = AccountLedgerService.Build(zeroJournal, [withdrawal], [negativeTrade], []);

        Assert.Equal(-15m, negative.Summary.CurrentBalance);
        Assert.Null(negative.Summary.RealizedTwrPercent);
        Assert.Contains("unavailable", negative.Summary.StatusMessage, StringComparison.OrdinalIgnoreCase);

        var fundedJournal = Journal(Guid.NewGuid(), null, start);
        var deposit = Transaction(fundedJournal.Id, AccountTransactionType.Deposit, 100m, start.AddDays(1), 1);
        var trade = Trade(fundedJournal.Id, 10m, start.AddDays(2), 1);
        var funded = AccountLedgerService.Build(fundedJournal, [deposit], [trade], []);

        Assert.Equal(110m, funded.Summary.CurrentBalance);
        Assert.Equal(10m, funded.Summary.RealizedTwrPercent);
    }

    [Fact]
    public void LedgerIsPaginatedAndOpenTradesAreExcluded()
    {
        var journalId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var journal = Journal(journalId, 500m, start);
        var transactions = Enumerable.Range(1, 3)
            .Select(index => Transaction(journalId, AccountTransactionType.Deposit, index * 10m, start.AddDays(index), index))
            .ToArray();
        var open = new Trade
        {
            Id = Guid.NewGuid(), JournalId = journalId, EntryUtc = start.AddDays(4), Status = "open", NetPnl = 999m
        };

        var page = AccountLedgerService.Build(journal, transactions, [open], [], 2, 2);

        Assert.Equal(4, page.TotalCount); // opening row plus three deposits
        Assert.Equal(2, page.PageCount);
        Assert.Equal(2, page.Entries.Count);
        Assert.DoesNotContain(page.Entries, entry => entry.Kind == "realized_trade_pnl");
        Assert.Equal(560m, page.Summary.CurrentBalance);
    }

    [Fact]
    public void ManualTransactionsAreJournalScopedAuditedRevisionCheckedAndSoftDeleted()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var otherJournal = database.CreateJournal("Other", "paper", string.Empty, "UTC", "USD", "flat_to_flat");
            var effective = new DateTimeOffset(2026, 9, 10, 13, 15, 0, TimeSpan.Zero);

            var created = database.CreateAccountTransaction(journal.Id, new AccountTransactionDraft
            {
                Type = AccountTransactionType.Deposit, EffectiveUtc = effective, Amount = 1_234.5678m, Note = "Initial funding"
            });
            Assert.Equal(1_234.5678m, database.GetAccountTransaction(journal.Id, created.Id)!.Amount);
            Assert.Single(database.GetAccountTransactions(journal.Id));
            Assert.Empty(database.GetAccountTransactions(otherJournal.Id));

            var updated = database.UpdateAccountTransaction(journal.Id, created.Id, 1, new AccountTransactionDraft
            {
                Type = AccountTransactionType.Withdrawal, EffectiveUtc = effective.AddHours(1), Amount = 42.10m, Note = "Owner draw"
            });
            Assert.True(updated.Saved);
            Assert.Equal(2, updated.Transaction!.Revision);
            Assert.Equal(AccountTransactionType.Withdrawal, database.GetAccountTransaction(journal.Id, created.Id)!.Type);

            var conflict = database.UpdateAccountTransaction(journal.Id, created.Id, 1, new AccountTransactionDraft
            {
                Type = AccountTransactionType.Deposit, EffectiveUtc = effective, Amount = 99m
            });
            Assert.True(conflict.Conflict);
            Assert.Equal(42.10m, database.GetAccountTransaction(journal.Id, created.Id)!.Amount);

            var history = database.GetAccountTransactionHistory(journal.Id, created.Id);
            Assert.Equal(["updated", "created"], history.Select(entry => entry.Action).ToArray());
            Assert.Contains(history, entry => entry.BeforeJson.Contains("Initial funding", StringComparison.Ordinal));
            Assert.Contains(history, entry => entry.AfterJson.Contains("Owner draw", StringComparison.Ordinal));

            var deleted = database.DeleteAccountTransaction(journal.Id, created.Id, 2);
            Assert.True(deleted.Saved);
            Assert.Empty(database.GetAccountTransactions(journal.Id));
            var tombstone = Assert.Single(database.GetAccountTransactions(journal.Id, includeDeleted: true));
            Assert.Equal(3, tombstone.Revision);
            Assert.NotNull(tombstone.DeletedUtc);
            Assert.Contains(database.GetAccountTransactionHistory(journal.Id, created.Id), entry => entry.Action == "deleted");

            var parsed = new ParsedImport { SourceType = "sierra-account-balance", SourceApplication = TradeFoundryConstants.SierraChart };
            parsed.Records.Add(new ParsedRecord
            {
                SourceType = parsed.SourceType, SourceKey = "balance-1", RowNumber = 1, PayloadJson = "{}",
                AccountBalance = new AccountBalanceDraft
                {
                    SourceType = parsed.SourceType, SourceKey = "balance-1", EventUtc = effective, SourceTimeText = effective.ToString("O"),
                    Account = "SIM", Balance = 2_000m, RowNumber = 1
                }
            });
            var import = database.CommitImport(journal.Id, "balance.csv", parsed, "flat_to_flat", "source");
            Assert.Single(database.GetAccountBalanceEvents(journal.Id));
            database.RemoveImportBatch(import.Batch.Id);
            Assert.Empty(database.GetAccountBalanceEvents(journal.Id));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void CorrectionsAdjustBalanceBySignWithoutCountingAsCashFlowOrProfit()
    {
        var journalId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var journal = Journal(journalId, 1_000m, start);
        var raise = Transaction(journalId, AccountTransactionType.Correction, 40m, start.AddDays(1), 1);
        var lower = Transaction(journalId, AccountTransactionType.Correction, -15m, start.AddDays(2), 2);
        var trade = Trade(journalId, 100m, start.AddDays(3), 1);

        var ledger = AccountLedgerService.Build(journal, [raise, lower], [trade], []);

        Assert.Equal(1_125m, ledger.Summary.CurrentBalance);
        Assert.Equal(25m, ledger.Summary.NetCorrections);
        Assert.Equal(0m, ledger.Summary.TotalDeposits);
        Assert.Equal(0m, ledger.Summary.TotalWithdrawals);
        Assert.Equal(100m, ledger.Summary.RealizedProfit);
        var negative = Assert.Single(ledger.Entries, entry => entry.Amount == -15m);
        Assert.Equal("correction", negative.Kind);
        Assert.Equal("Correction", negative.Label);
        Assert.Equal(1_025m, negative.CalculatedBalance);

        var windowed = AccountLedgerService.Build(journal, [raise, lower], [trade], [], 1, 50, start.AddDays(2), start.AddDays(4));
        Assert.Equal(-15m, windowed.Period!.Corrections);
        Assert.Equal(1_040m, windowed.Period.OpeningBalance);
    }

    [Fact]
    public void CorrectionsPersistSignedAndRejectZero()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var effective = new DateTimeOffset(2026, 9, 10, 13, 15, 0, TimeSpan.Zero);

            var created = database.CreateAccountTransaction(journal.Id, new AccountTransactionDraft
            {
                Type = AccountTransactionType.Correction, EffectiveUtc = effective, Amount = -12.34m, Note = "Broker fee not imported"
            });
            var stored = database.GetAccountTransaction(journal.Id, created.Id)!;
            Assert.Equal(AccountTransactionType.Correction, stored.Type);
            Assert.Equal(-12.34m, stored.Amount);

            Assert.Throws<ArgumentException>(() => database.CreateAccountTransaction(journal.Id, new AccountTransactionDraft
            {
                Type = AccountTransactionType.Correction, EffectiveUtc = effective, Amount = 0m
            }));
            Assert.Throws<ArgumentException>(() => database.CreateAccountTransaction(journal.Id, new AccountTransactionDraft
            {
                Type = AccountTransactionType.Deposit, EffectiveUtc = effective, Amount = -5m
            }));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void LegacyAccountTransactionTableIsRebuiltToAcceptCorrections()
    {
        var directory = NewDirectory();
        try
        {
            Guid journalId;
            Guid depositId;
            var effective = new DateTimeOffset(2026, 9, 10, 13, 15, 0, TimeSpan.Zero);
            {
                var database = CreateDatabase(directory);
                database.CreateOwner("Owner", "test-hash");
                journalId = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat").Id;
                depositId = database.CreateAccountTransaction(journalId, new AccountTransactionDraft
                {
                    Type = AccountTransactionType.Deposit, EffectiveUtc = effective, Amount = 500m, Note = "Funding"
                }).Id;
            }

            // Recreate the pre-correction schema with its narrower CHECK constraint.
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(directory, "journal.db")};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA foreign_keys = OFF;
                    BEGIN;
                    CREATE TABLE legacy (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, transaction_type TEXT NOT NULL CHECK(transaction_type IN ('deposit', 'withdrawal')), effective_utc TEXT NOT NULL, amount TEXT NOT NULL, note TEXT NOT NULL DEFAULT '', revision INTEGER NOT NULL DEFAULT 1, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL, deleted_utc TEXT NULL);
                    INSERT INTO legacy SELECT id, journal_id, transaction_type, effective_utc, amount, note, revision, created_utc, updated_utc, deleted_utc FROM account_transactions;
                    DROP TABLE account_transactions;
                    ALTER TABLE legacy RENAME TO account_transactions;
                    COMMIT;
                    """;
                command.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            var migrated = CreateDatabase(directory);
            var existing = migrated.GetAccountTransaction(journalId, depositId)!;
            Assert.Equal(500m, existing.Amount);
            Assert.Single(migrated.GetAccountTransactionHistory(journalId, depositId));

            var correction = migrated.CreateAccountTransaction(journalId, new AccountTransactionDraft
            {
                Type = AccountTransactionType.Correction, EffectiveUtc = effective.AddHours(1), Amount = -20m
            });
            Assert.Equal(2, migrated.GetAccountTransactions(journalId).Count);
            Assert.Equal(-20m, migrated.GetAccountTransaction(journalId, correction.Id)!.Amount);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void ShowFilterNarrowsRowsButKeepsFullHistoryBalances()
    {
        var journalId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var journal = Journal(journalId, 1_000m, start);
        var deposit = Transaction(journalId, AccountTransactionType.Deposit, 500m, start.AddDays(1), 1);
        var withdrawal = Transaction(journalId, AccountTransactionType.Withdrawal, 100m, start.AddDays(3), 1);
        var correction = Transaction(journalId, AccountTransactionType.Correction, -25m, start.AddDays(4), 1);
        var trade = Trade(journalId, 200m, start.AddDays(2), 1);
        var snapshot = Snapshot(journalId, 1_700m, start.AddDays(2).AddHours(1), 1);

        AccountLedgerPage Show(string? show) => AccountLedgerService.Build(journal, [deposit, withdrawal, correction], [trade], [snapshot], show: show);

        var manual = Show(AccountLedgerShow.Manual);
        Assert.Equal(AccountLedgerShow.Manual, manual.Show);
        Assert.Equal(["correction", "withdrawal", "deposit"], manual.Entries.Select(entry => entry.Kind));
        Assert.All(manual.Entries, entry => Assert.False(entry.IsReadOnly));
        Assert.Equal(1_575m, manual.Entries[0].CalculatedBalance);
        Assert.Equal(1_575m, manual.Summary.CurrentBalance);

        Assert.Equal(["withdrawal"], Show(AccountLedgerShow.Withdrawals).Entries.Select(entry => entry.Kind));
        Assert.Equal(["realized_trade_pnl"], Show(AccountLedgerShow.Trades).Entries.Select(entry => entry.Kind));
        Assert.Equal(["imported_balance", "opening_balance"], Show(AccountLedgerShow.Balances).Entries.Select(entry => entry.Kind));
        Assert.Equal(6, Show(null).TotalCount);
        Assert.Equal(AccountLedgerShow.All, Show("bogus").Show);
        Assert.Equal(6, Show("bogus").TotalCount);
    }

    [Fact]
    public void PagePastTheEndShowsTheLastPage()
    {
        var journalId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var journal = Journal(journalId, 1_000m, start);
        var deposits = Enumerable.Range(1, 5).Select(day => Transaction(journalId, AccountTransactionType.Deposit, 10m, start.AddDays(day), 1)).ToArray();

        var ledger = AccountLedgerService.Build(journal, deposits, [], [], page: 9, pageSize: 2, show: AccountLedgerShow.Manual);

        Assert.Equal(3, ledger.Page);
        Assert.Equal(3, ledger.PageCount);
        Assert.Single(ledger.Entries);
    }

    private static Journal Journal(Guid id, decimal? startingEquity, DateTimeOffset createdUtc) => new()
    {
        Id = id, Name = "Test", TimeZone = "UTC", Currency = "USD", StartingEquity = startingEquity, CreatedUtc = createdUtc
    };

    private static AccountTransaction Transaction(Guid journalId, AccountTransactionType type, decimal amount, DateTimeOffset effectiveUtc, int revision) => new()
    {
        Id = Guid.NewGuid(), JournalId = journalId, Type = type, Amount = amount, EffectiveUtc = effectiveUtc,
        Revision = revision, CreatedUtc = effectiveUtc.AddMinutes(-revision), UpdatedUtc = effectiveUtc.AddMinutes(-revision)
    };

    private static Trade Trade(Guid journalId, decimal netPnl, DateTimeOffset exitUtc, int sequence) => new()
    {
        Id = Guid.NewGuid(), JournalId = journalId, Sequence = sequence, Symbol = "MESZ26", Instrument = "MES", Account = "SIM",
        Direction = "Long", EntryUtc = exitUtc.AddMinutes(-30), ExitUtc = exitUtc, Status = "closed", Quantity = 1, ClosedQuantity = 1, NetPnl = netPnl
    };

    private static AccountBalanceEvent Snapshot(Guid journalId, decimal balance, DateTimeOffset eventUtc, int rowNumber) => new()
    {
        Id = Guid.NewGuid(), JournalId = journalId, ImportBatchId = Guid.NewGuid(), SourceType = "sierra-account-balance",
        SourceKey = $"balance-{rowNumber}", EventUtc = eventUtc, Account = "SIM", Balance = balance, RowNumber = rowNumber
    };

    private static TradeFoundryDb CreateDatabase(string directory) => new(Options.Create(new StorageOptions { DataDirectory = directory, DatabaseFileName = "journal.db" }));

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "TradeFoundry.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string directory)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

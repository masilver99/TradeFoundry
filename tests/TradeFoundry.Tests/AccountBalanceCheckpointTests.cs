using TradeFoundry.Core;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class AccountBalanceCheckpointTests
{
    private static readonly Guid JournalId = Guid.NewGuid();

    [Fact]
    public void StatementsGiveTheEarliestOpeningAndEachCloseAtTheChicagoSessionClose()
    {
        var first = Statement(BrokerStatementKind.Daily, new(2026, 11, 23), new(2026, 11, 23), opening: 1_000m, newCash: 1_050m);
        var second = Statement(BrokerStatementKind.Daily, new(2026, 11, 24), new(2026, 11, 24), opening: 1_050m, newCash: 990m);

        var checkpoints = AccountBalanceCheckpoints.FromStatements([second, first]);

        Assert.Collection(checkpoints,
            opening =>
            {
                // The Nov 23 trade date opens at 5 p.m. Chicago (CST, UTC-6) on Nov 22.
                Assert.Equal(new DateTimeOffset(2026, 11, 22, 23, 0, 0, TimeSpan.Zero), opening.EventUtc);
                Assert.Equal(1_000m, opening.Balance);
                Assert.True(AccountBalanceCheckpoints.IsStatement(opening));
            },
            close =>
            {
                Assert.Equal(new DateTimeOffset(2026, 11, 23, 23, 0, 0, TimeSpan.Zero), close.EventUtc);
                Assert.Equal(1_050m, close.Balance);
            },
            close =>
            {
                Assert.Equal(new DateTimeOffset(2026, 11, 24, 23, 0, 0, TimeSpan.Zero), close.EventUtc);
                Assert.Equal(990m, close.Balance);
            });
        // Rebuilding gives the same ids, so a checkpoint is stable across page loads.
        Assert.Equal(checkpoints.Select(c => c.Id), AccountBalanceCheckpoints.FromStatements([first, second]).Select(c => c.Id));
    }

    [Fact]
    public void SessionCloseFollowsDaylightSavingTime()
    {
        // CDT is UTC-5.
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 22, 0, 0, TimeSpan.Zero), AccountBalanceCheckpoints.SessionCloseUtc(new(2026, 7, 1)));
    }

    [Fact]
    public void AMonthlyStatementWinsOverADailyOneClosingTheSameDay()
    {
        var daily = Statement(BrokerStatementKind.Daily, new(2026, 10, 30), new(2026, 10, 30), opening: null, newCash: 1_000m, imported: 2);
        var monthly = Statement(BrokerStatementKind.Monthly, new(2026, 10, 1), new(2026, 10, 30), opening: null, newCash: 1_010m, imported: 1);

        var checkpoint = Assert.Single(AccountBalanceCheckpoints.FromStatements([daily, monthly]));

        Assert.Equal(1_010m, checkpoint.Balance);
        Assert.Contains("monthly", checkpoint.Note);
    }

    [Fact]
    public void StatementsReplacePlatformBalancesAndPlatformBalancesRemainTheFallback()
    {
        var sierra = new AccountBalanceEvent { Id = Guid.NewGuid(), JournalId = JournalId, SourceType = "sierra-account-balance", EventUtc = DateTimeOffset.UnixEpoch, Balance = 5m };
        var statement = Statement(BrokerStatementKind.Daily, new(2026, 11, 23), new(2026, 11, 23), opening: 1_000m, newCash: 1_050m);

        Assert.All(AccountBalanceCheckpoints.Select([statement], [sierra]), c => Assert.True(AccountBalanceCheckpoints.IsStatement(c)));
        Assert.Same(sierra, Assert.Single(AccountBalanceCheckpoints.Select([], [sierra])));
        // A statement without any balance figures gives nothing to compare, so the platform balances stay.
        var blank = Statement(BrokerStatementKind.Daily, new(2026, 11, 23), new(2026, 11, 23), opening: null, newCash: null);
        Assert.Same(sierra, Assert.Single(AccountBalanceCheckpoints.Select([blank], [sierra])));
    }

    [Fact]
    public void StatementsForSeveralAccountsGiveNoJournalWideBalance()
    {
        var one = Statement(BrokerStatementKind.Daily, new(2026, 11, 23), new(2026, 11, 23), opening: 1_000m, newCash: 1_050m, account: "111");
        var two = Statement(BrokerStatementKind.Daily, new(2026, 11, 23), new(2026, 11, 23), opening: 2_000m, newCash: 2_050m, account: "222");

        Assert.Empty(AccountBalanceCheckpoints.FromStatements([one, two]));
    }

    [Fact]
    public void LedgerOpensOnTheStatementCashBalanceAndReconcilesEachClose()
    {
        var journal = new Journal { Id = JournalId, Name = "Test", TimeZone = "UTC", Currency = "USD", CreatedUtc = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero) };
        var close = AccountBalanceCheckpoints.SessionCloseUtc(new(2026, 11, 23));
        var trades = new[]
        {
            // Before the first statement, so outside the account period.
            Trade(25m, new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero), 1),
            Trade(50m, close.AddHours(-2), 2),
            // Exits as the next session opens: the Nov 24 trade date, after the Nov 23 close.
            Trade(20m, close, 3)
        };
        var statement = Statement(BrokerStatementKind.Daily, new(2026, 11, 23), new(2026, 11, 23), opening: 1_000m, newCash: 1_050m);

        var ledger = AccountLedgerService.Build(journal, [], trades, AccountBalanceCheckpoints.FromStatements([statement]));

        Assert.Equal("First broker statement cash balance", ledger.Summary.OpeningBalanceSource);
        Assert.Equal(1_000m, ledger.Summary.OpeningBalance);
        Assert.True(ledger.Summary.HasPreBaselineActivity);
        Assert.Equal(1_070m, ledger.Summary.CurrentBalance);
        Assert.Equal(1_050m, ledger.Summary.LatestReportedBalance);
        Assert.Equal(0m, ledger.Summary.LatestReportedVariance);
        Assert.Equal("Broker statement", ledger.Summary.LatestReportedSource);
        // The statement balance, plus the trade recorded after it.
        Assert.Equal(1_070m, ledger.Summary.ReportedCurrentBalance);
        var closeRow = Assert.Single(ledger.Entries, e => e.Label == "Statement balance");
        Assert.Equal(1_050m, closeRow.CalculatedBalance);
        Assert.Equal("statement.pdf", closeRow.Note);
    }

    private static readonly TimeSpan Est = TimeSpan.FromHours(-5);
    private static readonly DateTimeOffset Now = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Nov 23 opens at $1,000 and closes at $1,050 after a $50 morning trade. A $20 trade at 6:30 p.m. Eastern belongs
    /// to the Nov 24 trade date but to the journal's Nov 23; then $10 after midnight and $20 on the Nov 24 morning.
    /// </summary>
    private static (Journal Journal, Trade[] Trades) EasternDays()
    {
        var journal = new Journal { Id = JournalId, Name = "Test", TimeZone = "America/New_York", Currency = "USD", CreatedUtc = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero) };
        return (journal,
        [
            Trade(50m, new(2026, 11, 23, 10, 0, 0, Est), 1),
            Trade(20m, new(2026, 11, 23, 18, 30, 0, Est), 2),
            Trade(10m, new(2026, 11, 24, 1, 0, 0, Est), 3),
            Trade(20m, new(2026, 11, 24, 10, 0, 0, Est), 4)
        ]);
    }

    [Fact]
    public void EachJournalMidnightAfterAStatementGetsTheStatementBalancePlusTheEveningAndIsCheckedByTheNextStatement()
    {
        var (journal, trades) = EasternDays();
        var statements = new[]
        {
            Statement(BrokerStatementKind.Daily, new(2026, 11, 23), new(2026, 11, 23), opening: 1_000m, newCash: 1_050m),
            Statement(BrokerStatementKind.Daily, new(2026, 11, 24), new(2026, 11, 24), opening: 1_050m, newCash: 1_100m)
        };

        var ledger = AccountLedgerService.Build(journal, [], trades, AccountBalanceCheckpoints.FromStatements(statements), nowUtc: Now);

        var midnights = ledger.Entries.Where(e => e.Kind == "midnight_balance").OrderBy(e => e.EffectiveUtc).ToArray();
        Assert.Collection(midnights,
            // The opening cash balance is struck at 6 p.m. Eastern on Nov 22, with nothing after it before midnight.
            nov22 =>
            {
                Assert.Equal(new DateTimeOffset(2026, 11, 23, 0, 0, 0, Est), nov22.EffectiveUtc);
                Assert.Equal(1_000m, nov22.ReportedBalance);
                Assert.Equal(0m, nov22.NextStatementDifference);
                Assert.Contains("no activity after it", nov22.Description);
            },
            nov23 =>
            {
                Assert.Equal(new DateTimeOffset(2026, 11, 24, 0, 0, 0, Est), nov23.EffectiveUtc);
                Assert.Equal(1_070m, nov23.ReportedBalance);
                Assert.Equal(1_070m, nov23.CalculatedBalance);
                Assert.Equal(0m, nov23.Variance);
                Assert.Equal(0m, nov23.NextStatementDifference);
                Assert.Contains("plus 1 trade after it", nov23.Description);
                Assert.Contains("Nov 24, 2026", nov23.NextStatementDescription);
            },
            nov24 =>
            {
                Assert.Equal(1_100m, nov24.ReportedBalance);
                Assert.Null(nov24.NextStatementDifference);
            });

        // The derived rows never stand in for the statements themselves.
        Assert.Equal(1_100m, ledger.Summary.LatestReportedBalance);
        Assert.Equal("Broker statement", ledger.Summary.LatestReportedSource);
        Assert.Equal(1_100m, ledger.Summary.ReportedCurrentBalance);
        Assert.Equal(1_100m, ledger.Summary.CurrentBalance);
    }

    [Fact]
    public void AMidnightBalanceShowsHowFarTheNextStatementIsFromIt()
    {
        var (journal, trades) = EasternDays();
        var statements = new[]
        {
            Statement(BrokerStatementKind.Daily, new(2026, 11, 23), new(2026, 11, 23), opening: 1_000m, newCash: 1_050m),
            // $5 more than the journal's trades explain.
            Statement(BrokerStatementKind.Daily, new(2026, 11, 24), new(2026, 11, 24), opening: 1_050m, newCash: 1_105m)
        };

        var ledger = AccountLedgerService.Build(journal, [], trades, AccountBalanceCheckpoints.FromStatements(statements), nowUtc: Now);

        var nov23 = Assert.Single(ledger.Entries, e => e.Kind == "midnight_balance" && e.EffectiveUtc == new DateTimeOffset(2026, 11, 24, 0, 0, 0, Est));
        Assert.Equal(1_070m, nov23.ReportedBalance);
        Assert.Equal(5m, nov23.NextStatementDifference);
    }

    [Fact]
    public void AMidnightThatHasNotHappenedYetGivesNoBalance()
    {
        var (journal, trades) = EasternDays();
        var statement = Statement(BrokerStatementKind.Daily, new(2026, 11, 23), new(2026, 11, 23), opening: 1_000m, newCash: 1_050m);

        var ledger = AccountLedgerService.Build(journal, [], trades, AccountBalanceCheckpoints.FromStatements([statement]), nowUtc: new DateTimeOffset(2026, 11, 23, 20, 0, 0, Est));

        Assert.Equal([new DateTimeOffset(2026, 11, 23, 0, 0, 0, Est)], ledger.Entries.Where(e => e.Kind == "midnight_balance").Select(e => e.EffectiveUtc));
    }

    [Fact]
    public void MidnightBalancesAreListedWithTheBalances()
    {
        var (journal, trades) = EasternDays();
        var statement = Statement(BrokerStatementKind.Daily, new(2026, 11, 23), new(2026, 11, 23), opening: 1_000m, newCash: 1_050m);
        var checkpoints = AccountBalanceCheckpoints.FromStatements([statement]);

        var balances = AccountLedgerService.Build(journal, [], trades, checkpoints, show: AccountLedgerShow.Balances, nowUtc: Now);
        var tradesOnly = AccountLedgerService.Build(journal, [], trades, checkpoints, show: AccountLedgerShow.Trades, nowUtc: Now);

        Assert.Equal(2, balances.Entries.Count(e => e.Kind == "midnight_balance"));
        Assert.DoesNotContain(balances.Entries, e => e.TradeId.HasValue);
        Assert.DoesNotContain(tradesOnly.Entries, e => e.Kind == "midnight_balance");
    }

    [Fact]
    public void PlatformBalancesGiveNoMidnightBalance()
    {
        var (journal, trades) = EasternDays();
        var sierra = new AccountBalanceEvent { Id = Guid.NewGuid(), JournalId = JournalId, SourceType = "sierra-account-balance", EventUtc = new(2026, 11, 23, 12, 0, 0, Est), Balance = 1_050m };

        var ledger = AccountLedgerService.Build(journal, [], trades, [sierra], nowUtc: Now);

        Assert.DoesNotContain(ledger.Entries, e => e.Kind == "midnight_balance");
    }

    private static BrokerStatement Statement(BrokerStatementKind kind, DateOnly start, DateOnly end, decimal? opening, decimal? newCash, int imported = 0, string account = "12345") =>
        new(Guid.NewGuid(), JournalId, "AMP", kind, account, start, end, "statement.pdf", Guid.NewGuid().ToString("N"),
            new StatementAccountSummary(opening, null, null, newCash, newCash is null ? null : newCash + 100m), [], 1,
            new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(imported));

    private static Trade Trade(decimal netPnl, DateTimeOffset exitUtc, int sequence) => new()
    {
        Id = Guid.NewGuid(), JournalId = JournalId, Sequence = sequence, Symbol = "MESZ26", Instrument = "MES", Account = "12345",
        Direction = "Long", EntryUtc = exitUtc.AddMinutes(-30), ExitUtc = exitUtc, Status = "closed", Quantity = 1, ClosedQuantity = 1, NetPnl = netPnl
    };
}

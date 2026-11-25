using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;
using TradeFoundry.Services.Statements;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class StatementReconciliationTests : IDisposable
{
    // Column positions (in characters) of the statement layout; a Courier 8pt character is 4.8pt wide.
    private const int Buy = 30;
    private const int Sell = 38;
    private const double CharWidth = 4.8d;
    private const double LeftMargin = 20d;

    private readonly string _directory = Path.Combine(Environment.GetEnvironmentVariable("TRADEFOUNDRY_TEST_ROOT") ?? Path.GetTempPath(), "statement-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TradeFoundryDb _db;
    private readonly Journal _journal;
    private readonly StatementReconciliationService _service;

    public StatementReconciliationTests()
    {
        Directory.CreateDirectory(_directory);
        _db = new TradeFoundryDb(Options.Create(new StorageOptions { DataDirectory = _directory }));
        _db.CreateOwner("Owner", "test-hash");
        _journal = _db.CreateJournal("Live", "live", "", "America/New_York", "USD", "flat_to_flat", 1000m);
        _service = new StatementReconciliationService(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- statement fixtures -------------------------------------------------------------

    private sealed record Row(params (int Column, string Text)[] Cells);

    private static Row Text(int column, string text) => new((column, text));

    private static Row Header() => new((0, "DATE"), (10, "NUMBER"), (19, "MARKET"), (Buy - 1, "BUY"), (Sell - 1, "SELL"), (43, "CONTRACT DESCRIPTION"), (70, "TRADE PRICE"), (84, "CCY"), (90, "DEBIT/CREDIT"));

    private static Row Fill(string date, string number, string side, int quantity, string price) =>
        new((0, date), (10, number), (19, "CME"), (side == "BUY" ? Buy : Sell, quantity.ToString()), (43, "MES Future SEP 26"), (72, price), (84, "USD"));

    private static Row Total(int buy, int sell, string? pnl = null)
    {
        var cells = new List<(int, string)> { (0, "TOTAL") };
        if (buy > 0) cells.Add((Buy, buy.ToString()));
        if (sell > 0) cells.Add((Sell, sell.ToString()));
        cells.Add((43, "EX- 18-SEP-26"));
        if (pnl is not null) cells.Add((70, "P&S USD " + pnl));
        return new([.. cells]);
    }

    private static Row Fee(string name, string amount) => new((62, name), (80, "USD"), (90, amount + " DR"));

    private static IEnumerable<Row> FeeBlock(int roundTurns, decimal exchange = .35m, decimal nfa = .01m, decimal clearing = .13m) =>
    [
        Fee("Exchange", (exchange * roundTurns * 2).ToString("0.00")),
        Fee("NFA", (nfa * roundTurns * 2).ToString("0.00")),
        Fee("Clearing Teton", (clearing * roundTurns * 2).ToString("0.00")),
        new((50, "AVERAGE LONG"), (66, "7612.00000"))
    ];

    private static List<Row> DailyStatement() =>
    [
        Text(35, "DAILY STATEMENT"),
        new((5, "Test Trader"), (66, "14-SEP-26")),
        new((5, "1 Main Street"), (66, "Test Trader")),
        new((5, "Springfield"), (66, "123456")),
        Text(30, "T R A D E S   C O N F I R M A T I O N S"),
        Text(0, "THE FOLLOWING TRADES HAVE BEEN EXECUTED THIS DAY FOR YOUR ACCOUNT AND RISK."),
        Header(),
        Fill("14-SEP-26", "1001", "SELL", 1, "7670.75"),
        Fill("14-SEP-26", "1002", "BUY", 1, "7674.00"),
        Fill("14-SEP-26", "1003", "SELL", 2, "7685.75"),
        Fill("14-SEP-26", "1004", "BUY", 2, "7680.00"),
        Total(3, 3),
        .. FeeBlock(3),
        Text(30, "P U R C H A S E   &   S A L E"),
        Header(),
        Fill("14-SEP-26", "1001", "SELL", 1, "7670.75"),
        Total(3, 3, "41.25 CR"),
        Text(25, "Account Summary as of 09/14/26"),
        new((0, "ACCOUNT CASH BALANCE"), (40, "1,000.00 CR"), (70, "1,000.00 CR")),
        new((0, "REALIZED PROFIT/LOSS"), (40, "41.25 CR"), (70, "41.25 CR")),
        new((0, "TOTAL COMMISSION & FEES"), (40, "2.94 DR"), (70, "2.94 DR")),
        new((0, "NEW CASH BALANCE"), (40, "1,038.31 CR"), (70, "1,038.31 CR"))
    ];

    /// <summary>Aug 3 (+$17.50), Aug 4 (+$81.25) and Aug 5 (−$5.00), with Aug 4's fees continued on page 2.</summary>
    private static List<List<Row>> MonthlyStatement(decimal clearing = .13m) =>
    [
        [
            Text(35, "MONTHLY STATEMENT"),
            new((5, "Test Trader"), (66, "Test Trader")),
            new((5, "1 Main Street"), (66, "123456")),
            Text(19, "T R A D E S   C O N F I R M A T I O N S   F R O M  01-AUG-26  T O  31-AUG-26"),
            Text(0, "THE FOLLOWING TRADES HAVE BEEN EXECUTED AS OF 03-AUG-26"),
            Header(),
            Fill("03-AUG-26", "2001", "BUY", 1, "7597.50"),
            Fill("03-AUG-26", "2002", "SELL", 1, "7599.50"),
            Fill("03-AUG-26", "2003", "BUY", 1, "7612.00"),
            Fill("03-AUG-26", "2004", "SELL", 1, "7612.25"),
            Fill("03-AUG-26", "2005", "BUY", 1, "7626.50"),
            Fill("03-AUG-26", "2006", "SELL", 1, "7627.75"),
            Total(3, 3),
            .. FeeBlock(3, clearing: clearing),
            Text(0, "THE FOLLOWING TRADES HAVE BEEN EXECUTED AS OF 04-AUG-26"),
            Header(),
            Fill("04-AUG-26", "2101", "BUY", 1, "7700.50"),
            Fill("04-AUG-26", "2102", "SELL", 1, "7716.75"),
            Total(1, 1),
        ],
        [
            new((0, "01-SEP-26"), (66, "123456")),
            Text(19, "T R A D E S   C O N F I R M A T I O N S   F R O M  01-AUG-26  T O  31-AUG-26"),
            Text(0, "THE FOLLOWING TRADES HAVE BEEN EXECUTED AS OF 04-AUG-26"),
            Header(),
            .. FeeBlock(1, clearing: clearing),
            Text(0, "THE FOLLOWING TRADES HAVE BEEN EXECUTED AS OF 05-AUG-26"),
            Header(),
            Fill("05-AUG-26", "2201", "BUY", 1, "7800.00"),
            Fill("05-AUG-26", "2202", "SELL", 1, "7799.00"),
            Total(1, 1),
            .. FeeBlock(1, clearing: clearing),
            Text(19, "P U R C H A S E   &   S A L E   F R O M  08/01/26  T O  08/31/26"),
            Text(0, "THE FOLLOWING PURCHASE & SALE HAVE BEEN MADE AS OF 03-AUG-26"),
            Header(),
            Fill("03-AUG-26", "2001", "BUY", 1, "7597.50"),
            Total(3, 3, "17.50 CR"),
            Text(0, "THE FOLLOWING PURCHASE & SALE HAVE BEEN MADE AS OF 04-AUG-26"),
            Header(),
            Total(1, 1, "81.25 CR"),
            Text(0, "THE FOLLOWING PURCHASE & SALE HAVE BEEN MADE AS OF 05-AUG-26"),
            Header(),
            Total(1, 1, "5.00 DR"),
            Text(25, "Account Summary from 08/03/26 to 08/31/26"),
            new((0, "ACCOUNT CASH BALANCE"), (40, "2,000.00 CR")),
            new((0, "REALIZED PROFIT/LOSS"), (40, "93.75 CR")),
            new((0, "NEW CASH BALANCE"), (40, "2,086.89 CR"))
        ]
    ];

    private static IEnumerable<StatementWord> Words(Row row)
    {
        foreach (var (column, text) in row.Cells)
        {
            foreach (Match token in Regex.Matches(text, @"\S+"))
            {
                var left = LeftMargin + (column + token.Index) * CharWidth;
                yield return new StatementWord(token.Value, left, left + token.Length * CharWidth);
            }
        }
    }

    private static List<StatementLine> Lines(params List<Row>[] pages) =>
        pages.SelectMany((rows, index) => rows.Select(row => new StatementLine(index + 1, Words(row).OrderBy(w => w.Left).ToArray()))).ToList();

    private static byte[] Pdf(params List<Row>[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Courier);
        foreach (var rows in pages)
        {
            var page = builder.AddPage(PageSize.Letter);
            var y = 760d;
            foreach (var row in rows)
            {
                foreach (var word in Words(row)) page.AddText(word.Text, 8, new PdfPoint(word.Left, y), font);
                y -= 11d;
            }
        }
        return builder.Build();
    }

    private async Task<StatementImportOutcome> Import(byte[] pdf, string name)
    {
        using var stream = new MemoryStream(pdf);
        return await _service.ImportAsync(_journal.Id, stream, name, pdf.Length);
    }

    /// <summary>Imports MES round trips as Sierra fills, with the instrument charging the given clearing rate per side.</summary>
    private void ImportTrades(decimal clearingPerSide, params (string Time, string Side, int Quantity, decimal Price)[] fills)
    {
        var instrument = Assert.Single(_db.GetInstruments(), item => item.Code == "MES");
        Assert.True(_db.SaveInstrument(instrument.Id, "MES", null, .35m, .01m, clearingPerSide, 5m, .25m));
        var csv = string.Join('\n', new[] { "Activity Type,DateTime,Symbol,BuySell,Quantity,Fill Price" }
            .Concat(fills.Select(f => $"Fills,{f.Time},MESU26,{f.Side},{f.Quantity},{f.Price}")));
        var parsed = new ImportService(_db).Parse(csv, "sierra", "source", "UTC", configuration: _db.GetInstrumentConfiguration());
        _db.CommitImport(_journal.Id, $"fills-{Guid.NewGuid():N}.txt", parsed, "flat_to_flat", "source");
    }

    private static readonly (string, string, int, decimal)[] AugustFills =
    [
        ("2026-08-03 14:00:00", "Buy", 1, 7597.50m), ("2026-08-03 14:05:00", "Sell", 1, 7599.50m),
        ("2026-08-03 15:00:00", "Buy", 1, 7612.00m), ("2026-08-03 15:05:00", "Sell", 1, 7612.25m),
        ("2026-08-03 16:00:00", "Buy", 1, 7626.50m), ("2026-08-03 16:05:00", "Sell", 1, 7627.75m),
        ("2026-08-04 14:00:00", "Buy", 1, 7700.50m), ("2026-08-04 14:30:00", "Sell", 1, 7716.75m),
        // Inside the statement's month, but not on it: perhaps another account.
        ("2026-08-10 14:00:00", "Buy", 1, 7800.00m), ("2026-08-10 14:10:00", "Sell", 1, 7801.00m),
        // After the statement's month: not covered by any statement.
        ("2026-09-02 14:00:00", "Buy", 1, 7900.00m), ("2026-09-02 14:10:00", "Sell", 1, 7901.00m)
    ];

    // ---- parser ---------------------------------------------------------------------------

    [Fact]
    public void ParsesDailyStatementFillSidesFeesPnlAndSummary()
    {
        var statement = AmpStatementParser.Parse(Lines(DailyStatement()));

        Assert.Equal(BrokerStatementKind.Daily, statement.Kind);
        Assert.Equal("123456", statement.Account);
        Assert.Equal(new DateOnly(2026, 9, 14), statement.PeriodStart);
        Assert.Equal(statement.PeriodStart, statement.PeriodEnd);
        var day = Assert.Single(statement.Days);
        Assert.Equal((3, 3), (day.BuyContracts, day.SellContracts));
        Assert.Equal(["SELL", "BUY", "SELL", "BUY"], day.Fills.Select(f => f.Side));
        Assert.Equal([1, 1, 2, 2], day.Fills.Select(f => f.Quantity));
        Assert.Equal(7685.75m, day.Fills[2].Price);
        Assert.Equal("MES Future SEP 26", day.Fills[0].Contract);
        Assert.Equal((2.10m, .06m, .78m, 2.94m), (day.ExchangeFees, day.NfaFees, day.ClearingFees, day.TotalFees));
        Assert.Equal(["Exchange", "NFA", "Clearing Teton"], day.Fees.Select(f => f.Name));
        Assert.True(day.HasRealizedPnl);
        Assert.Equal(41.25m, day.RealizedPnl);
        Assert.Equal(new StatementAccountSummary(1000m, 41.25m, 2.94m, 1038.31m, null), statement.Summary);
        Assert.Empty(statement.Warnings);
    }

    [Fact]
    public void ParsesMonthlyStatementDaysAndFeesContinuedOnTheNextPage()
    {
        var pages = MonthlyStatement();
        var statement = AmpStatementParser.Parse(Lines(pages[0], pages[1]));

        Assert.Equal(BrokerStatementKind.Monthly, statement.Kind);
        Assert.Equal((new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31)), (statement.PeriodStart, statement.PeriodEnd));
        Assert.Equal([new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 4), new DateOnly(2026, 8, 5)], statement.Days.Select(d => d.TradeDate));
        Assert.Equal([17.50m, 81.25m, -5.00m], statement.Days.Select(d => d.RealizedPnl));
        Assert.Equal([2.94m, .98m, .98m], statement.Days.Select(d => d.TotalFees));
        Assert.Equal([6, 2, 2], statement.Days.Select(d => d.ContractSides));
        Assert.Equal(2, statement.Days[1].Fills.Count);
        Assert.Equal(93.75m, statement.Summary.RealizedPnl);
        Assert.Null(statement.Summary.TotalFees);
        Assert.Empty(statement.Warnings);
    }

    [Fact]
    public void ReadsTheSameFiguresFromARenderedPdf()
    {
        var pages = MonthlyStatement();
        var fromPdf = AmpStatementParser.Parse(StatementText.ReadPdf(Pdf(pages[0], pages[1])));
        Assert.Equal([2.94m, .98m, .98m], fromPdf.Days.Select(d => d.TotalFees));
        Assert.Equal([17.50m, 81.25m, -5.00m], fromPdf.Days.Select(d => d.RealizedPnl));
        Assert.All(fromPdf.Days.SelectMany(d => d.Fills), fill => Assert.Contains(fill.Side, new[] { "BUY", "SELL" }));
        Assert.Empty(fromPdf.Warnings);
    }

    [Fact]
    public void RejectsFilesThatAreNotStatements()
    {
        Assert.Throws<StatementParseException>(() => AmpStatementParser.Parse(Lines([Text(0, "QUARTERLY NEWSLETTER"), Text(0, "Nothing to see here")])));
        Assert.Throws<StatementParseException>(() => StatementText.ReadPdf("%PDF-1.4 truncated"u8.ToArray()));
    }

    [Fact]
    public async Task ImportRejectsNonPdfContentAndSkipsDuplicates()
    {
        var notPdf = await Import("Date,Price\n"u8.ToArray(), "trades.csv");
        Assert.False(notPdf.Imported);
        Assert.Contains("not a PDF", notPdf.Message);

        var pdf = Pdf(DailyStatement());
        var first = await Import(pdf, "daily.pdf");
        var second = await Import(pdf, "renamed.pdf");
        Assert.True(first.Imported);
        Assert.True(second.Duplicate);
        Assert.Single(_db.GetBrokerStatements(_journal.Id));
        Assert.Single(_db.GetBrokerStatementDays(_journal.Id));
    }

    // ---- reconciliation -------------------------------------------------------------------

    [Fact]
    public async Task ReconcilesEachDayAndAppliesStatementFeesThroughTheReviewLayer()
    {
        // The journal charged $0.25 clearing per side; AMP charged $0.13.
        ImportTrades(.25m, AugustFills);
        var pages = MonthlyStatement();
        Assert.True((await Import(Pdf(pages[0], pages[1]), "august.pdf")).Imported);

        var report = _service.BuildReport(_journal.Id);
        var byDate = report.Days.ToDictionary(d => d.Date);
        Assert.Equal(ReconciliationStatus.FeesDiffer, byDate[new(2026, 8, 3)].Status);
        Assert.Equal(ReconciliationStatus.FeesDiffer, byDate[new(2026, 8, 4)].Status);
        Assert.Equal(ReconciliationStatus.MissingFromJournal, byDate[new(2026, 8, 5)].Status);
        Assert.Equal(ReconciliationStatus.NotOnStatement, byDate[new(2026, 8, 10)].Status);
        Assert.Equal([new DateOnly(2026, 9, 2)], report.UncoveredTradeDates);
        var august3 = byDate[new(2026, 8, 3)];
        Assert.Equal((6, 6), (august3.StatementContractSides, august3.JournalContractSides));
        Assert.Equal(17.50m, august3.JournalGrossPnl);
        Assert.Equal(3.66m, august3.JournalFees);
        Assert.Equal(.72m, august3.FeeDifference);
        Assert.True(august3.CanApplyFees);
        Assert.False(byDate[new(2026, 8, 5)].CanApplyFees);
        Assert.Equal(2, report.FeeOnlyDifferences.Count());

        var (days, trades) = _service.ApplyAllFeeDifferences(_journal.Id);
        Assert.Equal((2, 4), (days, trades));

        var after = _service.BuildReport(_journal.Id).Days.ToDictionary(d => d.Date);
        Assert.Equal(ReconciliationStatus.Matched, after[new(2026, 8, 3)].Status);
        Assert.Equal(ReconciliationStatus.Matched, after[new(2026, 8, 4)].Status);
        Assert.Equal(2.94m, after[new(2026, 8, 3)].JournalFees);
        Assert.Equal((2.10m, .06m), (after[new(2026, 8, 3)].JournalExchangeFees, after[new(2026, 8, 3)].JournalNfaFees));

        // The imported trades are untouched; the statement fees live in each trade's revisioned review.
        var trade = after[new(2026, 8, 3)].Trades[0];
        var annotation = _db.GetTradeReviewAnnotations(_journal.Id)[trade.ReviewKey];
        Assert.Equal((.70m, .02m, .26m), (annotation.ExchangeFees, annotation.NfaFees, annotation.ClearingFees));
        Assert.Null(annotation.AllInCommission);
        var history = _db.GetTradeReviewHistory(_journal.Id, trade.ReviewKey);
        Assert.Contains(history, h => h.Action == "statement fees" && h.Reason.Contains("august.pdf"));
        using (var connection = new SqliteConnection($"Data Source={_db.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT fees FROM trades WHERE review_key = $key";
            command.Parameters.AddWithValue("$key", trade.ReviewKey);
            Assert.Equal("1.22", Convert.ToDecimal(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ToString("0.00"));
        }

        // Nothing left to apply, and a fee-matched day refuses a second application.
        Assert.Equal((0, 0), _service.ApplyAllFeeDifferences(_journal.Id));
        Assert.Throws<InvalidOperationException>(() => _service.ApplyFees(_journal.Id, new DateOnly(2026, 8, 3)));
    }

    [Fact]
    public async Task KeepsOtherReviewFieldsWhenApplyingFees()
    {
        ImportTrades(.25m, AugustFills[6..8]);
        var trade = Assert.Single(_db.GetAllTrades(_journal.Id));
        var saved = _db.SaveTradeReview(_journal.Id, trade.ReviewKey, new TradeReviewPatch { Setup = "Opening drive", Lessons = "Waited for the retest", AllInCommission = 5m, ExpectedRevision = 0 });
        Assert.True(saved.Saved);
        var pages = MonthlyStatement();
        await Import(Pdf(pages[0], pages[1]), "august.pdf");

        Assert.Equal(1, _service.ApplyFees(_journal.Id, new DateOnly(2026, 8, 4)));

        var annotation = _db.GetTradeReviewAnnotations(_journal.Id)[trade.ReviewKey];
        Assert.Equal("Opening drive", annotation.Setup);
        Assert.Equal("Waited for the retest", annotation.Lessons);
        Assert.Null(annotation.AllInCommission);
        Assert.Equal(.98m, Assert.Single(_db.GetAllTrades(_journal.Id)).Fees);
    }

    [Fact]
    public async Task FlagsContractCountDifferencesWithoutOfferingTheFeeFix()
    {
        // Only two of Aug 3's three round trips are in the journal.
        ImportTrades(.13m, AugustFills[0..4]);
        var pages = MonthlyStatement();
        await Import(Pdf(pages[0], pages[1]), "august.pdf");

        var day = _service.BuildReport(_journal.Id).Days.Single(d => d.Date == new DateOnly(2026, 8, 3));
        Assert.Equal(ReconciliationStatus.TradesDiffer, day.Status);
        Assert.Equal((6, 4), (day.StatementContractSides, day.JournalContractSides));
        Assert.False(day.CanApplyFees);
        Assert.Throws<InvalidOperationException>(() => _service.ApplyFees(_journal.Id, day.Date));
    }

    [Fact]
    public async Task FlagsDailyAndMonthlyStatementsThatDisagree()
    {
        ImportTrades(.25m, AugustFills[0..6]);
        var pages = MonthlyStatement();
        await Import(Pdf(pages[0], pages[1]), "august.pdf");
        List<Row> daily =
        [
            Text(35, "DAILY STATEMENT"),
            new((5, "Test Trader"), (66, "03-AUG-26")),
            Text(30, "T R A D E S   C O N F I R M A T I O N S"),
            Text(0, "THE FOLLOWING TRADES HAVE BEEN EXECUTED THIS DAY FOR YOUR ACCOUNT AND RISK."),
            Header(),
            Fill("03-AUG-26", "2001", "BUY", 3, "7612.00"),
            Fill("03-AUG-26", "2002", "SELL", 3, "7613.16"),
            Total(3, 3),
            .. FeeBlock(3, clearing: .20m),
            Text(30, "P U R C H A S E   &   S A L E"),
            Header(),
            Total(3, 3, "17.50 CR")
        ];
        await Import(Pdf(daily), "daily-0803.pdf");

        var day = _service.BuildReport(_journal.Id).Days.Single(d => d.Date == new DateOnly(2026, 8, 3));
        Assert.Equal(ReconciliationStatus.StatementsDisagree, day.Status);
        Assert.Equal(BrokerStatementKind.Monthly, day.Source!.Kind);
        Assert.Single(day.OtherSources);
        Assert.False(day.CanApplyFees);
    }

    [Fact]
    public async Task RemovingAStatementRemovesItsDays()
    {
        var outcome = await Import(Pdf(DailyStatement()), "daily.pdf");
        Assert.True(_db.DeleteBrokerStatement(_journal.Id, outcome.Statement!.Id));
        Assert.Empty(_db.GetBrokerStatements(_journal.Id));
        Assert.Empty(_db.GetBrokerStatementDays(_journal.Id));
        Assert.Empty(_service.BuildReport(_journal.Id).Days);
    }

    [Fact]
    public void AllocatesFeesBySidesAndKeepsTheRoundingRemainderOnTheLargestTrade()
    {
        var trades = new[]
        {
            new Trade { Id = Guid.NewGuid(), Quantity = 1, ClosedQuantity = 1 },
            new Trade { Id = Guid.NewGuid(), Quantity = 2, ClosedQuantity = 2 },
            new Trade { Id = Guid.NewGuid(), Quantity = 1, ClosedQuantity = 1 }
        };
        var day = new StatementDay { TradeDate = new(2026, 8, 3), BuyContracts = 4, SellContracts = 4 };
        day.Fees.Add(new StatementFee("Exchange", StatementFeeCategory.Exchange, 1.00m, "USD"));
        day.Fees.Add(new StatementFee("NFA", StatementFeeCategory.Nfa, .08m, "USD"));
        day.Fees.Add(new StatementFee("Clearing Teton", StatementFeeCategory.Clearing, 1.04m, "USD"));

        var shares = StatementReconciliationService.Allocate(day, trades);

        Assert.Equal([2, 4, 2], shares.Select(s => s.ContractSides));
        Assert.Equal([.25m, .50m, .25m], shares.Select(s => s.ExchangeFees));
        Assert.Equal(1.00m, shares.Sum(s => s.ExchangeFees));
        Assert.Equal(.08m, shares.Sum(s => s.NfaFees));
        Assert.Equal(1.04m, shares.Sum(s => s.ClearingFees));

        var thirds = new StatementDay { TradeDate = day.TradeDate, BuyContracts = 3, SellContracts = 3 };
        thirds.Fees.Add(new StatementFee("Clearing", StatementFeeCategory.Clearing, 1.00m, "USD"));
        var even = new[] { trades[0], trades[2], new Trade { Id = Guid.NewGuid(), Quantity = 1, ClosedQuantity = 1 } };
        var split = StatementReconciliationService.Allocate(thirds, even);
        Assert.Equal(1.00m, split.Sum(s => s.ClearingFees));
        Assert.Equal([.34m, .33m, .33m], split.Select(s => s.ClearingFees));
    }

    [Theory]
    [InlineData("2026-08-04T19:30:00Z", "2026-08-04")] // 2:30 p.m. Chicago
    [InlineData("2026-08-04T22:30:00Z", "2026-08-05")] // 5:30 p.m. Chicago opens the next trade date
    [InlineData("2026-08-09T23:15:00Z", "2026-08-10")] // Sunday evening belongs to Monday
    public void UsesTheCmeTradeDate(string exitUtc, string expected)
    {
        var trade = new Trade { EntryUtc = DateTimeOffset.Parse(exitUtc).AddMinutes(-5), ExitUtc = DateTimeOffset.Parse(exitUtc), Status = "closed" };
        Assert.Equal(DateOnly.Parse(expected), StatementReconciliationService.ExchangeTradeDate(trade));
    }

    [Fact]
    public void FeeNamesAreClassifiedIntoTradeFeeComponents()
    {
        Assert.Equal(StatementFeeCategory.Exchange, StatementFeeCategory.Classify("Exchange"));
        Assert.Equal(StatementFeeCategory.Nfa, StatementFeeCategory.Classify("NFA"));
        Assert.Equal(StatementFeeCategory.Clearing, StatementFeeCategory.Classify("Clearing Teton"));
        Assert.Equal(StatementFeeCategory.Clearing, StatementFeeCategory.Classify("Commission"));
    }
}

using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Pages;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class ExpenseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.GetEnvironmentVariable("TRADEFOUNDRY_TEST_ROOT") ?? Path.GetTempPath(), "expense-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TradeFoundryDb _db;
    private readonly Journal _journal;
    private readonly Journal _other;
    private readonly Guid _category;
    private readonly ExpenseService _service;

    public ExpenseTests()
    {
        Directory.CreateDirectory(_directory);
        _db = NewDatabase();
        _db.CreateOwner("Owner", "test-hash");
        _journal = _db.CreateJournal("Live", "live", "", "America/New_York", "USD", "flat_to_flat", 1000m);
        _other = _db.CreateJournal("Paper", "paper", "", "UTC", "EUR", "flat_to_flat");
        _category = _db.GetExpenseCategories(_journal.Id).First(c => c.Name == "Software").Id;
        _service = new ExpenseService(_db);
    }

    private TradeFoundryDb NewDatabase() => new(Options.Create(new StorageOptions { DataDirectory = _directory }));
    private ExpenseDraft Draft(decimal amount = 12.34m) => new() { Date = new(2026, 9, 30), CategoryId = _category, Description = "Charting", Amount = amount };

    [Fact]
    public void ExpensesPersistRemainIsolatedAndDoNotChangeAccountOrTradeMetrics()
    {
        var before = new AccountLedgerService(_db).GetLedger(_journal.Id).Summary;
        var expense = _db.CreateExpense(_journal.Id, Draft());
        var reopened = NewDatabase();
        Assert.Equal(12.34m, reopened.GetExpense(_journal.Id, expense.Id)!.Details.Amount);
        Assert.Null(reopened.GetExpense(_other.Id, expense.Id));
        Assert.Empty(reopened.GetExpenses(_other.Id, DateOnly.MinValue, DateOnly.MaxValue));
        Assert.Empty(reopened.GetExpenseHistory(_other.Id, expense.Id));
        Assert.Empty(reopened.GetAllTrades(_journal.Id));
        var after = new AccountLedgerService(reopened).GetLedger(_journal.Id).Summary;
        Assert.Equal(before.CurrentBalance, after.CurrentBalance);
        Assert.Equal(before.RealizedProfit, after.RealizedProfit);
        Assert.Equal(before.RealizedTwrPercent, after.RealizedTwrPercent);
        Assert.Throws<InvalidOperationException>(() => _db.CreateExpense(_other.Id, Draft()));
    }

    [Fact]
    public void CategorySeedingUniquenessArchiveAndRevisionChecksWork()
    {
        var categories = _db.GetExpenseCategories(_journal.Id);
        Assert.Equal(6, categories.Count);
        var software = categories.Single(c => c.Id == _category);
        var expense = _db.CreateExpense(_journal.Id, Draft());
        _db.UpdateExpenseCategory(_journal.Id, software.Id, 1, "Subscriptions", false);
        Assert.Throws<ExpenseConflictException>(() => _db.UpdateExpenseCategory(_journal.Id, software.Id, 1, "Outdated", true));
        Assert.Throws<InvalidOperationException>(() => _db.CreateExpenseCategory(_journal.Id, " subscriptions "));
        Assert.Throws<InvalidOperationException>(() => _db.CreateExpense(_journal.Id, Draft()));
        Assert.Equal("Subscriptions", _db.GetExpense(_journal.Id, expense.Id)!.CategoryName);
        _db.UpdateExpense(_journal.Id, expense.Id, 1, Draft(15m)); // unchanged archived category is allowed
        Assert.Equal(6, NewDatabase().GetExpenseCategories(_journal.Id).Count);
        _db.UpdateExpenseCategory(_journal.Id, software.Id, 2, "Subscriptions", true);
        Assert.NotNull(_db.CreateExpense(_journal.Id, Draft()));
        Assert.Throws<ExpenseConflictException>(() => _db.UpdateExpenseCategory(_other.Id, software.Id, 3, "Wrong journal", false));
        var custom = _db.CreateExpenseCategory(_journal.Id, "Research");
        Assert.Contains(_db.GetExpenseCategories(_journal.Id), c => c.Id == custom);
    }

    [Fact]
    public void ExpenseEditsAreAuditedAndStaleOrCrossJournalMutationsFail()
    {
        var expense = _db.CreateExpense(_journal.Id, Draft());
        var updated = _db.UpdateExpense(_journal.Id, expense.Id, 1, Draft(20m));
        Assert.Equal(2, updated.Revision);
        Assert.Throws<ExpenseConflictException>(() => _db.UpdateExpense(_journal.Id, expense.Id, 1, Draft(999m)));
        Assert.Throws<ExpenseConflictException>(() => _db.DeleteExpense(_journal.Id, expense.Id, 1));
        Assert.Throws<InvalidOperationException>(() => _db.UpdateExpense(_other.Id, expense.Id, 2, Draft()));
        _db.DeleteExpense(_journal.Id, expense.Id, 2);
        Assert.Null(_db.GetExpense(_journal.Id, expense.Id));
        Assert.IsType<NotFoundResult>(PageModel().OnPostSave(expense.Id, 2));
        Assert.Empty(_db.GetExpenses(_journal.Id, DateOnly.MinValue, DateOnly.MaxValue));
        Assert.Equal(new[] { "deleted", "updated", "created" }, _db.GetExpenseHistory(_journal.Id, expense.Id).Select(h => h.Action));
        Assert.Contains("20", _db.GetExpenseHistory(_journal.Id, expense.Id)[0].BeforeJson);
    }

    [Fact]
    public void DatesFiltersDecimalsPaginationAndExportUseAllMatches()
    {
        var otherCategory = _db.CreateExpenseCategory(_journal.Id, "Hardware");
        for (var i = 1; i <= 30; i++) _db.CreateExpense(_journal.Id, Draft(0.1m) with { Date = new(2026, 9, i), Description = "Expense " + i });
        _db.CreateExpense(_journal.Id, Draft(2m) with { CategoryId = otherCategory, Date = new(2026, 8, 31) });
        var model = PageModel();
        model.Start = new(2026, 9, 1); model.End = new(2026, 9, 30); model.PageNumber = 2;
        Assert.IsType<PageResult>(model.OnGet());
        Assert.Equal(30, model.Matches.Count);
        Assert.Equal(5, model.Rows.Count);
        Assert.Equal(2, model.PageCount);
        Assert.Equal(3m, model.Matches.Sum(e => e.Details.Amount));
        Assert.Equal(new DateOnly(2026, 9, 30), model.Matches[0].Details.Date);
        Assert.Single(_db.GetExpenses(_journal.Id, new(2026, 8, 31), new(2026, 9, 1), otherCategory));
        var export = Assert.IsType<FileContentResult>(model.OnGetExport());
        Assert.Equal(32, Encoding.UTF8.GetString(export.FileContents).Split("\r\n").Length); // header + 30 rows + trailing newline
    }

    [Theory]
    [InlineData(0, "Description", 0)]
    [InlineData(-1, "Description", 0)]
    [InlineData(1, "", 0)]
    [InlineData(1, "Description", 2001)]
    public void InvalidExpensesAreRejected(int amount, string description, int noteLength)
    {
        Assert.Throws<InvalidOperationException>(() => _db.CreateExpense(_journal.Id, Draft(amount) with { Description = description, Notes = new string('a', noteLength) }));
        Assert.Empty(_db.GetExpenses(_journal.Id, DateOnly.MinValue, DateOnly.MaxValue));
    }

    [Fact]
    public void RequiredDateAndTextLimitsAreEnforcedAndOptionalFieldsMayBeEmpty()
    {
        Assert.Throws<InvalidOperationException>(() => _db.CreateExpense(_journal.Id, Draft() with { Date = default }));
        Assert.Throws<InvalidOperationException>(() => _db.CreateExpense(_journal.Id, Draft() with { Vendor = new string('v', 181) }));
        Assert.Throws<InvalidOperationException>(() => _db.CreateExpense(_journal.Id, Draft() with { Description = new string('d', 251) }));
        Assert.NotNull(_db.CreateExpense(_journal.Id, Draft() with { Vendor = new string('v', 180), Description = new string('d', 250), Notes = new string('n', 2000) }));
        Assert.NotNull(_db.CreateExpense(_journal.Id, Draft() with { Vendor = null, Notes = null }));
        var model = PageModel();
        model.Input = Draft() with { Date = default };
        Assert.IsType<PageResult>(model.OnPostSave(null, 0));
        Assert.False(model.ModelState.IsValid);
    }

    [Fact]
    public void CurrencyIsCapturedAndPreservedAfterJournalChange()
    {
        var usd = _db.CreateExpense(_journal.Id, Draft());
        _db.UpdateJournal(_journal.Id, _journal.Name, _journal.ExecutionContext, _journal.Labels, _journal.TimeZone, "EUR", _journal.GroupingPolicy, _journal.StartingEquity);
        Assert.Equal("USD", _db.UpdateExpense(_journal.Id, usd.Id, 1, Draft(30m)).Currency);
        Assert.Equal("EUR", _db.CreateExpense(_journal.Id, Draft()).Currency);
        Assert.Equal(2, _db.GetExpenses(_journal.Id, DateOnly.MinValue, DateOnly.MaxValue).GroupBy(e => e.Currency).Count());
    }

    [Theory]
    [InlineData("application/pdf", "%PDF-1.7\nfixture")]
    [InlineData("image/jpeg", "jpeg")]
    [InlineData("image/png", "png")]
    [InlineData("image/webp", "RIFF0000WEBP")]
    public async Task ReceiptFormatsArePrivateAndAudited(string type, string fixture)
    {
        var bytes = fixture switch { "jpeg" => new byte[] { 255, 216, 255, 224 }, "png" => new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, _ => Encoding.ASCII.GetBytes(fixture) };
        var expense = _db.CreateExpense(_journal.Id, Draft());
        await using var stream = new MemoryStream(bytes);
        var receipt = await _service.AttachAsync(_journal.Id, expense.Id, 1, stream, "../../receipt", type, bytes.Length);
        Assert.Equal("receipt", receipt.OriginalFileName);
        Assert.True(File.Exists(_service.GetReceiptPath(_journal.Id, expense.Id, receipt.Id)));
        Assert.Null(_service.GetReceiptPath(_other.Id, expense.Id, receipt.Id));
        Assert.Null(_service.GetReceiptPath(_journal.Id, Guid.NewGuid(), receipt.Id));
        Assert.Equal(1, _db.GetExpense(_journal.Id, expense.Id)!.ReceiptCount);
        Assert.Contains(receipt.StorageKey, _db.GetExpenseHistory(_journal.Id, expense.Id)[0].AfterJson);
        Assert.NotNull(NewDatabase().GetExpenseReceipts(_journal.Id, expense.Id).Single());
        var path = _service.GetReceiptPath(_journal.Id, expense.Id, receipt.Id)!;
        _service.RemoveReceipt(_journal.Id, expense.Id, receipt.Id, 2);
        Assert.False(File.Exists(path));
        Assert.Empty(_db.GetExpenseReceipts(_journal.Id, expense.Id));
        Assert.Contains(receipt.StorageKey, _db.GetExpenseHistory(_journal.Id, expense.Id)[0].BeforeJson);
    }

    [Fact]
    public async Task DeletedExpensesHideReceiptsButRetainFilesAndHistory()
    {
        var expense = _db.CreateExpense(_journal.Id, Draft());
        await using var stream = new MemoryStream("%PDF-1.7"u8.ToArray());
        var receipt = await _service.AttachAsync(_journal.Id, expense.Id, 1, stream, "receipt.pdf", "application/pdf", stream.Length);
        var path = _service.GetReceiptPath(_journal.Id, expense.Id, receipt.Id)!;
        Assert.IsType<PhysicalFileResult>(PageModel().OnGetReceipt(expense.Id, receipt.Id));
        _db.DeleteExpense(_journal.Id, expense.Id, 2);
        Assert.Null(_service.GetReceiptPath(_journal.Id, expense.Id, receipt.Id));
        Assert.IsType<NotFoundResult>(PageModel().OnGetReceipt(expense.Id, receipt.Id));
        Assert.True(File.Exists(path));
        Assert.Equal(3, _db.GetExpenseHistory(_journal.Id, expense.Id).Count);
    }

    [Fact]
    public async Task ReceiptValidationAndMetadataFailureLeaveNoOrphans()
    {
        var expense = _db.CreateExpense(_journal.Id, Draft());
        await using (var invalid = new MemoryStream("not a PDF"u8.ToArray()))
            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.AttachAsync(_journal.Id, expense.Id, 1, invalid, "bad.pdf", "application/pdf", invalid.Length));
        await using (var large = new MemoryStream(new byte[ExpenseService.MaxReceiptLength + 1]))
            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.AttachAsync(_journal.Id, expense.Id, 1, large, "large.pdf", "application/pdf", 1));
        await using (var rejected = new MemoryStream("%PDF-1.7"u8.ToArray()))
            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.AttachAsync(_journal.Id, expense.Id, 1, rejected, "bad.svg", "image/svg+xml", rejected.Length));
        // Change the revision after the upload has begun to exercise metadata-failure cleanup.
        await using (var changed = new CallbackStream("%PDF-1.7"u8.ToArray(), () => _db.UpdateExpense(_journal.Id, expense.Id, 1, Draft(20m))))
            await Assert.ThrowsAsync<ExpenseConflictException>(() => _service.AttachAsync(_journal.Id, expense.Id, 1, changed, "receipt.pdf", "application/pdf", changed.Length));
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "expense-receipts", _journal.Id.ToString("D"))));
        Assert.Equal(20m, _db.GetExpense(_journal.Id, expense.Id)!.Details.Amount);
        Assert.Empty(_db.GetExpenseReceipts(_journal.Id, expense.Id));
    }

    [Fact]
    public void CsvQuotesUnicodeAndDangerousCellsAndPageRequiresAuthorization()
    {
        var expense = _db.CreateExpense(_journal.Id, Draft() with { Vendor = "=HYPERLINK(\"bad\")", Description = "Café, charting", Notes = "Line one\nLine two" });
        var csv = Encoding.UTF8.GetString(ExpenseService.ExportCsv([expense]));
        Assert.Contains("\"'=HYPERLINK(\"\"bad\"\")\"", csv);
        Assert.Contains("\"Café, charting\"", csv);
        Assert.Contains("\"Line one\nLine two\"", csv);
        Assert.NotNull(Attribute.GetCustomAttribute(typeof(ExpensesModel), typeof(AuthorizeAttribute)));
        Assert.NotNull(Attribute.GetCustomAttribute(typeof(ExpensesModel), typeof(ValidateAntiForgeryTokenAttribute)));
        var otherPage = PageModel(); otherPage.JournalId = _other.Id;
        Assert.IsType<NotFoundResult>(otherPage.OnGetReceipt(expense.Id, Guid.NewGuid()));
        Assert.Contains(CommandPaletteCatalog.Items, i => i.Title == "Expenses" && i.Route == "/expenses");
    }

    private ExpensesModel PageModel() => new(_db, _service) { JournalId = _journal.Id, PageContext = new PageContext { HttpContext = new DefaultHttpContext() } };

    private sealed class CallbackStream(byte[] bytes, Action callback) : MemoryStream(bytes)
    {
        private bool _called;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_called) { _called = true; callback(); }
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

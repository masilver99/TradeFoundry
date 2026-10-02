using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeFoundry.Core;

namespace TradeFoundry.Data;

public sealed partial class TradeFoundryDb
{
    private static void InitializeExpenses(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS expense_category_seeds (journal_id TEXT PRIMARY KEY REFERENCES journals(id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS expense_categories (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, name TEXT NOT NULL, normalized_name TEXT NOT NULL, active INTEGER NOT NULL DEFAULT 1, revision INTEGER NOT NULL DEFAULT 1, UNIQUE(journal_id, normalized_name), UNIQUE(journal_id, id));
            CREATE TABLE IF NOT EXISTS expenses (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, category_id TEXT NOT NULL, expense_date TEXT NOT NULL, details_json TEXT NOT NULL, currency TEXT NOT NULL, revision INTEGER NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL, deleted_utc TEXT NULL, UNIQUE(journal_id, id), FOREIGN KEY(journal_id, category_id) REFERENCES expense_categories(journal_id, id));
            CREATE INDEX IF NOT EXISTS ix_expenses_journal_date ON expenses(journal_id, expense_date DESC, id) WHERE deleted_utc IS NULL;
            CREATE TABLE IF NOT EXISTS expense_receipts (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL, expense_id TEXT NOT NULL, storage_key TEXT NOT NULL, original_file_name TEXT NOT NULL, content_type TEXT NOT NULL, length INTEGER NOT NULL, created_utc TEXT NOT NULL, FOREIGN KEY(journal_id, expense_id) REFERENCES expenses(journal_id, id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS ix_expense_receipts_expense ON expense_receipts(journal_id, expense_id);
            CREATE TABLE IF NOT EXISTS expense_history (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL, expense_id TEXT NOT NULL, revision INTEGER NOT NULL, action TEXT NOT NULL, before_json TEXT NOT NULL, after_json TEXT NOT NULL, created_utc TEXT NOT NULL, FOREIGN KEY(journal_id, expense_id) REFERENCES expenses(journal_id, id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS ix_expense_history_expense ON expense_history(journal_id, expense_id, revision DESC);
            """;
        command.ExecuteNonQuery();
    }

    private static SqliteCommand ExpenseCommand(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string, object?)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public IReadOnlyList<ExpenseCategory> GetExpenseCategories(Guid journalId)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using (var transaction = connection.BeginTransaction())
        {
            using var seed = ExpenseCommand(connection, transaction, "INSERT OR IGNORE INTO expense_category_seeds VALUES ($journal)", ("$journal", journalId.ToString()));
            if (seed.ExecuteNonQuery() == 1)
                foreach (var name in new[] { "Software", "Market Data", "Tools & Equipment", "Education", "Services", "Other" })
                    InsertExpenseCategory(connection, transaction, journalId, name);
            transaction.Commit();
        }
        using var command = ExpenseCommand(connection, null, "SELECT id, name, active, revision FROM expense_categories WHERE journal_id=$journal ORDER BY active DESC, name COLLATE NOCASE, id", ("$journal", journalId.ToString()));
        using var reader = command.ExecuteReader();
        var results = new List<ExpenseCategory>();
        while (reader.Read()) results.Add(new(Guid.Parse(reader.GetString(0)), journalId, reader.GetString(1), reader.GetInt32(2) != 0, reader.GetInt32(3)));
        return results;
    }

    private static string ValidateCategoryName(string? name)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 80) throw new InvalidOperationException("Category names must be between 1 and 80 characters.");
        return name;
    }

    private static Guid InsertExpenseCategory(SqliteConnection connection, SqliteTransaction? transaction, Guid journalId, string name)
    {
        var id = Guid.NewGuid();
        using var command = ExpenseCommand(connection, transaction, "INSERT INTO expense_categories(id,journal_id,name,normalized_name) VALUES($id,$journal,$name,$normalized)", ("$id", id.ToString()), ("$journal", journalId.ToString()), ("$name", name), ("$normalized", name.ToUpperInvariant()));
        try { command.ExecuteNonQuery(); }
        catch (SqliteException e) when (e.SqliteExtendedErrorCode == 2067) { throw new InvalidOperationException("That category name is already in use.", e); }
        return id;
    }

    public Guid CreateExpenseCategory(Guid journalId, string name)
    {
        GetExpenseCategories(journalId);
        using var connection = OpenConnection();
        return InsertExpenseCategory(connection, null, journalId, ValidateCategoryName(name));
    }

    public void UpdateExpenseCategory(Guid journalId, Guid categoryId, int expectedRevision, string name, bool active)
    {
        name = ValidateCategoryName(name);
        using var connection = OpenConnection();
        using var command = ExpenseCommand(connection, null, "UPDATE expense_categories SET name=$name,normalized_name=$normalized,active=$active,revision=revision+1 WHERE journal_id=$journal AND id=$id AND revision=$revision", ("$name", name), ("$normalized", name.ToUpperInvariant()), ("$active", active ? 1 : 0), ("$journal", journalId.ToString()), ("$id", categoryId.ToString()), ("$revision", expectedRevision));
        try { if (command.ExecuteNonQuery() != 1) throw new ExpenseConflictException(); }
        catch (SqliteException e) when (e.SqliteExtendedErrorCode == 2067) { throw new InvalidOperationException("That category name is already in use.", e); }
    }

    private const string ExpenseSelect = "SELECT e.id,e.category_id,e.expense_date,e.details_json,e.currency,e.revision,e.created_utc,e.updated_utc,e.deleted_utc,c.name,(SELECT COUNT(*) FROM expense_receipts r WHERE r.journal_id=e.journal_id AND r.expense_id=e.id) FROM expenses e JOIN expense_categories c ON c.id=e.category_id AND c.journal_id=e.journal_id";

    private static Expense ReadExpenseRow(SqliteDataReader reader, Guid journalId) => new()
    {
        Id = Guid.Parse(reader.GetString(0)), JournalId = journalId,
        Details = JsonSerializer.Deserialize<ExpenseDraft>(reader.GetString(3))!, Currency = reader.GetString(4), Revision = reader.GetInt32(5),
        CreatedUtc = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture), UpdatedUtc = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
        DeletedUtc = reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture), CategoryName = reader.GetString(9), ReceiptCount = reader.GetInt32(10)
    };

    private static Expense? ReadExpense(SqliteConnection connection, SqliteTransaction? transaction, Guid journalId, Guid id)
    {
        using var command = ExpenseCommand(connection, transaction, ExpenseSelect + " WHERE e.journal_id=$journal AND e.id=$id AND e.deleted_utc IS NULL", ("$journal", journalId.ToString()), ("$id", id.ToString()));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadExpenseRow(reader, journalId) : null;
    }

    public Expense? GetExpense(Guid journalId, Guid id)
    {
        using var connection = OpenConnection();
        return ReadExpense(connection, null, journalId, id);
    }

    public IReadOnlyList<Expense> GetExpenses(Guid journalId, DateOnly start, DateOnly end, Guid? categoryId = null)
    {
        using var connection = OpenConnection();
        using var command = ExpenseCommand(connection, null, ExpenseSelect + " WHERE e.journal_id=$journal AND e.deleted_utc IS NULL AND e.expense_date >= $start AND e.expense_date <= $end AND ($category IS NULL OR e.category_id=$category) ORDER BY e.expense_date DESC,e.created_utc DESC,e.id", ("$journal", journalId.ToString()), ("$start", start.ToString("yyyy-MM-dd")), ("$end", end.ToString("yyyy-MM-dd")), ("$category", categoryId?.ToString()));
        using var reader = command.ExecuteReader();
        var results = new List<Expense>();
        while (reader.Read()) results.Add(ReadExpenseRow(reader, journalId));
        return results;
    }

    private static ExpenseDraft ValidateExpenseDraft(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, ExpenseDraft draft, Guid? existingCategory = null)
    {
        var clean = draft with { Vendor = draft.Vendor?.Trim() ?? "", Description = draft.Description?.Trim() ?? "", Notes = draft.Notes?.Trim() ?? "" };
        if (clean.Date == default || clean.Amount <= 0 || clean.Vendor.Length > 180 || clean.Description.Length is < 1 or > 250 || clean.Notes.Length > 2000)
            throw new InvalidOperationException("Enter a date, positive amount, and description (up to 250 characters). Vendor is limited to 180 characters and notes to 2,000.");
        using var category = ExpenseCommand(connection, transaction, "SELECT active FROM expense_categories WHERE journal_id=$journal AND id=$id", ("$journal", journalId.ToString()), ("$id", clean.CategoryId.ToString()));
        var active = category.ExecuteScalar();
        if (active is null || (Convert.ToInt32(active) == 0 && existingCategory != clean.CategoryId)) throw new InvalidOperationException("Choose an active category from this journal.");
        return clean;
    }

    public Expense CreateExpense(Guid journalId, ExpenseDraft draft)
    {
        var journal = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        GetExpenseCategories(journalId);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var expense = new Expense { Id = Guid.NewGuid(), JournalId = journalId, Details = ValidateExpenseDraft(connection, transaction, journalId, draft), Currency = journal.Currency, Revision = 1, CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow };
        using var insert = ExpenseCommand(connection, transaction, "INSERT INTO expenses(id,journal_id,category_id,expense_date,details_json,currency,revision,created_utc,updated_utc) VALUES($id,$journal,$category,$date,$details,$currency,1,$created,$updated)", ("$id", expense.Id.ToString()), ("$journal", journalId.ToString()), ("$category", expense.Details.CategoryId.ToString()), ("$date", expense.Details.Date.ToString("yyyy-MM-dd")), ("$details", JsonSerializer.Serialize(expense.Details)), ("$currency", expense.Currency), ("$created", expense.CreatedUtc.ToString("O")), ("$updated", expense.UpdatedUtc.ToString("O")));
        insert.ExecuteNonQuery();
        WriteExpenseHistory(connection, transaction, expense, "created", "{}", JsonSerializer.Serialize(expense));
        transaction.Commit();
        return GetExpense(journalId, expense.Id)!;
    }

    private static void WriteExpenseHistory(SqliteConnection connection, SqliteTransaction transaction, Expense expense, string action, string before, string after)
    {
        using var command = ExpenseCommand(connection, transaction, "INSERT INTO expense_history VALUES($id,$journal,$expense,$revision,$action,$before,$after,$created)", ("$id", Guid.NewGuid().ToString()), ("$journal", expense.JournalId.ToString()), ("$expense", expense.Id.ToString()), ("$revision", expense.Revision), ("$action", action), ("$before", before), ("$after", after), ("$created", expense.UpdatedUtc.ToString("O")));
        command.ExecuteNonQuery();
    }

    private Expense MutateExpense(Guid journalId, Guid id, int expectedRevision, string action, Func<SqliteConnection, SqliteTransaction, Expense, Expense> mutate, ExpenseReceipt? receiptEvidence = null)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var current = ReadExpense(connection, transaction, journalId, id) ?? throw new InvalidOperationException("Expense was not found.");
        if (current.Revision != expectedRevision) throw new ExpenseConflictException();
        var updated = mutate(connection, transaction, current) with { Revision = current.Revision + 1, UpdatedUtc = DateTimeOffset.UtcNow };
        using var command = ExpenseCommand(connection, transaction, "UPDATE expenses SET category_id=$category,expense_date=$date,details_json=$details,revision=$revision,updated_utc=$updated,deleted_utc=$deleted WHERE id=$id AND journal_id=$journal AND revision=$expected AND deleted_utc IS NULL", ("$category", updated.Details.CategoryId.ToString()), ("$date", updated.Details.Date.ToString("yyyy-MM-dd")), ("$details", JsonSerializer.Serialize(updated.Details)), ("$revision", updated.Revision), ("$updated", updated.UpdatedUtc.ToString("O")), ("$deleted", updated.DeletedUtc?.ToString("O")), ("$id", id.ToString()), ("$journal", journalId.ToString()), ("$expected", expectedRevision));
        if (command.ExecuteNonQuery() != 1) throw new ExpenseConflictException();
        var before = receiptEvidence is null ? JsonSerializer.Serialize(current) : JsonSerializer.Serialize(new { Expense = current, Receipt = action == "receipt_removed" ? receiptEvidence : null });
        var after = receiptEvidence is null ? JsonSerializer.Serialize(updated) : JsonSerializer.Serialize(new { Expense = updated, Receipt = action == "receipt_added" ? receiptEvidence : null });
        WriteExpenseHistory(connection, transaction, updated, action, before, after);
        transaction.Commit();
        return updated;
    }

    public Expense UpdateExpense(Guid journalId, Guid id, int revision, ExpenseDraft draft) => MutateExpense(journalId, id, revision, "updated", (c, t, e) => e with { Details = ValidateExpenseDraft(c, t, journalId, draft, e.Details.CategoryId) });
    public void DeleteExpense(Guid journalId, Guid id, int revision) => MutateExpense(journalId, id, revision, "deleted", (_, _, e) => e with { DeletedUtc = DateTimeOffset.UtcNow });

    public IReadOnlyList<ExpenseReceipt> GetExpenseReceipts(Guid journalId, Guid expenseId)
    {
        using var connection = OpenConnection();
        using var command = ExpenseCommand(connection, null, "SELECT r.id,r.storage_key,r.original_file_name,r.content_type,r.length,r.created_utc FROM expense_receipts r JOIN expenses e ON e.id=r.expense_id AND e.journal_id=r.journal_id WHERE r.journal_id=$journal AND r.expense_id=$expense AND e.deleted_utc IS NULL ORDER BY r.created_utc,r.id", ("$journal", journalId.ToString()), ("$expense", expenseId.ToString()));
        using var reader = command.ExecuteReader();
        var results = new List<ExpenseReceipt>();
        while (reader.Read()) results.Add(new(Guid.Parse(reader.GetString(0)), journalId, expenseId, reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
        return results;
    }

    public void AddExpenseReceipt(ExpenseReceipt receipt, int revision) => MutateExpense(receipt.JournalId, receipt.ExpenseId, revision, "receipt_added", (connection, transaction, expense) =>
    {
        using var command = ExpenseCommand(connection, transaction, "INSERT INTO expense_receipts VALUES($id,$journal,$expense,$key,$name,$type,$length,$created)", ("$id", receipt.Id.ToString()), ("$journal", receipt.JournalId.ToString()), ("$expense", receipt.ExpenseId.ToString()), ("$key", receipt.StorageKey), ("$name", receipt.OriginalFileName), ("$type", receipt.ContentType), ("$length", receipt.Length), ("$created", receipt.CreatedUtc.ToString("O")));
        command.ExecuteNonQuery();
        return expense with { ReceiptCount = expense.ReceiptCount + 1 };
    }, receipt);

    public ExpenseReceipt RemoveExpenseReceipt(Guid journalId, Guid expenseId, Guid receiptId, int revision)
    {
        var receipt = GetExpenseReceipts(journalId, expenseId).FirstOrDefault(r => r.Id == receiptId) ?? throw new InvalidOperationException("Receipt was not found.");
        MutateExpense(journalId, expenseId, revision, "receipt_removed", (connection, transaction, expense) =>
        {
            using var command = ExpenseCommand(connection, transaction, "DELETE FROM expense_receipts WHERE journal_id=$journal AND expense_id=$expense AND id=$id", ("$journal", journalId.ToString()), ("$expense", expenseId.ToString()), ("$id", receiptId.ToString()));
            if (command.ExecuteNonQuery() != 1) throw new ExpenseConflictException();
            return expense with { ReceiptCount = expense.ReceiptCount - 1 };
        }, receipt);
        return receipt;
    }

    public IReadOnlyList<ExpenseHistoryEntry> GetExpenseHistory(Guid journalId, Guid expenseId)
    {
        using var connection = OpenConnection();
        using var command = ExpenseCommand(connection, null, "SELECT revision,action,before_json,after_json,created_utc FROM expense_history WHERE journal_id=$journal AND expense_id=$expense ORDER BY revision DESC", ("$journal", journalId.ToString()), ("$expense", expenseId.ToString()));
        using var reader = command.ExecuteReader();
        var results = new List<ExpenseHistoryEntry>();
        while (reader.Read()) results.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        return results;
    }
}

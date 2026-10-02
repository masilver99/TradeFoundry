namespace TradeFoundry.Core;

public sealed record ExpenseCategory(Guid Id, Guid JournalId, string Name, bool Active, int Revision);

public sealed record ExpenseDraft
{
    public DateOnly Date { get; set; }
    public Guid CategoryId { get; set; }
    public string? Vendor { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public string? Notes { get; set; } = "";
}

public sealed record Expense
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public ExpenseDraft Details { get; init; } = new();
    public string Currency { get; init; } = "USD";
    public int Revision { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public DateTimeOffset? DeletedUtc { get; init; }
    public string CategoryName { get; init; } = "";
    public int ReceiptCount { get; init; }
}

public sealed record ExpenseReceipt(Guid Id, Guid JournalId, Guid ExpenseId, string StorageKey,
    string OriginalFileName, string ContentType, long Length, DateTimeOffset CreatedUtc);

public sealed record ExpenseHistoryEntry(int Revision, string Action, string BeforeJson, string AfterJson, DateTimeOffset CreatedUtc);

public sealed class ExpenseConflictException() : InvalidOperationException("This item changed in another window. Reload the current values before trying again.");

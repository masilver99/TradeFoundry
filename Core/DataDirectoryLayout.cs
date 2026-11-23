namespace TradeFoundry.Core;

/// <summary>
/// Names of the top-level entries TradeFoundry keeps beside the database in the data directory.
/// Every store takes its directory name from here, so backup and restore can enumerate exactly what
/// belongs to a journal. A new top-level entry must be added here and classified in
/// <see cref="UserContentDirectories"/> or as an exclusion.
/// </summary>
public static class DataDirectoryLayout
{
    /// <summary>Images pasted into daily journal entries.</summary>
    public const string DailyJournalImages = "daily-journal-images";

    /// <summary>Images attached to trade reviews.</summary>
    public const string ReviewAttachments = "review-attachments";

    /// <summary>Expense receipts (PDFs and images).</summary>
    public const string ExpenseReceipts = "expense-receipts";

    /// <summary>Per-journal image encoding settings that are not stored in the database.</summary>
    public const string ImageSettings = "image-settings";

    /// <summary>Safety copies written by one-time migrations. Never part of a user backup.</summary>
    public const string MigrationBackups = "backups";

    /// <summary>Directories holding user content that a full backup must include, besides the database.</summary>
    public static IReadOnlyList<string> UserContentDirectories { get; } =
        [DailyJournalImages, ReviewAttachments, ExpenseReceipts, ImageSettings];
}

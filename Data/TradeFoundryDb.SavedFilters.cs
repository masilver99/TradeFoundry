using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeFoundry.Core;

namespace TradeFoundry.Data;

public sealed partial class TradeFoundryDb
{
    public const int SavedFilterNameMaxLength = 60;
    public const int SavedFilterLimit = 50;

    private static void InitializeSavedFilters(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS journal_saved_filters (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, name TEXT NOT NULL, normalized_name TEXT NOT NULL, filter_query TEXT NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL, UNIQUE(journal_id, normalized_name));
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<SavedJournalFilter> GetSavedFilters(Guid journalId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, filter_query FROM journal_saved_filters WHERE journal_id = $journal ORDER BY name COLLATE NOCASE, id";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        var filters = new List<SavedJournalFilter>();
        while (reader.Read()) filters.Add(new SavedJournalFilter(Guid.Parse(reader.GetString(0)), reader.GetString(1), JournalFilter.FromQueryString(reader.GetString(2))));
        return filters;
    }

    /// <summary>
    /// Saves a filter under a name. Saving under a name that already exists (ignoring case) replaces
    /// that filter, so a saved filter can be updated by saving it again.
    /// </summary>
    public SavedJournalFilter SaveFilter(Guid journalId, string name, JournalFilter filter)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        name = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) throw new InvalidOperationException("Enter a name for the filter.");
        if (name.Length > SavedFilterNameMaxLength) throw new InvalidOperationException($"Keep the name to {SavedFilterNameMaxLength} characters or fewer.");
        if (!filter.IsActive) throw new InvalidOperationException("Choose dates or a direction before saving a filter.");

        var normalized = name.ToUpperInvariant();
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT id FROM journal_saved_filters WHERE journal_id = $journal AND normalized_name = $normalized";
            existing.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            existing.Parameters.AddWithValue("$normalized", normalized);
            if (existing.ExecuteScalar() is string existingId)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE journal_saved_filters SET name = $name, filter_query = $query, updated_utc = $now WHERE id = $id";
                update.Parameters.AddWithValue("$name", name);
                update.Parameters.AddWithValue("$query", filter.ToQueryString());
                update.Parameters.AddWithValue("$now", now);
                update.Parameters.AddWithValue("$id", existingId);
                update.ExecuteNonQuery();
                transaction.Commit();
                return new SavedJournalFilter(Guid.Parse(existingId), name, filter);
            }
        }

        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM journal_saved_filters WHERE journal_id = $journal";
            count.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) >= SavedFilterLimit)
                throw new InvalidOperationException($"A journal can keep up to {SavedFilterLimit} saved filters. Delete one first.");
        }

        var id = Guid.NewGuid();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO journal_saved_filters (id, journal_id, name, normalized_name, filter_query, created_utc, updated_utc) VALUES ($id, $journal, $name, $normalized, $query, $now, $now)";
            insert.Parameters.AddWithValue("$id", id.ToString("D"));
            insert.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$normalized", normalized);
            insert.Parameters.AddWithValue("$query", filter.ToQueryString());
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return new SavedJournalFilter(id, name, filter);
    }

    public bool DeleteSavedFilter(Guid journalId, Guid filterId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM journal_saved_filters WHERE journal_id = $journal AND id = $id";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$id", filterId.ToString("D"));
        return command.ExecuteNonQuery() == 1;
    }
}

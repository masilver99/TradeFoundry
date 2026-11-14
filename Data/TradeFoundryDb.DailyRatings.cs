using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeFoundry.Core;

namespace TradeFoundry.Data;

public sealed partial class TradeFoundryDb
{
    private static void InitializeDailyRatings(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS daily_review_ratings (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, review_date TEXT NOT NULL, revision INTEGER NOT NULL DEFAULT 0, rating INTEGER NULL CHECK(rating IS NULL OR rating BETWEEN 1 AND 5), description TEXT NOT NULL DEFAULT '', updated_utc TEXT NULL, UNIQUE(journal_id, review_date));
            CREATE TABLE IF NOT EXISTS daily_review_rating_history (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, review_date TEXT NOT NULL, revision INTEGER NOT NULL, action TEXT NOT NULL, before_json TEXT NOT NULL DEFAULT '{}', after_json TEXT NOT NULL DEFAULT '{}', reason TEXT NOT NULL DEFAULT '', created_utc TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_daily_review_rating_history_date ON daily_review_rating_history(journal_id, review_date, revision DESC);
            """;
        command.ExecuteNonQuery();
    }

    public DailyRating GetDailyRating(Guid journalId, DateOnly date)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT journal_id, review_date, revision, rating, description, updated_utc FROM daily_review_ratings WHERE journal_id = $journal AND review_date = $date";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDailyRating(reader) : new DailyRating { JournalId = journalId, Date = date };
    }

    /// <summary>Every rated day in the journal, keyed by date. Cleared ratings are left out.</summary>
    public IReadOnlyDictionary<DateOnly, DailyRating> GetDailyRatings(Guid journalId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT journal_id, review_date, revision, rating, description, updated_utc FROM daily_review_ratings WHERE journal_id = $journal AND rating IS NOT NULL";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        var ratings = new Dictionary<DateOnly, DailyRating>();
        while (reader.Read())
        {
            var rating = ReadDailyRating(reader);
            ratings[rating.Date] = rating;
        }
        return ratings;
    }

    /// <summary>
    /// Saves the day's rating and description. A null rating with an empty description clears the day;
    /// a description without a rating is rejected. Every save and conflict is recorded in the history.
    /// </summary>
    public DailyRatingSaveResult SaveDailyRating(Guid journalId, DateOnly date, int? rating, string? description, int expectedRevision, string action = "saved")
    {
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        if (rating is < DailyRating.MinRating or > DailyRating.MaxRating)
            throw new ArgumentException($"Choose a rating from {DailyRating.MinRating} to {DailyRating.MaxRating}.");

        var normalizedDescription = (description ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (normalizedDescription.Length > DailyRating.DescriptionMaxLength)
            throw new ArgumentException($"The description can be at most {DailyRating.DescriptionMaxLength:N0} characters.");
        if (!rating.HasValue && normalizedDescription.Length > 0)
            throw new ArgumentException("Choose a rating for the day before saving a description.");

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var current = ReadDailyRatingOrDefault(connection, transaction, journalId, date);
        var now = DateTimeOffset.UtcNow;
        if (current.Revision != expectedRevision)
        {
            InsertDailyRatingHistory(connection, transaction, journalId, date, current.Revision, "conflict", JsonSerializer.Serialize(current), JsonSerializer.Serialize(new { rating, description = normalizedDescription }), "Stale daily rating revision.", now);
            transaction.Commit();
            return new DailyRatingSaveResult { Conflict = true, Rating = current };
        }

        var saved = new DailyRating
        {
            JournalId = journalId,
            Date = date,
            Revision = current.Revision + 1,
            Rating = rating,
            Description = normalizedDescription,
            UpdatedUtc = now
        };
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO daily_review_ratings (id, journal_id, review_date, revision, rating, description, updated_utc) VALUES ($id, $journal, $date, $revision, $rating, $description, $updated) ON CONFLICT(journal_id, review_date) DO UPDATE SET revision = excluded.revision, rating = excluded.rating, description = excluded.description, updated_utc = excluded.updated_utc";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$revision", saved.Revision);
            command.Parameters.AddWithValue("$rating", rating.HasValue ? rating.Value : DBNull.Value);
            command.Parameters.AddWithValue("$description", saved.Description);
            command.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        var historyAction = rating.HasValue ? action : "cleared";
        InsertDailyRatingHistory(connection, transaction, journalId, date, saved.Revision, historyAction, JsonSerializer.Serialize(current), JsonSerializer.Serialize(saved), TrimTo(historyAction, 500), now);
        transaction.Commit();
        return new DailyRatingSaveResult { Saved = true, Rating = saved };
    }

    private static DailyRating ReadDailyRating(SqliteDataReader reader) => new()
    {
        JournalId = Guid.Parse(reader.GetString(0)),
        Date = DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        Revision = reader.GetInt32(2),
        Rating = reader.IsDBNull(3) ? null : reader.GetInt32(3),
        Description = reader.GetString(4),
        UpdatedUtc = reader.IsDBNull(5) ? null : ParseDate(reader.GetString(5))
    };

    private static DailyRating ReadDailyRatingOrDefault(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, DateOnly date)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT journal_id, review_date, revision, rating, description, updated_utc FROM daily_review_ratings WHERE journal_id = $journal AND review_date = $date";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDailyRating(reader) : new DailyRating { JournalId = journalId, Date = date };
    }

    private static void InsertDailyRatingHistory(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, DateOnly date, int revision, string action, string beforeJson, string afterJson, string reason, DateTimeOffset createdUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO daily_review_rating_history (id, journal_id, review_date, revision, action, before_json, after_json, reason, created_utc) VALUES ($id, $journal, $date, $revision, $action, $before, $after, $reason, $created)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$before", beforeJson);
        command.Parameters.AddWithValue("$after", afterJson);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$created", createdUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
}

using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeFoundry.Core;
using TradeFoundry.Publishing;

namespace TradeFoundry.Data;

public sealed partial class TradeFoundryDb
{
    private static void InitializePublishing(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS journal_publishing (
                journal_id TEXT PRIMARY KEY REFERENCES journals(id) ON DELETE CASCADE,
                settings_json TEXT NOT NULL, protected_token TEXT NOT NULL DEFAULT '',
                draft_json TEXT, remote_status_json TEXT, last_success_utc TEXT);
            """;
        command.ExecuteNonQuery();
    }

    public PublishingState GetPublishingState(Guid journalId)
    {
        var journal = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT settings_json, protected_token, draft_json, remote_status_json, last_success_utc FROM journal_publishing WHERE journal_id=$journal";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new(new(Title: journal.Name), "", null, null, null);
        return new(JsonSerializer.Deserialize<PublishingSettings>(reader.GetString(0), PublicationJson.Options)!, reader.GetString(1),
            reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<PublicationUpload>(reader.GetString(2), PublicationJson.Options),
            reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<PublicationStatus>(reader.GetString(3), PublicationJson.Options),
            reader.IsDBNull(4) ? null : ParseDate(reader.GetString(4)));
    }

    public void SavePublishingSettings(Guid journalId, PublishingSettings settings, string protectedToken)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO journal_publishing (journal_id, settings_json, protected_token) VALUES ($journal, $settings, $token)
            ON CONFLICT(journal_id) DO UPDATE SET settings_json=$settings, protected_token=$token, draft_json=NULL,
              remote_status_json=CASE WHEN settings_json=$settings THEN remote_status_json ELSE NULL END;
            """;
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(settings, PublicationJson.Options));
        command.Parameters.AddWithValue("$token", protectedToken);
        command.ExecuteNonQuery();
    }

    public void SavePublicationDraft(Guid journalId, PublicationUpload draft)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE journal_publishing SET draft_json=$draft WHERE journal_id=$journal";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$draft", JsonSerializer.Serialize(draft, PublicationJson.Options));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Save publishing settings before creating a preview.");
    }

    public void SavePublicationStatus(Guid journalId, PublicationStatus status, bool published = false)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE journal_publishing SET remote_status_json=$status, last_success_utc=CASE WHEN $published=1 THEN $utc ELSE last_success_utc END WHERE journal_id=$journal";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$status", JsonSerializer.Serialize(status, PublicationJson.Options));
        command.Parameters.AddWithValue("$published", published ? 1 : 0);
        command.Parameters.AddWithValue("$utc", status.PublishedUtc?.ToString("O") ?? (object)DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<DailyJournalEntry> GetPublicationJournalEntries(Guid journalId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, journal_id, review_date, revision, editor_state_json, search_text, updated_utc FROM daily_review_journals WHERE journal_id=$journal AND TRIM(editor_state_json) <> '' ORDER BY review_date";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        var result = new List<DailyJournalEntry>();
        while (reader.Read()) result.Add(ReadDailyJournal(reader));
        return result;
    }
}

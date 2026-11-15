using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TradeFoundry.Data;

public sealed class AiPromptRecord
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string Scope { get; init; } = "day";
    public bool Archived { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
}

public sealed partial class TradeFoundryDb
{
    private static void InitializeAiPrompts(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ai_review_prompts (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                body TEXT NOT NULL,
                scope TEXT NOT NULL DEFAULT 'day',
                archived INTEGER NOT NULL DEFAULT 0,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL)
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<AiPromptRecord> GetAiPrompts(bool includeArchived = false)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, description, body, scope, archived, created_utc, updated_utc FROM ai_review_prompts"
            + (includeArchived ? string.Empty : " WHERE archived = 0") + " ORDER BY name COLLATE NOCASE";
        using var reader = command.ExecuteReader();
        var prompts = new List<AiPromptRecord>();
        while (reader.Read()) prompts.Add(ReadAiPrompt(reader));
        return prompts;
    }

    public AiPromptRecord? GetAiPrompt(Guid id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, description, body, scope, archived, created_utc, updated_utc FROM ai_review_prompts WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAiPrompt(reader) : null;
    }

    public void SaveAiPrompt(AiPromptRecord prompt)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ai_review_prompts (id, name, description, body, scope, archived, created_utc, updated_utc)
            VALUES ($id, $name, $description, $body, $scope, $archived, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET name=$name, description=$description, body=$body, scope=$scope, archived=$archived, updated_utc=$updated
            """;
        command.Parameters.AddWithValue("$id", prompt.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", prompt.Name);
        command.Parameters.AddWithValue("$description", prompt.Description);
        command.Parameters.AddWithValue("$body", prompt.Body);
        command.Parameters.AddWithValue("$scope", prompt.Scope);
        command.Parameters.AddWithValue("$archived", prompt.Archived ? 1 : 0);
        command.Parameters.AddWithValue("$created", prompt.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", prompt.UpdatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public string? GetAppSetting(string key)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT settings_value FROM app_settings WHERE settings_key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetAppSetting(string key, string? value)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        if (value is null)
        {
            command.CommandText = "DELETE FROM app_settings WHERE settings_key = $key";
        }
        else
        {
            command.CommandText = "INSERT INTO app_settings (settings_key, settings_value) VALUES ($key, $value) ON CONFLICT(settings_key) DO UPDATE SET settings_value = $value";
            command.Parameters.AddWithValue("$value", value);
        }
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    private static AiPromptRecord ReadAiPrompt(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        Description = reader.GetString(2),
        Body = reader.GetString(3),
        Scope = reader.GetString(4),
        Archived = reader.GetInt64(5) != 0,
        CreatedUtc = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        UpdatedUtc = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
    };
}

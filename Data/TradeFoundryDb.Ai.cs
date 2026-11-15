using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TradeFoundry.Data;

/// <summary>Instance-level AI provider connection. Credentials arrive already protected; this layer never sees plaintext tokens.</summary>
public sealed class AiConnectionRecord
{
    public string Provider { get; init; } = string.Empty;
    public string HostId { get; init; } = string.Empty;
    public string? ClientId { get; init; }
    public string? Subject { get; init; }
    public string? Email { get; init; }
    public string? DisplayName { get; init; }
    public string Scopes { get; init; } = string.Empty;
    public string ProtectedCredentials { get; init; } = string.Empty;
    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
    public DateTimeOffset? UpdatedUtc { get; init; }
}

public sealed partial class TradeFoundryDb
{
    private static void InitializeAi(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ai_connections (
                provider TEXT PRIMARY KEY,
                host_id TEXT NOT NULL,
                client_id TEXT NULL,
                subject TEXT NULL,
                email TEXT NULL,
                display_name TEXT NULL,
                scopes TEXT NOT NULL DEFAULT '',
                protected_credentials TEXT NOT NULL DEFAULT '',
                model TEXT NULL,
                reasoning_effort TEXT NULL,
                updated_utc TEXT NULL)
            """;
        command.ExecuteNonQuery();
    }

    public AiConnectionRecord? GetAiConnection(string provider)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT provider, host_id, client_id, subject, email, display_name, scopes, protected_credentials, model, reasoning_effort, updated_utc FROM ai_connections WHERE provider = $provider";
        command.Parameters.AddWithValue("$provider", provider);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        string? Nullable(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        return new AiConnectionRecord
        {
            Provider = reader.GetString(0),
            HostId = reader.GetString(1),
            ClientId = Nullable(2),
            Subject = Nullable(3),
            Email = Nullable(4),
            DisplayName = Nullable(5),
            Scopes = reader.GetString(6),
            ProtectedCredentials = reader.GetString(7),
            Model = Nullable(8),
            ReasoningEffort = Nullable(9),
            UpdatedUtc = Nullable(10) is { } updated ? DateTimeOffset.Parse(updated, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
        };
    }

    public void SaveAiConnection(AiConnectionRecord record)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ai_connections (provider, host_id, client_id, subject, email, display_name, scopes, protected_credentials, model, reasoning_effort, updated_utc)
            VALUES ($provider, $host, $client, $subject, $email, $name, $scopes, $credentials, $model, $effort, $updated)
            ON CONFLICT(provider) DO UPDATE SET host_id=$host, client_id=$client, subject=$subject, email=$email, display_name=$name,
                scopes=$scopes, protected_credentials=$credentials, model=$model, reasoning_effort=$effort, updated_utc=$updated
            """;
        command.Parameters.AddWithValue("$provider", record.Provider);
        command.Parameters.AddWithValue("$host", record.HostId);
        command.Parameters.AddWithValue("$client", (object?)record.ClientId ?? DBNull.Value);
        command.Parameters.AddWithValue("$subject", (object?)record.Subject ?? DBNull.Value);
        command.Parameters.AddWithValue("$email", (object?)record.Email ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", (object?)record.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$scopes", record.Scopes);
        command.Parameters.AddWithValue("$credentials", record.ProtectedCredentials);
        command.Parameters.AddWithValue("$model", (object?)record.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$effort", (object?)record.ReasoningEffort ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
}

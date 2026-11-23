using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeFoundry.Core;

namespace TradeFoundry.Data;

public sealed partial class TradeFoundryDb
{
    private const string InstallationIdKey = "installation_id";
    private const string BackupFolderKey = "backup_folder";
    private const string BackupRunColumns = "id, trigger, status, started_utc, finished_utc, maintenance_ms, archive_name, archive_size, message, warnings";

    private static void InitializeBackup(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS backup_runs (id TEXT PRIMARY KEY, trigger TEXT NOT NULL, status TEXT NOT NULL, started_utc TEXT NOT NULL, finished_utc TEXT NULL, maintenance_ms INTEGER NULL, archive_name TEXT NOT NULL DEFAULT '', archive_size INTEGER NULL, message TEXT NOT NULL DEFAULT '', warnings TEXT NOT NULL DEFAULT '');" +
            "CREATE INDEX IF NOT EXISTS ix_backup_runs_started ON backup_runs(started_utc DESC)";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A stable id for this installation, created on first use. Backup names and cloud prefixes use it
    /// so two installations never prune or overwrite each other's backups.
    /// </summary>
    public Guid GetOrCreateInstallationId()
    {
        using var connection = OpenConnection();
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT OR IGNORE INTO app_settings (settings_key, settings_value) VALUES ($key, $value)";
            insert.Parameters.AddWithValue("$key", InstallationIdKey);
            insert.Parameters.AddWithValue("$value", Guid.NewGuid().ToString("D"));
            insert.ExecuteNonQuery();
        }

        using var select = connection.CreateCommand();
        select.CommandText = "SELECT settings_value FROM app_settings WHERE settings_key = $key";
        select.Parameters.AddWithValue("$key", InstallationIdKey);
        return Guid.Parse((string)select.ExecuteScalar()!);
    }

    /// <summary>The configured backup folder, or null to use the default.</summary>
    public string? GetBackupFolder()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT settings_value FROM app_settings WHERE settings_key = $key";
        command.Parameters.AddWithValue("$key", BackupFolderKey);
        return command.ExecuteScalar() as string is { Length: > 0 } folder ? folder : null;
    }

    public void SetBackupFolder(string? folder)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(folder))
        {
            command.CommandText = "DELETE FROM app_settings WHERE settings_key = $key";
        }
        else
        {
            command.CommandText = "INSERT INTO app_settings (settings_key, settings_value) VALUES ($key, $value) ON CONFLICT(settings_key) DO UPDATE SET settings_value = excluded.settings_value";
            command.Parameters.AddWithValue("$value", folder);
        }

        command.Parameters.AddWithValue("$key", BackupFolderKey);
        command.ExecuteNonQuery();
    }

    public void InsertBackupRun(BackupRun run)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO backup_runs ({BackupRunColumns}) VALUES ($id, $trigger, $status, $started, $finished, $maintenance, $archive, $size, $message, $warnings)";
        AddBackupRunParameters(command, run);
        command.ExecuteNonQuery();
    }

    public void UpdateBackupRun(BackupRun run)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backup_runs SET trigger = $trigger, status = $status, started_utc = $started, finished_utc = $finished, maintenance_ms = $maintenance, archive_name = $archive, archive_size = $size, message = $message, warnings = $warnings WHERE id = $id";
        AddBackupRunParameters(command, run);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<BackupRun> GetBackupRuns(int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BackupRunColumns} FROM backup_runs ORDER BY started_utc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var runs = new List<BackupRun>();
        while (reader.Read())
        {
            runs.Add(new BackupRun(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.GetString(8),
                reader.GetString(9).Split('\n', StringSplitOptions.RemoveEmptyEntries)));
        }

        return runs;
    }

    /// <summary>Marks runs left "running" by a crash or forced exit as interrupted.</summary>
    public int FailInterruptedBackupRuns()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backup_runs SET status = $status, finished_utc = $now, message = 'TradeFoundry stopped before the backup finished.' WHERE status = $running";
        command.Parameters.AddWithValue("$status", BackupRunStatus.Interrupted);
        command.Parameters.AddWithValue("$running", BackupRunStatus.Running);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery();
    }

    private static void AddBackupRunParameters(SqliteCommand command, BackupRun run)
    {
        command.Parameters.AddWithValue("$id", run.Id.ToString("D"));
        command.Parameters.AddWithValue("$trigger", run.Trigger);
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$started", run.StartedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$finished", run.FinishedUtc is { } finished ? finished.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$maintenance", run.MaintenanceMilliseconds is { } milliseconds ? milliseconds : DBNull.Value);
        command.Parameters.AddWithValue("$archive", run.ArchiveName);
        command.Parameters.AddWithValue("$size", run.ArchiveSize is { } size ? size : DBNull.Value);
        command.Parameters.AddWithValue("$message", run.Message);
        command.Parameters.AddWithValue("$warnings", string.Join('\n', run.Warnings));
    }
}

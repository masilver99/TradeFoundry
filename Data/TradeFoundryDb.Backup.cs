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

    private const string IntegrationsPausedKey = "integrations_paused";
    private const string RestoredFromKey = "restored_from";
    private const string SessionEpochKey = "session_epoch";

    /// <summary>
    /// Clean means no journals at all, archived ones included. Only a clean installation may be restored
    /// into, so a restore can never silently replace someone's data.
    /// </summary>
    public bool IsClean()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM journals";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0;
    }

    /// <summary>
    /// True after a restore until the owner confirms: watched import folders are not watched, because
    /// saved absolute paths may point somewhere else on this machine.
    /// </summary>
    public bool AreIntegrationsPaused() => GetSetting(IntegrationsPausedKey) == "1";

    public void ResumeIntegrations() => DeleteSetting(IntegrationsPausedKey);

    /// <summary>JSON describing the backup this installation was restored from, until the owner dismisses it.</summary>
    public string? GetRestoredFrom() => GetSetting(RestoredFromKey);

    public void DismissRestoredFrom() => DeleteSetting(RestoredFromKey);

    /// <summary>
    /// Changes when a restore replaces the owner account, so sign-ins from before it stop working.
    /// Null until the first restore or start clean.
    /// </summary>
    public string? GetSessionEpoch() => GetSetting(SessionEpochKey);

    private string? GetSetting(string key)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT settings_value FROM app_settings WHERE settings_key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private void DeleteSetting(string key)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM app_settings WHERE settings_key = $key";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Prepares a staged, migrated copy for use as this installation: revokes MCP tokens, pauses watched
    /// imports, drops this machine's backup folder and installation id, records where it came from and
    /// starts a new sign-in epoch. Works on a plain connection to the staged file.
    /// </summary>
    internal static void PrepareRestoredDatabase(SqliteConnection connection, BackupManifest manifest, DateTimeOffset restoredUtc)
    {
        var now = restoredUtc.ToString("O", CultureInfo.InvariantCulture);
        using var transaction = connection.BeginTransaction();
        void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
        }

        void Set(string key, string value) => Execute(
            "INSERT INTO app_settings (settings_key, settings_value) VALUES ($key, $value) ON CONFLICT(settings_key) DO UPDATE SET settings_value = excluded.settings_value",
            ("$key", key), ("$value", value));

        Execute("UPDATE mcp_access_tokens SET revoked_utc = $now WHERE revoked_utc IS NULL", ("$now", now));
        Execute("DELETE FROM app_settings WHERE settings_key IN ($installation, $folder)", ("$installation", InstallationIdKey), ("$folder", BackupFolderKey));
        Set(IntegrationsPausedKey, "1");
        Set(SessionEpochKey, Guid.NewGuid().ToString("N"));
        Set(RestoredFromKey, System.Text.Json.JsonSerializer.Serialize(new RestoredFrom(manifest.BackupId, manifest.CreatedUtc, manifest.AppVersion, restoredUtc)));
        // The backup was taken while its own run was still in progress; that run evidently finished.
        Execute("UPDATE backup_runs SET status = $status, finished_utc = $now, message = 'Restored from this backup.' WHERE id = $id AND status = $running",
            ("$status", BackupRunStatus.Completed), ("$now", now), ("$id", manifest.BackupId.ToString("D")), ("$running", BackupRunStatus.Running));
        transaction.Commit();
    }

    /// <summary>Creates the owner account in a fresh database, keeping its id, name and password, plus this installation's backup settings.</summary>
    internal void SeedFreshDatabase(StartCleanOwner owner)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var user = connection.CreateCommand())
        {
            user.Transaction = transaction;
            user.CommandText = "INSERT INTO app_users (id, display_name, password_hash, created_utc) VALUES ($id, $name, $hash, $created)";
            user.Parameters.AddWithValue("$id", owner.Id);
            user.Parameters.AddWithValue("$name", owner.DisplayName);
            user.Parameters.AddWithValue("$hash", owner.PasswordHash);
            user.Parameters.AddWithValue("$created", owner.CreatedUtc);
            user.ExecuteNonQuery();
        }

        foreach (var (key, value) in owner.Settings)
        {
            using var setting = connection.CreateCommand();
            setting.Transaction = transaction;
            setting.CommandText = "INSERT OR REPLACE INTO app_settings (settings_key, settings_value) VALUES ($key, $value)";
            setting.Parameters.AddWithValue("$key", key);
            setting.Parameters.AddWithValue("$value", value);
            setting.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>The owner account and the settings "start clean" carries into the fresh database.</summary>
    internal StartCleanOwner GetStartCleanOwner()
    {
        using var connection = OpenConnection();
        StartCleanOwner owner;
        using (var user = connection.CreateCommand())
        {
            user.CommandText = "SELECT id, display_name, password_hash, created_utc FROM app_users ORDER BY created_utc LIMIT 1";
            using var reader = user.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("This installation has no owner account.");
            owner = new StartCleanOwner(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), new Dictionary<string, string>());
        }

        using var settings = connection.CreateCommand();
        settings.CommandText = "SELECT settings_key, settings_value FROM app_settings WHERE settings_key IN ($installation, $folder, $epoch)";
        settings.Parameters.AddWithValue("$installation", InstallationIdKey);
        settings.Parameters.AddWithValue("$folder", BackupFolderKey);
        settings.Parameters.AddWithValue("$epoch", SessionEpochKey);
        using var rows = settings.ExecuteReader();
        var values = new Dictionary<string, string>();
        while (rows.Read()) values[rows.GetString(0)] = rows.GetString(1);
        return owner with { Settings = values };
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

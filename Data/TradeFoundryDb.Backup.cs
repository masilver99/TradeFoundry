using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeFoundry.Core;

namespace TradeFoundry.Data;

public sealed partial class TradeFoundryDb
{
    private const string InstallationIdKey = "installation_id";
    private const string BackupFolderKey = "backup_folder";
    private const string BackupRunColumns = "id, trigger, status, started_utc, finished_utc, maintenance_ms, archive_name, archive_size, message, warnings, cloud_status, cloud_message";

    /// <summary>
    /// Settings that hold secrets for this machine's backups. They are removed from every backup copy, so a
    /// backup can never be used to delete the other backups or to read them.
    /// </summary>
    internal const string BackupPassphraseKey = "backup_passphrase";
    internal const string BackupCloudKey = "backup_cloud";
    internal const string BackupCloudMissingKey = "backup_cloud_missing";
    internal static readonly string[] MachineOnlyBackupSettings = [BackupPassphraseKey, BackupCloudKey, BackupCloudMissingKey];

    private static void InitializeBackup(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS backup_runs (id TEXT PRIMARY KEY, trigger TEXT NOT NULL, status TEXT NOT NULL, started_utc TEXT NOT NULL, finished_utc TEXT NULL, maintenance_ms INTEGER NULL, archive_name TEXT NOT NULL DEFAULT '', archive_size INTEGER NULL, message TEXT NOT NULL DEFAULT '', warnings TEXT NOT NULL DEFAULT '');" +
            "CREATE INDEX IF NOT EXISTS ix_backup_runs_started ON backup_runs(started_utc DESC);" +
            // Backups this installation uploaded, so one that disappears without being pruned is noticed.
            "CREATE TABLE IF NOT EXISTS backup_cloud_objects (archive_key TEXT PRIMARY KEY, uploaded_utc TEXT NOT NULL, pruned_utc TEXT NULL)";
        command.ExecuteNonQuery();
        EnsureColumn(connection, "backup_runs", "cloud_status", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "backup_runs", "cloud_message", "TEXT NOT NULL DEFAULT ''");
    }

    public string? GetProtectedBackupPassphrase() => GetSetting(BackupPassphraseKey);

    public void SetProtectedBackupPassphrase(string? value)
    {
        if (value is null) DeleteSetting(BackupPassphraseKey);
        else SetSetting(BackupPassphraseKey, value);
    }

    public BackupCloudSettings? GetBackupCloudSettings()
    {
        if (GetSetting(BackupCloudKey) is not { } json) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<BackupCloudSettings>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public void SetBackupCloudSettings(BackupCloudSettings? settings)
    {
        if (settings is null) DeleteSetting(BackupCloudKey);
        else SetSetting(BackupCloudKey, System.Text.Json.JsonSerializer.Serialize(settings));
    }

    /// <summary>Uploaded backups that were not pruned by this app but are no longer visible in the bucket.</summary>
    public IReadOnlyList<string> GetMissingCloudBackups() =>
        GetSetting(BackupCloudMissingKey) is { Length: > 0 } value ? value.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];

    public void SetMissingCloudBackups(IReadOnlyCollection<string> keys)
    {
        if (keys.Count == 0) DeleteSetting(BackupCloudMissingKey);
        else SetSetting(BackupCloudMissingKey, string.Join('\n', keys));
    }

    public void RecordCloudUpload(string archiveKey, DateTimeOffset uploadedUtc)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO backup_cloud_objects (archive_key, uploaded_utc) VALUES ($key, $now) ON CONFLICT(archive_key) DO UPDATE SET uploaded_utc = excluded.uploaded_utc, pruned_utc = NULL";
        command.Parameters.AddWithValue("$key", archiveKey);
        command.Parameters.AddWithValue("$now", uploadedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public void RecordCloudPrune(string archiveKey, DateTimeOffset prunedUtc)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backup_cloud_objects SET pruned_utc = $now WHERE archive_key = $key";
        command.Parameters.AddWithValue("$key", archiveKey);
        command.Parameters.AddWithValue("$now", prunedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>Archive keys this installation uploaded and has not pruned.</summary>
    public IReadOnlyList<string> GetUnprunedCloudUploads()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT archive_key FROM backup_cloud_objects WHERE pruned_utc IS NULL";
        using var reader = command.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read()) keys.Add(reader.GetString(0));
        return keys;
    }

    /// <summary>Marks uploads left "uploading" by a crash or forced exit as failed, so they are retried.</summary>
    public int FailInterruptedCloudUploads()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backup_runs SET cloud_status = $failed, cloud_message = 'TradeFoundry stopped before the upload finished.' WHERE cloud_status = $uploading";
        command.Parameters.AddWithValue("$failed", BackupCloudStatus.Failed);
        command.Parameters.AddWithValue("$uploading", BackupCloudStatus.Uploading);
        return command.ExecuteNonQuery();
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
        command.CommandText = $"INSERT INTO backup_runs ({BackupRunColumns}) VALUES ($id, $trigger, $status, $started, $finished, $maintenance, $archive, $size, $message, $warnings, $cloudStatus, $cloudMessage)";
        AddBackupRunParameters(command, run);
        command.ExecuteNonQuery();
    }

    public void UpdateBackupRun(BackupRun run)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backup_runs SET trigger = $trigger, status = $status, started_utc = $started, finished_utc = $finished, maintenance_ms = $maintenance, archive_name = $archive, archive_size = $size, message = $message, warnings = $warnings, cloud_status = $cloudStatus, cloud_message = $cloudMessage WHERE id = $id";
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
                reader.GetString(9).Split('\n', StringSplitOptions.RemoveEmptyEntries),
                reader.GetString(10),
                reader.GetString(11)));
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

    private const string ScheduleKey = "backup_schedule";
    private const string LastScheduledSlotKey = "backup_last_scheduled_slot";

    /// <summary>The backup schedule, or the default (daily at 02:00, keep 7/4/3) when none is saved.</summary>
    public BackupSchedule GetBackupSchedule()
    {
        if (GetSetting(ScheduleKey) is not { } json) return BackupSchedule.Default;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<BackupSchedule>(json) ?? BackupSchedule.Default;
        }
        catch (System.Text.Json.JsonException)
        {
            return BackupSchedule.Default;
        }
    }

    public void SetBackupSchedule(BackupSchedule schedule) => SetSetting(ScheduleKey, System.Text.Json.JsonSerializer.Serialize(schedule));

    /// <summary>The scheduled instant most recently handled: backed up, or given up on after retries.</summary>
    public DateTimeOffset? GetLastScheduledSlot() =>
        GetSetting(LastScheduledSlotKey) is { } value && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var slot) ? slot : null;

    public void SetLastScheduledSlot(DateTimeOffset slot) => SetSetting(LastScheduledSlotKey, slot.ToString("O", CultureInfo.InvariantCulture));

    public BackupRun? GetBackupRun(Guid id) => GetBackupRuns(int.MaxValue).FirstOrDefault(run => run.Id == id);

    private void SetSetting(string key, string value)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO app_settings (settings_key, settings_value) VALUES ($key, $value) ON CONFLICT(settings_key) DO UPDATE SET settings_value = excluded.settings_value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
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
    internal static void PrepareRestoredDatabase(SqliteConnection connection, BackupManifest manifest, DateTimeOffset restoredUtc, IReadOnlyDictionary<Guid, string>? protectedPublishingTokens = null)
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
        Execute("DELETE FROM app_settings WHERE settings_key IN ($installation, $folder, $passphrase, $cloud, $missing)",
            ("$installation", InstallationIdKey), ("$folder", BackupFolderKey), ("$passphrase", BackupPassphraseKey), ("$cloud", BackupCloudKey), ("$missing", BackupCloudMissingKey));
        Execute("DELETE FROM backup_cloud_objects");
        foreach (var (journalId, token) in protectedPublishingTokens ?? new Dictionary<Guid, string>())
            Execute("UPDATE journal_publishing SET protected_token = $token WHERE journal_id = $journal", ("$token", token), ("$journal", journalId.ToString("D")));
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
        // Backup settings stay with the machine, so start clean keeps backing up where it did.
        settings.CommandText = "SELECT settings_key, settings_value FROM app_settings WHERE settings_key IN ($installation, $folder, $epoch, $passphrase, $cloud, $schedule)";
        settings.Parameters.AddWithValue("$installation", InstallationIdKey);
        settings.Parameters.AddWithValue("$folder", BackupFolderKey);
        settings.Parameters.AddWithValue("$epoch", SessionEpochKey);
        settings.Parameters.AddWithValue("$passphrase", BackupPassphraseKey);
        settings.Parameters.AddWithValue("$cloud", BackupCloudKey);
        settings.Parameters.AddWithValue("$schedule", ScheduleKey);
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
        command.Parameters.AddWithValue("$cloudStatus", run.CloudStatus);
        command.Parameters.AddWithValue("$cloudMessage", run.CloudMessage);
    }
}

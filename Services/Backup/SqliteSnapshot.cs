using Microsoft.Data.Sqlite;
using SQLitePCL;
using TradeFoundry.Core;

namespace TradeFoundry.Services.Backup;

/// <summary>
/// Copies a live SQLite database with the online backup API and prepares the copy for an archive.
/// </summary>
/// <remarks>
/// Measured on a 3 GB journal: a private connection that steps 8 MB at a time with the destination
/// unsynchronised takes about 20 s. The app's own shared-cache connection would block every other
/// request for the whole copy, and a plain file copy can miss committed pages still in the WAL.
/// </remarks>
public static class SqliteSnapshot
{
    private const int PagesPerStep = 2048;
    private const long FlushEveryBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Copies <paramref name="sourcePath"/> to a new file at <paramref name="destinationPath"/>.
    /// Reports the fraction copied. Blocks the calling thread; run it off the request path.
    /// </summary>
    public static void Copy(string sourcePath, string destinationPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath)) throw new IOException($"The snapshot file {destinationPath} already exists.");

        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        source.Open();
        Execute(source, "PRAGMA busy_timeout = 5000");
        var pageSize = Convert.ToInt64(Scalar(source, "PRAGMA page_size"));

        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        destination.Open();
        Execute(destination, "PRAGMA journal_mode = OFF");
        Execute(destination, "PRAGMA synchronous = OFF");

        // A read transaction pins one consistent version of the source for the whole copy.
        Execute(source, "BEGIN");
        Scalar(source, "SELECT COUNT(*) FROM sqlite_master");
        try
        {
            var backup = raw.sqlite3_backup_init(destination.Handle, "main", source.Handle, "main");
            if (backup is null || backup.IsInvalid)
                throw new SqliteException(raw.sqlite3_errmsg(destination.Handle).utf8_to_string(), raw.sqlite3_errcode(destination.Handle));

            // Flushing through a second handle keeps dirty pages from piling up in the OS cache,
            // which otherwise stalls the whole machine when they are finally written.
            using var flusher = new FileStream(destinationPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                long flushedBytes = 0;
                int result;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result = raw.sqlite3_backup_step(backup, PagesPerStep);
                    if (result is raw.SQLITE_BUSY or raw.SQLITE_LOCKED)
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    if (result is not raw.SQLITE_OK and not raw.SQLITE_DONE)
                        throw new SqliteException(raw.sqlite3_errstr(result).utf8_to_string(), result);

                    var total = raw.sqlite3_backup_pagecount(backup);
                    var copied = total - raw.sqlite3_backup_remaining(backup);
                    var copiedBytes = copied * pageSize;
                    if (copiedBytes - flushedBytes >= FlushEveryBytes)
                    {
                        flusher.Flush(flushToDisk: true);
                        flushedBytes = copiedBytes;
                    }

                    if (total > 0) progress?.Report((double)copied / total);
                }
                while (result != raw.SQLITE_DONE);
                flusher.Flush(flushToDisk: true);
            }
            finally
            {
                raw.sqlite3_backup_finish(backup);
            }
        }
        finally
        {
            Execute(source, "COMMIT");
        }
    }

    /// <summary>
    /// Makes the copy self-contained and checks it: switches it out of WAL mode, overwrites stored
    /// secrets so they do not linger in free pages, and runs <c>quick_check</c>.
    /// </summary>
    public static SnapshotDetails Prepare(string snapshotPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = snapshotPath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA journal_mode = DELETE");
        Execute(connection, "PRAGMA secure_delete = ON");

        // Tokens are protected with keys that stay on this machine, so they would be useless after a
        // restore elsewhere and a liability in a file that leaves it.
        if (TableHasColumn(connection, "ai_connections", "protected_credentials"))
            Execute(connection, "UPDATE ai_connections SET protected_credentials = '' WHERE protected_credentials <> ''");
        if (TableHasColumn(connection, "journal_publishing", "protected_token"))
            Execute(connection, "UPDATE journal_publishing SET protected_token = '' WHERE protected_token <> ''");

        var check = Convert.ToString(Scalar(connection, "PRAGMA quick_check(1)"));
        if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The database copy failed its integrity check: {check}");

        var schemaVersion = Convert.ToInt32(Scalar(connection, "PRAGMA user_version"));
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (name, sql) in CountQueries)
        {
            if (TableExists(connection, sql.Table)) counts[name] = Convert.ToInt64(Scalar(connection, sql.Query));
        }

        var referenced = new List<string>();
        foreach (var (directory, table, condition) in ReferencedFileTables)
        {
            if (!TableExists(connection, table)) continue;
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT journal_id, storage_key FROM {table}{condition}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                referenced.Add($"{directory}/{reader.GetString(0)}/{reader.GetString(1)}");
        }

        return new SnapshotDetails(schemaVersion, counts, referenced);
    }

    private static readonly (string Name, (string Table, string Query) Sql)[] CountQueries =
    [
        ("journals", ("journals", "SELECT COUNT(*) FROM journals WHERE archived = 0")),
        ("trades", ("trades", "SELECT COUNT(*) FROM trades")),
        ("fills", ("fills", "SELECT COUNT(*) FROM fills")),
        ("reviews", ("trade_review_annotations", "SELECT COUNT(*) FROM trade_review_annotations")),
        ("dailyJournalEntries", ("daily_review_journals", "SELECT COUNT(*) FROM daily_review_journals WHERE editor_state_json <> ''")),
        ("images", ("daily_journal_images", "SELECT (SELECT COUNT(*) FROM daily_journal_images) + (SELECT COUNT(*) FROM trade_review_attachments WHERE removed_utc IS NULL)")),
        ("receipts", ("expense_receipts", "SELECT COUNT(*) FROM expense_receipts"))
    ];

    private static readonly (string Directory, string Table, string Condition)[] ReferencedFileTables =
    [
        (DataDirectoryLayout.DailyJournalImages, "daily_journal_images", string.Empty),
        (DataDirectoryLayout.ReviewAttachments, "trade_review_attachments", " WHERE removed_utc IS NULL"),
        (DataDirectoryLayout.ExpenseReceipts, "expense_receipts", string.Empty)
    ];

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static bool TableHasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pragma_table_info($table) WHERE name = $column";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return command.ExecuteScalar() is not null;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

/// <summary>What a prepared snapshot contains: its schema version, row counts, and the data files it references.</summary>
public sealed record SnapshotDetails(int SchemaVersion, IReadOnlyDictionary<string, long> Counts, IReadOnlyList<string> ReferencedFiles);

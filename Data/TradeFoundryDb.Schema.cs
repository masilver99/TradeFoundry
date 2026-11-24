using Microsoft.Data.Sqlite;

namespace TradeFoundry.Data;

public sealed partial class TradeFoundryDb
{
    /// <summary>
    /// The schema this build writes, stored in <c>PRAGMA user_version</c> after initialization.
    /// Bump it whenever <see cref="Initialize"/> gains a table, column, index or data migration, so an
    /// older build refuses a database it does not understand instead of running against it, and so a
    /// backup can record which app versions are able to restore it.
    /// </summary>
    /// <remarks>
    /// 1: the schema as of trade and day tags. Builds before it never stamped.
    /// 2: backup_runs.
    /// 3: journal_daily_limits.
    /// </remarks>
    public const int SchemaVersion = 3;

    /// <summary>Throws when the database was written by a newer TradeFoundry than this one.</summary>
    private void EnsureSchemaNotNewer(SqliteConnection connection)
    {
        var version = ReadSchemaVersion(connection);
        if (version > SchemaVersion)
        {
            throw new DatabaseNewerThanAppException(_databasePath, version, SchemaVersion);
        }
    }

    private static void StampSchemaVersion(SqliteConnection connection)
    {
        if (ReadSchemaVersion(connection) >= SchemaVersion)
        {
            return;
        }

        using var command = connection.CreateCommand();
        // PRAGMA arguments cannot be parameters; the value is a compile-time integer.
        command.CommandText = $"PRAGMA user_version = {SchemaVersion}";
        command.ExecuteNonQuery();
    }

    internal static int ReadSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The database's schema version is newer than this build supports. Opening it could silently
/// ignore or damage data that the newer version added, so startup stops instead.
/// </summary>
public sealed class DatabaseNewerThanAppException(string databasePath, int databaseVersion, int supportedVersion)
    : InvalidOperationException(
        $"The journal database at {databasePath} was last used by a newer version of TradeFoundry " +
        $"(schema {databaseVersion}; this version supports up to {supportedVersion}). Install the newer version, " +
        "or restore a backup made with this version.")
{
    public string DatabasePath { get; } = databasePath;
    public int DatabaseVersion { get; } = databaseVersion;
    public int SupportedVersion { get; } = supportedVersion;
}

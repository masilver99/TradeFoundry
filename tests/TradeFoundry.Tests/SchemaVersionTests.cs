using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class SchemaVersionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tradefoundry-schema-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "journal.db");

    [Fact]
    public void NewDatabaseIsStampedWithTheCurrentSchemaVersion()
    {
        Open();

        Assert.Equal(TradeFoundryDb.SchemaVersion, ReadUserVersion());
    }

    [Fact]
    public void UnstampedDatabaseFromAnOlderBuildIsUpgradedAndStamped()
    {
        Open();
        SetUserVersion(0);

        Open();

        Assert.Equal(TradeFoundryDb.SchemaVersion, ReadUserVersion());
    }

    [Fact]
    public void DatabaseFromANewerBuildIsRefusedBeforeAnythingIsWritten()
    {
        Directory.CreateDirectory(_directory);
        SetUserVersion(TradeFoundryDb.SchemaVersion + 1);

        var exception = Assert.Throws<DatabaseNewerThanAppException>(Open);

        Assert.Equal(TradeFoundryDb.SchemaVersion + 1, exception.DatabaseVersion);
        Assert.Equal(TradeFoundryDb.SchemaVersion, exception.SupportedVersion);
        Assert.Contains("newer version of TradeFoundry", exception.Message, StringComparison.Ordinal);
        Assert.Equal(TradeFoundryDb.SchemaVersion + 1, ReadUserVersion());
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void StoresUseTheSharedDataDirectoryNames()
    {
        Assert.Equal(
            ["daily-journal-images", "review-attachments", "expense-receipts", "image-settings"],
            DataDirectoryLayout.UserContentDirectories);
        Assert.DoesNotContain(DataDirectoryLayout.MigrationBackups, DataDirectoryLayout.UserContentDirectories);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private TradeFoundryDb Open() => new(Options.Create(new StorageOptions { DataDirectory = _directory, DatabaseFileName = "journal.db" }));

    private SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private long ReadUserVersion()
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)command.ExecuteScalar()!;
    }

    private void SetUserVersion(int version)
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {version}";
        command.ExecuteNonQuery();
    }
}

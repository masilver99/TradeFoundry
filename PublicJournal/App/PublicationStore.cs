using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeFoundry.Publishing;

namespace TradeFoundry.PublicJournal;

public sealed class PublicationConflictException(string message) : Exception(message);
public sealed record PublicPublication(PublicationStatus Status, PublicationSnapshot Snapshot);

public sealed class PublicationStore
{
    private readonly string _connectionString;

    public PublicationStore(string directory)
    {
        Directory.CreateDirectory(Path.GetFullPath(directory));
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetFullPath(directory), "publication.db"), Pooling = false }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS publication (
                id INTEGER PRIMARY KEY CHECK (id = 1), revision INTEGER NOT NULL DEFAULT 0,
                journal_id TEXT, snapshot_id TEXT, published_utc TEXT, snapshot_json TEXT, payload_hash TEXT);
            INSERT OR IGNORE INTO publication (id, revision) VALUES (1, 0);
            CREATE TABLE IF NOT EXISTS publication_images (id TEXT PRIMARY KEY, snapshot_id TEXT NOT NULL, content_type TEXT NOT NULL, content BLOB NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=10000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static PublicationStatus ReadStatus(SqliteDataReader reader) => new(reader.GetInt64(0),
        reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
        reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture));

    public PublicationStatus Status()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision, journal_id, snapshot_id, published_utc FROM publication WHERE id = 1";
        using var reader = command.ExecuteReader();
        reader.Read();
        return ReadStatus(reader);
    }

    public PublicPublication? Read()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision, journal_id, snapshot_id, published_utc, snapshot_json FROM publication WHERE id = 1";
        using var reader = command.ExecuteReader();
        reader.Read();
        return reader.IsDBNull(4) ? null : new(ReadStatus(reader), JsonSerializer.Deserialize<PublicationSnapshot>(reader.GetString(4), PublicationJson.Options)!);
    }

    public PublicationImage? Image(Guid snapshotId, string imageId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content_type, content FROM publication_images WHERE id = $id AND snapshot_id = $snapshot AND EXISTS (SELECT 1 FROM publication WHERE snapshot_id = $snapshot)";
        command.Parameters.AddWithValue("$id", imageId);
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(imageId, reader.GetString(0), (byte[])reader[1]) : null;
    }

    public PublicationStatus Publish(PublicationUpload upload)
    {
        PublicationValidation.Validate(upload.Snapshot);
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(upload.Snapshot, PublicationJson.Options)));
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var current = connection.CreateCommand();
        current.Transaction = transaction;
        current.CommandText = "SELECT revision, journal_id, snapshot_id, published_utc, payload_hash FROM publication WHERE id=1";
        PublicationStatus status;
        string? oldHash;
        using (var reader = current.ExecuteReader()) { reader.Read(); status = ReadStatus(reader); oldHash = reader.IsDBNull(4) ? null : reader.GetString(4); }
        if (status.BoundJournalId is not null && status.BoundJournalId != upload.Snapshot.JournalId)
            throw new PublicationConflictException("This website is bound to a different journal. Unpublish and explicitly reset its binding first.");
        if (status.SnapshotId == upload.Snapshot.SnapshotId)
        {
            if (hash != oldHash) throw new PublicationConflictException("A snapshot ID cannot be reused with different content.");
            return status;
        }
        if (status.Revision != upload.ExpectedRevision) throw new PublicationConflictException("The website changed after preview. Refresh status and create a new preview.");
        using var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = "DELETE FROM publication_images";
        clear.ExecuteNonQuery();
        foreach (var image in upload.Snapshot.Images)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO publication_images VALUES ($id, $snapshot, $type, $content)";
            insert.Parameters.AddWithValue("$id", image.Id);
            insert.Parameters.AddWithValue("$snapshot", upload.Snapshot.SnapshotId.ToString("D"));
            insert.Parameters.AddWithValue("$type", image.ContentType);
            insert.Parameters.AddWithValue("$content", image.Content);
            insert.ExecuteNonQuery();
        }
        var published = DateTimeOffset.UtcNow;
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE publication SET revision=revision+1, journal_id=$journal, snapshot_id=$snapshot, published_utc=$published, snapshot_json=$json, payload_hash=$hash WHERE id=1";
        update.Parameters.AddWithValue("$journal", upload.Snapshot.JournalId.ToString("D"));
        update.Parameters.AddWithValue("$snapshot", upload.Snapshot.SnapshotId.ToString("D"));
        update.Parameters.AddWithValue("$published", published.ToString("O"));
        update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(upload.Snapshot with { Images = [] }, PublicationJson.Options));
        update.Parameters.AddWithValue("$hash", hash);
        update.ExecuteNonQuery();
        transaction.Commit();
        return new(status.Revision + 1, upload.Snapshot.JournalId, upload.Snapshot.SnapshotId, published);
    }

    public PublicationStatus Unpublish(PublicationDelete request)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision, journal_id, snapshot_id, published_utc FROM publication WHERE id=1";
        PublicationStatus status;
        using (var reader = command.ExecuteReader()) { reader.Read(); status = ReadStatus(reader); }
        if (status.Revision != request.ExpectedRevision || status.BoundJournalId is not null && status.BoundJournalId != request.JournalId)
            throw new PublicationConflictException("Publication changed or belongs to another journal. Refresh status before unpublishing.");
        command.CommandText = "DELETE FROM publication_images; UPDATE publication SET revision=revision+1, snapshot_id=NULL, published_utc=NULL, snapshot_json=NULL, payload_hash=NULL, journal_id=$journal WHERE id=1";
        command.Parameters.AddWithValue("$journal", request.ResetBinding ? DBNull.Value : (object?)status.BoundJournalId?.ToString("D") ?? DBNull.Value);
        command.ExecuteNonQuery();
        transaction.Commit();
        return new(status.Revision + 1, request.ResetBinding ? null : status.BoundJournalId, null, null);
    }
}

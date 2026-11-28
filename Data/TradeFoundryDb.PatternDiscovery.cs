using System.Text.Json;
using TradeFoundry.Core;

namespace TradeFoundry.Data;

public sealed partial class TradeFoundryDb
{
    private static void InitializePatternDiscovery(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS pattern_discovery (
                journal_id TEXT PRIMARY KEY REFERENCES journals(id) ON DELETE CASCADE,
                input_version TEXT NOT NULL,
                snapshot_json TEXT NOT NULL
            )
            """;
        command.ExecuteNonQuery();
    }

    internal string GetPatternInputVersion(Guid journalId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        // Bar imports are shared across journals. Trade rebuilds can finish after the import commits.
        command.CommandText = """
            SELECT json_array(j.timezone, j.grouping_policy,
                (SELECT json_array(COUNT(*), MAX(imported_utc), SUM(new_rows))
                 FROM import_batches WHERE new_rows > 0 AND (source_type IN ($sierraBars, $bars)
                      OR (journal_id = j.id AND source_type <> $benchmark))),
                (SELECT json_array(COUNT(*), MAX(created_utc)) FROM trades WHERE journal_id = j.id),
                (SELECT json_array(COUNT(*), MAX(created_utc)) FROM trade_review_history WHERE journal_id = j.id))
            FROM journals j WHERE j.id = $journal
            """;
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$sierraBars", TradeFoundryConstants.SierraOhlcBars);
        command.Parameters.AddWithValue("$bars", TradeFoundryConstants.OhlcvBars);
        command.Parameters.AddWithValue("$benchmark", TradeFoundryConstants.BenchmarkSeries);
        return (string?)command.ExecuteScalar() ?? string.Empty;
    }

    internal (string Version, PatternDiscoverySnapshot Snapshot)? GetPatternSnapshot(Guid journalId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT input_version, snapshot_json FROM pattern_discovery WHERE journal_id = $journal";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var snapshot = JsonSerializer.Deserialize<PatternDiscoverySnapshot>(reader.GetString(1));
        return snapshot is null ? null : (reader.GetString(0), snapshot);
    }

    internal void SavePatternSnapshot(Guid journalId, string version, PatternDiscoverySnapshot snapshot)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pattern_discovery (journal_id, input_version, snapshot_json) VALUES ($journal, $version, $snapshot)
            ON CONFLICT(journal_id) DO UPDATE SET input_version = excluded.input_version, snapshot_json = excluded.snapshot_json
            """;
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(snapshot));
        command.ExecuteNonQuery();
    }

    internal IReadOnlyDictionary<string, PatternEntry> GetPatternEntries(Guid journalId)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        // Persisted allocations cover closing fills. Replay the same grouping that built the trades to recover first fills.
        var positions = BuildPositions(ReadFills(connection, transaction, journalId));
        return positions.Select((position, index) => (Position: position, Key: DerivedTradeSourceKey(position, index + 1)))
            .Where(item => item.Position.FirstOpeningFill.SourceType == TradeFoundryConstants.SierraFills)
            .ToDictionary(item => item.Key, item =>
            {
                var state = item.Position;
                return new PatternEntry(state.FirstOpeningFill.OrderId, state.EntryUtc, state.FirstOpeningFill.Price,
                    state.EntryReferences[0].Quantity, state.EntryReferences.Count == 1, state.FirstExitUtc);
            }, StringComparer.Ordinal);
    }

    internal IReadOnlyList<OrderEvent> GetPatternProtectiveEvents(Guid journalId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {OrderEventColumns} FROM order_events
            WHERE journal_id = $journal AND source_type = $source AND parent_order_id NOT IN ('', '0')
                AND (order_type LIKE '%stop%' OR order_type = 'Limit' COLLATE NOCASE)
            ORDER BY event_utc, row_number, id
            """;
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$source", TradeFoundryConstants.SierraFills);
        using var reader = command.ExecuteReader();
        var events = new List<OrderEvent>();
        while (reader.Read()) events.Add(ReadOrderEvent(reader));
        return events;
    }

    internal sealed record PatternEntry(string OrderId, DateTimeOffset Time, decimal Price, int Quantity,
        bool SingleOpeningFill, DateTimeOffset? FirstExitUtc);
}

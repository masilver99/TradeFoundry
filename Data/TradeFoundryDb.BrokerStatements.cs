using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeFoundry.Core;

namespace TradeFoundry.Data;

/// <summary>
/// Broker statements are imported evidence: once stored, a statement and its days are never edited,
/// only removed whole. Corrections they justify are written to the trade review layer.
/// </summary>
public sealed partial class TradeFoundryDb
{
    private static void InitializeBrokerStatements(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS broker_statements (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, broker TEXT NOT NULL, kind TEXT NOT NULL CHECK(kind IN ('daily', 'monthly')), account TEXT NOT NULL DEFAULT '', period_start TEXT NOT NULL, period_end TEXT NOT NULL, file_name TEXT NOT NULL, content_hash TEXT NOT NULL, summary_json TEXT NOT NULL, warnings_json TEXT NOT NULL DEFAULT '[]', imported_utc TEXT NOT NULL, UNIQUE(journal_id, content_hash));
            CREATE TABLE IF NOT EXISTS broker_statement_days (statement_id TEXT NOT NULL REFERENCES broker_statements(id) ON DELETE CASCADE, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, trade_date TEXT NOT NULL, buy_contracts INTEGER NOT NULL, sell_contracts INTEGER NOT NULL, realized_pnl TEXT NOT NULL, has_realized_pnl INTEGER NOT NULL, fees_json TEXT NOT NULL, fills_json TEXT NOT NULL, PRIMARY KEY(statement_id, trade_date));
            CREATE INDEX IF NOT EXISTS ix_broker_statement_days_journal_date ON broker_statement_days(journal_id, trade_date);
            """;
        command.ExecuteNonQuery();
    }

    private static readonly JsonSerializerOptions StatementJson = new(JsonSerializerDefaults.Web);

    private static string KindText(BrokerStatementKind kind) => kind == BrokerStatementKind.Daily ? "daily" : "monthly";
    private static BrokerStatementKind ParseKind(string text) => text == "daily" ? BrokerStatementKind.Daily : BrokerStatementKind.Monthly;

    /// <summary>Stores a parsed statement. Returns the existing statement and <c>Duplicate = true</c> when the same file was already imported.</summary>
    public (BrokerStatement Statement, bool Duplicate) ImportBrokerStatement(Guid journalId, ParsedBrokerStatement parsed, string fileName, string contentHash)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        if (GetBrokerStatements(journalId).FirstOrDefault(s => s.ContentHash == contentHash) is { } existing) return (existing, true);

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO broker_statements (id, journal_id, broker, kind, account, period_start, period_end, file_name, content_hash, summary_json, warnings_json, imported_utc) VALUES ($id, $journal, $broker, $kind, $account, $start, $end, $file, $hash, $summary, $warnings, $imported)";
            insert.Parameters.AddWithValue("$id", id.ToString());
            insert.Parameters.AddWithValue("$journal", journalId.ToString());
            insert.Parameters.AddWithValue("$broker", parsed.Broker);
            insert.Parameters.AddWithValue("$kind", KindText(parsed.Kind));
            insert.Parameters.AddWithValue("$account", parsed.Account);
            insert.Parameters.AddWithValue("$start", parsed.PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$end", parsed.PeriodEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$file", fileName);
            insert.Parameters.AddWithValue("$hash", contentHash);
            insert.Parameters.AddWithValue("$summary", JsonSerializer.Serialize(parsed.Summary, StatementJson));
            insert.Parameters.AddWithValue("$warnings", JsonSerializer.Serialize(parsed.Warnings, StatementJson));
            insert.Parameters.AddWithValue("$imported", now.ToString("O"));
            insert.ExecuteNonQuery();
        }

        foreach (var day in parsed.Days)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO broker_statement_days (statement_id, journal_id, trade_date, buy_contracts, sell_contracts, realized_pnl, has_realized_pnl, fees_json, fills_json) VALUES ($statement, $journal, $date, $buy, $sell, $pnl, $hasPnl, $fees, $fills)";
            insert.Parameters.AddWithValue("$statement", id.ToString());
            insert.Parameters.AddWithValue("$journal", journalId.ToString());
            insert.Parameters.AddWithValue("$date", day.TradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$buy", day.BuyContracts);
            insert.Parameters.AddWithValue("$sell", day.SellContracts);
            insert.Parameters.AddWithValue("$pnl", day.RealizedPnl.ToString(CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$hasPnl", day.HasRealizedPnl ? 1 : 0);
            insert.Parameters.AddWithValue("$fees", JsonSerializer.Serialize(day.Fees, StatementJson));
            insert.Parameters.AddWithValue("$fills", JsonSerializer.Serialize(day.Fills, StatementJson));
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return (GetBrokerStatements(journalId).First(s => s.Id == id), false);
    }

    public IReadOnlyList<BrokerStatement> GetBrokerStatements(Guid journalId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT s.id, s.broker, s.kind, s.account, s.period_start, s.period_end, s.file_name, s.content_hash, s.summary_json, s.warnings_json, s.imported_utc, (SELECT COUNT(*) FROM broker_statement_days d WHERE d.statement_id = s.id) FROM broker_statements s WHERE s.journal_id = $journal ORDER BY s.period_start DESC, s.kind, s.imported_utc DESC";
        command.Parameters.AddWithValue("$journal", journalId.ToString());
        using var reader = command.ExecuteReader();
        var results = new List<BrokerStatement>();
        while (reader.Read())
        {
            results.Add(new BrokerStatement(
                Guid.Parse(reader.GetString(0)),
                journalId,
                reader.GetString(1),
                ParseKind(reader.GetString(2)),
                reader.GetString(3),
                DateOnly.ParseExact(reader.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateOnly.ParseExact(reader.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetString(6),
                reader.GetString(7),
                JsonSerializer.Deserialize<StatementAccountSummary>(reader.GetString(8), StatementJson) ?? new(null, null, null, null, null),
                JsonSerializer.Deserialize<string[]>(reader.GetString(9), StatementJson) ?? [],
                reader.GetInt32(11),
                DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture)));
        }
        return results;
    }

    public IReadOnlyList<BrokerStatementDayRecord> GetBrokerStatementDays(Guid journalId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT d.statement_id, s.kind, s.file_name, s.imported_utc, d.trade_date, d.buy_contracts, d.sell_contracts, d.realized_pnl, d.has_realized_pnl, d.fees_json, d.fills_json, s.account, s.warnings_json FROM broker_statement_days d JOIN broker_statements s ON s.id = d.statement_id WHERE d.journal_id = $journal ORDER BY d.trade_date, s.kind, s.imported_utc";
        command.Parameters.AddWithValue("$journal", journalId.ToString());
        using var reader = command.ExecuteReader();
        var results = new List<BrokerStatementDayRecord>();
        while (reader.Read())
        {
            var day = new StatementDay
            {
                TradeDate = DateOnly.ParseExact(reader.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                BuyContracts = reader.GetInt32(5),
                SellContracts = reader.GetInt32(6),
                RealizedPnl = decimal.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                HasRealizedPnl = reader.GetInt32(8) != 0,
                Fees = JsonSerializer.Deserialize<List<StatementFee>>(reader.GetString(9), StatementJson) ?? [],
                Fills = JsonSerializer.Deserialize<List<StatementFill>>(reader.GetString(10), StatementJson) ?? []
            };
            var warnings = JsonSerializer.Deserialize<string[]>(reader.GetString(12), StatementJson) ?? [];
            results.Add(new BrokerStatementDayRecord(Guid.Parse(reader.GetString(0)), ParseKind(reader.GetString(1)), reader.GetString(2), reader.GetString(11), warnings.Length > 0, DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), day));
        }
        return results;
    }

    public bool DeleteBrokerStatement(Guid journalId, Guid statementId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM broker_statements WHERE journal_id = $journal AND id = $id";
        command.Parameters.AddWithValue("$journal", journalId.ToString());
        command.Parameters.AddWithValue("$id", statementId.ToString());
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// The current review revision of every trade review in the journal, matching what a save checks
    /// against: the annotation's revision, or the last history revision once an annotation is gone.
    /// </summary>
    public IReadOnlyDictionary<string, int> GetTradeReviewRevisions(Guid journalId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT review_key, MAX(revision) FROM (SELECT review_key, revision FROM trade_review_annotations WHERE journal_id = $journal UNION ALL SELECT review_key, revision FROM trade_review_history WHERE journal_id = $journal) GROUP BY review_key";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        var results = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read()) results[reader.GetString(0)] = reader.GetInt32(1);
        return results;
    }

    /// <summary>
    /// Writes fee components to several trade reviews in one transaction, clearing any all-in override and
    /// keeping every other review field. Each write is a normal revision, so it shows in history and can be
    /// reverted. If any review is no longer at its expected revision, nothing is written.
    /// </summary>
    public int ApplyTradeReviewFees(Guid journalId, IReadOnlyList<TradeFeeUpdate> updates)
    {
        foreach (var update in updates)
        {
            if (string.IsNullOrWhiteSpace(update.ReviewKey)) throw new ArgumentException("A review key is required.", nameof(updates));
            if (update.ExchangeFees < 0m || update.NfaFees < 0m || update.ClearingFees < 0m) throw new ArgumentOutOfRangeException(nameof(updates), "Fee components cannot be negative.");
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var catalog = ReadTagCatalog(connection, transaction, journalId);
        var now = DateTimeOffset.UtcNow;
        foreach (var update in updates)
        {
            using (var exists = connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = "SELECT 1 FROM trades WHERE journal_id = $journal AND review_key = $reviewKey LIMIT 1";
                exists.Parameters.AddWithValue("$journal", journalId.ToString("D"));
                exists.Parameters.AddWithValue("$reviewKey", update.ReviewKey);
                if (exists.ExecuteScalar() is null) throw new StatementFeeConflictException("A trade for these fees no longer exists. Reload and try again.");
            }

            var current = ReadTradeReviewOrHistoryRevision(connection, transaction, journalId, update.ReviewKey);
            if (current.Revision != update.ExpectedRevision)
                throw new StatementFeeConflictException("A trade review changed after these fees were calculated, so nothing was saved. Reload and try again.");

            var patch = PatchFromAnnotation(current, current.Revision, update.Reason);
            patch.ExchangeFees = update.ExchangeFees;
            patch.NfaFees = update.NfaFees;
            patch.ClearingFees = update.ClearingFees;
            patch.AllInCommission = null;
            var normalized = NormalizeReviewPatch(patch);
            var tags = current.TagIds.Select(id => catalog.Resolve(id)).OfType<Tag>().ToArray();
            var annotation = AnnotationFromPatch(journalId, update.ReviewKey, current.Revision + 1, normalized, tags, now);
            WriteTradeReview(connection, transaction, annotation);
            InsertTradeReviewHistory(connection, transaction, journalId, update.ReviewKey, annotation.Revision, "statement fees", SerializeReview(current), SerializeReview(annotation), normalized.Reason, now);
        }

        transaction.Commit();
        return updates.Count;
    }
}

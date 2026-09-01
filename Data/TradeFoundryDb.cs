using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;

namespace TradeFoundry.Data;

/// <summary>
/// The phase 1 persistence boundary.  SQLite is intentionally kept behind this
/// class so a later PostgreSQL provider can be introduced without changing the
/// import and page models.
/// </summary>
public sealed class TradeFoundryDb
{
    private const string DerivedFillSource = "Derived fills";
    private readonly string _connectionString;

    public TradeFoundryDb(IOptions<StorageOptions> options)
    {
        var storage = options.Value;
        var directory = Path.GetFullPath(string.IsNullOrWhiteSpace(storage.DataDirectory) ? "data" : storage.DataDirectory);
        Directory.CreateDirectory(directory);
        var fileName = string.IsNullOrWhiteSpace(storage.DatabaseFileName) ? "journal.db" : storage.DatabaseFileName;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, fileName),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        Initialize();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON";
            foreignKeys.ExecuteNonQuery();
        }
        using (var busyTimeout = connection.CreateCommand())
        {
            busyTimeout.CommandText = "PRAGMA busy_timeout = 5000";
            busyTimeout.ExecuteNonQuery();
        }
        using (var journalMode = connection.CreateCommand())
        {
            journalMode.CommandText = "PRAGMA journal_mode = WAL";
            journalMode.ExecuteScalar();
        }
        return connection;
    }

    private void Initialize()
    {
        using (var connection = OpenConnection())
        {
        var statements = new[]
        {
            "CREATE TABLE IF NOT EXISTS app_users (id TEXT PRIMARY KEY, display_name TEXT NOT NULL, password_hash TEXT NOT NULL, created_utc TEXT NOT NULL)",
            "CREATE TABLE IF NOT EXISTS journals (id TEXT PRIMARY KEY, owner_user_id TEXT NOT NULL REFERENCES app_users(id), name TEXT NOT NULL, execution_context TEXT NOT NULL, labels TEXT NOT NULL DEFAULT '', timezone TEXT NOT NULL DEFAULT 'UTC', currency TEXT NOT NULL DEFAULT 'USD', grouping_policy TEXT NOT NULL DEFAULT 'flat_to_flat', created_utc TEXT NOT NULL, archived INTEGER NOT NULL DEFAULT 0)",
            "CREATE INDEX IF NOT EXISTS ix_journals_owner ON journals(owner_user_id, archived, created_utc)",
            "CREATE TABLE IF NOT EXISTS import_batches (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id), file_name TEXT NOT NULL, source_type TEXT NOT NULL, imported_utc TEXT NOT NULL, total_rows INTEGER NOT NULL, new_rows INTEGER NOT NULL, duplicate_rows INTEGER NOT NULL, status TEXT NOT NULL, message TEXT NOT NULL DEFAULT '')",
            "CREATE INDEX IF NOT EXISTS ix_import_batches_journal ON import_batches(journal_id, imported_utc DESC)",
            "CREATE TABLE IF NOT EXISTS raw_records (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id), import_batch_id TEXT NOT NULL REFERENCES import_batches(id), source_type TEXT NOT NULL, source_key TEXT NOT NULL, row_number INTEGER NOT NULL, payload_json TEXT NOT NULL, status TEXT NOT NULL, error TEXT NOT NULL DEFAULT '', UNIQUE(journal_id, source_type, source_key))",
            "CREATE TABLE IF NOT EXISTS fills (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id), import_batch_id TEXT NOT NULL REFERENCES import_batches(id), source_type TEXT NOT NULL, source_key TEXT NOT NULL, event_utc TEXT NOT NULL, source_time_text TEXT NOT NULL DEFAULT '', symbol TEXT NOT NULL, account TEXT NOT NULL DEFAULT '', side TEXT NOT NULL, quantity INTEGER NOT NULL, price TEXT NOT NULL, open_close TEXT NOT NULL DEFAULT '', high TEXT NULL, low TEXT NULL, note TEXT NOT NULL DEFAULT '', position_quantity INTEGER NULL, order_id TEXT NOT NULL DEFAULT '', service_order_id TEXT NOT NULL DEFAULT '', fees TEXT NOT NULL DEFAULT '0', row_number INTEGER NOT NULL, UNIQUE(journal_id, source_type, source_key))",
            "CREATE INDEX IF NOT EXISTS ix_fills_journal_time ON fills(journal_id, symbol, account, event_utc, row_number)",
            "CREATE TABLE IF NOT EXISTS trades (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id), import_batch_id TEXT NULL REFERENCES import_batches(id), source_type TEXT NOT NULL, source_key TEXT NOT NULL, grouping_policy TEXT NOT NULL, sequence INTEGER NOT NULL, symbol TEXT NOT NULL, account TEXT NOT NULL DEFAULT '', direction TEXT NOT NULL, entry_utc TEXT NOT NULL, exit_utc TEXT NULL, entry_price TEXT NOT NULL, exit_price TEXT NULL, quantity INTEGER NOT NULL, closed_quantity INTEGER NOT NULL, gross_points TEXT NOT NULL DEFAULT '0', average_points TEXT NOT NULL DEFAULT '0', gross_pnl TEXT NOT NULL DEFAULT '0', fees TEXT NOT NULL DEFAULT '0', net_pnl TEXT NOT NULL DEFAULT '0', mae_points TEXT NULL, mfe_points TEXT NULL, status TEXT NOT NULL, note TEXT NOT NULL DEFAULT '', created_utc TEXT NOT NULL, UNIQUE(journal_id, source_type, source_key))",
            "CREATE INDEX IF NOT EXISTS ix_trades_journal_time ON trades(journal_id, entry_utc)",
            "CREATE TABLE IF NOT EXISTS trade_fill_allocations (trade_id TEXT NOT NULL REFERENCES trades(id) ON DELETE CASCADE, fill_id TEXT NOT NULL REFERENCES fills(id) ON DELETE CASCADE, quantity INTEGER NOT NULL, PRIMARY KEY(trade_id, fill_id))",
            "CREATE TABLE IF NOT EXISTS bar_series (id TEXT PRIMARY KEY, symbol TEXT NOT NULL, interval TEXT NOT NULL, series_key TEXT NOT NULL UNIQUE, created_utc TEXT NOT NULL)",
            "CREATE TABLE IF NOT EXISTS journal_bar_series (journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, series_id TEXT NOT NULL REFERENCES bar_series(id) ON DELETE CASCADE, PRIMARY KEY(journal_id, series_id))",
            "CREATE TABLE IF NOT EXISTS bars (id TEXT PRIMARY KEY, series_id TEXT NOT NULL REFERENCES bar_series(id) ON DELETE CASCADE, import_batch_id TEXT NULL REFERENCES import_batches(id) ON DELETE SET NULL, event_utc TEXT NOT NULL, open TEXT NOT NULL, high TEXT NOT NULL, low TEXT NOT NULL, close TEXT NOT NULL, volume INTEGER NULL, UNIQUE(series_id, event_utc))",
            "CREATE INDEX IF NOT EXISTS ix_bars_series_time ON bars(series_id, event_utc)"
        };
        foreach (var statement in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        }

        // Older Sierra imports may have persisted fixed-point prices before
        // the importer could identify the display price in OrderActionSource.
        // Repair normalized values from immutable raw_records at startup, then
        // rebuild the derived trades that depend on them.
        RepairSierraPriceScales();
    }

    private void RepairSierraPriceScales()
    {
        var repairs = new List<SierraPriceRepair>();
        using (var connection = OpenConnection())
        {
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT f.id, f.journal_id, f.price, f.high, f.low, f.symbol, r.payload_json FROM fills f JOIN raw_records r ON r.journal_id = f.journal_id AND r.source_type = f.source_type AND r.source_key = f.source_key WHERE f.source_type = $source";
            query.Parameters.AddWithValue("$source", TradeFoundryConstants.SierraFills);
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                if (!TryParseJson(reader.GetString(6), out var payload) ||
                    !TryJsonDecimal(payload, out var rawPrice, "fillprice", "price"))
                    continue;

                var symbol = TryJsonString(payload, out var rawSymbol, "symbol", "ticker", "instrument")
                    ? rawSymbol
                    : reader.GetString(5);
                var orderActionSource = TryJsonString(payload, out var sourceText, "orderactionsource") ? sourceText : string.Empty;
                var scale = SierraPriceNormalizer.DetermineScale(rawPrice, symbol, orderActionSource);
                if (scale == 1m) continue;

                var correctedPrice = rawPrice / scale;
                decimal? correctedHigh = TryJsonDecimal(payload, out var rawHigh, "highduringposition", "high") ? rawHigh / scale : null;
                decimal? correctedLow = TryJsonDecimal(payload, out var rawLow, "lowduringposition", "low") ? rawLow / scale : null;
                var currentPrice = ParseDecimal(reader.GetString(2));
                decimal? currentHigh = reader.IsDBNull(3) ? null : ParseDecimal(reader.GetString(3));
                decimal? currentLow = reader.IsDBNull(4) ? null : ParseDecimal(reader.GetString(4));
                if (currentPrice != correctedPrice || currentHigh != correctedHigh || currentLow != correctedLow)
                {
                    repairs.Add(new SierraPriceRepair(
                        Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), correctedPrice, correctedHigh, correctedLow));
                }
            }

            if (repairs.Count > 0)
            {
                using var transaction = connection.BeginTransaction();
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE fills SET price = $price, high = $high, low = $low WHERE id = $id";
                var idParameter = update.Parameters.Add("$id", SqliteType.Text);
                var priceParameter = update.Parameters.Add("$price", SqliteType.Text);
                var highParameter = update.Parameters.Add("$high", SqliteType.Text);
                var lowParameter = update.Parameters.Add("$low", SqliteType.Text);
                foreach (var repair in repairs)
                {
                    idParameter.Value = repair.FillId.ToString("D");
                    priceParameter.Value = NumberFormat.Decimal(repair.Price);
                    highParameter.Value = repair.High.HasValue ? NumberFormat.Decimal(repair.High.Value) : DBNull.Value;
                    lowParameter.Value = repair.Low.HasValue ? NumberFormat.Decimal(repair.Low.Value) : DBNull.Value;
                    update.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        foreach (var journalId in repairs.Select(x => x.JournalId).Distinct())
            RebuildFlatTrades(journalId, GetJournal(journalId)?.GroupingPolicy ?? "flat_to_flat");
    }

    private static bool TryParseJson(string text, out JsonElement payload)
    {
        payload = default;
        try
        {
            using var document = JsonDocument.Parse(text);
            payload = document.RootElement.Clone();
            return payload.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryJsonDecimal(JsonElement payload, out decimal value, params string[] names)
    {
        value = 0m;
        foreach (var name in names)
        {
            if (!payload.TryGetProperty(name, out var element)) continue;
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value)) return true;
            if (element.ValueKind == JsonValueKind.String && decimal.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value)) return true;
        }
        return false;
    }

    private static bool TryJsonString(JsonElement payload, out string value, params string[] names)
    {
        value = string.Empty;
        foreach (var name in names)
        {
            if (!payload.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) continue;
            value = element.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }
        return false;
    }

    public bool HasOwner()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM app_users WHERE id = $id)";
        command.Parameters.AddWithValue("$id", TradeFoundryConstants.OwnerUserId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    public AppUser? GetOwner()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, display_name FROM app_users WHERE id = $id";
        command.Parameters.AddWithValue("$id", TradeFoundryConstants.OwnerUserId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new AppUser { Id = reader.GetString(0), DisplayName = reader.GetString(1) }
            : null;
    }

    public void CreateOwner(string displayName, string passwordHash)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO app_users (id, display_name, password_hash, created_utc) VALUES ($id, $name, $hash, $created)";
        command.Parameters.AddWithValue("$id", TradeFoundryConstants.OwnerUserId);
        command.Parameters.AddWithValue("$name", displayName.Trim());
        command.Parameters.AddWithValue("$hash", passwordHash);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public string? GetOwnerPasswordHash()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT password_hash FROM app_users WHERE id = $id";
        command.Parameters.AddWithValue("$id", TradeFoundryConstants.OwnerUserId);
        return command.ExecuteScalar() as string;
    }

    public IReadOnlyList<Journal> GetJournals(bool includeArchived = false)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, name, execution_context, labels, timezone, currency, grouping_policy, created_utc FROM journals WHERE owner_user_id = $owner {(includeArchived ? string.Empty : "AND archived = 0")} ORDER BY created_utc";
        command.Parameters.AddWithValue("$owner", TradeFoundryConstants.OwnerUserId);
        using var reader = command.ExecuteReader();
        var journals = new List<Journal>();
        while (reader.Read()) journals.Add(ReadJournal(reader));
        return journals;
    }

    public Journal? GetJournal(Guid journalId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, execution_context, labels, timezone, currency, grouping_policy, created_utc FROM journals WHERE id = $id AND owner_user_id = $owner AND archived = 0";
        command.Parameters.AddWithValue("$id", journalId.ToString("D"));
        command.Parameters.AddWithValue("$owner", TradeFoundryConstants.OwnerUserId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJournal(reader) : null;
    }

    public Journal CreateJournal(string name, string executionContext, string labels, string timeZone, string currency, string groupingPolicy)
    {
        var journal = new Journal
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? "My futures journal" : name.Trim(),
            ExecutionContext = string.IsNullOrWhiteSpace(executionContext) ? "live" : executionContext.Trim().ToLowerInvariant(),
            Labels = labels?.Trim() ?? string.Empty,
            TimeZone = string.IsNullOrWhiteSpace(timeZone) ? "UTC" : timeZone.Trim(),
            Currency = string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant(),
            GroupingPolicy = string.IsNullOrWhiteSpace(groupingPolicy) ? "flat_to_flat" : groupingPolicy.Trim(),
            CreatedUtc = DateTimeOffset.UtcNow
        };
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO journals (id, owner_user_id, name, execution_context, labels, timezone, currency, grouping_policy, created_utc) VALUES ($id, $owner, $name, $context, $labels, $timezone, $currency, $grouping, $created)";
        command.Parameters.AddWithValue("$id", journal.Id.ToString("D"));
        command.Parameters.AddWithValue("$owner", TradeFoundryConstants.OwnerUserId);
        command.Parameters.AddWithValue("$name", journal.Name);
        command.Parameters.AddWithValue("$context", journal.ExecutionContext);
        command.Parameters.AddWithValue("$labels", journal.Labels);
        command.Parameters.AddWithValue("$timezone", journal.TimeZone);
        command.Parameters.AddWithValue("$currency", journal.Currency);
        command.Parameters.AddWithValue("$grouping", journal.GroupingPolicy);
        command.Parameters.AddWithValue("$created", journal.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        return journal;
    }

    public void ArchiveJournal(Guid journalId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE journals SET archived = 1 WHERE id = $id AND owner_user_id = $owner";
        command.Parameters.AddWithValue("$id", journalId.ToString("D"));
        command.Parameters.AddWithValue("$owner", TradeFoundryConstants.OwnerUserId);
        command.ExecuteNonQuery();
    }

    public JournalSnapshot GetSnapshot(Guid journalId)
    {
        var journal = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        var trades = new List<Trade>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, journal_id, import_batch_id, source_type, source_key, grouping_policy, sequence, symbol, account, direction, entry_utc, exit_utc, entry_price, exit_price, quantity, closed_quantity, gross_points, average_points, gross_pnl, fees, net_pnl, mae_points, mfe_points, status, note FROM trades WHERE journal_id = $journal ORDER BY entry_utc, sequence";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read()) trades.Add(ReadTrade(reader));
        }

        var imports = new List<ImportBatch>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, journal_id, file_name, source_type, imported_utc, total_rows, new_rows, duplicate_rows, status, message FROM import_batches WHERE journal_id = $journal ORDER BY imported_utc DESC LIMIT 25";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read()) imports.Add(ReadImport(reader));
        }

        var symbols = trades.Select(x => x.Symbol).Concat(GetFillSymbols(connection, journalId)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        var daily = trades.Where(x => x.ExitUtc.HasValue).GroupBy(x => DateOnly.FromDateTime((x.ExitUtc ?? x.EntryUtc).UtcDateTime.Date)).OrderBy(x => x.Key).Select(x => new DailyPnl { Date = x.Key, NetPnl = x.Sum(t => t.NetPnl), TradeCount = x.Count() }).ToArray();
        return new JournalSnapshot { Journal = journal, Trades = trades, Imports = imports, Symbols = symbols, DailyPnl = daily };
    }

    public Trade? GetTrade(Guid tradeId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, journal_id, import_batch_id, source_type, source_key, grouping_policy, sequence, symbol, account, direction, entry_utc, exit_utc, entry_price, exit_price, quantity, closed_quantity, gross_points, average_points, gross_pnl, fees, net_pnl, mae_points, mfe_points, status, note FROM trades WHERE id = $id";
        command.Parameters.AddWithValue("$id", tradeId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTrade(reader) : null;
    }

    public IReadOnlyList<Bar> GetBarsForTrade(Guid journalId, string symbol, DateTimeOffset start, DateTimeOffset end, string interval = "source")
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT b.id, b.series_id, s.symbol, s.interval, b.event_utc, b.open, b.high, b.low, b.close, b.volume FROM bars b JOIN bar_series s ON s.id = b.series_id JOIN journal_bar_series jbs ON jbs.series_id = s.id WHERE jbs.journal_id = $journal AND s.symbol = $symbol AND ($interval = '' OR s.interval = $interval) AND b.event_utc >= $start AND b.event_utc <= $end ORDER BY b.event_utc";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$symbol", symbol);
        command.Parameters.AddWithValue("$interval", interval);
        command.Parameters.AddWithValue("$start", start.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$end", end.ToString("O", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        var bars = new List<Bar>();
        while (reader.Read()) bars.Add(ReadBar(reader));
        return bars;
    }

    public ImportResult CommitImport(Guid journalId, string fileName, ParsedImport parsed, string groupingPolicy, string interval)
    {
        var batchId = Guid.NewGuid();
        var importedUtc = DateTimeOffset.UtcNow;
        var newRows = 0;
        var duplicateRows = 0;
        var insertedFills = 0;
        var messages = new List<string>(parsed.Warnings);

        using (var connection = OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using (var batch = connection.CreateCommand())
            {
                batch.Transaction = transaction;
                batch.CommandText = "INSERT INTO import_batches (id, journal_id, file_name, source_type, imported_utc, total_rows, new_rows, duplicate_rows, status, message) VALUES ($id, $journal, $file, $source, $utc, $total, 0, 0, 'completed', '')";
                batch.Parameters.AddWithValue("$id", batchId.ToString("D"));
                batch.Parameters.AddWithValue("$journal", journalId.ToString("D"));
                batch.Parameters.AddWithValue("$file", fileName);
                batch.Parameters.AddWithValue("$source", parsed.SourceType);
                batch.Parameters.AddWithValue("$utc", importedUtc.ToString("O", CultureInfo.InvariantCulture));
                batch.Parameters.AddWithValue("$total", parsed.Records.Count + parsed.Trades.Count);
                batch.ExecuteNonQuery();
            }

            foreach (var record in parsed.Records)
            {
                using var raw = connection.CreateCommand();
                raw.Transaction = transaction;
                raw.CommandText = "INSERT OR IGNORE INTO raw_records (id, journal_id, import_batch_id, source_type, source_key, row_number, payload_json, status) VALUES ($id, $journal, $batch, $source, $key, $row, $payload, 'new')";
                raw.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                raw.Parameters.AddWithValue("$journal", journalId.ToString("D"));
                raw.Parameters.AddWithValue("$batch", batchId.ToString("D"));
                raw.Parameters.AddWithValue("$source", record.SourceType);
                raw.Parameters.AddWithValue("$key", record.SourceKey);
                raw.Parameters.AddWithValue("$row", record.RowNumber);
                raw.Parameters.AddWithValue("$payload", record.PayloadJson);
                if (raw.ExecuteNonQuery() == 0)
                {
                    duplicateRows++;
                    continue;
                }

                newRows++;
                if (record.Fill is not null && InsertFill(connection, transaction, journalId, batchId, record.Fill)) insertedFills++;
                if (record.Bar is not null) InsertBar(connection, transaction, journalId, batchId, record.Bar);
            }

            foreach (var trade in parsed.Trades)
            {
                if (InsertDirectTrade(connection, transaction, journalId, batchId, trade, groupingPolicy)) newRows++;
                else duplicateRows++;
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE import_batches SET new_rows = $new, duplicate_rows = $duplicates, message = $message WHERE id = $id";
            update.Parameters.AddWithValue("$new", newRows);
            update.Parameters.AddWithValue("$duplicates", duplicateRows);
            update.Parameters.AddWithValue("$message", string.Join(" ", messages));
            update.Parameters.AddWithValue("$id", batchId.ToString("D"));
            update.ExecuteNonQuery();
            transaction.Commit();
        }

        if (insertedFills > 0)
            RebuildFlatTrades(journalId, groupingPolicy);

        var batchResult = GetImport(batchId) ?? throw new InvalidOperationException("The import batch could not be read after commit.");
        return new ImportResult { Batch = batchResult, Warnings = parsed.Warnings };
    }

    public void RemoveImportBatch(Guid batchId)
    {
        Guid? journalId = null;
        string? sourceType = null;
        using (var connection = OpenConnection())
        {
            using var lookup = connection.CreateCommand();
            lookup.CommandText = "SELECT journal_id, source_type FROM import_batches WHERE id = $id";
            lookup.Parameters.AddWithValue("$id", batchId.ToString("D"));
            using var reader = lookup.ExecuteReader();
            if (reader.Read())
            {
                journalId = Guid.Parse(reader.GetString(0));
                sourceType = reader.GetString(1);
            }
        }
        if (!journalId.HasValue) return;

        using (var connection = OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            foreach (var sql in new[]
            {
                "DELETE FROM trade_fill_allocations WHERE trade_id IN (SELECT id FROM trades WHERE import_batch_id = $batch)",
                "DELETE FROM trades WHERE import_batch_id = $batch",
                "DELETE FROM fills WHERE import_batch_id = $batch",
                "DELETE FROM bars WHERE import_batch_id = $batch",
                "DELETE FROM raw_records WHERE import_batch_id = $batch",
                "DELETE FROM import_batches WHERE id = $batch"
            })
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        if (sourceType != TradeFoundryConstants.TradingViewStrategy)
            RebuildFlatTrades(journalId.Value, GetJournal(journalId.Value)?.GroupingPolicy ?? "flat_to_flat");
    }

    public void RebuildFlatTrades(Guid journalId, string groupingPolicy)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var deleteAllocations = connection.CreateCommand())
        {
            deleteAllocations.Transaction = transaction;
            deleteAllocations.CommandText = "DELETE FROM trade_fill_allocations WHERE trade_id IN (SELECT id FROM trades WHERE journal_id = $journal AND source_type = $source)";
            deleteAllocations.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            deleteAllocations.Parameters.AddWithValue("$source", DerivedFillSource);
            deleteAllocations.ExecuteNonQuery();
        }
        using (var deleteTrades = connection.CreateCommand())
        {
            deleteTrades.Transaction = transaction;
            deleteTrades.CommandText = "DELETE FROM trades WHERE journal_id = $journal AND source_type = $source";
            deleteTrades.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            deleteTrades.Parameters.AddWithValue("$source", DerivedFillSource);
            deleteTrades.ExecuteNonQuery();
        }

        var fills = ReadFills(connection, transaction, journalId);
        var states = new Dictionary<string, PositionState>(StringComparer.OrdinalIgnoreCase);
        var sequence = 0;
        foreach (var fill in fills)
        {
            if (fill.Quantity <= 0 || string.IsNullOrWhiteSpace(fill.Symbol)) continue;
            var signed = IsBuy(fill.Side) ? fill.Quantity : -fill.Quantity;
            var key = $"{fill.Account}\u001f{fill.Symbol}";
            states.TryGetValue(key, out var state);
            var remaining = Math.Abs(signed);
            while (remaining > 0)
            {
                if (state is null)
                {
                    state = PositionState.Start(fill, Math.Sign(signed), remaining);
                    states[key] = state;
                    remaining = 0;
                    continue;
                }

                var sameDirection = Math.Sign(state.SignedQuantity) == Math.Sign(signed);
                if (sameDirection)
                {
                    state.AddOpening(fill, remaining);
                    remaining = 0;
                    continue;
                }

                var closing = Math.Min(state.OpenQuantity, remaining);
                state.Close(fill, closing);
                remaining -= closing;
                if (state.OpenQuantity == 0)
                {
                    InsertDerivedTrade(connection, transaction, journalId, state, groupingPolicy, ++sequence);
                    state = null;
                    states.Remove(key);
                }
                if (remaining > 0)
                {
                    state = PositionState.Start(ScaleFill(fill, remaining), Math.Sign(signed), remaining);
                    states[key] = state;
                    remaining = 0;
                }
            }
        }

        foreach (var state in states.Values)
            InsertDerivedTrade(connection, transaction, journalId, state, groupingPolicy, ++sequence);

        transaction.Commit();
    }

    private static bool IsBuy(string side) => side.Equals("Buy", StringComparison.OrdinalIgnoreCase) || side.Equals("Long", StringComparison.OrdinalIgnoreCase) || side.Equals("Buy to Open", StringComparison.OrdinalIgnoreCase);

    private static bool InsertFill(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, Guid batchId, FillDraft fill)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO fills (id, journal_id, import_batch_id, source_type, source_key, event_utc, source_time_text, symbol, account, side, quantity, price, open_close, high, low, note, position_quantity, order_id, service_order_id, fees, row_number) VALUES ($id, $journal, $batch, $source, $key, $event, $sourceTime, $symbol, $account, $side, $quantity, $price, $openClose, $high, $low, $note, $position, $order, $serviceOrder, $fees, $row)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$source", fill.SourceType);
        command.Parameters.AddWithValue("$key", fill.SourceKey);
        command.Parameters.AddWithValue("$event", fill.EventUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$sourceTime", fill.SourceTimeText);
        command.Parameters.AddWithValue("$symbol", fill.Symbol);
        command.Parameters.AddWithValue("$account", fill.Account);
        command.Parameters.AddWithValue("$side", fill.Side);
        command.Parameters.AddWithValue("$quantity", fill.Quantity);
        command.Parameters.AddWithValue("$price", NumberFormat.Decimal(fill.Price));
        command.Parameters.AddWithValue("$openClose", fill.OpenClose);
        AddNullable(command, "$high", fill.High);
        AddNullable(command, "$low", fill.Low);
        command.Parameters.AddWithValue("$note", fill.Note);
        AddNullable(command, "$position", fill.PositionQuantity);
        command.Parameters.AddWithValue("$order", fill.OrderId);
        command.Parameters.AddWithValue("$serviceOrder", fill.ServiceOrderId);
        command.Parameters.AddWithValue("$fees", NumberFormat.Decimal(fill.Fees));
        command.Parameters.AddWithValue("$row", fill.RowNumber);
        return command.ExecuteNonQuery() > 0;
    }

    private static bool InsertDirectTrade(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, Guid batchId, ImportedTradeDraft draft, string groupingPolicy)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO trades (id, journal_id, import_batch_id, source_type, source_key, grouping_policy, sequence, symbol, account, direction, entry_utc, exit_utc, entry_price, exit_price, quantity, closed_quantity, gross_points, average_points, gross_pnl, fees, net_pnl, status, note, created_utc) VALUES ($id, $journal, $batch, $source, $key, $grouping, $sequence, $symbol, $account, $direction, $entry, $exit, $entryPrice, $exitPrice, $quantity, $closed, $grossPoints, $averagePoints, $grossPnl, $fees, $netPnl, $status, $note, $created)";
        var quantity = Math.Max(1, draft.Quantity);
        var averagePoints = quantity == 0 ? 0m : draft.GrossPoints / quantity;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$source", draft.SourceType);
        command.Parameters.AddWithValue("$key", draft.SourceKey);
        command.Parameters.AddWithValue("$grouping", groupingPolicy);
        command.Parameters.AddWithValue("$sequence", 0);
        command.Parameters.AddWithValue("$symbol", draft.Symbol);
        command.Parameters.AddWithValue("$account", draft.Account);
        command.Parameters.AddWithValue("$direction", draft.Direction);
        command.Parameters.AddWithValue("$entry", draft.EntryUtc.ToString("O", CultureInfo.InvariantCulture));
        AddNullable(command, "$exit", draft.ExitUtc);
        command.Parameters.AddWithValue("$entryPrice", NumberFormat.Decimal(draft.EntryPrice));
        AddNullable(command, "$exitPrice", draft.ExitPrice);
        command.Parameters.AddWithValue("$quantity", quantity);
        command.Parameters.AddWithValue("$closed", quantity);
        command.Parameters.AddWithValue("$grossPoints", NumberFormat.Decimal(draft.GrossPoints));
        command.Parameters.AddWithValue("$averagePoints", NumberFormat.Decimal(averagePoints));
        command.Parameters.AddWithValue("$grossPnl", NumberFormat.Decimal(draft.GrossPnl));
        command.Parameters.AddWithValue("$fees", NumberFormat.Decimal(draft.Fees));
        command.Parameters.AddWithValue("$netPnl", NumberFormat.Decimal(draft.NetPnl));
        command.Parameters.AddWithValue("$status", draft.ExitUtc.HasValue ? "closed" : "open");
        command.Parameters.AddWithValue("$note", draft.Note);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery() > 0;
    }

    private static void InsertBar(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, Guid batchId, BarDraft bar)
    {
        var seriesKey = $"{bar.Symbol}\u001f{bar.Interval}";
        var seriesId = string.Empty;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT id FROM bar_series WHERE series_key = $key";
            find.Parameters.AddWithValue("$key", seriesKey);
            seriesId = find.ExecuteScalar() as string ?? string.Empty;
        }
        if (string.IsNullOrEmpty(seriesId))
        {
            seriesId = Guid.NewGuid().ToString("D");
            using var insertSeries = connection.CreateCommand();
            insertSeries.Transaction = transaction;
            insertSeries.CommandText = "INSERT INTO bar_series (id, symbol, interval, series_key, created_utc) VALUES ($id, $symbol, $interval, $key, $created)";
            insertSeries.Parameters.AddWithValue("$id", seriesId);
            insertSeries.Parameters.AddWithValue("$symbol", bar.Symbol);
            insertSeries.Parameters.AddWithValue("$interval", bar.Interval);
            insertSeries.Parameters.AddWithValue("$key", seriesKey);
            insertSeries.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            insertSeries.ExecuteNonQuery();
        }
        using (var link = connection.CreateCommand())
        {
            link.Transaction = transaction;
            link.CommandText = "INSERT OR IGNORE INTO journal_bar_series (journal_id, series_id) VALUES ($journal, $series)";
            link.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            link.Parameters.AddWithValue("$series", seriesId);
            link.ExecuteNonQuery();
        }
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO bars (id, series_id, import_batch_id, event_utc, open, high, low, close, volume) VALUES ($id, $series, $batch, $event, $open, $high, $low, $close, $volume)";
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        insert.Parameters.AddWithValue("$series", seriesId);
        insert.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        insert.Parameters.AddWithValue("$event", bar.EventUtc.ToString("O", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$open", NumberFormat.Decimal(bar.Open));
        insert.Parameters.AddWithValue("$high", NumberFormat.Decimal(bar.High));
        insert.Parameters.AddWithValue("$low", NumberFormat.Decimal(bar.Low));
        insert.Parameters.AddWithValue("$close", NumberFormat.Decimal(bar.Close));
        AddNullable(insert, "$volume", bar.Volume);
        insert.ExecuteNonQuery();
    }

    private static void InsertDerivedTrade(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, PositionState state, string groupingPolicy, int sequence)
    {
        var tradeId = Guid.NewGuid().ToString("D");
        var closed = state.ClosedQuantity;
        var entryPrice = closed > 0 ? state.ClosedEntryCost / closed : state.EntryCost / Math.Max(1, state.OpenQuantity);
        var exitPrice = closed > 0 ? state.ExitCost / closed : (decimal?)null;
        var averagePoints = closed > 0 ? state.GrossPoints / closed : 0m;
        var status = state.OpenQuantity > 0 ? "open" : "closed";
        var note = string.Join(" ", state.Notes.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(3));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO trades (id, journal_id, import_batch_id, source_type, source_key, grouping_policy, sequence, symbol, account, direction, entry_utc, exit_utc, entry_price, exit_price, quantity, closed_quantity, gross_points, average_points, gross_pnl, fees, net_pnl, mae_points, mfe_points, status, note, created_utc) VALUES ($id, $journal, $batch, $source, $key, $grouping, $sequence, $symbol, $account, $direction, $entry, $exit, $entryPrice, $exitPrice, $quantity, $closed, $grossPoints, $averagePoints, $grossPnl, $fees, $netPnl, $mae, $mfe, $status, $note, $created)";
        command.Parameters.AddWithValue("$id", tradeId);
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$batch", state.ImportBatchId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$source", DerivedFillSource);
        command.Parameters.AddWithValue("$key", $"{state.EntrySourceKey}:{sequence}");
        command.Parameters.AddWithValue("$grouping", groupingPolicy);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$symbol", state.Symbol);
        command.Parameters.AddWithValue("$account", state.Account);
        command.Parameters.AddWithValue("$direction", state.Direction);
        command.Parameters.AddWithValue("$entry", state.EntryUtc.ToString("O", CultureInfo.InvariantCulture));
        AddNullable(command, "$exit", state.LastExitUtc);
        command.Parameters.AddWithValue("$entryPrice", NumberFormat.Decimal(entryPrice));
        AddNullable(command, "$exitPrice", exitPrice);
        command.Parameters.AddWithValue("$quantity", Math.Max(state.MaxOpenQuantity, closed));
        command.Parameters.AddWithValue("$closed", closed);
        command.Parameters.AddWithValue("$grossPoints", NumberFormat.Decimal(state.GrossPoints));
        command.Parameters.AddWithValue("$averagePoints", NumberFormat.Decimal(averagePoints));
        command.Parameters.AddWithValue("$grossPnl", NumberFormat.Decimal(state.GrossPnl));
        command.Parameters.AddWithValue("$fees", NumberFormat.Decimal(state.Fees));
        command.Parameters.AddWithValue("$netPnl", NumberFormat.Decimal(state.GrossPnl - state.Fees));
        AddNullable(command, "$mae", state.MaePoints);
        AddNullable(command, "$mfe", state.MfePoints);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$note", note);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();

        foreach (var allocation in state.Allocations)
        {
            using var allocationCommand = connection.CreateCommand();
            allocationCommand.Transaction = transaction;
            allocationCommand.CommandText = "INSERT OR IGNORE INTO trade_fill_allocations (trade_id, fill_id, quantity) SELECT $trade, id, $quantity FROM fills WHERE journal_id = $journal AND source_type = $fillSource AND source_key = $key";
            allocationCommand.Parameters.AddWithValue("$trade", tradeId);
            allocationCommand.Parameters.AddWithValue("$quantity", allocation.Quantity);
            allocationCommand.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            allocationCommand.Parameters.AddWithValue("$fillSource", allocation.SourceType);
            allocationCommand.Parameters.AddWithValue("$key", allocation.SourceKey);
            allocationCommand.ExecuteNonQuery();
        }
    }

    private static List<Fill> ReadFills(SqliteConnection connection, SqliteTransaction transaction, Guid journalId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, journal_id, import_batch_id, source_type, source_key, event_utc, source_time_text, symbol, account, side, quantity, price, open_close, high, low, note, position_quantity, order_id, service_order_id, fees, row_number FROM fills WHERE journal_id = $journal ORDER BY account, symbol, event_utc, row_number, id";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        var fills = new List<Fill>();
        while (reader.Read())
        {
            fills.Add(new Fill
            {
                Id = Guid.Parse(reader.GetString(0)), JournalId = Guid.Parse(reader.GetString(1)), ImportBatchId = Guid.Parse(reader.GetString(2)), SourceType = reader.GetString(3), SourceKey = reader.GetString(4), EventUtc = ParseDate(reader.GetString(5)), SourceTimeText = reader.GetString(6), Symbol = reader.GetString(7), Account = reader.GetString(8), Side = reader.GetString(9), Quantity = reader.GetInt32(10), Price = ParseDecimal(reader.GetString(11)), OpenClose = reader.GetString(12), High = NullableDecimal(reader, 13), Low = NullableDecimal(reader, 14), Note = reader.GetString(15), PositionQuantity = reader.IsDBNull(16) ? null : reader.GetInt32(16), OrderId = reader.GetString(17), ServiceOrderId = reader.GetString(18), Fees = ParseDecimal(reader.GetString(19)), RowNumber = reader.GetInt32(20)
            });
        }
        return fills;
    }

    private ImportBatch? GetImport(Guid importId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, journal_id, file_name, source_type, imported_utc, total_rows, new_rows, duplicate_rows, status, message FROM import_batches WHERE id = $id";
        command.Parameters.AddWithValue("$id", importId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadImport(reader) : null;
    }

    private static IEnumerable<string> GetFillSymbols(SqliteConnection connection, Guid journalId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT symbol FROM fills WHERE journal_id = $journal";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return reader.GetString(0);
    }

    private static Journal ReadJournal(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)), Name = reader.GetString(1), ExecutionContext = reader.GetString(2), Labels = reader.GetString(3), TimeZone = reader.GetString(4), Currency = reader.GetString(5), GroupingPolicy = reader.GetString(6), CreatedUtc = ParseDate(reader.GetString(7))
    };

    private static ImportBatch ReadImport(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)), JournalId = Guid.Parse(reader.GetString(1)), FileName = reader.GetString(2), SourceType = reader.GetString(3), ImportedUtc = ParseDate(reader.GetString(4)), TotalRows = reader.GetInt32(5), NewRows = reader.GetInt32(6), DuplicateRows = reader.GetInt32(7), Status = reader.GetString(8), Message = reader.GetString(9)
    };

    private static Trade ReadTrade(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)), JournalId = Guid.Parse(reader.GetString(1)), ImportBatchId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), SourceType = reader.GetString(3), SourceKey = reader.GetString(4), GroupingPolicy = reader.GetString(5), Sequence = reader.GetInt32(6), Symbol = reader.GetString(7), Account = reader.GetString(8), Direction = reader.GetString(9), EntryUtc = ParseDate(reader.GetString(10)), ExitUtc = reader.IsDBNull(11) ? null : ParseDate(reader.GetString(11)), EntryPrice = ParseDecimal(reader.GetString(12)), ExitPrice = reader.IsDBNull(13) ? null : ParseDecimal(reader.GetString(13)), Quantity = reader.GetInt32(14), ClosedQuantity = reader.GetInt32(15), GrossPoints = ParseDecimal(reader.GetString(16)), AveragePoints = ParseDecimal(reader.GetString(17)), GrossPnl = ParseDecimal(reader.GetString(18)), Fees = ParseDecimal(reader.GetString(19)), NetPnl = ParseDecimal(reader.GetString(20)), MaePoints = reader.IsDBNull(21) ? null : ParseDecimal(reader.GetString(21)), MfePoints = reader.IsDBNull(22) ? null : ParseDecimal(reader.GetString(22)), Status = reader.GetString(23), Note = reader.GetString(24)
    };

    private static Bar ReadBar(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)), SeriesId = Guid.Parse(reader.GetString(1)), Symbol = reader.GetString(2), Interval = reader.GetString(3), EventUtc = ParseDate(reader.GetString(4)), Open = ParseDecimal(reader.GetString(5)), High = ParseDecimal(reader.GetString(6)), Low = ParseDecimal(reader.GetString(7)), Close = ParseDecimal(reader.GetString(8)), Volume = reader.IsDBNull(9) ? null : reader.GetInt64(9)
    };

    private static Fill ScaleFill(Fill fill, int quantity)
    {
        var fee = fill.Quantity == 0 ? 0m : fill.Fees * quantity / fill.Quantity;
        return new Fill
        {
            Id = fill.Id, JournalId = fill.JournalId, ImportBatchId = fill.ImportBatchId, SourceType = fill.SourceType, SourceKey = fill.SourceKey,
            EventUtc = fill.EventUtc, SourceTimeText = fill.SourceTimeText, Symbol = fill.Symbol, Account = fill.Account, Side = fill.Side,
            Quantity = quantity, Price = fill.Price, OpenClose = fill.OpenClose, High = fill.High, Low = fill.Low, Note = fill.Note,
            PositionQuantity = fill.PositionQuantity, OrderId = fill.OrderId, ServiceOrderId = fill.ServiceOrderId, Fees = fee, RowNumber = fill.RowNumber
        };
    }

    private static void AddNullable(SqliteCommand command, string name, object? value)
    {
        var converted = value switch
        {
            null => DBNull.Value,
            DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
            decimal number => NumberFormat.Decimal(number),
            _ => value
        };
        command.Parameters.AddWithValue(name, converted);
    }
    private static decimal ParseDecimal(string value) => decimal.Parse(value, NumberStyles.Any, CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static decimal? NullableDecimal(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ParseDecimal(reader.GetString(ordinal));

    private sealed record SierraPriceRepair(Guid FillId, Guid JournalId, decimal Price, decimal? High, decimal? Low);

    private sealed class PositionState
    {
        private PositionState(Fill fill, int direction, int quantity)
        {
            Symbol = fill.Symbol; Account = fill.Account; Direction = direction > 0 ? "Long" : "Short"; SignedQuantity = direction * quantity; OpenQuantity = quantity; MaxOpenQuantity = quantity; EntryCost = fill.Price * quantity; EntryUtc = fill.EventUtc; EntrySourceKey = fill.SourceKey; ImportBatchId = fill.ImportBatchId; Fees = fill.Fees; Notes.Add(fill.Note); AddExcursion(fill, fill.Price, direction);
        }

        public string Symbol { get; }
        public string Account { get; }
        public string Direction { get; }
        public int SignedQuantity { get; private set; }
        public int OpenQuantity { get; private set; }
        public int MaxOpenQuantity { get; private set; }
        public int ClosedQuantity { get; private set; }
        public decimal EntryCost { get; private set; }
        public decimal ClosedEntryCost { get; private set; }
        public decimal ExitCost { get; private set; }
        public decimal GrossPoints { get; private set; }
        public decimal GrossPnl { get; private set; }
        public decimal Fees { get; private set; }
        public decimal? MaePoints { get; private set; }
        public decimal? MfePoints { get; private set; }
        public DateTimeOffset EntryUtc { get; }
        public DateTimeOffset? LastExitUtc { get; private set; }
        public string EntrySourceKey { get; }
        public Guid? ImportBatchId { get; }
        public List<string> Notes { get; } = new();
        public List<Allocation> Allocations { get; } = new();

        public static PositionState Start(Fill fill, int direction, int quantity) => new(fill, direction, quantity);

        public void AddOpening(Fill fill, int quantity)
        {
            var total = OpenQuantity + quantity;
            EntryCost += fill.Price * quantity;
            OpenQuantity = total;
            SignedQuantity = Math.Sign(SignedQuantity) * total;
            MaxOpenQuantity = Math.Max(MaxOpenQuantity, total);
            Fees += fill.Fees;
            Notes.Add(fill.Note);
            AddExcursion(fill, EntryCost / Math.Max(1, OpenQuantity), Math.Sign(SignedQuantity));
        }

        public void Close(Fill fill, int quantity)
        {
            var entryAverage = EntryCost / Math.Max(1, OpenQuantity);
            var direction = Math.Sign(SignedQuantity);
            var move = direction > 0 ? fill.Price - entryAverage : entryAverage - fill.Price;
            var pointValue = InstrumentCatalog.Resolve(Symbol).PointValue;
            var feePortion = fill.Quantity == 0 ? 0m : fill.Fees * quantity / fill.Quantity;
            ClosedEntryCost += entryAverage * quantity;
            ExitCost += fill.Price * quantity;
            GrossPoints += move * quantity;
            GrossPnl += move * quantity * pointValue;
            Fees += feePortion;
            ClosedQuantity += quantity;
            EntryCost -= entryAverage * quantity;
            OpenQuantity -= quantity;
            SignedQuantity = direction * OpenQuantity;
            LastExitUtc = fill.EventUtc;
            Notes.Add(fill.Note);
            Allocations.Add(new Allocation(fill.SourceType, fill.SourceKey, quantity));
            AddExcursion(fill, entryAverage, direction);
        }

        private void AddExcursion(Fill fill, decimal entryAverage, int direction)
        {
            var high = fill.High ?? fill.Price;
            var low = fill.Low ?? fill.Price;
            var favorable = direction > 0 ? high - entryAverage : entryAverage - low;
            var adverse = direction > 0 ? entryAverage - low : high - entryAverage;
            MfePoints = Math.Max(MfePoints ?? 0m, favorable);
            MaePoints = Math.Max(MaePoints ?? 0m, adverse);
        }
    }

    private readonly record struct Allocation(string SourceType, string SourceKey, int Quantity);
}

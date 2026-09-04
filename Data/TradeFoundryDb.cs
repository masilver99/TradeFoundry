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
    private const string JournalColumns = "id, name, execution_context, labels, timezone, currency, grouping_policy, starting_equity, created_utc";
    private const string TradeColumns = "id, journal_id, import_batch_id, source_type, source_key, grouping_policy, sequence, symbol, account, direction, entry_utc, exit_utc, entry_price, exit_price, quantity, closed_quantity, gross_points, average_points, gross_pnl, fees, net_pnl, mae_points, mfe_points, point_value, tick_size, initial_stop_price, initial_target_price, initial_risk_points, initial_risk_currency, r_multiple, exit_type, entry_order_price, exit_order_price, entry_chase_points, exit_chase_points, status, note";
    private const string FillColumns = "id, journal_id, import_batch_id, source_type, source_key, activity_type, order_action_source, event_utc, transaction_utc, source_time_text, symbol, account, side, quantity, price, price2, filled_quantity, open_close, order_type, order_status, parent_order_id, high, low, note, position_quantity, order_id, service_order_id, exchange_order_id, fill_execution_id, client_order_id, time_in_force, username, is_automated, account_balance, fees, row_number";
    private const string OrderEventColumns = "id, journal_id, import_batch_id, source_type, source_key, order_action_source, event_utc, transaction_utc, source_time_text, symbol, account, internal_order_id, service_order_id, parent_order_id, exchange_order_id, fill_execution_id, order_type, order_status, side, open_close, price, price2, quantity, filled_quantity, fill_price, position_quantity, note, client_order_id, time_in_force, username, is_automated, fees, row_number";
    private const string AccountBalanceColumns = "id, journal_id, import_batch_id, source_type, source_key, event_utc, transaction_utc, source_time_text, account, balance, note, row_number";
    private readonly string _connectionString;
    private readonly string _databasePath;

    public string DatabasePath => _databasePath;
    public long DatabaseSizeBytes => File.Exists(_databasePath) ? new FileInfo(_databasePath).Length : 0L;

    public TradeFoundryDb(IOptions<StorageOptions> options)
    {
        var storage = options.Value;
        var directory = Path.GetFullPath(string.IsNullOrWhiteSpace(storage.DataDirectory) ? "data" : storage.DataDirectory);
        Directory.CreateDirectory(directory);
        var fileName = string.IsNullOrWhiteSpace(storage.DatabaseFileName) ? "journal.db" : storage.DatabaseFileName;
        _databasePath = Path.Combine(directory, fileName);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
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
            "CREATE TABLE IF NOT EXISTS journals (id TEXT PRIMARY KEY, owner_user_id TEXT NOT NULL REFERENCES app_users(id), name TEXT NOT NULL, execution_context TEXT NOT NULL, labels TEXT NOT NULL DEFAULT '', timezone TEXT NOT NULL DEFAULT 'UTC', currency TEXT NOT NULL DEFAULT 'USD', grouping_policy TEXT NOT NULL DEFAULT 'flat_to_flat', starting_equity TEXT NULL, created_utc TEXT NOT NULL, archived INTEGER NOT NULL DEFAULT 0)",
            "CREATE INDEX IF NOT EXISTS ix_journals_owner ON journals(owner_user_id, archived, created_utc)",
            "CREATE TABLE IF NOT EXISTS import_batches (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id), file_name TEXT NOT NULL, source_type TEXT NOT NULL, imported_utc TEXT NOT NULL, total_rows INTEGER NOT NULL, new_rows INTEGER NOT NULL, duplicate_rows INTEGER NOT NULL, status TEXT NOT NULL, message TEXT NOT NULL DEFAULT '')",
            "CREATE INDEX IF NOT EXISTS ix_import_batches_journal ON import_batches(journal_id, imported_utc DESC)",
            "CREATE TABLE IF NOT EXISTS raw_records (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id), import_batch_id TEXT NOT NULL REFERENCES import_batches(id), source_type TEXT NOT NULL, source_key TEXT NOT NULL, row_number INTEGER NOT NULL, payload_json TEXT NOT NULL, status TEXT NOT NULL, error TEXT NOT NULL DEFAULT '', UNIQUE(journal_id, source_type, source_key))",
            "CREATE TABLE IF NOT EXISTS fills (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id), import_batch_id TEXT NOT NULL REFERENCES import_batches(id), source_type TEXT NOT NULL, source_key TEXT NOT NULL, activity_type TEXT NOT NULL DEFAULT 'Fills', order_action_source TEXT NOT NULL DEFAULT '', event_utc TEXT NOT NULL, transaction_utc TEXT NULL, source_time_text TEXT NOT NULL DEFAULT '', symbol TEXT NOT NULL, account TEXT NOT NULL DEFAULT '', side TEXT NOT NULL, quantity INTEGER NOT NULL, price TEXT NOT NULL, price2 TEXT NULL, filled_quantity INTEGER NULL, open_close TEXT NOT NULL DEFAULT '', order_type TEXT NOT NULL DEFAULT '', order_status TEXT NOT NULL DEFAULT '', parent_order_id TEXT NOT NULL DEFAULT '', high TEXT NULL, low TEXT NULL, note TEXT NOT NULL DEFAULT '', position_quantity INTEGER NULL, order_id TEXT NOT NULL DEFAULT '', service_order_id TEXT NOT NULL DEFAULT '', exchange_order_id TEXT NOT NULL DEFAULT '', fill_execution_id TEXT NOT NULL DEFAULT '', client_order_id TEXT NOT NULL DEFAULT '', time_in_force TEXT NOT NULL DEFAULT '', username TEXT NOT NULL DEFAULT '', is_automated INTEGER NULL, account_balance TEXT NULL, fees TEXT NOT NULL DEFAULT '0', row_number INTEGER NOT NULL, UNIQUE(journal_id, source_type, source_key))",
            "CREATE INDEX IF NOT EXISTS ix_fills_journal_time ON fills(journal_id, symbol, account, event_utc, row_number)",
            "CREATE TABLE IF NOT EXISTS trades (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id), import_batch_id TEXT NULL REFERENCES import_batches(id), source_type TEXT NOT NULL, source_key TEXT NOT NULL, grouping_policy TEXT NOT NULL, sequence INTEGER NOT NULL, symbol TEXT NOT NULL, account TEXT NOT NULL DEFAULT '', direction TEXT NOT NULL, entry_utc TEXT NOT NULL, exit_utc TEXT NULL, entry_price TEXT NOT NULL, exit_price TEXT NULL, quantity INTEGER NOT NULL, closed_quantity INTEGER NOT NULL, gross_points TEXT NOT NULL DEFAULT '0', average_points TEXT NOT NULL DEFAULT '0', gross_pnl TEXT NOT NULL DEFAULT '0', fees TEXT NOT NULL DEFAULT '0', net_pnl TEXT NOT NULL DEFAULT '0', mae_points TEXT NULL, mfe_points TEXT NULL, point_value TEXT NOT NULL DEFAULT '1', tick_size TEXT NOT NULL DEFAULT '0', initial_stop_price TEXT NULL, initial_target_price TEXT NULL, initial_risk_points TEXT NULL, initial_risk_currency TEXT NULL, r_multiple TEXT NULL, exit_type TEXT NOT NULL DEFAULT '', entry_order_price TEXT NULL, exit_order_price TEXT NULL, entry_chase_points TEXT NULL, exit_chase_points TEXT NULL, status TEXT NOT NULL, note TEXT NOT NULL DEFAULT '', created_utc TEXT NOT NULL, UNIQUE(journal_id, source_type, source_key))",
            "CREATE INDEX IF NOT EXISTS ix_trades_journal_time ON trades(journal_id, entry_utc)",
            "CREATE INDEX IF NOT EXISTS ix_trades_journal_status_time ON trades(journal_id, status, entry_utc)",
            "CREATE INDEX IF NOT EXISTS ix_trades_journal_symbol_time ON trades(journal_id, symbol, entry_utc)",
            "CREATE TABLE IF NOT EXISTS trade_fill_allocations (trade_id TEXT NOT NULL REFERENCES trades(id) ON DELETE CASCADE, fill_id TEXT NOT NULL REFERENCES fills(id) ON DELETE CASCADE, quantity INTEGER NOT NULL, PRIMARY KEY(trade_id, fill_id))",
            "CREATE TABLE IF NOT EXISTS order_events (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id), import_batch_id TEXT NOT NULL REFERENCES import_batches(id), source_type TEXT NOT NULL, source_key TEXT NOT NULL, order_action_source TEXT NOT NULL DEFAULT '', event_utc TEXT NOT NULL, transaction_utc TEXT NULL, source_time_text TEXT NOT NULL DEFAULT '', symbol TEXT NOT NULL DEFAULT '', account TEXT NOT NULL DEFAULT '', internal_order_id TEXT NOT NULL DEFAULT '', service_order_id TEXT NOT NULL DEFAULT '', parent_order_id TEXT NOT NULL DEFAULT '', exchange_order_id TEXT NOT NULL DEFAULT '', fill_execution_id TEXT NOT NULL DEFAULT '', order_type TEXT NOT NULL DEFAULT '', order_status TEXT NOT NULL DEFAULT '', side TEXT NOT NULL DEFAULT '', open_close TEXT NOT NULL DEFAULT '', price TEXT NULL, price2 TEXT NULL, quantity INTEGER NULL, filled_quantity INTEGER NULL, fill_price TEXT NULL, position_quantity INTEGER NULL, note TEXT NOT NULL DEFAULT '', client_order_id TEXT NOT NULL DEFAULT '', time_in_force TEXT NOT NULL DEFAULT '', username TEXT NOT NULL DEFAULT '', is_automated INTEGER NULL, fees TEXT NOT NULL DEFAULT '0', row_number INTEGER NOT NULL, UNIQUE(journal_id, source_type, source_key))",
            "CREATE INDEX IF NOT EXISTS ix_order_events_journal_order_time ON order_events(journal_id, account, symbol, internal_order_id, event_utc, row_number)",
            "CREATE INDEX IF NOT EXISTS ix_order_events_journal_parent ON order_events(journal_id, parent_order_id, event_utc)",
            "CREATE TABLE IF NOT EXISTS account_balance_events (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id), import_batch_id TEXT NOT NULL REFERENCES import_batches(id), source_type TEXT NOT NULL, source_key TEXT NOT NULL, event_utc TEXT NOT NULL, transaction_utc TEXT NULL, source_time_text TEXT NOT NULL DEFAULT '', account TEXT NOT NULL DEFAULT '', balance TEXT NULL, note TEXT NOT NULL DEFAULT '', row_number INTEGER NOT NULL, UNIQUE(journal_id, source_type, source_key))",
            "CREATE INDEX IF NOT EXISTS ix_account_balance_events_journal_time ON account_balance_events(journal_id, account, event_utc, row_number)",
            "CREATE TABLE IF NOT EXISTS benchmark_series (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, symbol TEXT NOT NULL, interval TEXT NOT NULL DEFAULT '1d', series_key TEXT NOT NULL UNIQUE, created_utc TEXT NOT NULL)",
            "CREATE TABLE IF NOT EXISTS benchmark_points (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, series_id TEXT NOT NULL REFERENCES benchmark_series(id) ON DELETE CASCADE, import_batch_id TEXT NULL REFERENCES import_batches(id) ON DELETE SET NULL, source_type TEXT NOT NULL, source_key TEXT NOT NULL, event_utc TEXT NOT NULL, value TEXT NOT NULL, source_time_text TEXT NOT NULL DEFAULT '', row_number INTEGER NOT NULL, UNIQUE(journal_id, source_type, source_key), UNIQUE(series_id, event_utc))",
            "CREATE INDEX IF NOT EXISTS ix_benchmark_points_series_time ON benchmark_points(series_id, event_utc)",
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

        // The application started without migrations, so keep schema upgrades
        // additive and idempotent for existing local SQLite files.
        EnsureColumn(connection, "journals", "starting_equity", "TEXT NULL");
        EnsureColumn(connection, "fills", "activity_type", "TEXT NOT NULL DEFAULT 'Fills'");
        EnsureColumn(connection, "fills", "order_action_source", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "fills", "transaction_utc", "TEXT NULL");
        EnsureColumn(connection, "fills", "price2", "TEXT NULL");
        EnsureColumn(connection, "fills", "filled_quantity", "INTEGER NULL");
        EnsureColumn(connection, "fills", "order_type", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "fills", "order_status", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "fills", "parent_order_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "fills", "exchange_order_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "fills", "fill_execution_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "fills", "client_order_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "fills", "time_in_force", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "fills", "username", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "fills", "is_automated", "INTEGER NULL");
        EnsureColumn(connection, "fills", "account_balance", "TEXT NULL");
        EnsureColumn(connection, "order_events", "order_action_source", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "order_events", "service_order_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "trades", "point_value", "TEXT NOT NULL DEFAULT '1'");
        EnsureColumn(connection, "trades", "tick_size", "TEXT NOT NULL DEFAULT '0'");
        EnsureColumn(connection, "trades", "initial_stop_price", "TEXT NULL");
        EnsureColumn(connection, "trades", "initial_target_price", "TEXT NULL");
        EnsureColumn(connection, "trades", "initial_risk_points", "TEXT NULL");
        EnsureColumn(connection, "trades", "initial_risk_currency", "TEXT NULL");
        EnsureColumn(connection, "trades", "r_multiple", "TEXT NULL");
        EnsureColumn(connection, "trades", "exit_type", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "trades", "entry_order_price", "TEXT NULL");
        EnsureColumn(connection, "trades", "exit_order_price", "TEXT NULL");
        EnsureColumn(connection, "trades", "entry_chase_points", "TEXT NULL");
        EnsureColumn(connection, "trades", "exit_chase_points", "TEXT NULL");

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
        command.CommandText = $"SELECT {JournalColumns} FROM journals WHERE owner_user_id = $owner {(includeArchived ? string.Empty : "AND archived = 0")} ORDER BY created_utc";
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
        command.CommandText = $"SELECT {JournalColumns} FROM journals WHERE id = $id AND owner_user_id = $owner AND archived = 0";
        command.Parameters.AddWithValue("$id", journalId.ToString("D"));
        command.Parameters.AddWithValue("$owner", TradeFoundryConstants.OwnerUserId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJournal(reader) : null;
    }

    public Journal CreateJournal(string name, string executionContext, string labels, string timeZone, string currency, string groupingPolicy, decimal? startingEquity = null)
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
            StartingEquity = startingEquity,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO journals (id, owner_user_id, name, execution_context, labels, timezone, currency, grouping_policy, starting_equity, created_utc) VALUES ($id, $owner, $name, $context, $labels, $timezone, $currency, $grouping, $startingEquity, $created)";
        command.Parameters.AddWithValue("$id", journal.Id.ToString("D"));
        command.Parameters.AddWithValue("$owner", TradeFoundryConstants.OwnerUserId);
        command.Parameters.AddWithValue("$name", journal.Name);
        command.Parameters.AddWithValue("$context", journal.ExecutionContext);
        command.Parameters.AddWithValue("$labels", journal.Labels);
        command.Parameters.AddWithValue("$timezone", journal.TimeZone);
        command.Parameters.AddWithValue("$currency", journal.Currency);
        command.Parameters.AddWithValue("$grouping", journal.GroupingPolicy);
        AddNullable(command, "$startingEquity", journal.StartingEquity);
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

    public bool UpdateJournal(Guid journalId, string name, string executionContext, string labels, string timeZone, string currency, string groupingPolicy, decimal? startingEquity = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE journals SET name = $name, execution_context = $context, labels = $labels, timezone = $timezone, currency = $currency, grouping_policy = $grouping, starting_equity = $startingEquity WHERE id = $id AND owner_user_id = $owner AND archived = 0";
        command.Parameters.AddWithValue("$id", journalId.ToString("D"));
        command.Parameters.AddWithValue("$owner", TradeFoundryConstants.OwnerUserId);
        command.Parameters.AddWithValue("$name", string.IsNullOrWhiteSpace(name) ? "My futures journal" : name.Trim());
        command.Parameters.AddWithValue("$context", string.IsNullOrWhiteSpace(executionContext) ? "live" : executionContext.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("$labels", labels?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$timezone", string.IsNullOrWhiteSpace(timeZone) ? "UTC" : timeZone.Trim());
        command.Parameters.AddWithValue("$currency", string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$grouping", string.IsNullOrWhiteSpace(groupingPolicy) ? "flat_to_flat" : groupingPolicy.Trim());
        AddNullable(command, "$startingEquity", startingEquity);
        return command.ExecuteNonQuery() == 1;
    }

    public JournalOverview GetOverview(Guid journalId)
    {
        var journal = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        var startingEquity = journal.StartingEquity ?? ReadFirstAccountBalance(connection, journalId);

        decimal grossPnl;
        decimal netPnl;
        decimal points;
        decimal fees;
        decimal winningPnl;
        decimal losingPnl;
        int closedTradeCount;
        int openTradeCount;
        int winningTrades;
        int losingTrades;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COALESCE(SUM(CAST(gross_pnl AS REAL)), 0), COALESCE(SUM(CAST(net_pnl AS REAL)), 0), COALESCE(SUM(CAST(gross_points AS REAL)), 0), COALESCE(SUM(CAST(fees AS REAL)), 0), COALESCE(SUM(CASE WHEN status = 'closed' AND CAST(net_pnl AS REAL) > 0 THEN CAST(net_pnl AS REAL) ELSE 0 END), 0), COALESCE(SUM(CASE WHEN status = 'closed' AND CAST(net_pnl AS REAL) < 0 THEN CAST(net_pnl AS REAL) ELSE 0 END), 0), SUM(CASE WHEN status = 'closed' THEN 1 ELSE 0 END), SUM(CASE WHEN status <> 'closed' THEN 1 ELSE 0 END), SUM(CASE WHEN status = 'closed' AND CAST(net_pnl AS REAL) > 0 THEN 1 ELSE 0 END), SUM(CASE WHEN status = 'closed' AND CAST(net_pnl AS REAL) < 0 THEN 1 ELSE 0 END) FROM trades WHERE journal_id = $journal";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("The journal metrics could not be read.");
            grossPnl = AggregateDecimal(reader, 0);
            netPnl = AggregateDecimal(reader, 1);
            points = AggregateDecimal(reader, 2);
            fees = AggregateDecimal(reader, 3);
            winningPnl = AggregateDecimal(reader, 4);
            losingPnl = Math.Abs(AggregateDecimal(reader, 5));
            closedTradeCount = AggregateInt(reader, 6);
            openTradeCount = AggregateInt(reader, 7);
            winningTrades = AggregateInt(reader, 8);
            losingTrades = AggregateInt(reader, 9);
        }

        var recentTrades = new List<Trade>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {TradeColumns} FROM trades WHERE journal_id = $journal ORDER BY COALESCE(exit_utc, entry_utc) DESC, sequence DESC LIMIT 8";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read()) recentTrades.Add(ReadTrade(reader));
        }

        var recentImports = new List<ImportBatch>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, journal_id, file_name, source_type, imported_utc, total_rows, new_rows, duplicate_rows, status, message FROM import_batches WHERE journal_id = $journal ORDER BY imported_utc DESC LIMIT 8";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read()) recentImports.Add(ReadImport(reader));
        }

        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT symbol FROM trades WHERE journal_id = $journal";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read()) symbols.Add(reader.GetString(0));
        }
        foreach (var symbol in GetFillSymbols(connection, journalId)) symbols.Add(symbol);

        var dailyPnl = new List<DailyPnl>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT substr(exit_utc, 1, 10), SUM(CAST(net_pnl AS REAL)), COUNT(*) FROM trades WHERE journal_id = $journal AND exit_utc IS NOT NULL GROUP BY substr(exit_utc, 1, 10) ORDER BY substr(exit_utc, 1, 10) DESC LIMIT 180";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read()) dailyPnl.Add(new DailyPnl { Date = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture), NetPnl = AggregateDecimal(reader, 1), TradeCount = AggregateInt(reader, 2) });
        }
        dailyPnl.Reverse();

        var equity = new List<EquityPoint>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "WITH running AS (SELECT id, exit_utc, sequence, SUM(CAST(net_pnl AS REAL)) OVER (ORDER BY exit_utc, sequence, id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS cumulative_pnl FROM trades WHERE journal_id = $journal AND exit_utc IS NOT NULL) SELECT exit_utc, cumulative_pnl FROM running ORDER BY exit_utc DESC, sequence DESC, id DESC LIMIT 180";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read()) equity.Add(new EquityPoint { ExitUtc = ParseDate(reader.GetString(0)), CumulativePnl = AggregateDecimal(reader, 1) });
        }
        equity.Reverse();

        var profitFactor = losingPnl == 0m ? (winningPnl > 0m ? decimal.MaxValue : 0m) : winningPnl / losingPnl;
        return new JournalOverview
        {
            Journal = journal,
            RecentTrades = recentTrades,
            RecentImports = recentImports,
            Symbols = symbols.OrderBy(x => x).ToArray(),
            DailyPnl = dailyPnl,
            Equity = equity,
            NetPnl = netPnl,
            GrossPnl = grossPnl,
            Points = points,
            Fees = fees,
            StartingEquity = startingEquity,
            ClosedTradeCount = closedTradeCount,
            OpenTradeCount = openTradeCount,
            WinningTrades = winningTrades,
            LosingTrades = losingTrades,
            ProfitFactor = profitFactor
        };
    }

    public PagedResult<Trade> GetTrades(TradeQuery query)
    {
        _ = GetJournal(query.JournalId) ?? throw new InvalidOperationException("Journal was not found.");
        var pageSize = Math.Clamp(query.PageSize, 10, 100);
        var page = Math.Max(query.Page, 1);
        var filters = new List<string>();
        using var connection = OpenConnection();
        using var count = connection.CreateCommand();
        filters.Add("journal_id = $journal");
        count.Parameters.AddWithValue("$journal", query.JournalId.ToString("D"));
        AddTradeFilter(filters, count, query);
        count.CommandText = $"SELECT COUNT(*) FROM trades WHERE {string.Join(" AND ", filters)}";
        var totalCount = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);

        var sort = query.Sort switch
        {
            "entry_asc" => "entry_utc ASC, sequence ASC, id ASC",
            "pnl_desc" => "CAST(net_pnl AS REAL) DESC, entry_utc DESC, sequence DESC, id DESC",
            "pnl_asc" => "CAST(net_pnl AS REAL) ASC, entry_utc DESC, sequence DESC, id DESC",
            _ => "entry_utc DESC, sequence DESC, id DESC"
        };
        var trades = new List<Trade>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {TradeColumns} FROM trades WHERE {string.Join(" AND ", filters)} ORDER BY {sort} LIMIT $limit OFFSET $offset";
            command.Parameters.AddWithValue("$journal", query.JournalId.ToString("D"));
            AddTradeFilter(new List<string>(), command, query);
            command.Parameters.AddWithValue("$limit", pageSize);
            command.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
            using var reader = command.ExecuteReader();
            while (reader.Read()) trades.Add(ReadTrade(reader));
        }
        return new PagedResult<Trade> { Items = trades, Page = page, PageSize = pageSize, TotalCount = totalCount };
    }

    public IReadOnlyList<Trade> GetAllTrades(Guid journalId)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TradeColumns} FROM trades WHERE journal_id = $journal ORDER BY COALESCE(exit_utc, entry_utc), sequence, id";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        var trades = new List<Trade>();
        while (reader.Read()) trades.Add(ReadTrade(reader));
        return trades;
    }

    public IReadOnlyList<OrderEvent> GetOrderEvents(Guid journalId)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {OrderEventColumns} FROM order_events WHERE journal_id = $journal ORDER BY account, symbol, event_utc, row_number, id";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        var events = new List<OrderEvent>();
        while (reader.Read()) events.Add(ReadOrderEvent(reader));
        return events;
    }

    public IReadOnlyList<AccountBalanceEvent> GetAccountBalanceEvents(Guid journalId)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {AccountBalanceColumns} FROM account_balance_events WHERE journal_id = $journal ORDER BY event_utc, row_number, id";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        var events = new List<AccountBalanceEvent>();
        while (reader.Read()) events.Add(ReadAccountBalanceEvent(reader));
        return events;
    }

    public IReadOnlyList<BenchmarkPoint> GetBenchmarkPoints(Guid journalId, string? symbol = null)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT p.id, p.journal_id, p.import_batch_id, p.series_id, p.source_type, p.source_key, s.symbol, p.event_utc, p.value, p.source_time_text, p.row_number FROM benchmark_points p JOIN benchmark_series s ON s.id = p.series_id WHERE p.journal_id = $journal" + (string.IsNullOrWhiteSpace(symbol) ? string.Empty : " AND s.symbol = $symbol") + " ORDER BY s.symbol, p.event_utc, p.row_number, p.id";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        if (!string.IsNullOrWhiteSpace(symbol)) command.Parameters.AddWithValue("$symbol", symbol.Trim());
        using var reader = command.ExecuteReader();
        var points = new List<BenchmarkPoint>();
        while (reader.Read()) points.Add(ReadBenchmarkPoint(reader));
        return points;
    }

    public PagedResult<ImportBatch> GetImports(ImportQuery query)
    {
        _ = GetJournal(query.JournalId) ?? throw new InvalidOperationException("Journal was not found.");
        var pageSize = Math.Clamp(query.PageSize, 10, 100);
        var page = Math.Max(query.Page, 1);
        var filters = new List<string> { "journal_id = $journal" };
        using var connection = OpenConnection();
        using var count = connection.CreateCommand();
        count.Parameters.AddWithValue("$journal", query.JournalId.ToString("D"));
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            filters.Add("(file_name LIKE $search OR source_type LIKE $search OR status LIKE $search)");
            count.Parameters.AddWithValue("$search", $"%{query.Search.Trim()}%");
        }
        count.CommandText = $"SELECT COUNT(*) FROM import_batches WHERE {string.Join(" AND ", filters)}";
        var totalCount = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        var imports = new List<ImportBatch>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT id, journal_id, file_name, source_type, imported_utc, total_rows, new_rows, duplicate_rows, status, message FROM import_batches WHERE {string.Join(" AND ", filters)} ORDER BY imported_utc DESC LIMIT $limit OFFSET $offset";
            command.Parameters.AddWithValue("$journal", query.JournalId.ToString("D"));
            if (!string.IsNullOrWhiteSpace(query.Search)) command.Parameters.AddWithValue("$search", $"%{query.Search.Trim()}%");
            command.Parameters.AddWithValue("$limit", pageSize);
            command.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
            using var reader = command.ExecuteReader();
            while (reader.Read()) imports.Add(ReadImport(reader));
        }
        return new PagedResult<ImportBatch> { Items = imports, Page = page, PageSize = pageSize, TotalCount = totalCount };
    }

    public bool HasImportBatch(Guid journalId, Guid batchId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM import_batches WHERE id = $batch AND journal_id = $journal)";
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    public Trade? GetTrade(Guid tradeId) => GetTradeInternal(tradeId, null);

    public Trade? GetTrade(Guid journalId, Guid tradeId) => GetTradeInternal(tradeId, journalId);

    private Trade? GetTradeInternal(Guid tradeId, Guid? journalId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TradeColumns} FROM trades WHERE id = $id" + (journalId.HasValue ? " AND journal_id = $journal" : string.Empty);
        command.Parameters.AddWithValue("$id", tradeId.ToString("D"));
        if (journalId.HasValue) command.Parameters.AddWithValue("$journal", journalId.Value.ToString("D"));
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
        var insertedOrderEvents = 0;
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
                if (record.OrderEvent is not null && InsertOrderEvent(connection, transaction, journalId, batchId, record.OrderEvent)) insertedOrderEvents++;
                if (record.AccountBalance is not null) InsertAccountBalanceEvent(connection, transaction, journalId, batchId, record.AccountBalance);
                if (record.Benchmark is not null) InsertBenchmarkPoint(connection, transaction, journalId, batchId, record.Benchmark);
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

        if (insertedFills > 0 || insertedOrderEvents > 0)
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
                "DELETE FROM order_events WHERE import_batch_id = $batch",
                "DELETE FROM account_balance_events WHERE import_batch_id = $batch",
                "DELETE FROM benchmark_points WHERE import_batch_id = $batch",
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
        var orderLifecycles = BuildOrderLifecycles(ReadOrderEvents(connection, transaction, journalId));
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
                    InsertDerivedTrade(connection, transaction, journalId, state, groupingPolicy, ++sequence, orderLifecycles);
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
            InsertDerivedTrade(connection, transaction, journalId, state, groupingPolicy, ++sequence, orderLifecycles);

        transaction.Commit();
    }

    private static void AddTradeFilter(List<string> filters, SqliteCommand command, TradeQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            filters.Add("(symbol LIKE $search OR account LIKE $search OR direction LIKE $search OR status LIKE $search)");
            command.Parameters.AddWithValue("$search", $"%{query.Search.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(query.Symbol))
        {
            filters.Add("symbol = $symbol");
            command.Parameters.AddWithValue("$symbol", query.Symbol.Trim());
        }
        if (!string.IsNullOrWhiteSpace(query.Direction))
        {
            filters.Add("direction = $direction");
            command.Parameters.AddWithValue("$direction", query.Direction.Trim());
        }
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            filters.Add("status = $status");
            command.Parameters.AddWithValue("$status", query.Status.Trim());
        }
    }

    private static bool IsBuy(string side) => side.Equals("Buy", StringComparison.OrdinalIgnoreCase) || side.Equals("Long", StringComparison.OrdinalIgnoreCase) || side.Equals("Buy to Open", StringComparison.OrdinalIgnoreCase);

    private static bool InsertFill(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, Guid batchId, FillDraft fill)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO fills (id, journal_id, import_batch_id, source_type, source_key, activity_type, order_action_source, event_utc, transaction_utc, source_time_text, symbol, account, side, quantity, price, price2, filled_quantity, open_close, order_type, order_status, parent_order_id, high, low, note, position_quantity, order_id, service_order_id, exchange_order_id, fill_execution_id, client_order_id, time_in_force, username, is_automated, account_balance, fees, row_number) VALUES ($id, $journal, $batch, $source, $key, $activity, $orderActionSource, $event, $transaction, $sourceTime, $symbol, $account, $side, $quantity, $price, $price2, $filledQuantity, $openClose, $orderType, $orderStatus, $parent, $high, $low, $note, $position, $order, $serviceOrder, $exchangeOrder, $fillExecution, $clientOrder, $timeInForce, $username, $automated, $accountBalance, $fees, $row)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$source", fill.SourceType);
        command.Parameters.AddWithValue("$key", fill.SourceKey);
        command.Parameters.AddWithValue("$activity", fill.ActivityType);
        command.Parameters.AddWithValue("$orderActionSource", fill.OrderActionSource);
        command.Parameters.AddWithValue("$event", fill.EventUtc.ToString("O", CultureInfo.InvariantCulture));
        AddNullable(command, "$transaction", fill.TransactionUtc);
        command.Parameters.AddWithValue("$sourceTime", fill.SourceTimeText);
        command.Parameters.AddWithValue("$symbol", fill.Symbol);
        command.Parameters.AddWithValue("$account", fill.Account);
        command.Parameters.AddWithValue("$side", fill.Side);
        command.Parameters.AddWithValue("$quantity", fill.Quantity);
        command.Parameters.AddWithValue("$price", NumberFormat.Decimal(fill.Price));
        AddNullable(command, "$price2", fill.Price2);
        AddNullable(command, "$filledQuantity", fill.FilledQuantity);
        command.Parameters.AddWithValue("$openClose", fill.OpenClose);
        command.Parameters.AddWithValue("$orderType", fill.OrderType);
        command.Parameters.AddWithValue("$orderStatus", fill.OrderStatus);
        command.Parameters.AddWithValue("$parent", fill.ParentOrderId);
        AddNullable(command, "$high", fill.High);
        AddNullable(command, "$low", fill.Low);
        command.Parameters.AddWithValue("$note", fill.Note);
        AddNullable(command, "$position", fill.PositionQuantity);
        command.Parameters.AddWithValue("$order", fill.OrderId);
        command.Parameters.AddWithValue("$serviceOrder", fill.ServiceOrderId);
        command.Parameters.AddWithValue("$exchangeOrder", fill.ExchangeOrderId);
        command.Parameters.AddWithValue("$fillExecution", fill.FillExecutionId);
        command.Parameters.AddWithValue("$clientOrder", fill.ClientOrderId);
        command.Parameters.AddWithValue("$timeInForce", fill.TimeInForce);
        command.Parameters.AddWithValue("$username", fill.Username);
        AddNullable(command, "$automated", fill.IsAutomated.HasValue ? (fill.IsAutomated.Value ? 1 : 0) : null);
        AddNullable(command, "$accountBalance", fill.AccountBalance);
        command.Parameters.AddWithValue("$fees", NumberFormat.Decimal(fill.Fees));
        command.Parameters.AddWithValue("$row", fill.RowNumber);
        return command.ExecuteNonQuery() > 0;
    }

    private static bool InsertOrderEvent(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, Guid batchId, OrderEventDraft orderEvent)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO order_events (id, journal_id, import_batch_id, source_type, source_key, order_action_source, event_utc, transaction_utc, source_time_text, symbol, account, internal_order_id, service_order_id, parent_order_id, exchange_order_id, fill_execution_id, order_type, order_status, side, open_close, price, price2, quantity, filled_quantity, fill_price, position_quantity, note, client_order_id, time_in_force, username, is_automated, fees, row_number) VALUES ($id, $journal, $batch, $source, $key, $orderActionSource, $event, $transaction, $sourceTime, $symbol, $account, $internal, $serviceOrder, $parent, $exchange, $fillExecution, $orderType, $orderStatus, $side, $openClose, $price, $price2, $quantity, $filledQuantity, $fillPrice, $position, $note, $clientOrder, $timeInForce, $username, $automated, $fees, $row)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$source", orderEvent.SourceType);
        command.Parameters.AddWithValue("$key", orderEvent.SourceKey);
        command.Parameters.AddWithValue("$orderActionSource", orderEvent.OrderActionSource);
        command.Parameters.AddWithValue("$event", orderEvent.EventUtc.ToString("O", CultureInfo.InvariantCulture));
        AddNullable(command, "$transaction", orderEvent.TransactionUtc);
        command.Parameters.AddWithValue("$sourceTime", orderEvent.SourceTimeText);
        command.Parameters.AddWithValue("$symbol", orderEvent.Symbol);
        command.Parameters.AddWithValue("$account", orderEvent.Account);
        command.Parameters.AddWithValue("$internal", orderEvent.InternalOrderId);
        command.Parameters.AddWithValue("$serviceOrder", orderEvent.ServiceOrderId);
        command.Parameters.AddWithValue("$parent", orderEvent.ParentOrderId);
        command.Parameters.AddWithValue("$exchange", orderEvent.ExchangeOrderId);
        command.Parameters.AddWithValue("$fillExecution", orderEvent.FillExecutionId);
        command.Parameters.AddWithValue("$orderType", orderEvent.OrderType);
        command.Parameters.AddWithValue("$orderStatus", orderEvent.OrderStatus);
        command.Parameters.AddWithValue("$side", orderEvent.Side);
        command.Parameters.AddWithValue("$openClose", orderEvent.OpenClose);
        AddNullable(command, "$price", orderEvent.Price);
        AddNullable(command, "$price2", orderEvent.Price2);
        AddNullable(command, "$quantity", orderEvent.Quantity);
        AddNullable(command, "$filledQuantity", orderEvent.FilledQuantity);
        AddNullable(command, "$fillPrice", orderEvent.FillPrice);
        AddNullable(command, "$position", orderEvent.PositionQuantity);
        command.Parameters.AddWithValue("$note", orderEvent.Note);
        command.Parameters.AddWithValue("$clientOrder", orderEvent.ClientOrderId);
        command.Parameters.AddWithValue("$timeInForce", orderEvent.TimeInForce);
        command.Parameters.AddWithValue("$username", orderEvent.Username);
        AddNullable(command, "$automated", orderEvent.IsAutomated.HasValue ? (orderEvent.IsAutomated.Value ? 1 : 0) : null);
        command.Parameters.AddWithValue("$fees", NumberFormat.Decimal(orderEvent.Fees));
        command.Parameters.AddWithValue("$row", orderEvent.RowNumber);
        return command.ExecuteNonQuery() > 0;
    }

    private static void InsertAccountBalanceEvent(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, Guid batchId, AccountBalanceDraft balance)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO account_balance_events (id, journal_id, import_batch_id, source_type, source_key, event_utc, transaction_utc, source_time_text, account, balance, note, row_number) VALUES ($id, $journal, $batch, $source, $key, $event, $transaction, $sourceTime, $account, $balance, $note, $row)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$source", balance.SourceType);
        command.Parameters.AddWithValue("$key", balance.SourceKey);
        command.Parameters.AddWithValue("$event", balance.EventUtc.ToString("O", CultureInfo.InvariantCulture));
        AddNullable(command, "$transaction", balance.TransactionUtc);
        command.Parameters.AddWithValue("$sourceTime", balance.SourceTimeText);
        command.Parameters.AddWithValue("$account", balance.Account);
        AddNullable(command, "$balance", balance.Balance);
        command.Parameters.AddWithValue("$note", balance.Note);
        command.Parameters.AddWithValue("$row", balance.RowNumber);
        command.ExecuteNonQuery();
    }

    private static void InsertBenchmarkPoint(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, Guid batchId, BenchmarkPointDraft benchmark)
    {
        var seriesKey = $"{journalId:D}\u001f{benchmark.Symbol}\u001f1d";
        var seriesId = string.Empty;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT id FROM benchmark_series WHERE series_key = $key";
            find.Parameters.AddWithValue("$key", seriesKey);
            seriesId = find.ExecuteScalar() as string ?? string.Empty;
        }
        if (string.IsNullOrEmpty(seriesId))
        {
            seriesId = Guid.NewGuid().ToString("D");
            using var insertSeries = connection.CreateCommand();
            insertSeries.Transaction = transaction;
            insertSeries.CommandText = "INSERT INTO benchmark_series (id, journal_id, symbol, interval, series_key, created_utc) VALUES ($id, $journal, $symbol, '1d', $key, $created)";
            insertSeries.Parameters.AddWithValue("$id", seriesId);
            insertSeries.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            insertSeries.Parameters.AddWithValue("$symbol", benchmark.Symbol);
            insertSeries.Parameters.AddWithValue("$key", seriesKey);
            insertSeries.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            insertSeries.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO benchmark_points (id, journal_id, series_id, import_batch_id, source_type, source_key, event_utc, value, source_time_text, row_number) VALUES ($id, $journal, $series, $batch, $source, $key, $event, $value, $sourceTime, $row)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$series", seriesId);
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$source", benchmark.SourceType);
        command.Parameters.AddWithValue("$key", benchmark.SourceKey);
        command.Parameters.AddWithValue("$event", benchmark.EventUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$value", NumberFormat.Decimal(benchmark.Value));
        command.Parameters.AddWithValue("$sourceTime", benchmark.SourceTimeText);
        command.Parameters.AddWithValue("$row", benchmark.RowNumber);
        command.ExecuteNonQuery();
    }

    private static bool InsertDirectTrade(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, Guid batchId, ImportedTradeDraft draft, string groupingPolicy)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO trades (id, journal_id, import_batch_id, source_type, source_key, grouping_policy, sequence, symbol, account, direction, entry_utc, exit_utc, entry_price, exit_price, quantity, closed_quantity, gross_points, average_points, gross_pnl, fees, net_pnl, mae_points, mfe_points, point_value, tick_size, initial_stop_price, initial_target_price, initial_risk_points, initial_risk_currency, r_multiple, exit_type, entry_order_price, exit_order_price, entry_chase_points, exit_chase_points, status, note, created_utc) VALUES ($id, $journal, $batch, $source, $key, $grouping, $sequence, $symbol, $account, $direction, $entry, $exit, $entryPrice, $exitPrice, $quantity, $closed, $grossPoints, $averagePoints, $grossPnl, $fees, $netPnl, NULL, NULL, $pointValue, $tickSize, $initialStop, $initialTarget, $initialRiskPoints, $initialRiskCurrency, $rMultiple, $exitType, NULL, NULL, NULL, NULL, $status, $note, $created)";
        var quantity = Math.Max(1, draft.Quantity);
        var averagePoints = quantity == 0 ? 0m : draft.GrossPoints / quantity;
        var pointValue = InstrumentCatalog.Resolve(draft.Symbol).PointValue;
        var tickSize = InstrumentCatalog.Resolve(draft.Symbol).TickSize;
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
        command.Parameters.AddWithValue("$pointValue", NumberFormat.Decimal(pointValue));
        command.Parameters.AddWithValue("$tickSize", NumberFormat.Decimal(tickSize));
        AddNullable(command, "$initialStop", draft.InitialStopPrice);
        AddNullable(command, "$initialTarget", draft.InitialTargetPrice);
        AddNullable(command, "$initialRiskPoints", draft.InitialRiskPoints);
        AddNullable(command, "$initialRiskCurrency", draft.InitialRiskCurrency);
        AddNullable(command, "$rMultiple", draft.RMultiple);
        command.Parameters.AddWithValue("$exitType", draft.ExitType);
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

    private static void InsertDerivedTrade(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, PositionState state, string groupingPolicy, int sequence, OrderLifecycleIndex orderLifecycles)
    {
        var tradeId = Guid.NewGuid().ToString("D");
        var closed = state.ClosedQuantity;
        var entryPrice = closed > 0 ? state.ClosedEntryCost / closed : state.EntryCost / Math.Max(1, state.OpenQuantity);
        var exitPrice = closed > 0 ? state.ExitCost / closed : (decimal?)null;
        var averagePoints = closed > 0 ? state.GrossPoints / closed : 0m;
        var status = state.OpenQuantity > 0 ? "open" : "closed";
        var note = string.Join(" ", state.Notes.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(3));
        var enrichment = EnrichTrade(state, entryPrice, exitPrice, Math.Max(state.MaxOpenQuantity, closed), orderLifecycles);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO trades (id, journal_id, import_batch_id, source_type, source_key, grouping_policy, sequence, symbol, account, direction, entry_utc, exit_utc, entry_price, exit_price, quantity, closed_quantity, gross_points, average_points, gross_pnl, fees, net_pnl, mae_points, mfe_points, point_value, tick_size, initial_stop_price, initial_target_price, initial_risk_points, initial_risk_currency, r_multiple, exit_type, entry_order_price, exit_order_price, entry_chase_points, exit_chase_points, status, note, created_utc) VALUES ($id, $journal, $batch, $source, $key, $grouping, $sequence, $symbol, $account, $direction, $entry, $exit, $entryPrice, $exitPrice, $quantity, $closed, $grossPoints, $averagePoints, $grossPnl, $fees, $netPnl, $mae, $mfe, $pointValue, $tickSize, $initialStop, $initialTarget, $initialRiskPoints, $initialRiskCurrency, $rMultiple, $exitType, $entryOrderPrice, $exitOrderPrice, $entryChasePoints, $exitChasePoints, $status, $note, $created)";
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
        command.Parameters.AddWithValue("$pointValue", NumberFormat.Decimal(enrichment.PointValue));
        command.Parameters.AddWithValue("$tickSize", NumberFormat.Decimal(enrichment.TickSize));
        AddNullable(command, "$initialStop", enrichment.InitialStopPrice);
        AddNullable(command, "$initialTarget", enrichment.InitialTargetPrice);
        AddNullable(command, "$initialRiskPoints", enrichment.InitialRiskPoints);
        AddNullable(command, "$initialRiskCurrency", enrichment.InitialRiskCurrency);
        AddNullable(command, "$rMultiple", enrichment.RMultiple);
        command.Parameters.AddWithValue("$exitType", enrichment.ExitType);
        AddNullable(command, "$entryOrderPrice", enrichment.EntryOrderPrice);
        AddNullable(command, "$exitOrderPrice", enrichment.ExitOrderPrice);
        AddNullable(command, "$entryChasePoints", enrichment.EntryChasePoints);
        AddNullable(command, "$exitChasePoints", enrichment.ExitChasePoints);
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
        command.CommandText = $"SELECT {FillColumns} FROM fills WHERE journal_id = $journal ORDER BY account, symbol, event_utc, row_number, id";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        var fills = new List<Fill>();
        while (reader.Read())
        {
            fills.Add(new Fill
            {
                Id = Guid.Parse(reader.GetString(0)), JournalId = Guid.Parse(reader.GetString(1)), ImportBatchId = Guid.Parse(reader.GetString(2)), SourceType = reader.GetString(3), SourceKey = reader.GetString(4), ActivityType = reader.GetString(5), OrderActionSource = reader.GetString(6), EventUtc = ParseDate(reader.GetString(7)), TransactionUtc = reader.IsDBNull(8) ? null : ParseDate(reader.GetString(8)), SourceTimeText = reader.GetString(9), Symbol = reader.GetString(10), Account = reader.GetString(11), Side = reader.GetString(12), Quantity = reader.GetInt32(13), Price = ParseDecimal(reader.GetString(14)), Price2 = NullableDecimal(reader, 15), FilledQuantity = reader.IsDBNull(16) ? null : reader.GetInt32(16), OpenClose = reader.GetString(17), OrderType = reader.GetString(18), OrderStatus = reader.GetString(19), ParentOrderId = reader.GetString(20), High = NullableDecimal(reader, 21), Low = NullableDecimal(reader, 22), Note = reader.GetString(23), PositionQuantity = reader.IsDBNull(24) ? null : reader.GetInt32(24), OrderId = reader.GetString(25), ServiceOrderId = reader.GetString(26), ExchangeOrderId = reader.GetString(27), FillExecutionId = reader.GetString(28), ClientOrderId = reader.GetString(29), TimeInForce = reader.GetString(30), Username = reader.GetString(31), IsAutomated = NullableBool(reader, 32), AccountBalance = NullableDecimal(reader, 33), Fees = ParseDecimal(reader.GetString(34)), RowNumber = reader.GetInt32(35)
            });
        }
        return fills;
    }

    private static List<OrderEvent> ReadOrderEvents(SqliteConnection connection, SqliteTransaction transaction, Guid journalId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {OrderEventColumns} FROM order_events WHERE journal_id = $journal ORDER BY account, symbol, event_utc, row_number, id";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        var events = new List<OrderEvent>();
        while (reader.Read()) events.Add(ReadOrderEvent(reader));
        return events;
    }

    private static OrderEvent ReadOrderEvent(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)), JournalId = Guid.Parse(reader.GetString(1)), ImportBatchId = Guid.Parse(reader.GetString(2)), SourceType = reader.GetString(3), SourceKey = reader.GetString(4), OrderActionSource = reader.GetString(5), EventUtc = ParseDate(reader.GetString(6)), TransactionUtc = reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)), SourceTimeText = reader.GetString(8), Symbol = reader.GetString(9), Account = reader.GetString(10), InternalOrderId = reader.GetString(11), ServiceOrderId = reader.GetString(12), ParentOrderId = reader.GetString(13), ExchangeOrderId = reader.GetString(14), FillExecutionId = reader.GetString(15), OrderType = reader.GetString(16), OrderStatus = reader.GetString(17), Side = reader.GetString(18), OpenClose = reader.GetString(19), Price = NullableDecimal(reader, 20), Price2 = NullableDecimal(reader, 21), Quantity = reader.IsDBNull(22) ? null : reader.GetInt32(22), FilledQuantity = reader.IsDBNull(23) ? null : reader.GetInt32(23), FillPrice = NullableDecimal(reader, 24), PositionQuantity = reader.IsDBNull(25) ? null : reader.GetInt32(25), Note = reader.GetString(26), ClientOrderId = reader.GetString(27), TimeInForce = reader.GetString(28), Username = reader.GetString(29), IsAutomated = NullableBool(reader, 30), Fees = ParseDecimal(reader.GetString(31)), RowNumber = reader.GetInt32(32)
    };

    private static AccountBalanceEvent ReadAccountBalanceEvent(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)), JournalId = Guid.Parse(reader.GetString(1)), ImportBatchId = Guid.Parse(reader.GetString(2)), SourceType = reader.GetString(3), SourceKey = reader.GetString(4), EventUtc = ParseDate(reader.GetString(5)), TransactionUtc = reader.IsDBNull(6) ? null : ParseDate(reader.GetString(6)), SourceTimeText = reader.GetString(7), Account = reader.GetString(8), Balance = NullableDecimal(reader, 9), Note = reader.GetString(10), RowNumber = reader.GetInt32(11)
    };

    private static BenchmarkPoint ReadBenchmarkPoint(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)), JournalId = Guid.Parse(reader.GetString(1)), ImportBatchId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), SeriesId = Guid.Parse(reader.GetString(3)), SourceType = reader.GetString(4), SourceKey = reader.GetString(5), Symbol = reader.GetString(6), EventUtc = ParseDate(reader.GetString(7)), Value = ParseDecimal(reader.GetString(8)), SourceTimeText = reader.GetString(9), RowNumber = reader.GetInt32(10)
    };

    private static OrderLifecycleIndex BuildOrderLifecycles(IReadOnlyList<OrderEvent> events)
    {
        var lifecycles = new List<OrderLifecycle>();
        foreach (var group in events
            .Where(x => !string.IsNullOrWhiteSpace(x.InternalOrderId))
            .GroupBy(x => $"{x.Account}\u001f{x.InternalOrderId}", StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.OrderBy(x => x.EventUtc).ThenBy(x => x.RowNumber).ThenBy(x => x.Id).ToArray();
            var first = rows[0];
            var last = rows[^1];
            var filled = rows.Where(x => x.OrderStatus.Equals("Filled", StringComparison.OrdinalIgnoreCase)).LastOrDefault();
            var lifecycle = new OrderLifecycle
            {
                Account = first.Account,
                Symbol = first.Symbol,
                InternalOrderId = first.InternalOrderId,
                ParentOrderId = rows.Select(x => x.ParentOrderId).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty,
                OrderType = first.OrderType,
                Side = first.Side,
                OpenClose = first.OpenClose,
                InitialPrice = rows.Select(x => x.Price).FirstOrDefault(x => x.HasValue),
                InitialPrice2 = rows.Select(x => x.Price2).FirstOrDefault(x => x.HasValue),
                SubmitUtc = first.EventUtc,
                LastUpdateUtc = last.EventUtc,
                FinalStatus = last.OrderStatus,
                FillPrice = filled?.FillPrice,
                FilledQuantity = filled?.FilledQuantity,
                FillExecutionId = filled?.FillExecutionId ?? rows.Select(x => x.FillExecutionId).LastOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty,
                ExchangeOrderId = filled?.ExchangeOrderId ?? rows.Select(x => x.ExchangeOrderId).LastOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty,
                ModifyCount = rows.Count(x => x.OrderStatus.Contains("modify", StringComparison.OrdinalIgnoreCase)),
                IsFilled = filled is not null,
                IsCanceled = last.OrderStatus.Equals("Canceled", StringComparison.OrdinalIgnoreCase),
                IsPartial = filled is null && rows.Any(x => x.OrderStatus.Contains("partial", StringComparison.OrdinalIgnoreCase))
            };
            lifecycles.Add(lifecycle);
        }

        var index = new OrderLifecycleIndex(lifecycles);
        return index;
    }

    private static TradeEnrichment EnrichTrade(PositionState state, decimal entryPrice, decimal? exitPrice, int quantity, OrderLifecycleIndex orderLifecycles)
    {
        var spec = InstrumentCatalog.Resolve(state.Symbol);
        var entryOrderReferences = state.EntryReferences
            .Select(reference => (Reference: reference, Order: orderLifecycles.Find(state.Account, reference.InternalOrderId, reference.ExchangeOrderId, reference.FillExecutionId)))
            .Where(x => x.Order?.InitialPrice.HasValue == true)
            .ToArray();
        var totalEntryOrderQuantity = entryOrderReferences.Sum(x => x.Reference.Quantity);
        var entryOrderPrice = totalEntryOrderQuantity > 0
            ? entryOrderReferences.Sum(x => x.Order!.InitialPrice!.Value * x.Reference.Quantity) / totalEntryOrderQuantity
            : (decimal?)null;

        OrderReference? lastExitReference = state.ExitReferences.Count > 0 ? state.ExitReferences[^1] : null;
        var lastExitOrder = lastExitReference is null
            ? null
            : orderLifecycles.Find(state.Account, lastExitReference.Value.InternalOrderId, lastExitReference.Value.ExchangeOrderId, lastExitReference.Value.FillExecutionId);
        var exitOrderPrice = lastExitOrder?.InitialPrice;

        var initialStop = FindProtectiveOrder(orderLifecycles, state, "stop");
        var initialTarget = FindProtectiveOrder(orderLifecycles, state, "target");
        var initialStopPrice = initialStop?.InitialPrice;
        var initialTargetPrice = initialTarget?.InitialPrice;
        var initialRiskPoints = initialStopPrice.HasValue ? Math.Abs(entryPrice - initialStopPrice.Value) : (decimal?)null;
        var initialRiskCurrency = initialRiskPoints.HasValue ? initialRiskPoints.Value * quantity * spec.PointValue : (decimal?)null;
        var rMultiple = initialRiskCurrency is > 0m ? state.GrossPnl / initialRiskCurrency.Value : (decimal?)null;

        var entryChasePoints = entryOrderPrice.HasValue
            ? (state.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase) ? entryPrice - entryOrderPrice.Value : entryOrderPrice.Value - entryPrice)
            : (decimal?)null;
        var exitChasePoints = exitOrderPrice.HasValue && exitPrice.HasValue
            ? (state.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase) ? exitOrderPrice.Value - exitPrice.Value : exitPrice.Value - exitOrderPrice.Value)
            : (decimal?)null;

        var exitType = state.LastExitType;
        if (string.IsNullOrWhiteSpace(exitType) && lastExitOrder is not null)
            exitType = ClassifyExitType(lastExitOrder.OrderType);

        return new TradeEnrichment(
            spec.PointValue,
            spec.TickSize,
            initialStopPrice,
            initialTargetPrice,
            initialRiskPoints,
            initialRiskCurrency,
            rMultiple,
            exitType,
            entryOrderPrice,
            exitOrderPrice,
            entryChasePoints,
            exitChasePoints);
    }

    private static OrderLifecycle? FindProtectiveOrder(OrderLifecycleIndex orderLifecycles, PositionState state, string kind)
    {
        var entryOrderIds = state.EntryReferences
            .Select(x => x.InternalOrderId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return orderLifecycles.All
            .Where(x => x.Account.Equals(state.Account, StringComparison.OrdinalIgnoreCase)
                && entryOrderIds.Contains(x.ParentOrderId)
                && x.InitialPrice.HasValue
                && (kind.Equals("stop", StringComparison.OrdinalIgnoreCase)
                    ? x.OrderType.Contains("stop", StringComparison.OrdinalIgnoreCase)
                    : x.OrderType.Contains("limit", StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(x.OpenClose) || x.OpenClose.Equals("Close", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.SubmitUtc)
            .ThenBy(x => x.InternalOrderId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string ClassifyExitType(string orderType)
    {
        if (orderType.Contains("stop", StringComparison.OrdinalIgnoreCase)) return "stop";
        if (orderType.Contains("limit", StringComparison.OrdinalIgnoreCase)) return "target";
        return "manual";
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

    private static decimal? ReadFirstAccountBalance(SqliteConnection connection, Guid journalId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT balance FROM account_balance_events WHERE journal_id = $journal AND balance IS NOT NULL ORDER BY event_utc, row_number, id LIMIT 1";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : ParseDecimal(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        var exists = false;
        using (var query = connection.CreateCommand())
        {
            query.CommandText = $"PRAGMA table_info([{table}])";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }
        if (exists) return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE [{table}] ADD COLUMN [{column}] {definition}";
        alter.ExecuteNonQuery();
    }

    private static Journal ReadJournal(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)), Name = reader.GetString(1), ExecutionContext = reader.GetString(2), Labels = reader.GetString(3), TimeZone = reader.GetString(4), Currency = reader.GetString(5), GroupingPolicy = reader.GetString(6), StartingEquity = NullableDecimal(reader, 7), CreatedUtc = ParseDate(reader.GetString(8))
    };

    private static ImportBatch ReadImport(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)), JournalId = Guid.Parse(reader.GetString(1)), FileName = reader.GetString(2), SourceType = reader.GetString(3), ImportedUtc = ParseDate(reader.GetString(4)), TotalRows = reader.GetInt32(5), NewRows = reader.GetInt32(6), DuplicateRows = reader.GetInt32(7), Status = reader.GetString(8), Message = reader.GetString(9)
    };

    private static Trade ReadTrade(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)), JournalId = Guid.Parse(reader.GetString(1)), ImportBatchId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), SourceType = reader.GetString(3), SourceKey = reader.GetString(4), GroupingPolicy = reader.GetString(5), Sequence = reader.GetInt32(6), Symbol = reader.GetString(7), Account = reader.GetString(8), Direction = reader.GetString(9), EntryUtc = ParseDate(reader.GetString(10)), ExitUtc = reader.IsDBNull(11) ? null : ParseDate(reader.GetString(11)), EntryPrice = ParseDecimal(reader.GetString(12)), ExitPrice = reader.IsDBNull(13) ? null : ParseDecimal(reader.GetString(13)), Quantity = reader.GetInt32(14), ClosedQuantity = reader.GetInt32(15), GrossPoints = ParseDecimal(reader.GetString(16)), AveragePoints = ParseDecimal(reader.GetString(17)), GrossPnl = ParseDecimal(reader.GetString(18)), Fees = ParseDecimal(reader.GetString(19)), NetPnl = ParseDecimal(reader.GetString(20)), MaePoints = reader.IsDBNull(21) ? null : ParseDecimal(reader.GetString(21)), MfePoints = reader.IsDBNull(22) ? null : ParseDecimal(reader.GetString(22)), PointValue = ParseDecimal(reader.GetString(23)), TickSize = ParseDecimal(reader.GetString(24)), InitialStopPrice = NullableDecimal(reader, 25), InitialTargetPrice = NullableDecimal(reader, 26), InitialRiskPoints = NullableDecimal(reader, 27), InitialRiskCurrency = NullableDecimal(reader, 28), RMultiple = NullableDecimal(reader, 29), ExitType = reader.GetString(30), EntryOrderPrice = NullableDecimal(reader, 31), ExitOrderPrice = NullableDecimal(reader, 32), EntryChasePoints = NullableDecimal(reader, 33), ExitChasePoints = NullableDecimal(reader, 34), Status = reader.GetString(35), Note = reader.GetString(36)
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
            ActivityType = fill.ActivityType, OrderActionSource = fill.OrderActionSource, EventUtc = fill.EventUtc, TransactionUtc = fill.TransactionUtc, SourceTimeText = fill.SourceTimeText, Symbol = fill.Symbol, Account = fill.Account, Side = fill.Side,
            Quantity = quantity, Price = fill.Price, Price2 = fill.Price2, FilledQuantity = fill.FilledQuantity, OpenClose = fill.OpenClose, OrderType = fill.OrderType, OrderStatus = fill.OrderStatus, ParentOrderId = fill.ParentOrderId, High = fill.High, Low = fill.Low, Note = fill.Note,
            PositionQuantity = fill.PositionQuantity, OrderId = fill.OrderId, ServiceOrderId = fill.ServiceOrderId, ExchangeOrderId = fill.ExchangeOrderId, FillExecutionId = fill.FillExecutionId, ClientOrderId = fill.ClientOrderId, TimeInForce = fill.TimeInForce, Username = fill.Username, IsAutomated = fill.IsAutomated, AccountBalance = fill.AccountBalance, Fees = fee, RowNumber = fill.RowNumber
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
    private static bool? NullableBool(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture) != 0;
    private static decimal AggregateDecimal(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return 0m;
        var text = Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0m;
    }
    private static int AggregateInt(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return 0;
        return Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private sealed record SierraPriceRepair(Guid FillId, Guid JournalId, decimal Price, decimal? High, decimal? Low);

    private sealed class OrderLifecycleIndex
    {
        private readonly Dictionary<string, OrderLifecycle> _byInternal = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, OrderLifecycle> _byExchange = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, OrderLifecycle> _byExecution = new(StringComparer.OrdinalIgnoreCase);

        public OrderLifecycleIndex(IReadOnlyList<OrderLifecycle> all)
        {
            All = all;
            foreach (var order in all)
            {
                if (!string.IsNullOrWhiteSpace(order.InternalOrderId)) _byInternal.TryAdd(Key(order.Account, order.InternalOrderId), order);
                if (!string.IsNullOrWhiteSpace(order.ExchangeOrderId)) _byExchange.TryAdd(Key(order.Account, order.ExchangeOrderId), order);
                if (!string.IsNullOrWhiteSpace(order.FillExecutionId)) _byExecution.TryAdd(Key(order.Account, order.FillExecutionId), order);
            }
        }

        public IReadOnlyList<OrderLifecycle> All { get; }

        public OrderLifecycle? Find(string account, string internalOrderId, string exchangeOrderId, string fillExecutionId)
        {
            if (!string.IsNullOrWhiteSpace(exchangeOrderId) && _byExchange.TryGetValue(Key(account, exchangeOrderId), out var byExchange)) return byExchange;
            if (!string.IsNullOrWhiteSpace(fillExecutionId) && _byExecution.TryGetValue(Key(account, fillExecutionId), out var byExecution)) return byExecution;
            if (!string.IsNullOrWhiteSpace(internalOrderId) && _byInternal.TryGetValue(Key(account, internalOrderId), out var byInternal)) return byInternal;
            return null;
        }

        private static string Key(string account, string value) => $"{account}\u001f{value}";
    }

    private sealed class OrderLifecycle
    {
        public string Account { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public string InternalOrderId { get; init; } = string.Empty;
        public string ParentOrderId { get; init; } = string.Empty;
        public string OrderType { get; init; } = string.Empty;
        public string Side { get; init; } = string.Empty;
        public string OpenClose { get; init; } = string.Empty;
        public decimal? InitialPrice { get; init; }
        public decimal? InitialPrice2 { get; init; }
        public DateTimeOffset SubmitUtc { get; init; }
        public DateTimeOffset LastUpdateUtc { get; init; }
        public string FinalStatus { get; init; } = string.Empty;
        public decimal? FillPrice { get; init; }
        public int? FilledQuantity { get; init; }
        public string FillExecutionId { get; init; } = string.Empty;
        public string ExchangeOrderId { get; init; } = string.Empty;
        public int ModifyCount { get; init; }
        public bool IsFilled { get; init; }
        public bool IsCanceled { get; init; }
        public bool IsPartial { get; init; }
    }

    private readonly record struct OrderReference(string InternalOrderId, string ExchangeOrderId, string FillExecutionId, int Quantity)
    {
        public static OrderReference From(Fill fill, int quantity) => new(fill.OrderId, fill.ExchangeOrderId, fill.FillExecutionId, quantity);
    }

    private readonly record struct TradeEnrichment(
        decimal PointValue,
        decimal TickSize,
        decimal? InitialStopPrice,
        decimal? InitialTargetPrice,
        decimal? InitialRiskPoints,
        decimal? InitialRiskCurrency,
        decimal? RMultiple,
        string ExitType,
        decimal? EntryOrderPrice,
        decimal? ExitOrderPrice,
        decimal? EntryChasePoints,
        decimal? ExitChasePoints);

    private sealed class PositionState
    {
        private PositionState(Fill fill, int direction, int quantity)
        {
            Symbol = fill.Symbol; Account = fill.Account; Direction = direction > 0 ? "Long" : "Short"; SignedQuantity = direction * quantity; OpenQuantity = quantity; MaxOpenQuantity = quantity; EntryCost = fill.Price * quantity; EntryUtc = fill.EventUtc; EntrySourceKey = fill.SourceKey; ImportBatchId = fill.ImportBatchId; Fees = fill.Fees; EntryReferences.Add(OrderReference.From(fill, quantity)); Notes.Add(fill.Note); AddExcursion(fill, fill.Price, direction);
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
        public string LastExitType { get; private set; } = string.Empty;
        public string EntrySourceKey { get; }
        public Guid? ImportBatchId { get; }
        public List<string> Notes { get; } = new();
        public List<Allocation> Allocations { get; } = new();
        public List<OrderReference> EntryReferences { get; } = new();
        public List<OrderReference> ExitReferences { get; } = new();

        public static PositionState Start(Fill fill, int direction, int quantity) => new(fill, direction, quantity);

        public void AddOpening(Fill fill, int quantity)
        {
            var total = OpenQuantity + quantity;
            EntryCost += fill.Price * quantity;
            OpenQuantity = total;
            SignedQuantity = Math.Sign(SignedQuantity) * total;
            MaxOpenQuantity = Math.Max(MaxOpenQuantity, total);
            Fees += fill.Fees;
            EntryReferences.Add(OrderReference.From(fill, quantity));
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
            LastExitType = ClassifyExitType(fill.OrderType);
            Notes.Add(fill.Note);
            Allocations.Add(new Allocation(fill.SourceType, fill.SourceKey, quantity));
            ExitReferences.Add(OrderReference.From(fill, quantity));
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

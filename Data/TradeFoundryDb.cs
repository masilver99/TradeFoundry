using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    private const string JournalColumns = "id, name, execution_context, labels, description_markdown, timezone, currency, grouping_policy, starting_equity, created_utc";
    private const string ImportColumns = "id, journal_id, file_name, source_application, source_type, imported_utc, total_rows, new_rows, duplicate_rows, status, message";
    private const string EffectiveFeesSql = "COALESCE(r.all_in_commission, t.fees)";
    private const string EffectiveNetPnlSql = "CASE WHEN r.all_in_commission IS NOT NULL THEN CAST(t.gross_pnl AS REAL) - CAST(r.all_in_commission AS REAL) ELSE CAST(t.net_pnl AS REAL) END";
    private const string EffectiveNetPnlTextSql = "CASE WHEN r.all_in_commission IS NOT NULL THEN CAST(CAST(t.gross_pnl AS REAL) - CAST(r.all_in_commission AS REAL) AS TEXT) ELSE t.net_pnl END";
    private const string TradeFrom = "trades t LEFT JOIN trade_review_annotations r ON r.journal_id = t.journal_id AND r.review_key = t.review_key";
    private const string TradeColumns = "t.id, t.journal_id, t.import_batch_id, t.source_type, t.source_key, t.grouping_policy, t.sequence, t.symbol, t.account, t.direction, t.entry_utc, t.exit_utc, t.entry_price, t.exit_price, t.quantity, t.closed_quantity, t.gross_points, t.average_points, t.gross_pnl, " + EffectiveFeesSql + " AS fees, " + EffectiveNetPnlTextSql + " AS net_pnl, t.mae_points, t.mfe_points, t.point_value, t.tick_size, t.initial_stop_price, t.initial_target_price, t.initial_risk_points, t.initial_risk_currency, t.r_multiple, t.exit_type, t.entry_order_price, t.exit_order_price, t.entry_chase_points, t.exit_chase_points, t.status, t.note, t.instrument, t.review_key, CASE WHEN LENGTH(TRIM(COALESCE(r.review_note, ''))) > 0 THEN 1 ELSE 0 END AS has_review_note, EXISTS (SELECT 1 FROM trade_review_attachments a WHERE a.journal_id = t.journal_id AND a.review_key = t.review_key AND a.removed_utc IS NULL) AS has_review_image";
    private const string TradeReviewColumns = "id, journal_id, review_key, revision, review_note, setup, tags_json, planned_entry_price, planned_stop_price, planned_target_price, planned_risk_points, planned_risk_currency, all_in_commission, plan_adherence, process_rating, mistakes, lessons, updated_utc";
    private const string FillColumns = "id, journal_id, import_batch_id, source_type, source_key, activity_type, order_action_source, event_utc, transaction_utc, source_time_text, symbol, account, side, quantity, price, price2, filled_quantity, open_close, order_type, order_status, parent_order_id, high, low, note, position_quantity, order_id, service_order_id, exchange_order_id, fill_execution_id, client_order_id, time_in_force, username, is_automated, account_balance, fees, row_number, instrument, point_value, tick_size";
    private const string OrderEventColumns = "id, journal_id, import_batch_id, source_type, source_key, order_action_source, event_utc, transaction_utc, source_time_text, symbol, account, internal_order_id, service_order_id, parent_order_id, exchange_order_id, fill_execution_id, order_type, order_status, side, open_close, price, price2, quantity, filled_quantity, fill_price, position_quantity, note, client_order_id, time_in_force, username, is_automated, fees, row_number, instrument";
    private const string AccountBalanceColumns = "id, journal_id, import_batch_id, source_type, source_key, event_utc, transaction_utc, source_time_text, account, balance, note, row_number";
    private const string AccountTransactionColumns = "id, journal_id, transaction_type, effective_utc, amount, note, revision, created_utc, updated_utc, deleted_utc";
    private const string AccountTransactionHistoryColumns = "id, journal_id, transaction_id, revision, action, before_json, after_json, created_utc";
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
            "CREATE TABLE IF NOT EXISTS journals (id TEXT PRIMARY KEY, owner_user_id TEXT NOT NULL REFERENCES app_users(id), name TEXT NOT NULL, execution_context TEXT NOT NULL, labels TEXT NOT NULL DEFAULT '', description_markdown TEXT NOT NULL DEFAULT '', timezone TEXT NOT NULL DEFAULT 'UTC', currency TEXT NOT NULL DEFAULT 'USD', grouping_policy TEXT NOT NULL DEFAULT 'flat_to_flat', starting_equity TEXT NULL, created_utc TEXT NOT NULL, archived INTEGER NOT NULL DEFAULT 0)",
            "CREATE INDEX IF NOT EXISTS ix_journals_owner ON journals(owner_user_id, archived, created_utc)",
            "CREATE TABLE IF NOT EXISTS instruments (id TEXT PRIMARY KEY, code TEXT NOT NULL UNIQUE, default_commission TEXT NULL, point_value TEXT NOT NULL, tick_size TEXT NOT NULL DEFAULT '0', created_utc TEXT NOT NULL)",
            "CREATE TABLE IF NOT EXISTS source_instrument_mappings (id TEXT PRIMARY KEY, application_key TEXT NOT NULL, match_regex TEXT NOT NULL, instrument_code TEXT NOT NULL REFERENCES instruments(code), commission_override TEXT NULL, position INTEGER NOT NULL DEFAULT 0, created_utc TEXT NOT NULL, UNIQUE(application_key, match_regex))",
            "CREATE INDEX IF NOT EXISTS ix_source_instrument_mappings_application ON source_instrument_mappings(application_key, position, id)",
            "CREATE TABLE IF NOT EXISTS app_settings (settings_key TEXT PRIMARY KEY, settings_value TEXT NOT NULL)",
            "CREATE TABLE IF NOT EXISTS import_batches (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id), file_name TEXT NOT NULL, source_application TEXT NOT NULL DEFAULT '', source_type TEXT NOT NULL, imported_utc TEXT NOT NULL, total_rows INTEGER NOT NULL, new_rows INTEGER NOT NULL, duplicate_rows INTEGER NOT NULL, status TEXT NOT NULL, message TEXT NOT NULL DEFAULT '')",
            "CREATE INDEX IF NOT EXISTS ix_import_batches_journal ON import_batches(journal_id, imported_utc DESC)",
            "CREATE TABLE IF NOT EXISTS raw_records (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id), import_batch_id TEXT NOT NULL REFERENCES import_batches(id), source_type TEXT NOT NULL, source_key TEXT NOT NULL, row_number INTEGER NOT NULL, payload_json TEXT NOT NULL, status TEXT NOT NULL, error TEXT NOT NULL DEFAULT '', UNIQUE(journal_id, source_type, source_key))",
            "CREATE TABLE IF NOT EXISTS fills (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id), import_batch_id TEXT NOT NULL REFERENCES import_batches(id), source_type TEXT NOT NULL, source_key TEXT NOT NULL, activity_type TEXT NOT NULL DEFAULT 'Fills', order_action_source TEXT NOT NULL DEFAULT '', event_utc TEXT NOT NULL, transaction_utc TEXT NULL, source_time_text TEXT NOT NULL DEFAULT '', symbol TEXT NOT NULL, account TEXT NOT NULL DEFAULT '', side TEXT NOT NULL, quantity INTEGER NOT NULL, price TEXT NOT NULL, price2 TEXT NULL, filled_quantity INTEGER NULL, open_close TEXT NOT NULL DEFAULT '', order_type TEXT NOT NULL DEFAULT '', order_status TEXT NOT NULL DEFAULT '', parent_order_id TEXT NOT NULL DEFAULT '', high TEXT NULL, low TEXT NULL, note TEXT NOT NULL DEFAULT '', position_quantity INTEGER NULL, order_id TEXT NOT NULL DEFAULT '', service_order_id TEXT NOT NULL DEFAULT '', exchange_order_id TEXT NOT NULL DEFAULT '', fill_execution_id TEXT NOT NULL DEFAULT '', client_order_id TEXT NOT NULL DEFAULT '', time_in_force TEXT NOT NULL DEFAULT '', username TEXT NOT NULL DEFAULT '', is_automated INTEGER NULL, account_balance TEXT NULL, fees TEXT NOT NULL DEFAULT '0', row_number INTEGER NOT NULL, instrument TEXT NOT NULL DEFAULT '', point_value TEXT NOT NULL DEFAULT '0', tick_size TEXT NOT NULL DEFAULT '0', UNIQUE(journal_id, source_type, source_key))",
            "CREATE INDEX IF NOT EXISTS ix_fills_journal_time ON fills(journal_id, symbol, account, event_utc, row_number)",
            "CREATE TABLE IF NOT EXISTS trades (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id), import_batch_id TEXT NULL REFERENCES import_batches(id), source_type TEXT NOT NULL, source_key TEXT NOT NULL, grouping_policy TEXT NOT NULL, sequence INTEGER NOT NULL, symbol TEXT NOT NULL, account TEXT NOT NULL DEFAULT '', direction TEXT NOT NULL, entry_utc TEXT NOT NULL, exit_utc TEXT NULL, entry_price TEXT NOT NULL, exit_price TEXT NULL, quantity INTEGER NOT NULL, closed_quantity INTEGER NOT NULL, gross_points TEXT NOT NULL DEFAULT '0', average_points TEXT NOT NULL DEFAULT '0', gross_pnl TEXT NOT NULL DEFAULT '0', fees TEXT NOT NULL DEFAULT '0', net_pnl TEXT NOT NULL DEFAULT '0', mae_points TEXT NULL, mfe_points TEXT NULL, point_value TEXT NOT NULL DEFAULT '1', tick_size TEXT NOT NULL DEFAULT '0', initial_stop_price TEXT NULL, initial_target_price TEXT NULL, initial_risk_points TEXT NULL, initial_risk_currency TEXT NULL, r_multiple TEXT NULL, exit_type TEXT NOT NULL DEFAULT '', entry_order_price TEXT NULL, exit_order_price TEXT NULL, entry_chase_points TEXT NULL, exit_chase_points TEXT NULL, status TEXT NOT NULL, note TEXT NOT NULL DEFAULT '', created_utc TEXT NOT NULL, instrument TEXT NOT NULL DEFAULT '', review_key TEXT NOT NULL DEFAULT '', UNIQUE(journal_id, source_type, source_key))",
            "CREATE INDEX IF NOT EXISTS ix_trades_journal_time ON trades(journal_id, entry_utc)",
            "CREATE INDEX IF NOT EXISTS ix_trades_journal_status_time ON trades(journal_id, status, entry_utc)",
            "CREATE INDEX IF NOT EXISTS ix_trades_journal_symbol_time ON trades(journal_id, symbol, entry_utc)",
            "CREATE TABLE IF NOT EXISTS trade_fill_allocations (trade_id TEXT NOT NULL REFERENCES trades(id) ON DELETE CASCADE, fill_id TEXT NOT NULL REFERENCES fills(id) ON DELETE CASCADE, quantity INTEGER NOT NULL, PRIMARY KEY(trade_id, fill_id))",
            "CREATE TABLE IF NOT EXISTS trade_review_annotations (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, review_key TEXT NOT NULL, revision INTEGER NOT NULL, review_note TEXT NOT NULL DEFAULT '', setup TEXT NOT NULL DEFAULT '', tags_json TEXT NOT NULL DEFAULT '[]', planned_entry_price TEXT NULL, planned_stop_price TEXT NULL, planned_target_price TEXT NULL, planned_risk_points TEXT NULL, planned_risk_currency TEXT NULL, all_in_commission TEXT NULL, plan_adherence TEXT NOT NULL DEFAULT '', process_rating INTEGER NULL, mistakes TEXT NOT NULL DEFAULT '', lessons TEXT NOT NULL DEFAULT '', updated_utc TEXT NOT NULL, UNIQUE(journal_id, review_key))",
            "CREATE INDEX IF NOT EXISTS ix_trade_review_annotations_journal ON trade_review_annotations(journal_id, updated_utc DESC)",
            "CREATE TABLE IF NOT EXISTS trade_review_history (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, review_key TEXT NOT NULL, revision INTEGER NOT NULL, action TEXT NOT NULL, before_json TEXT NOT NULL DEFAULT '{}', after_json TEXT NOT NULL DEFAULT '{}', reason TEXT NOT NULL DEFAULT '', created_utc TEXT NOT NULL)",
            "CREATE INDEX IF NOT EXISTS ix_trade_review_history_trade ON trade_review_history(journal_id, review_key, revision DESC)",
            "CREATE TABLE IF NOT EXISTS trade_review_attachments (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, review_key TEXT NOT NULL, storage_key TEXT NOT NULL, original_file_name TEXT NOT NULL, content_type TEXT NOT NULL, length INTEGER NOT NULL, created_utc TEXT NOT NULL, removed_utc TEXT NULL)",
            "CREATE INDEX IF NOT EXISTS ix_trade_review_attachments_trade ON trade_review_attachments(journal_id, review_key, created_utc DESC)",
            "CREATE TABLE IF NOT EXISTS daily_review_journals (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, review_date TEXT NOT NULL, revision INTEGER NOT NULL DEFAULT 0, journal_text TEXT NOT NULL DEFAULT '', updated_utc TEXT NULL, UNIQUE(journal_id, review_date))",
            "CREATE INDEX IF NOT EXISTS ix_daily_review_journals_journal_date ON daily_review_journals(journal_id, review_date)",
            "CREATE TABLE IF NOT EXISTS daily_review_journal_history (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, review_date TEXT NOT NULL, revision INTEGER NOT NULL, action TEXT NOT NULL, before_json TEXT NOT NULL DEFAULT '{}', after_json TEXT NOT NULL DEFAULT '{}', reason TEXT NOT NULL DEFAULT '', created_utc TEXT NOT NULL)",
            "CREATE INDEX IF NOT EXISTS ix_daily_review_journal_history_date ON daily_review_journal_history(journal_id, review_date, revision DESC)",
            "CREATE TABLE IF NOT EXISTS order_events (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id), import_batch_id TEXT NOT NULL REFERENCES import_batches(id), source_type TEXT NOT NULL, source_key TEXT NOT NULL, order_action_source TEXT NOT NULL DEFAULT '', event_utc TEXT NOT NULL, transaction_utc TEXT NULL, source_time_text TEXT NOT NULL DEFAULT '', symbol TEXT NOT NULL DEFAULT '', account TEXT NOT NULL DEFAULT '', internal_order_id TEXT NOT NULL DEFAULT '', service_order_id TEXT NOT NULL DEFAULT '', parent_order_id TEXT NOT NULL DEFAULT '', exchange_order_id TEXT NOT NULL DEFAULT '', fill_execution_id TEXT NOT NULL DEFAULT '', order_type TEXT NOT NULL DEFAULT '', order_status TEXT NOT NULL DEFAULT '', side TEXT NOT NULL DEFAULT '', open_close TEXT NOT NULL DEFAULT '', price TEXT NULL, price2 TEXT NULL, quantity INTEGER NULL, filled_quantity INTEGER NULL, fill_price TEXT NULL, position_quantity INTEGER NULL, note TEXT NOT NULL DEFAULT '', client_order_id TEXT NOT NULL DEFAULT '', time_in_force TEXT NOT NULL DEFAULT '', username TEXT NOT NULL DEFAULT '', is_automated INTEGER NULL, fees TEXT NOT NULL DEFAULT '0', row_number INTEGER NOT NULL, instrument TEXT NOT NULL DEFAULT '', UNIQUE(journal_id, source_type, source_key))",
            "CREATE INDEX IF NOT EXISTS ix_order_events_journal_order_time ON order_events(journal_id, account, symbol, internal_order_id, event_utc, row_number)",
            "CREATE INDEX IF NOT EXISTS ix_order_events_journal_parent ON order_events(journal_id, parent_order_id, event_utc)",
            "CREATE TABLE IF NOT EXISTS account_balance_events (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id), import_batch_id TEXT NOT NULL REFERENCES import_batches(id), source_type TEXT NOT NULL, source_key TEXT NOT NULL, event_utc TEXT NOT NULL, transaction_utc TEXT NULL, source_time_text TEXT NOT NULL DEFAULT '', account TEXT NOT NULL DEFAULT '', balance TEXT NULL, note TEXT NOT NULL DEFAULT '', row_number INTEGER NOT NULL, UNIQUE(journal_id, source_type, source_key))",
            "CREATE INDEX IF NOT EXISTS ix_account_balance_events_journal_time ON account_balance_events(journal_id, account, event_utc, row_number)",
            "CREATE TABLE IF NOT EXISTS account_transactions (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, transaction_type TEXT NOT NULL CHECK(transaction_type IN ('deposit', 'withdrawal')), effective_utc TEXT NOT NULL, amount TEXT NOT NULL, note TEXT NOT NULL DEFAULT '', revision INTEGER NOT NULL DEFAULT 1, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL, deleted_utc TEXT NULL)",
            "CREATE INDEX IF NOT EXISTS ix_account_transactions_journal_time ON account_transactions(journal_id, effective_utc, created_utc, id)",
            "CREATE TABLE IF NOT EXISTS account_transaction_history (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, transaction_id TEXT NOT NULL REFERENCES account_transactions(id) ON DELETE CASCADE, revision INTEGER NOT NULL, action TEXT NOT NULL, before_json TEXT NOT NULL DEFAULT '{}', after_json TEXT NOT NULL DEFAULT '{}', created_utc TEXT NOT NULL)",
            "CREATE INDEX IF NOT EXISTS ix_account_transaction_history_transaction ON account_transaction_history(journal_id, transaction_id, revision DESC)",
            "CREATE TABLE IF NOT EXISTS benchmark_series (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, symbol TEXT NOT NULL, interval TEXT NOT NULL DEFAULT '1d', series_key TEXT NOT NULL UNIQUE, created_utc TEXT NOT NULL, provider TEXT NOT NULL DEFAULT 'Imported CSV', source_url TEXT NOT NULL DEFAULT '', last_fetched_utc TEXT NULL, last_attempted_utc TEXT NULL, requested_start TEXT NULL, requested_end TEXT NULL, last_error TEXT NOT NULL DEFAULT '')",
            "CREATE TABLE IF NOT EXISTS benchmark_points (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, series_id TEXT NOT NULL REFERENCES benchmark_series(id) ON DELETE CASCADE, import_batch_id TEXT NULL REFERENCES import_batches(id) ON DELETE SET NULL, source_type TEXT NOT NULL, source_key TEXT NOT NULL, event_utc TEXT NOT NULL, value TEXT NOT NULL, source_time_text TEXT NOT NULL DEFAULT '', row_number INTEGER NOT NULL, UNIQUE(journal_id, source_type, source_key), UNIQUE(series_id, event_utc))",
            "CREATE INDEX IF NOT EXISTS ix_benchmark_points_series_time ON benchmark_points(series_id, event_utc)",
            "CREATE TABLE IF NOT EXISTS bar_series (id TEXT PRIMARY KEY, symbol TEXT NOT NULL, interval TEXT NOT NULL, series_key TEXT NOT NULL UNIQUE, created_utc TEXT NOT NULL)",
            "CREATE INDEX IF NOT EXISTS ix_bar_series_symbol_interval ON bar_series(symbol, interval)",
            "CREATE TABLE IF NOT EXISTS journal_bar_series (journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, series_id TEXT NOT NULL REFERENCES bar_series(id) ON DELETE CASCADE, PRIMARY KEY(journal_id, series_id))",
            "CREATE INDEX IF NOT EXISTS ix_journal_bar_series_series ON journal_bar_series(series_id, journal_id)",
            "CREATE TABLE IF NOT EXISTS bars (id TEXT PRIMARY KEY, series_id TEXT NOT NULL REFERENCES bar_series(id) ON DELETE CASCADE, import_batch_id TEXT NULL REFERENCES import_batches(id) ON DELETE SET NULL, event_utc TEXT NOT NULL, open TEXT NOT NULL, high TEXT NOT NULL, low TEXT NOT NULL, close TEXT NOT NULL, volume INTEGER NULL, number_of_trades INTEGER NULL, bid_volume INTEGER NULL, ask_volume INTEGER NULL, UNIQUE(series_id, event_utc))",
            "CREATE INDEX IF NOT EXISTS ix_bars_series_time ON bars(series_id, event_utc)",
            "CREATE TABLE IF NOT EXISTS bar_imports (import_batch_id TEXT NOT NULL REFERENCES import_batches(id) ON DELETE CASCADE, bar_id TEXT NOT NULL REFERENCES bars(id) ON DELETE CASCADE, PRIMARY KEY(import_batch_id, bar_id))",
            "CREATE INDEX IF NOT EXISTS ix_bar_imports_bar ON bar_imports(bar_id)",
            "CREATE TABLE IF NOT EXISTS mcp_access_tokens (id TEXT PRIMARY KEY, owner_user_id TEXT NOT NULL REFERENCES app_users(id), name TEXT NOT NULL, token_prefix TEXT NOT NULL, token_hash TEXT NOT NULL UNIQUE, scopes TEXT NOT NULL DEFAULT 'read', created_utc TEXT NOT NULL, last_used_utc TEXT NULL, revoked_utc TEXT NULL)",
            "CREATE INDEX IF NOT EXISTS ix_mcp_access_tokens_owner ON mcp_access_tokens(owner_user_id, revoked_utc, created_utc)",
            "CREATE TABLE IF NOT EXISTS mcp_token_journals (token_id TEXT NOT NULL REFERENCES mcp_access_tokens(id) ON DELETE CASCADE, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, PRIMARY KEY(token_id, journal_id))",
            "CREATE INDEX IF NOT EXISTS ix_mcp_token_journals_journal ON mcp_token_journals(journal_id, token_id)"
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
        EnsureColumn(connection, "journals", "description_markdown", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "import_batches", "source_application", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "benchmark_series", "provider", "TEXT NOT NULL DEFAULT 'Imported CSV'");
        EnsureColumn(connection, "benchmark_series", "source_url", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "benchmark_series", "last_fetched_utc", "TEXT NULL");
        EnsureColumn(connection, "benchmark_series", "last_attempted_utc", "TEXT NULL");
        EnsureColumn(connection, "benchmark_series", "requested_start", "TEXT NULL");
        EnsureColumn(connection, "benchmark_series", "requested_end", "TEXT NULL");
        EnsureColumn(connection, "benchmark_series", "last_error", "TEXT NOT NULL DEFAULT ''");
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
        EnsureColumn(connection, "fills", "instrument", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "fills", "point_value", "TEXT NOT NULL DEFAULT '0'");
        EnsureColumn(connection, "fills", "tick_size", "TEXT NOT NULL DEFAULT '0'");
        EnsureColumn(connection, "order_events", "order_action_source", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "order_events", "service_order_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "order_events", "instrument", "TEXT NOT NULL DEFAULT ''");
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
        EnsureColumn(connection, "trades", "instrument", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "trades", "review_key", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "trade_review_annotations", "all_in_commission", "TEXT NULL");
        EnsureColumn(connection, "bars", "number_of_trades", "INTEGER NULL");
        EnsureColumn(connection, "bars", "bid_volume", "INTEGER NULL");
        EnsureColumn(connection, "bars", "ask_volume", "INTEGER NULL");

        BackfillTradeReviewKeys(connection);
        using (var reviewKeyIndex = connection.CreateCommand())
        {
            reviewKeyIndex.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ix_trades_journal_review_key ON trades(journal_id, review_key) WHERE review_key <> ''";
            reviewKeyIndex.ExecuteNonQuery();
        }

        SeedInstrumentConfiguration(connection);
        }

        // Older Sierra imports may have persisted fixed-point prices before
        // the importer could identify the display price in OrderActionSource.
        // Repair normalized values from immutable raw_records at startup, then
        // rebuild the derived trades that depend on them.
        RepairSierraPriceScales();
    }

    private static void SeedInstrumentConfiguration(SqliteConnection connection)
    {
        using (var marker = connection.CreateCommand())
        {
            marker.CommandText = "SELECT settings_value FROM app_settings WHERE settings_key = 'instrument_configuration_seeded'";
            if (marker.ExecuteScalar() is not null) return;
        }

        using var transaction = connection.BeginTransaction();
        foreach (var spec in InstrumentCatalog.Defaults)
        {
            using var instrument = connection.CreateCommand();
            instrument.Transaction = transaction;
            instrument.CommandText = "INSERT OR IGNORE INTO instruments (id, code, default_commission, point_value, tick_size, created_utc) VALUES ($id, $code, NULL, $pointValue, $tickSize, $created)";
            instrument.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            instrument.Parameters.AddWithValue("$code", spec.Root);
            instrument.Parameters.AddWithValue("$pointValue", NumberFormat.Decimal(spec.PointValue));
            instrument.Parameters.AddWithValue("$tickSize", NumberFormat.Decimal(spec.TickSize));
            instrument.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            instrument.ExecuteNonQuery();

            var pattern = $"(?<![A-Z0-9]){spec.Root}(?:[FGHJKMNQUVXZ]\\d{{1,4}})?(?![A-Z0-9])";
            using var mapping = connection.CreateCommand();
            mapping.Transaction = transaction;
            mapping.CommandText = "INSERT OR IGNORE INTO source_instrument_mappings (id, application_key, match_regex, instrument_code, commission_override, position, created_utc) VALUES ($id, $application, $regex, $instrument, NULL, $position, $created)";
            mapping.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            mapping.Parameters.AddWithValue("$application", TradeFoundryConstants.SierraChart);
            mapping.Parameters.AddWithValue("$regex", pattern);
            mapping.Parameters.AddWithValue("$instrument", spec.Root);
            mapping.Parameters.AddWithValue("$position", Array.IndexOf(InstrumentCatalog.Defaults.ToArray(), spec));
            mapping.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            mapping.ExecuteNonQuery();
        }

        using (var marker = connection.CreateCommand())
        {
            marker.Transaction = transaction;
            marker.CommandText = "INSERT INTO app_settings (settings_key, settings_value) VALUES ('instrument_configuration_seeded', '1')";
            marker.ExecuteNonQuery();
        }
        transaction.Commit();
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

    public bool UpdateOwnerPasswordHash(string passwordHash)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE app_users SET password_hash = $hash WHERE id = $id";
        command.Parameters.AddWithValue("$hash", passwordHash);
        command.Parameters.AddWithValue("$id", TradeFoundryConstants.OwnerUserId);
        return command.ExecuteNonQuery() == 1;
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

    public McpAccessToken CreateMcpAccessToken(string name, string tokenPrefix, string tokenHash, IReadOnlyCollection<Guid> journalIds)
    {
        var cleanName = string.IsNullOrWhiteSpace(name) ? "Local AI client" : name.Trim();
        if (cleanName.Length > 100) cleanName = cleanName[..100];
        var allowed = journalIds.Distinct().ToArray();
        if (allowed.Length == 0) throw new InvalidOperationException("Select at least one journal for the MCP token.");

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var journalId in allowed)
        {
            using var exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandText = "SELECT EXISTS (SELECT 1 FROM journals WHERE id = $id AND owner_user_id = $owner AND archived = 0)";
            exists.Parameters.AddWithValue("$id", journalId.ToString("D"));
            exists.Parameters.AddWithValue("$owner", TradeFoundryConstants.OwnerUserId);
            if (Convert.ToInt32(exists.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("One or more selected journals were not found.");
        }

        var id = Guid.NewGuid();
        var created = DateTimeOffset.UtcNow;
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO mcp_access_tokens (id, owner_user_id, name, token_prefix, token_hash, scopes, created_utc) VALUES ($id, $owner, $name, $prefix, $hash, 'read', $created)";
            insert.Parameters.AddWithValue("$id", id.ToString("D"));
            insert.Parameters.AddWithValue("$owner", TradeFoundryConstants.OwnerUserId);
            insert.Parameters.AddWithValue("$name", cleanName);
            insert.Parameters.AddWithValue("$prefix", tokenPrefix);
            insert.Parameters.AddWithValue("$hash", tokenHash);
            insert.Parameters.AddWithValue("$created", created.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }
        foreach (var journalId in allowed)
        {
            using var grant = connection.CreateCommand();
            grant.Transaction = transaction;
            grant.CommandText = "INSERT INTO mcp_token_journals (token_id, journal_id) VALUES ($token, $journal)";
            grant.Parameters.AddWithValue("$token", id.ToString("D"));
            grant.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            grant.ExecuteNonQuery();
        }
        transaction.Commit();
        return new McpAccessToken { Id = id, Name = cleanName, TokenPrefix = tokenPrefix, Scopes = "read", CreatedUtc = created, JournalIds = allowed };
    }

    public IReadOnlyList<McpAccessToken> GetMcpAccessTokens()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, token_prefix, scopes, created_utc, last_used_utc, revoked_utc FROM mcp_access_tokens WHERE owner_user_id = $owner ORDER BY created_utc DESC";
        command.Parameters.AddWithValue("$owner", TradeFoundryConstants.OwnerUserId);
        var tokens = new List<McpAccessToken>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) tokens.Add(ReadMcpAccessToken(reader));
        }
        return tokens.Select(token => WithJournalIds(token, ReadMcpTokenJournalIds(connection, token.Id))).ToArray();
    }

    public McpAccessToken? FindActiveMcpAccessToken(string tokenHash)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, token_prefix, scopes, created_utc, last_used_utc, revoked_utc FROM mcp_access_tokens WHERE token_hash = $hash AND revoked_utc IS NULL LIMIT 1";
        command.Parameters.AddWithValue("$hash", tokenHash);
        McpAccessToken? token;
        using (var reader = command.ExecuteReader()) token = reader.Read() ? ReadMcpAccessToken(reader) : null;
        if (token is null) return null;
        return WithJournalIds(token, ReadMcpTokenJournalIds(connection, token.Id));
    }

    public bool RevokeMcpAccessToken(Guid tokenId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE mcp_access_tokens SET revoked_utc = $revoked WHERE id = $id AND owner_user_id = $owner AND revoked_utc IS NULL";
        command.Parameters.AddWithValue("$revoked", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", tokenId.ToString("D"));
        command.Parameters.AddWithValue("$owner", TradeFoundryConstants.OwnerUserId);
        return command.ExecuteNonQuery() == 1;
    }

    public void RecordMcpAccessTokenUsed(Guid tokenId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var now = DateTimeOffset.UtcNow;
        command.CommandText = "UPDATE mcp_access_tokens SET last_used_utc = $now WHERE id = $id AND revoked_utc IS NULL AND (last_used_utc IS NULL OR last_used_utc < $cutoff)";
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$cutoff", now.AddMinutes(-5).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", tokenId.ToString("D"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<Journal> GetJournalsForMcpToken(Guid tokenId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT j.id, j.name, j.execution_context, j.labels, j.description_markdown, j.timezone, j.currency, j.grouping_policy, j.starting_equity, j.created_utc FROM journals j JOIN mcp_token_journals g ON g.journal_id = j.id JOIN mcp_access_tokens t ON t.id = g.token_id WHERE t.id = $token AND t.revoked_utc IS NULL AND j.archived = 0 ORDER BY j.created_utc";
        command.Parameters.AddWithValue("$token", tokenId.ToString("D"));
        var journals = new List<Journal>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) journals.Add(ReadJournal(reader));
        return journals;
    }

    public Journal CreateJournal(string name, string executionContext, string labels, string timeZone, string currency, string groupingPolicy, decimal? startingEquity = null, string descriptionMarkdown = "")
    {
        var journal = new Journal
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? "My futures journal" : name.Trim(),
            ExecutionContext = string.IsNullOrWhiteSpace(executionContext) ? "live" : executionContext.Trim().ToLowerInvariant(),
            Labels = labels?.Trim() ?? string.Empty,
            DescriptionMarkdown = descriptionMarkdown?.Trim() ?? string.Empty,
            TimeZone = string.IsNullOrWhiteSpace(timeZone) ? "UTC" : timeZone.Trim(),
            Currency = string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant(),
            GroupingPolicy = string.IsNullOrWhiteSpace(groupingPolicy) ? "flat_to_flat" : groupingPolicy.Trim(),
            StartingEquity = startingEquity,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO journals (id, owner_user_id, name, execution_context, labels, description_markdown, timezone, currency, grouping_policy, starting_equity, created_utc) VALUES ($id, $owner, $name, $context, $labels, $description, $timezone, $currency, $grouping, $startingEquity, $created)";
        command.Parameters.AddWithValue("$id", journal.Id.ToString("D"));
        command.Parameters.AddWithValue("$owner", TradeFoundryConstants.OwnerUserId);
        command.Parameters.AddWithValue("$name", journal.Name);
        command.Parameters.AddWithValue("$context", journal.ExecutionContext);
        command.Parameters.AddWithValue("$labels", journal.Labels);
        command.Parameters.AddWithValue("$description", journal.DescriptionMarkdown);
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

    public bool UpdateJournal(Guid journalId, string name, string executionContext, string labels, string timeZone, string currency, string groupingPolicy, decimal? startingEquity = null, string descriptionMarkdown = "")
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE journals SET name = $name, execution_context = $context, labels = $labels, description_markdown = $description, timezone = $timezone, currency = $currency, grouping_policy = $grouping, starting_equity = $startingEquity WHERE id = $id AND owner_user_id = $owner AND archived = 0";
        command.Parameters.AddWithValue("$id", journalId.ToString("D"));
        command.Parameters.AddWithValue("$owner", TradeFoundryConstants.OwnerUserId);
        command.Parameters.AddWithValue("$name", string.IsNullOrWhiteSpace(name) ? "My futures journal" : name.Trim());
        command.Parameters.AddWithValue("$context", string.IsNullOrWhiteSpace(executionContext) ? "live" : executionContext.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("$labels", labels?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$description", descriptionMarkdown?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$timezone", string.IsNullOrWhiteSpace(timeZone) ? "UTC" : timeZone.Trim());
        command.Parameters.AddWithValue("$currency", string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$grouping", string.IsNullOrWhiteSpace(groupingPolicy) ? "flat_to_flat" : groupingPolicy.Trim());
        AddNullable(command, "$startingEquity", startingEquity);
        return command.ExecuteNonQuery() == 1;
    }

    public IReadOnlyList<InstrumentDefinition> GetInstruments()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, code, default_commission, point_value, tick_size FROM instruments ORDER BY code";
        using var reader = command.ExecuteReader();
        var instruments = new List<InstrumentDefinition>();
        while (reader.Read())
        {
            instruments.Add(new InstrumentDefinition
            {
                Id = Guid.Parse(reader.GetString(0)),
                Code = reader.GetString(1),
                DefaultCommission = NullableDecimal(reader, 2),
                PointValue = ParseDecimal(reader.GetString(3)),
                TickSize = ParseDecimal(reader.GetString(4))
            });
        }
        return instruments;
    }

    public IReadOnlyList<InstrumentMapping> GetInstrumentMappings(string? applicationKey = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var filter = string.IsNullOrWhiteSpace(applicationKey) ? string.Empty : " WHERE application_key = $application";
        command.CommandText = $"SELECT id, application_key, match_regex, instrument_code, commission_override, position FROM source_instrument_mappings{filter} ORDER BY application_key, position, id";
        if (!string.IsNullOrWhiteSpace(applicationKey)) command.Parameters.AddWithValue("$application", applicationKey.Trim());
        using var reader = command.ExecuteReader();
        var mappings = new List<InstrumentMapping>();
        while (reader.Read())
        {
            mappings.Add(new InstrumentMapping
            {
                Id = Guid.Parse(reader.GetString(0)),
                ApplicationKey = reader.GetString(1),
                MatchRegex = reader.GetString(2),
                InstrumentCode = reader.GetString(3),
                CommissionOverride = NullableDecimal(reader, 4),
                Position = reader.GetInt32(5)
            });
        }
        return mappings;
    }

    public InstrumentConfiguration GetInstrumentConfiguration() => new(GetInstruments(), GetInstrumentMappings());

    public bool SaveInstrument(Guid? instrumentId, string code, decimal? defaultCommission, decimal pointValue, decimal tickSize)
    {
        code = InstrumentConfiguration.NormalizeCode(code);
        if (string.IsNullOrWhiteSpace(code) || code.Length > 32 || pointValue <= 0m || tickSize < 0m || defaultCommission is < 0m) return false;

        using var connection = OpenConnection();
        try
        {
            if (instrumentId.HasValue && instrumentId.Value != Guid.Empty)
            {
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE instruments SET code = $code, default_commission = $commission, point_value = $pointValue, tick_size = $tickSize WHERE id = $id";
                update.Parameters.AddWithValue("$id", instrumentId.Value.ToString("D"));
                update.Parameters.AddWithValue("$code", code);
                AddNullable(update, "$commission", defaultCommission);
                update.Parameters.AddWithValue("$pointValue", NumberFormat.Decimal(pointValue));
                update.Parameters.AddWithValue("$tickSize", NumberFormat.Decimal(tickSize));
                return update.ExecuteNonQuery() == 1;
            }

            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO instruments (id, code, default_commission, point_value, tick_size, created_utc) VALUES ($id, $code, $commission, $pointValue, $tickSize, $created)";
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            insert.Parameters.AddWithValue("$code", code);
            AddNullable(insert, "$commission", defaultCommission);
            insert.Parameters.AddWithValue("$pointValue", NumberFormat.Decimal(pointValue));
            insert.Parameters.AddWithValue("$tickSize", NumberFormat.Decimal(tickSize));
            insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            return insert.ExecuteNonQuery() == 1;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    public bool DeleteInstrument(Guid instrumentId)
    {
        using var connection = OpenConnection();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM instruments WHERE id = $id";
            command.Parameters.AddWithValue("$id", instrumentId.ToString("D"));
            return command.ExecuteNonQuery() == 1;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    public bool SaveInstrumentMapping(Guid? mappingId, string applicationKey, string matchRegex, string instrumentCode, decimal? commissionOverride, int position)
    {
        applicationKey = (applicationKey ?? string.Empty).Trim().ToLowerInvariant();
        matchRegex = (matchRegex ?? string.Empty).Trim();
        instrumentCode = InstrumentConfiguration.NormalizeCode(instrumentCode);
        if (string.IsNullOrWhiteSpace(applicationKey) || string.IsNullOrWhiteSpace(matchRegex) || matchRegex.Length > 512 || commissionOverride is < 0m || position < 0) return false;
        if (!InstrumentConfiguration.IsValidMatchRegex(matchRegex)) return false;

        using var connection = OpenConnection();
        using (var instrument = connection.CreateCommand())
        {
            instrument.CommandText = "SELECT EXISTS (SELECT 1 FROM instruments WHERE code = $code)";
            instrument.Parameters.AddWithValue("$code", instrumentCode);
            if (Convert.ToInt32(instrument.ExecuteScalar(), CultureInfo.InvariantCulture) != 1) return false;
        }

        try
        {
            if (mappingId.HasValue && mappingId.Value != Guid.Empty)
            {
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE source_instrument_mappings SET application_key = $application, match_regex = $regex, instrument_code = $instrument, commission_override = $commission, position = $position WHERE id = $id";
                update.Parameters.AddWithValue("$id", mappingId.Value.ToString("D"));
                update.Parameters.AddWithValue("$application", applicationKey);
                update.Parameters.AddWithValue("$regex", matchRegex);
                update.Parameters.AddWithValue("$instrument", instrumentCode);
                AddNullable(update, "$commission", commissionOverride);
                update.Parameters.AddWithValue("$position", position);
                return update.ExecuteNonQuery() == 1;
            }

            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO source_instrument_mappings (id, application_key, match_regex, instrument_code, commission_override, position, created_utc) VALUES ($id, $application, $regex, $instrument, $commission, $position, $created)";
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            insert.Parameters.AddWithValue("$application", applicationKey);
            insert.Parameters.AddWithValue("$regex", matchRegex);
            insert.Parameters.AddWithValue("$instrument", instrumentCode);
            AddNullable(insert, "$commission", commissionOverride);
            insert.Parameters.AddWithValue("$position", position);
            insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            return insert.ExecuteNonQuery() == 1;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    public bool DeleteInstrumentMapping(Guid mappingId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM source_instrument_mappings WHERE id = $id";
        command.Parameters.AddWithValue("$id", mappingId.ToString("D"));
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
            command.CommandText = $"SELECT COALESCE(SUM(CAST(t.gross_pnl AS REAL)), 0), COALESCE(SUM({EffectiveNetPnlSql}), 0), COALESCE(SUM(CAST(t.gross_points AS REAL)), 0), COALESCE(SUM(CAST({EffectiveFeesSql} AS REAL)), 0), COALESCE(SUM(CASE WHEN t.status = 'closed' AND {EffectiveNetPnlSql} > 0 THEN {EffectiveNetPnlSql} ELSE 0 END), 0), COALESCE(SUM(CASE WHEN t.status = 'closed' AND {EffectiveNetPnlSql} < 0 THEN {EffectiveNetPnlSql} ELSE 0 END), 0), SUM(CASE WHEN t.status = 'closed' THEN 1 ELSE 0 END), SUM(CASE WHEN t.status <> 'closed' THEN 1 ELSE 0 END), SUM(CASE WHEN t.status = 'closed' AND {EffectiveNetPnlSql} > 0 THEN 1 ELSE 0 END), SUM(CASE WHEN t.status = 'closed' AND {EffectiveNetPnlSql} < 0 THEN 1 ELSE 0 END) FROM {TradeFrom} WHERE t.journal_id = $journal";
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
            command.CommandText = $"SELECT {TradeColumns} FROM {TradeFrom} WHERE t.journal_id = $journal ORDER BY COALESCE(t.exit_utc, t.entry_utc) DESC, t.sequence DESC LIMIT 8";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read()) recentTrades.Add(ReadTrade(reader));
        }

        var recentImports = new List<ImportBatch>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {ImportColumns} FROM import_batches WHERE journal_id = $journal ORDER BY imported_utc DESC LIMIT 8";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read()) recentImports.Add(ReadImport(reader));
        }

        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT DISTINCT t.symbol FROM {TradeFrom} WHERE t.journal_id = $journal";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read()) symbols.Add(reader.GetString(0));
        }
        foreach (var symbol in GetFillSymbols(connection, journalId)) symbols.Add(symbol);

        var dailyPnl = new List<DailyPnl>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT substr(t.exit_utc, 1, 10), SUM({EffectiveNetPnlSql}), COUNT(*) FROM {TradeFrom} WHERE t.journal_id = $journal AND t.exit_utc IS NOT NULL GROUP BY substr(t.exit_utc, 1, 10) ORDER BY substr(t.exit_utc, 1, 10) DESC LIMIT 180";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read()) dailyPnl.Add(new DailyPnl { Date = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture), NetPnl = AggregateDecimal(reader, 1), TradeCount = AggregateInt(reader, 2) });
        }
        dailyPnl.Reverse();

        var equity = new List<EquityPoint>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"WITH running AS (SELECT t.id, t.exit_utc, t.sequence, SUM({EffectiveNetPnlSql}) OVER (ORDER BY t.exit_utc, t.sequence, t.id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS cumulative_pnl FROM {TradeFrom} WHERE t.journal_id = $journal AND t.exit_utc IS NOT NULL) SELECT exit_utc, cumulative_pnl FROM running ORDER BY exit_utc DESC, sequence DESC, id DESC LIMIT 180";
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
        filters.Add("t.journal_id = $journal");
        count.Parameters.AddWithValue("$journal", query.JournalId.ToString("D"));
        AddTradeFilter(filters, count, query);
        count.CommandText = $"SELECT COUNT(*) FROM {TradeFrom} WHERE {string.Join(" AND ", filters)}";
        var totalCount = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);

        var sort = query.Sort switch
        {
            "entry_asc" => "t.entry_utc ASC, t.sequence ASC, t.id ASC",
            "pnl_desc" => $"{EffectiveNetPnlSql} DESC, t.entry_utc DESC, t.sequence DESC, t.id DESC",
            "pnl_asc" => $"{EffectiveNetPnlSql} ASC, t.entry_utc DESC, t.sequence DESC, t.id DESC",
            _ => "t.entry_utc DESC, t.sequence DESC, t.id DESC"
        };
        var trades = new List<Trade>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {TradeColumns} FROM {TradeFrom} WHERE {string.Join(" AND ", filters)} ORDER BY {sort} LIMIT $limit OFFSET $offset";
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
        command.CommandText = $"SELECT {TradeColumns} FROM {TradeFrom} WHERE t.journal_id = $journal ORDER BY COALESCE(t.exit_utc, t.entry_utc), t.sequence, t.id";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        var trades = new List<Trade>();
        while (reader.Read()) trades.Add(ReadTrade(reader));
        return trades;
    }

    public IReadOnlyDictionary<string, TradeReviewAnnotation> GetTradeReviewAnnotations(Guid journalId)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        var annotations = new Dictionary<string, TradeReviewAnnotation>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {TradeReviewColumns} FROM trade_review_annotations WHERE journal_id = $journal";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var annotation = ReadTradeReview(reader);
                annotations[annotation.ReviewKey] = annotation;
            }
        }

        // A source-removal tombstone has no active row, but its revision still
        // matters when a later import reintroduces the same stable review key.
        using (var history = connection.CreateCommand())
        {
            history.CommandText = "SELECT review_key, MAX(revision) FROM trade_review_history WHERE journal_id = $journal GROUP BY review_key";
            history.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            using var reader = history.ExecuteReader();
            while (reader.Read())
            {
                var reviewKey = reader.GetString(0);
                if (!annotations.ContainsKey(reviewKey))
                {
                    annotations[reviewKey] = EmptyTradeReview(journalId, reviewKey, reader.GetInt32(1));
                }
            }
        }

        return annotations;
    }

    public TradeReviewAnnotation? GetTradeReview(Guid journalId, string reviewKey)
    {
        if (string.IsNullOrWhiteSpace(reviewKey)) return null;
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {TradeReviewColumns} FROM trade_review_annotations WHERE journal_id = $journal AND review_key = $reviewKey";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            command.Parameters.AddWithValue("$reviewKey", reviewKey);
            using var reader = command.ExecuteReader();
            if (reader.Read()) return ReadTradeReview(reader);
        }

        using var history = connection.CreateCommand();
        history.CommandText = "SELECT MAX(revision) FROM trade_review_history WHERE journal_id = $journal AND review_key = $reviewKey";
        history.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        history.Parameters.AddWithValue("$reviewKey", reviewKey);
        var value = history.ExecuteScalar();
        return value is null or DBNull ? null : EmptyTradeReview(journalId, reviewKey, Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }

    public IReadOnlyList<TradeReviewHistoryEntry> GetTradeReviewHistory(Guid journalId, string reviewKey)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, journal_id, review_key, revision, action, before_json, after_json, reason, created_utc FROM trade_review_history WHERE journal_id = $journal AND review_key = $reviewKey ORDER BY revision DESC, created_utc DESC";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$reviewKey", reviewKey);
        using var reader = command.ExecuteReader();
        var history = new List<TradeReviewHistoryEntry>();
        while (reader.Read()) history.Add(ReadTradeReviewHistory(reader));
        return history;
    }

    public TradeReviewSaveResult SaveTradeReview(Guid journalId, string reviewKey, TradeReviewPatch patch, string action = "saved")
    {
        if (string.IsNullOrWhiteSpace(reviewKey)) throw new ArgumentException("A review key is required.", nameof(reviewKey));
        if (patch.ExpectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(patch.ExpectedRevision));
        if (patch.ProcessRating is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(patch.ProcessRating), "Process rating must be between 1 and 5.");
        if (patch.PlannedRiskPoints is < 0m || patch.PlannedRiskCurrency is < 0m) throw new ArgumentOutOfRangeException(nameof(patch), "Planned risk cannot be negative.");
        if (patch.AllInCommission is < 0m) throw new ArgumentOutOfRangeException(nameof(patch.AllInCommission), "All-in commission cannot be negative.");
        if (!string.IsNullOrWhiteSpace(patch.PlanAdherence) && !new[] { "adhered", "partial", "broken" }.Contains(patch.PlanAdherence, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Plan adherence must be Adhered, Partial, or Broken.", nameof(patch));
        _ = GetTradeByReviewKey(journalId, reviewKey) ?? throw new InvalidOperationException("The trade for this review could not be found.");

        var normalized = NormalizeReviewPatch(patch);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var current = ReadTradeReviewOrHistoryRevision(connection, transaction, journalId, reviewKey);
        if (patch.ExpectedRevision != current.Revision)
        {
            InsertTradeReviewHistory(connection, transaction, journalId, reviewKey, current.Revision, "conflict", SerializeReview(current), JsonSerializer.Serialize(normalized), "Stale review revision.", DateTimeOffset.UtcNow);
            transaction.Commit();
            return new TradeReviewSaveResult { Conflict = true, Annotation = current };
        }

        var now = DateTimeOffset.UtcNow;
        var annotation = AnnotationFromPatch(journalId, reviewKey, current.Revision + 1, normalized, now);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO trade_review_annotations (id, journal_id, review_key, revision, review_note, setup, tags_json, planned_entry_price, planned_stop_price, planned_target_price, planned_risk_points, planned_risk_currency, all_in_commission, plan_adherence, process_rating, mistakes, lessons, updated_utc) VALUES ($id, $journal, $reviewKey, $revision, $note, $setup, $tags, $plannedEntry, $plannedStop, $plannedTarget, $riskPoints, $riskCurrency, $allInCommission, $adherence, $rating, $mistakes, $lessons, $updated) ON CONFLICT(journal_id, review_key) DO UPDATE SET revision = excluded.revision, review_note = excluded.review_note, setup = excluded.setup, tags_json = excluded.tags_json, planned_entry_price = excluded.planned_entry_price, planned_stop_price = excluded.planned_stop_price, planned_target_price = excluded.planned_target_price, planned_risk_points = excluded.planned_risk_points, planned_risk_currency = excluded.planned_risk_currency, all_in_commission = excluded.all_in_commission, plan_adherence = excluded.plan_adherence, process_rating = excluded.process_rating, mistakes = excluded.mistakes, lessons = excluded.lessons, updated_utc = excluded.updated_utc";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            command.Parameters.AddWithValue("$reviewKey", reviewKey);
            command.Parameters.AddWithValue("$revision", annotation.Revision);
            command.Parameters.AddWithValue("$note", annotation.ReviewNote);
            command.Parameters.AddWithValue("$setup", annotation.Setup);
            command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(annotation.Tags));
            AddNullable(command, "$plannedEntry", annotation.PlannedEntryPrice);
            AddNullable(command, "$plannedStop", annotation.PlannedStopPrice);
            AddNullable(command, "$plannedTarget", annotation.PlannedTargetPrice);
            AddNullable(command, "$riskPoints", annotation.PlannedRiskPoints);
            AddNullable(command, "$riskCurrency", annotation.PlannedRiskCurrency);
            AddNullable(command, "$allInCommission", annotation.AllInCommission);
            command.Parameters.AddWithValue("$adherence", annotation.PlanAdherence);
            AddNullable(command, "$rating", annotation.ProcessRating);
            command.Parameters.AddWithValue("$mistakes", annotation.Mistakes);
            command.Parameters.AddWithValue("$lessons", annotation.Lessons);
            command.Parameters.AddWithValue("$updated", annotation.UpdatedUtc!.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        InsertTradeReviewHistory(connection, transaction, journalId, reviewKey, annotation.Revision, action, SerializeReview(current), SerializeReview(annotation), normalized.Reason, now);
        transaction.Commit();
        return new TradeReviewSaveResult { Saved = true, Annotation = annotation };
    }

    public TradeReviewSaveResult RevertTradeReview(Guid journalId, string reviewKey, int expectedRevision, int targetRevision, string reason = "Reverted review")
    {
        var target = GetTradeReviewHistory(journalId, reviewKey).FirstOrDefault(x => x.Revision == targetRevision && !x.Action.Equals("conflict", StringComparison.OrdinalIgnoreCase));
        if (target is null) throw new InvalidOperationException("The selected review history entry could not be found.");
        TradeReviewAnnotation restored;
        try
        {
            restored = string.IsNullOrWhiteSpace(target.AfterJson) || target.AfterJson == "{}"
                ? EmptyTradeReview(journalId, reviewKey)
                : JsonSerializer.Deserialize<TradeReviewAnnotation>(target.AfterJson) ?? EmptyTradeReview(journalId, reviewKey);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The selected review history entry is not readable.");
        }

        return SaveTradeReview(journalId, reviewKey, PatchFromAnnotation(restored, expectedRevision, reason), "reverted");
    }

    public IReadOnlyList<TradeReviewAttachment> GetTradeReviewAttachments(Guid journalId, string reviewKey)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, journal_id, review_key, storage_key, original_file_name, content_type, length, created_utc FROM trade_review_attachments WHERE journal_id = $journal AND review_key = $reviewKey AND removed_utc IS NULL ORDER BY created_utc DESC";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$reviewKey", reviewKey);
        using var reader = command.ExecuteReader();
        var attachments = new List<TradeReviewAttachment>();
        while (reader.Read()) attachments.Add(ReadTradeReviewAttachment(reader));
        return attachments;
    }

    public TradeReviewAttachment? GetTradeReviewAttachment(Guid journalId, Guid attachmentId)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, journal_id, review_key, storage_key, original_file_name, content_type, length, created_utc FROM trade_review_attachments WHERE id = $id AND journal_id = $journal AND removed_utc IS NULL";
        command.Parameters.AddWithValue("$id", attachmentId.ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTradeReviewAttachment(reader) : null;
    }

    public DailyJournalEntry GetDailyJournal(Guid journalId, DateOnly date)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, journal_id, review_date, revision, journal_text, updated_utc FROM daily_review_journals WHERE journal_id = $journal AND review_date = $date";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ReadDailyJournal(reader)
            : new DailyJournalEntry { JournalId = journalId, Date = date };
    }

    public DailyJournalSaveResult SaveDailyJournal(Guid journalId, DateOnly date, string? text, int expectedRevision, string action = "saved")
    {
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");

        var normalizedText = TrimTo(text, 12000);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var current = ReadDailyJournalOrDefault(connection, transaction, journalId, date);
        if (current.Revision != expectedRevision)
        {
            InsertDailyJournalHistory(connection, transaction, journalId, date, current.Revision, "conflict", JsonSerializer.Serialize(current), JsonSerializer.Serialize(new { text = normalizedText }), "Stale daily journal revision.", DateTimeOffset.UtcNow);
            transaction.Commit();
            return new DailyJournalSaveResult { Conflict = true, Entry = current };
        }

        var now = DateTimeOffset.UtcNow;
        var entry = new DailyJournalEntry
        {
            JournalId = journalId,
            Date = date,
            Revision = current.Revision + 1,
            Text = normalizedText,
            UpdatedUtc = now
        };
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO daily_review_journals (id, journal_id, review_date, revision, journal_text, updated_utc) VALUES ($id, $journal, $date, $revision, $text, $updated) ON CONFLICT(journal_id, review_date) DO UPDATE SET revision = excluded.revision, journal_text = excluded.journal_text, updated_utc = excluded.updated_utc";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$revision", entry.Revision);
            command.Parameters.AddWithValue("$text", entry.Text);
            command.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        InsertDailyJournalHistory(connection, transaction, journalId, date, entry.Revision, action, JsonSerializer.Serialize(current), JsonSerializer.Serialize(entry), TrimTo(action, 500), now);
        transaction.Commit();
        return new DailyJournalSaveResult { Saved = true, Entry = entry };
    }

    public TradeReviewAttachment AddTradeReviewAttachment(Guid journalId, string reviewKey, string storageKey, string originalFileName, string contentType, long length)
    {
        if (string.IsNullOrWhiteSpace(reviewKey)) throw new ArgumentException("A review key is required.", nameof(reviewKey));
        if (string.IsNullOrWhiteSpace(storageKey)) throw new ArgumentException("A storage key is required.", nameof(storageKey));
        _ = GetTradeByReviewKey(journalId, reviewKey) ?? throw new InvalidOperationException("The trade for this review could not be found.");
        var attachment = new TradeReviewAttachment
        {
            Id = Guid.NewGuid(), JournalId = journalId, ReviewKey = reviewKey, StorageKey = storageKey,
            OriginalFileName = originalFileName, ContentType = contentType, Length = length, CreatedUtc = DateTimeOffset.UtcNow
        };
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO trade_review_attachments (id, journal_id, review_key, storage_key, original_file_name, content_type, length, created_utc, removed_utc) VALUES ($id, $journal, $reviewKey, $storageKey, $fileName, $contentType, $length, $created, NULL)";
        command.Parameters.AddWithValue("$id", attachment.Id.ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$reviewKey", reviewKey);
        command.Parameters.AddWithValue("$storageKey", storageKey);
        command.Parameters.AddWithValue("$fileName", attachment.OriginalFileName);
        command.Parameters.AddWithValue("$contentType", attachment.ContentType);
        command.Parameters.AddWithValue("$length", attachment.Length);
        command.Parameters.AddWithValue("$created", attachment.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        return attachment;
    }

    public TradeReviewAttachment? RemoveTradeReviewAttachment(Guid journalId, Guid attachmentId)
    {
        var attachment = GetTradeReviewAttachment(journalId, attachmentId);
        if (attachment is null) return null;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE trade_review_attachments SET removed_utc = $removed WHERE id = $id AND journal_id = $journal AND removed_utc IS NULL";
        command.Parameters.AddWithValue("$removed", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", attachmentId.ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.ExecuteNonQuery();
        return attachment;
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

    public IReadOnlyList<AccountTransaction> GetAccountTransactions(Guid journalId, bool includeDeleted = false)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {AccountTransactionColumns} FROM account_transactions WHERE journal_id = $journal {(includeDeleted ? string.Empty : "AND deleted_utc IS NULL")} ORDER BY effective_utc, created_utc, id";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        var transactions = new List<AccountTransaction>();
        while (reader.Read()) transactions.Add(ReadAccountTransaction(reader));
        return transactions;
    }

    public AccountTransaction? GetAccountTransaction(Guid journalId, Guid transactionId, bool includeDeleted = false)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {AccountTransactionColumns} FROM account_transactions WHERE journal_id = $journal AND id = $id {(includeDeleted ? string.Empty : "AND deleted_utc IS NULL")}";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$id", transactionId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAccountTransaction(reader) : null;
    }

    public IReadOnlyList<AccountTransactionHistoryEntry> GetAccountTransactionHistory(Guid journalId, Guid transactionId)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {AccountTransactionHistoryColumns} FROM account_transaction_history WHERE journal_id = $journal AND transaction_id = $transaction ORDER BY revision DESC, created_utc DESC, id DESC";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$transaction", transactionId.ToString("D"));
        using var reader = command.ExecuteReader();
        var history = new List<AccountTransactionHistoryEntry>();
        while (reader.Read()) history.Add(ReadAccountTransactionHistory(reader));
        return history;
    }

    public AccountTransaction CreateAccountTransaction(Guid journalId, AccountTransactionDraft draft)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        ValidateAccountTransactionDraft(draft);

        var now = DateTimeOffset.UtcNow;
        var transaction = new AccountTransaction
        {
            Id = Guid.NewGuid(),
            JournalId = journalId,
            Type = draft.Type,
            EffectiveUtc = draft.EffectiveUtc.ToUniversalTime(),
            Amount = draft.Amount,
            Note = draft.Note?.Trim() ?? string.Empty,
            Revision = 1,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        using var connection = OpenConnection();
        using var dbTransaction = connection.BeginTransaction();
        InsertAccountTransaction(connection, dbTransaction, transaction);
        InsertAccountTransactionHistory(connection, dbTransaction, transaction, 1, "created", "{}", JsonSerializer.Serialize(transaction), now);
        dbTransaction.Commit();
        return transaction;
    }

    public AccountTransactionMutationResult UpdateAccountTransaction(Guid journalId, Guid transactionId, int expectedRevision, AccountTransactionDraft draft)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        ValidateAccountTransactionDraft(draft);

        using var connection = OpenConnection();
        using var dbTransaction = connection.BeginTransaction();
        var current = ReadAccountTransaction(connection, dbTransaction, journalId, transactionId, includeDeleted: false);
        if (current is null)
        {
            dbTransaction.Rollback();
            return new AccountTransactionMutationResult { NotFound = true };
        }
        if (current.Revision != expectedRevision)
        {
            dbTransaction.Rollback();
            return new AccountTransactionMutationResult { Conflict = true, Transaction = current };
        }

        var now = DateTimeOffset.UtcNow;
        var updated = new AccountTransaction
        {
            Id = current.Id,
            JournalId = current.JournalId,
            Type = draft.Type,
            EffectiveUtc = draft.EffectiveUtc.ToUniversalTime(),
            Amount = draft.Amount,
            Note = draft.Note?.Trim() ?? string.Empty,
            Revision = current.Revision + 1,
            CreatedUtc = current.CreatedUtc,
            UpdatedUtc = now
        };

        using (var command = connection.CreateCommand())
        {
            command.Transaction = dbTransaction;
            command.CommandText = "UPDATE account_transactions SET transaction_type = $type, effective_utc = $effective, amount = $amount, note = $note, revision = $revision, updated_utc = $updated WHERE id = $id AND journal_id = $journal AND revision = $expected AND deleted_utc IS NULL";
            command.Parameters.AddWithValue("$type", AccountTransactionTypeValue(updated.Type));
            command.Parameters.AddWithValue("$effective", updated.EffectiveUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$amount", NumberFormat.Decimal(updated.Amount));
            command.Parameters.AddWithValue("$note", updated.Note);
            command.Parameters.AddWithValue("$revision", updated.Revision);
            command.Parameters.AddWithValue("$updated", updated.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$id", transactionId.ToString("D"));
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (command.ExecuteNonQuery() != 1)
            {
                var latest = ReadAccountTransaction(connection, dbTransaction, journalId, transactionId, includeDeleted: false);
                dbTransaction.Rollback();
                return new AccountTransactionMutationResult { Conflict = true, Transaction = latest };
            }
        }

        InsertAccountTransactionHistory(connection, dbTransaction, updated, updated.Revision, "updated", JsonSerializer.Serialize(current), JsonSerializer.Serialize(updated), now);
        dbTransaction.Commit();
        return new AccountTransactionMutationResult { Saved = true, Transaction = updated };
    }

    public AccountTransactionMutationResult DeleteAccountTransaction(Guid journalId, Guid transactionId, int expectedRevision)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");

        using var connection = OpenConnection();
        using var dbTransaction = connection.BeginTransaction();
        var current = ReadAccountTransaction(connection, dbTransaction, journalId, transactionId, includeDeleted: false);
        if (current is null)
        {
            dbTransaction.Rollback();
            return new AccountTransactionMutationResult { NotFound = true };
        }
        if (current.Revision != expectedRevision)
        {
            dbTransaction.Rollback();
            return new AccountTransactionMutationResult { Conflict = true, Transaction = current };
        }

        var now = DateTimeOffset.UtcNow;
        var deleted = new AccountTransaction
        {
            Id = current.Id,
            JournalId = current.JournalId,
            Type = current.Type,
            EffectiveUtc = current.EffectiveUtc,
            Amount = current.Amount,
            Note = current.Note,
            Revision = current.Revision + 1,
            CreatedUtc = current.CreatedUtc,
            UpdatedUtc = now,
            DeletedUtc = now
        };

        using (var command = connection.CreateCommand())
        {
            command.Transaction = dbTransaction;
            command.CommandText = "UPDATE account_transactions SET revision = $revision, updated_utc = $updated, deleted_utc = $deleted WHERE id = $id AND journal_id = $journal AND revision = $expected AND deleted_utc IS NULL";
            command.Parameters.AddWithValue("$revision", deleted.Revision);
            command.Parameters.AddWithValue("$updated", deleted.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$deleted", deleted.DeletedUtc.Value.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$id", transactionId.ToString("D"));
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (command.ExecuteNonQuery() != 1)
            {
                var latest = ReadAccountTransaction(connection, dbTransaction, journalId, transactionId, includeDeleted: false);
                dbTransaction.Rollback();
                return new AccountTransactionMutationResult { Conflict = true, Transaction = latest };
            }
        }

        InsertAccountTransactionHistory(connection, dbTransaction, deleted, deleted.Revision, "deleted", JsonSerializer.Serialize(current), JsonSerializer.Serialize(deleted), now);
        dbTransaction.Commit();
        return new AccountTransactionMutationResult { Saved = true, Transaction = deleted };
    }

    public IReadOnlyList<BenchmarkPoint> GetBenchmarkPoints(Guid journalId, string? symbol = null)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT p.id, p.journal_id, p.import_batch_id, p.series_id, p.source_type, p.source_key, s.symbol, s.provider, p.event_utc, p.value, p.source_time_text, p.row_number FROM benchmark_points p JOIN benchmark_series s ON s.id = p.series_id WHERE p.journal_id = $journal" + (string.IsNullOrWhiteSpace(symbol) ? string.Empty : " AND s.symbol = $symbol") + " ORDER BY s.symbol, p.event_utc, p.row_number, p.id";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        if (!string.IsNullOrWhiteSpace(symbol)) command.Parameters.AddWithValue("$symbol", symbol.Trim());
        using var reader = command.ExecuteReader();
        var points = new List<BenchmarkPoint>();
        while (reader.Read()) points.Add(ReadBenchmarkPoint(reader));
        return points
            .GroupBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
            .SelectMany(symbolGroup => symbolGroup
                .GroupBy(x => x.SeriesId)
                .OrderBy(seriesGroup => seriesGroup.Any(x => x.Provider.Equals(TradeFoundryConstants.YahooFinanceBenchmarkSource, StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
                .ThenBy(seriesGroup => seriesGroup.Min(x => x.EventUtc))
                .First())
            .OrderBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.EventUtc)
            .ThenBy(x => x.RowNumber)
            .ThenBy(x => x.Id)
            .ToArray();
    }

    public BenchmarkSeriesStatus? GetBenchmarkSeriesStatus(Guid journalId, string? symbol = null, string? provider = null)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT s.id, s.journal_id, s.symbol, s.interval, s.provider, s.source_url, s.created_utc, s.last_fetched_utc, s.last_attempted_utc, s.requested_start, s.requested_end, s.last_error, COUNT(p.id), MIN(p.event_utc), MAX(p.event_utc) FROM benchmark_series s LEFT JOIN benchmark_points p ON p.series_id = s.id WHERE s.journal_id = $journal" + (string.IsNullOrWhiteSpace(symbol) ? string.Empty : " AND s.symbol = $symbol") + (string.IsNullOrWhiteSpace(provider) ? string.Empty : " AND s.provider = $provider") + " GROUP BY s.id, s.journal_id, s.symbol, s.interval, s.provider, s.source_url, s.created_utc, s.last_fetched_utc, s.last_attempted_utc, s.requested_start, s.requested_end, s.last_error ORDER BY CASE WHEN COUNT(p.id) > 0 THEN 0 ELSE 1 END, CASE WHEN s.provider = $automaticProvider THEN 0 ELSE 1 END, s.created_utc DESC LIMIT 1";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$automaticProvider", TradeFoundryConstants.YahooFinanceBenchmarkSource);
        if (!string.IsNullOrWhiteSpace(symbol)) command.Parameters.AddWithValue("$symbol", symbol.Trim());
        if (!string.IsNullOrWhiteSpace(provider)) command.Parameters.AddWithValue("$provider", provider.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBenchmarkSeriesStatus(reader) : null;
    }

    public int StoreDownloadedBenchmark(Guid journalId, string symbol, string provider, string sourceUrl, DateOnly requestedStart, DateOnly requestedEnd, DateTimeOffset fetchedUtc, IReadOnlyList<BenchmarkDownloadPoint> points)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        if (points.Count == 0) return 0;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var seriesId = EnsureDownloadedBenchmarkSeries(connection, transaction, journalId, symbol, provider, sourceUrl);
        var stored = 0;
        var rowNumber = 0;
        foreach (var point in points.OrderBy(x => x.EventUtc))
        {
            if (point.Value <= 0m) continue;
            rowNumber++;
            var sourceKey = $"{provider}\u001f{symbol}\u001f{DateOnly.FromDateTime(point.EventUtc.UtcDateTime.Date):yyyy-MM-dd}";
            var eventUtc = point.EventUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

            using (var removeMovedPoint = connection.CreateCommand())
            {
                removeMovedPoint.Transaction = transaction;
                removeMovedPoint.CommandText = "DELETE FROM benchmark_points WHERE series_id = $series AND source_key = $key AND event_utc <> $event";
                removeMovedPoint.Parameters.AddWithValue("$series", seriesId);
                removeMovedPoint.Parameters.AddWithValue("$key", sourceKey);
                removeMovedPoint.Parameters.AddWithValue("$event", eventUtc);
                removeMovedPoint.ExecuteNonQuery();
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT OR IGNORE INTO benchmark_points (id, journal_id, series_id, import_batch_id, source_type, source_key, event_utc, value, source_time_text, row_number) VALUES ($id, $journal, $series, NULL, $source, $key, $event, $value, $sourceTime, $row)";
                insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                insert.Parameters.AddWithValue("$journal", journalId.ToString("D"));
                insert.Parameters.AddWithValue("$series", seriesId);
                insert.Parameters.AddWithValue("$source", provider);
                insert.Parameters.AddWithValue("$key", sourceKey);
                insert.Parameters.AddWithValue("$event", eventUtc);
                insert.Parameters.AddWithValue("$value", NumberFormat.Decimal(point.Value));
                insert.Parameters.AddWithValue("$sourceTime", string.IsNullOrWhiteSpace(point.SourceTimeText) ? eventUtc : point.SourceTimeText);
                insert.Parameters.AddWithValue("$row", rowNumber);
                insert.ExecuteNonQuery();
            }

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE benchmark_points SET import_batch_id = NULL, source_type = $source, source_key = $key, value = $value, source_time_text = $sourceTime, row_number = $row WHERE series_id = $series AND event_utc = $event";
                update.Parameters.AddWithValue("$source", provider);
                update.Parameters.AddWithValue("$key", sourceKey);
                update.Parameters.AddWithValue("$value", NumberFormat.Decimal(point.Value));
                update.Parameters.AddWithValue("$sourceTime", string.IsNullOrWhiteSpace(point.SourceTimeText) ? eventUtc : point.SourceTimeText);
                update.Parameters.AddWithValue("$row", rowNumber);
                update.Parameters.AddWithValue("$series", seriesId);
                update.Parameters.AddWithValue("$event", eventUtc);
                if (update.ExecuteNonQuery() > 0) stored++;
            }
        }

        using (var metadata = connection.CreateCommand())
        {
            metadata.Transaction = transaction;
            metadata.CommandText = "UPDATE benchmark_series SET provider = $provider, source_url = $sourceUrl, last_fetched_utc = $fetched, last_attempted_utc = $fetched, requested_start = $start, requested_end = $end, last_error = '' WHERE id = $id";
            metadata.Parameters.AddWithValue("$provider", provider);
            metadata.Parameters.AddWithValue("$sourceUrl", sourceUrl);
            metadata.Parameters.AddWithValue("$fetched", fetchedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            metadata.Parameters.AddWithValue("$start", requestedStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            metadata.Parameters.AddWithValue("$end", requestedEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            metadata.Parameters.AddWithValue("$id", seriesId);
            metadata.ExecuteNonQuery();
        }
        transaction.Commit();
        return stored;
    }

    public void RecordBenchmarkDownloadFailure(Guid journalId, string symbol, string provider, string sourceUrl, DateOnly requestedStart, DateOnly requestedEnd, DateTimeOffset attemptedUtc, string error)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var seriesId = EnsureDownloadedBenchmarkSeries(connection, transaction, journalId, symbol, provider, sourceUrl);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE benchmark_series SET provider = $provider, source_url = $sourceUrl, last_attempted_utc = $attempted, requested_start = $start, requested_end = $end, last_error = $error WHERE id = $id";
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$sourceUrl", sourceUrl);
        command.Parameters.AddWithValue("$attempted", attemptedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$start", requestedStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$end", requestedEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$error", error.Length > 500 ? error[..500] : error);
        command.Parameters.AddWithValue("$id", seriesId);
        command.ExecuteNonQuery();
        transaction.Commit();
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
            filters.Add("(file_name LIKE $search OR source_application LIKE $search OR source_type LIKE $search OR status LIKE $search)");
            count.Parameters.AddWithValue("$search", $"%{query.Search.Trim()}%");
        }
        count.CommandText = $"SELECT COUNT(*) FROM import_batches WHERE {string.Join(" AND ", filters)}";
        var totalCount = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        var imports = new List<ImportBatch>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {ImportColumns} FROM import_batches WHERE {string.Join(" AND ", filters)} ORDER BY imported_utc DESC LIMIT $limit OFFSET $offset";
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

    public Trade? GetTradeByReviewKey(Guid journalId, string reviewKey)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TradeColumns} FROM {TradeFrom} WHERE t.journal_id = $journal AND t.review_key = $reviewKey LIMIT 1";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$reviewKey", reviewKey);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTrade(reader) : null;
    }

    public IReadOnlyList<TradeFillEvidence> GetTradeFillEvidence(Guid journalId, Guid tradeId, int limit = 101)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT f.event_utc, f.side, COALESCE(a.quantity, f.quantity), f.price, f.fees, f.source_type FROM trades t JOIN fills f ON f.journal_id = t.journal_id LEFT JOIN trade_fill_allocations a ON a.trade_id = t.id AND a.fill_id = f.id WHERE t.id = $trade AND t.journal_id = $journal AND (a.fill_id IS NOT NULL OR (f.import_batch_id = t.import_batch_id AND f.source_key = substr(t.source_key, 1, length(t.source_key) - length(CAST(t.sequence AS TEXT)) - 1))) ORDER BY f.event_utc, f.row_number, f.id LIMIT $limit";
        command.Parameters.AddWithValue("$trade", tradeId.ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 501));
        var fills = new List<TradeFillEvidence>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            fills.Add(new TradeFillEvidence(ParseDate(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2), ParseDecimal(reader.GetString(3)), ParseDecimal(reader.GetString(4)), reader.GetString(5)));
        return fills;
    }

    public int GetImportWarningBatchCount(Guid journalId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM import_batches WHERE journal_id = $journal AND TRIM(message) <> '' AND TRIM(message) NOT GLOB 'Source timezone: *.'";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private Trade? GetTradeInternal(Guid tradeId, Guid? journalId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TradeColumns} FROM {TradeFrom} WHERE t.id = $id" + (journalId.HasValue ? " AND t.journal_id = $journal" : string.Empty);
        command.Parameters.AddWithValue("$id", tradeId.ToString("D"));
        if (journalId.HasValue) command.Parameters.AddWithValue("$journal", journalId.Value.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTrade(reader) : null;
    }

    public IReadOnlyList<Bar> GetBarsForTrade(Guid journalId, string symbol, DateTimeOffset start, DateTimeOffset end, string interval = "source")
    {
        var normalizedSymbol = InstrumentCatalog.ExtractRoot(symbol);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        // Market bars are shared across journals. The journal argument remains
        // for API compatibility with the journal-scoped trade pages.
        command.CommandText = "SELECT b.id, b.series_id, s.symbol, s.interval, b.event_utc, b.open, b.high, b.low, b.close, b.volume, b.number_of_trades, b.bid_volume, b.ask_volume FROM bars b JOIN bar_series s ON s.id = b.series_id WHERE s.symbol = $symbol AND ($interval = '' OR s.interval = $interval) AND b.event_utc >= $start AND b.event_utc <= $end ORDER BY b.event_utc, b.id";
        command.Parameters.AddWithValue("$symbol", normalizedSymbol);
        command.Parameters.AddWithValue("$interval", interval);
        command.Parameters.AddWithValue("$start", start.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$end", end.ToString("O", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        var bars = new List<Bar>();
        while (reader.Read()) bars.Add(ReadBar(reader));
        return bars.GroupBy(x => x.EventUtc).Select(x => x.First()).ToArray();
    }

    public IReadOnlyList<BarSeriesInfo> GetBarSeries(Guid journalId, string symbol)
    {
        var normalizedSymbol = InstrumentCatalog.ExtractRoot(symbol);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT s.symbol, s.interval, COUNT(DISTINCT b.event_utc), MIN(b.event_utc), MAX(b.event_utc) FROM bar_series s LEFT JOIN bars b ON b.series_id = s.id WHERE s.symbol = $symbol GROUP BY s.symbol, s.interval ORDER BY CASE WHEN s.interval = 'source' THEN 1 ELSE 0 END, s.interval";
        command.Parameters.AddWithValue("$symbol", normalizedSymbol);
        using var reader = command.ExecuteReader();
        var series = new List<BarSeriesInfo>();
        while (reader.Read())
        {
            var interval = reader.GetString(1);
            series.Add(new BarSeriesInfo
            {
                Symbol = reader.GetString(0),
                Interval = interval,
                IntervalMinutes = BarIntervals.TryGetMinutes(interval, out var minutes) ? minutes : 0,
                BarCount = reader.GetInt64(2),
                FirstEventUtc = reader.IsDBNull(3) ? null : ParseDate(reader.GetString(3)),
                LastEventUtc = reader.IsDBNull(4) ? null : ParseDate(reader.GetString(4))
            });
        }
        return series;
    }

    public IReadOnlyList<string> GetOhlcSymbols()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT symbol FROM bar_series WHERE length(trim(symbol)) > 0 ORDER BY symbol COLLATE NOCASE";
        using var reader = command.ExecuteReader();
        var symbols = new List<string>();
        while (reader.Read()) symbols.Add(reader.GetString(0));
        return symbols;
    }

    public OhlcCalendar GetOhlcCalendar(DateOnly startMonth, string symbol = "")
    {
        var firstMonth = new DateOnly(startMonth.Year, startMonth.Month, 1);
        var rangeStart = new DateTimeOffset(firstMonth.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var rangeEnd = new DateTimeOffset(firstMonth.AddMonths(3).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var normalizedSymbol = string.IsNullOrWhiteSpace(symbol) ? string.Empty : InstrumentCatalog.ExtractRoot(symbol);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT s.symbol, s.interval, substr(b.event_utc, 1, 10), COUNT(*) FROM bars b JOIN bar_series s ON s.id = b.series_id WHERE ($symbol = '' OR s.symbol = $symbol) AND b.event_utc >= $start AND b.event_utc < $end GROUP BY s.symbol, s.interval, substr(b.event_utc, 1, 10) ORDER BY s.symbol COLLATE NOCASE, s.interval, substr(b.event_utc, 1, 10)";
        command.Parameters.AddWithValue("$symbol", normalizedSymbol);
        command.Parameters.AddWithValue("$start", rangeStart.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$end", rangeEnd.ToString("O", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        var rows = new List<(string SeriesKey, DateOnly Date, long BarCount, int IntervalMinutes)>();
        while (reader.Read())
        {
            if (!DateOnly.TryParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;

            var seriesSymbol = reader.GetString(0);
            var interval = reader.GetString(1);
            rows.Add(($"{seriesSymbol}\u001f{interval}", date, reader.GetInt64(3), BarIntervals.TryGetMinutes(interval, out var minutes) ? minutes : 0));
        }

        var coverageByDate = rows
            .GroupBy(x => x.Date)
            .ToDictionary(
                group => group.Key,
                group => new OhlcCalendarCell
                {
                    Date = group.Key,
                    Coverage = group.Any(x => IsFullCalendarDay(x.BarCount, x.IntervalMinutes)) ? "full" : "partial",
                    BarCount = checked(group.Sum(x => x.BarCount)),
                    SeriesCount = group.Select(x => x.SeriesKey).Distinct(StringComparer.Ordinal).Count()
                });

        var months = Enumerable.Range(0, 3)
            .Select(offset => BuildCalendarMonth(firstMonth.AddMonths(offset), coverageByDate))
            .ToArray();
        return new OhlcCalendar { StartMonth = firstMonth, Months = months };
    }

    public OhlcClearResult ClearOhlcData()
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        long Count(string sql, params (string Name, string Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var parameter in parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
        }

        var result = new OhlcClearResult
        {
            BarCount = Count("SELECT COUNT(*) FROM bars"),
            SeriesCount = Count("SELECT COUNT(*) FROM bar_series"),
            ImportBatchCount = Count(
                "SELECT COUNT(*) FROM import_batches WHERE source_type IN ($sierra, $legacy)",
                ("$sierra", TradeFoundryConstants.SierraOhlcBars),
                ("$legacy", TradeFoundryConstants.OhlcvBars))
        };

        foreach (var sql in new[]
        {
            "DELETE FROM journal_bar_series",
            "DELETE FROM bar_imports",
            "DELETE FROM bars",
            "DELETE FROM bar_series"
        })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        using (var raw = connection.CreateCommand())
        {
            raw.Transaction = transaction;
            raw.CommandText = "DELETE FROM raw_records WHERE import_batch_id IN (SELECT id FROM import_batches WHERE source_type IN ($sierra, $legacy))";
            raw.Parameters.AddWithValue("$sierra", TradeFoundryConstants.SierraOhlcBars);
            raw.Parameters.AddWithValue("$legacy", TradeFoundryConstants.OhlcvBars);
            raw.ExecuteNonQuery();
        }

        using (var batches = connection.CreateCommand())
        {
            batches.Transaction = transaction;
            batches.CommandText = "DELETE FROM import_batches WHERE source_type IN ($sierra, $legacy)";
            batches.Parameters.AddWithValue("$sierra", TradeFoundryConstants.SierraOhlcBars);
            batches.Parameters.AddWithValue("$legacy", TradeFoundryConstants.OhlcvBars);
            batches.ExecuteNonQuery();
        }

        transaction.Commit();
        return result;
    }

    public BarQueryResult GetBarWindow(Guid journalId, string symbol, DateTimeOffset start, DateTimeOffset end, string requestedInterval = BarIntervals.Source)
    {
        var requested = BarIntervals.Normalize(requestedInterval);
        var available = GetBarSeries(journalId, symbol);
        var fullyCovered = available.Where(x => x.FirstEventUtc.HasValue && x.LastEventUtc.HasValue && x.FirstEventUtc.Value <= start && x.LastEventUtc.Value >= end).ToArray();
        var overlapping = available.Where(x => x.FirstEventUtc.HasValue && x.LastEventUtc.HasValue && x.FirstEventUtc.Value <= end && x.LastEventUtc.Value >= start).ToArray();
        var candidates = fullyCovered.Length > 0 ? fullyCovered : overlapping.Length > 0 ? overlapping : available.ToArray();
        var requestedMinutes = BarIntervals.TryGetMinutes(requested, out var targetMinutes) ? targetMinutes : 0;
        var selected = SelectBarSeries(candidates, requestedMinutes);
        if (selected is null)
        {
            return new BarQueryResult
            {
                AvailableSeries = available,
                RequestedInterval = requested,
                AvailabilityNote = "No bar data is available for this symbol."
            };
        }

        var sourceBars = GetBarsForTrade(journalId, symbol, start, end, selected.Interval);
        var sourceMinutes = selected.IntervalMinutes;
        var consolidated = requestedMinutes > 0 && sourceMinutes > 0 && sourceMinutes < requestedMinutes;
        var coarser = requestedMinutes > 0 && sourceMinutes > requestedMinutes;
        var bars = consolidated ? ConsolidateBars(sourceBars, requestedMinutes) : sourceBars;
        var note = string.Empty;
        if (coarser)
        {
            note = $"Only {selected.Label} data was available for this trade window; the requested {BarIntervals.Label(requested)} timeframe was not available.";
        }
        else if (consolidated)
        {
            note = $"Displaying {BarIntervals.Label(requested)} bars consolidated from {selected.Label} data.";
        }

        return new BarQueryResult
        {
            Bars = bars,
            AvailableSeries = available,
            RequestedInterval = requested,
            ResolvedInterval = consolidated ? requested : selected.Interval,
            IsConsolidated = consolidated,
            IsCoarserThanRequested = coarser,
            AvailabilityNote = note
        };
    }

    public BarHistoryPage GetBarHistoryPage(Guid journalId, string symbol, string requestedInterval, DateTimeOffset cursor, bool before, int limit = 300)
    {
        if (limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(limit), "The bar page size must be between 1 and 500.");

        var requested = BarIntervals.Normalize(requestedInterval, allowSource: true);
        var available = GetBarSeries(journalId, symbol);
        var requestedMinutes = BarIntervals.TryGetMinutes(requested, out var targetMinutes) ? targetMinutes : 0;
        var selected = SelectBarSeries(available, requestedMinutes);
        if (selected is null)
        {
            return new BarHistoryPage
            {
                ResolvedInterval = requested,
                HasMore = false
            };
        }

        var sourceMinutes = selected.IntervalMinutes;
        var consolidated = requestedMinutes > 0 && sourceMinutes > 0 && sourceMinutes < requestedMinutes;
        var sourceLimit = consolidated
            ? checked((limit + 2) * Math.Max(1, requestedMinutes / sourceMinutes) + 2)
            : limit + 1;
        var queryCursor = cursor;
        if (consolidated)
        {
            var bucketTicks = TimeSpan.TicksPerMinute * (long)requestedMinutes;
            var bucketStartTicks = cursor.ToUniversalTime().UtcDateTime.Ticks / bucketTicks * bucketTicks;
            queryCursor = new DateTimeOffset(new DateTime(bucketStartTicks, DateTimeKind.Utc));
            if (!before)
                queryCursor = queryCursor.AddMinutes(requestedMinutes);
        }
        var sourceBars = GetBarHistoryRaw(symbol, selected.Interval, queryCursor, before, sourceLimit, afterInclusive: consolidated && !before);
        var candidates = consolidated ? ConsolidateBars(sourceBars, requestedMinutes) : sourceBars;
        var ordered = candidates.OrderBy(x => x.EventUtc).ToArray();
        var hasMore = ordered.Length > limit || sourceBars.Count >= sourceLimit;
        var page = before
            ? ordered.Skip(Math.Max(0, ordered.Length - limit)).Take(limit).ToArray()
            : ordered.Take(limit).ToArray();

        return new BarHistoryPage
        {
            Bars = page,
            ResolvedInterval = consolidated ? requested : selected.Interval,
            HasMore = hasMore
        };
    }

    private IReadOnlyList<Bar> GetBarHistoryRaw(string symbol, string interval, DateTimeOffset cursor, bool before, int limit, bool afterInclusive)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var comparison = before ? "<" : afterInclusive ? ">=" : ">";
        var order = before ? "DESC" : "ASC";
        command.CommandText = $"SELECT b.id, b.series_id, s.symbol, s.interval, b.event_utc, b.open, b.high, b.low, b.close, b.volume, b.number_of_trades, b.bid_volume, b.ask_volume FROM bars b JOIN bar_series s ON s.id = b.series_id WHERE s.symbol = $symbol AND s.interval = $interval AND b.event_utc {comparison} $cursor ORDER BY b.event_utc {order}, b.id {order} LIMIT $limit";
        command.Parameters.AddWithValue("$symbol", InstrumentCatalog.ExtractRoot(symbol));
        command.Parameters.AddWithValue("$interval", interval);
        command.Parameters.AddWithValue("$cursor", cursor.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", limit);

        var bars = new List<Bar>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) bars.Add(ReadBar(reader));
        return before
            ? bars.OrderBy(x => x.EventUtc).ThenBy(x => x.Id).GroupBy(x => x.EventUtc).Select(x => x.First()).ToArray()
            : bars.GroupBy(x => x.EventUtc).Select(x => x.First()).ToArray();
    }

    private static BarSeriesInfo? SelectBarSeries(IReadOnlyList<BarSeriesInfo> available, int requestedMinutes)
    {
        if (available.Count == 0) return null;
        if (requestedMinutes == 0)
            return available.Where(x => x.IntervalMinutes > 0).OrderBy(x => x.IntervalMinutes).FirstOrDefault()
                ?? available.FirstOrDefault(x => x.Interval.Equals(BarIntervals.Source, StringComparison.OrdinalIgnoreCase));

        return available.FirstOrDefault(x => x.IntervalMinutes == requestedMinutes)
            ?? available.Where(x => x.IntervalMinutes > 0 && x.IntervalMinutes < requestedMinutes && requestedMinutes % x.IntervalMinutes == 0)
                .OrderByDescending(x => x.IntervalMinutes)
                .FirstOrDefault()
            ?? available.Where(x => x.IntervalMinutes > requestedMinutes).OrderBy(x => x.IntervalMinutes).FirstOrDefault()
            ?? available.Where(x => x.IntervalMinutes > 0).OrderBy(x => Math.Abs(x.IntervalMinutes - requestedMinutes)).FirstOrDefault()
            ?? available.FirstOrDefault(x => x.Interval.Equals(BarIntervals.Source, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<Bar> ConsolidateBars(IReadOnlyList<Bar> source, int targetMinutes)
    {
        if (source.Count == 0) return Array.Empty<Bar>();
        var ticksPerBucket = TimeSpan.TicksPerMinute * (long)targetMinutes;
        return source
            .OrderBy(x => x.EventUtc)
            .GroupBy(x => x.EventUtc.UtcDateTime.Ticks / ticksPerBucket)
            .Select(group =>
            {
                var items = group.OrderBy(x => x.EventUtc).ToArray();
                var first = items[0];
                var last = items[^1];
                return new Bar
                {
                    SeriesId = first.SeriesId,
                    Symbol = first.Symbol,
                    Interval = BarIntervals.Format(targetMinutes),
                    EventUtc = new DateTimeOffset(new DateTime(group.Key * ticksPerBucket, DateTimeKind.Utc)),
                    Open = first.Open,
                    High = items.Max(x => x.High),
                    Low = items.Min(x => x.Low),
                    Close = last.Close,
                    Volume = SumBars(items.Select(x => x.Volume)),
                    NumberOfTrades = SumBars(items.Select(x => x.NumberOfTrades)),
                    BidVolume = SumBars(items.Select(x => x.BidVolume)),
                    AskVolume = SumBars(items.Select(x => x.AskVolume))
                };
            })
            .ToArray();
    }

    private static OhlcCalendarMonth BuildCalendarMonth(DateOnly month, IReadOnlyDictionary<DateOnly, OhlcCalendarCell> coverageByDate)
    {
        var cells = new List<OhlcCalendarCell>();
        for (var leading = 0; leading < (int)month.DayOfWeek; leading++)
            cells.Add(new OhlcCalendarCell());

        for (var day = 1; day <= DateTime.DaysInMonth(month.Year, month.Month); day++)
        {
            var date = new DateOnly(month.Year, month.Month, day);
            cells.Add(coverageByDate.TryGetValue(date, out var coverage)
                ? coverage
                : new OhlcCalendarCell { Date = date });
        }

        while (cells.Count % 7 != 0)
            cells.Add(new OhlcCalendarCell());

        return new OhlcCalendarMonth { Month = month, Cells = cells };
    }

    private static bool IsFullCalendarDay(long barCount, int intervalMinutes)
    {
        if (intervalMinutes <= 0) return barCount > 0;
        var minutes = intervalMinutes;
        var minimumExpected = Math.Max(1L, (390L + minutes - 1) / minutes);
        var threshold = Math.Max(1L, (long)Math.Ceiling(minimumExpected * 0.9d));
        return barCount >= threshold;
    }

    private static long? SumBars(IEnumerable<long?> values)
    {
        var present = values.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return present.Length == 0 ? null : checked(present.Sum());
    }

    public ImportResult CommitImport(Guid journalId, string fileName, ParsedImport parsed, string groupingPolicy, string interval, string? sourceTimeZone = null)
    {
        var batchId = Guid.NewGuid();
        var importedUtc = DateTimeOffset.UtcNow;
        var newRows = 0;
        var duplicateRows = 0;
        var insertedFills = 0;
        var insertedOrderEvents = 0;
        var messages = new List<string>(parsed.Warnings);
        if (!string.IsNullOrWhiteSpace(sourceTimeZone))
            messages.Add($"Source timezone: {TimeZoneCatalog.CanonicalId(sourceTimeZone)}.");

        using (var connection = OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using (var batch = connection.CreateCommand())
            {
                batch.Transaction = transaction;
                batch.CommandText = "INSERT INTO import_batches (id, journal_id, file_name, source_application, source_type, imported_utc, total_rows, new_rows, duplicate_rows, status, message) VALUES ($id, $journal, $file, $application, $source, $utc, $total, 0, 0, 'completed', '')";
                batch.Parameters.AddWithValue("$id", batchId.ToString("D"));
                batch.Parameters.AddWithValue("$journal", journalId.ToString("D"));
                batch.Parameters.AddWithValue("$file", fileName);
                batch.Parameters.AddWithValue("$application", parsed.SourceApplication);
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
        return new ImportResult
        {
            Batch = batchResult,
            Warnings = parsed.Warnings,
            ResolvedBarInterval = parsed.ResolvedBarInterval,
            ResolvedTimeZone = TimeZoneCatalog.CanonicalId(sourceTimeZone)
        };
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
            foreach (var reviewKey in ReadReviewKeysForImport(connection, transaction, journalId.Value, batchId))
                RemoveTradeReviewForSource(connection, transaction, journalId.Value, reviewKey);

            foreach (var sql in new[]
            {
                "DELETE FROM trade_fill_allocations WHERE trade_id IN (SELECT id FROM trades WHERE import_batch_id = $batch)",
                "DELETE FROM trades WHERE import_batch_id = $batch",
                "DELETE FROM order_events WHERE import_batch_id = $batch",
                "DELETE FROM account_balance_events WHERE import_batch_id = $batch",
                "DELETE FROM benchmark_points WHERE import_batch_id = $batch",
                "DELETE FROM fills WHERE import_batch_id = $batch",
                // Bar rows are shared market data. They must survive removal of
                // one journal's import so other journals can continue to use them.
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
            filters.Add("(t.symbol LIKE $search OR t.instrument LIKE $search OR t.account LIKE $search OR t.direction LIKE $search OR t.status LIKE $search)");
            command.Parameters.AddWithValue("$search", $"%{query.Search.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(query.Symbol))
        {
            filters.Add("t.symbol = $symbol");
            command.Parameters.AddWithValue("$symbol", query.Symbol.Trim());
        }
        if (!string.IsNullOrWhiteSpace(query.Direction))
        {
            filters.Add("t.direction = $direction");
            command.Parameters.AddWithValue("$direction", query.Direction.Trim());
        }
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            filters.Add("t.status = $status");
            command.Parameters.AddWithValue("$status", query.Status.Trim());
        }
    }

    private static bool IsBuy(string side) => side.Equals("Buy", StringComparison.OrdinalIgnoreCase) || side.Equals("Long", StringComparison.OrdinalIgnoreCase) || side.Equals("Buy to Open", StringComparison.OrdinalIgnoreCase);

    private static bool InsertFill(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, Guid batchId, FillDraft fill)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO fills (id, journal_id, import_batch_id, source_type, source_key, activity_type, order_action_source, event_utc, transaction_utc, source_time_text, symbol, account, side, quantity, price, price2, filled_quantity, open_close, order_type, order_status, parent_order_id, high, low, note, position_quantity, order_id, service_order_id, exchange_order_id, fill_execution_id, client_order_id, time_in_force, username, is_automated, account_balance, fees, row_number, instrument, point_value, tick_size) VALUES ($id, $journal, $batch, $source, $key, $activity, $orderActionSource, $event, $transaction, $sourceTime, $symbol, $account, $side, $quantity, $price, $price2, $filledQuantity, $openClose, $orderType, $orderStatus, $parent, $high, $low, $note, $position, $order, $serviceOrder, $exchangeOrder, $fillExecution, $clientOrder, $timeInForce, $username, $automated, $accountBalance, $fees, $row, $instrument, $pointValue, $tickSize)";
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
        command.Parameters.AddWithValue("$instrument", fill.Instrument);
        command.Parameters.AddWithValue("$pointValue", NumberFormat.Decimal(fill.PointValue));
        command.Parameters.AddWithValue("$tickSize", NumberFormat.Decimal(fill.TickSize));
        return command.ExecuteNonQuery() > 0;
    }

    private static bool InsertOrderEvent(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, Guid batchId, OrderEventDraft orderEvent)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO order_events (id, journal_id, import_batch_id, source_type, source_key, order_action_source, event_utc, transaction_utc, source_time_text, symbol, account, internal_order_id, service_order_id, parent_order_id, exchange_order_id, fill_execution_id, order_type, order_status, side, open_close, price, price2, quantity, filled_quantity, fill_price, position_quantity, note, client_order_id, time_in_force, username, is_automated, fees, row_number, instrument) VALUES ($id, $journal, $batch, $source, $key, $orderActionSource, $event, $transaction, $sourceTime, $symbol, $account, $internal, $serviceOrder, $parent, $exchange, $fillExecution, $orderType, $orderStatus, $side, $openClose, $price, $price2, $quantity, $filledQuantity, $fillPrice, $position, $note, $clientOrder, $timeInForce, $username, $automated, $fees, $row, $instrument)";
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
        command.Parameters.AddWithValue("$instrument", orderEvent.Instrument);
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

    private static string EnsureDownloadedBenchmarkSeries(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, string symbol, string provider, string sourceUrl)
    {
        var seriesKey = $"{journalId:D}\u001f{symbol}\u001f1d\u001f{provider}";
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT id FROM benchmark_series WHERE series_key = $key";
            find.Parameters.AddWithValue("$key", seriesKey);
            if (find.ExecuteScalar() is string existingId)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE benchmark_series SET provider = $provider, source_url = $sourceUrl WHERE id = $id";
                update.Parameters.AddWithValue("$provider", provider);
                update.Parameters.AddWithValue("$sourceUrl", sourceUrl);
                update.Parameters.AddWithValue("$id", existingId);
                update.ExecuteNonQuery();
                return existingId;
            }
        }

        var seriesId = Guid.NewGuid().ToString("D");
        using var insertSeries = connection.CreateCommand();
        insertSeries.Transaction = transaction;
        insertSeries.CommandText = "INSERT INTO benchmark_series (id, journal_id, symbol, interval, series_key, created_utc, provider, source_url) VALUES ($id, $journal, $symbol, '1d', $key, $created, $provider, $sourceUrl)";
        insertSeries.Parameters.AddWithValue("$id", seriesId);
        insertSeries.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        insertSeries.Parameters.AddWithValue("$symbol", symbol);
        insertSeries.Parameters.AddWithValue("$key", seriesKey);
        insertSeries.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        insertSeries.Parameters.AddWithValue("$provider", provider);
        insertSeries.Parameters.AddWithValue("$sourceUrl", sourceUrl);
        insertSeries.ExecuteNonQuery();
        return seriesId;
    }

    private static bool InsertDirectTrade(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, Guid batchId, ImportedTradeDraft draft, string groupingPolicy)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO trades (id, journal_id, import_batch_id, source_type, source_key, grouping_policy, sequence, symbol, account, direction, entry_utc, exit_utc, entry_price, exit_price, quantity, closed_quantity, gross_points, average_points, gross_pnl, fees, net_pnl, mae_points, mfe_points, point_value, tick_size, initial_stop_price, initial_target_price, initial_risk_points, initial_risk_currency, r_multiple, exit_type, entry_order_price, exit_order_price, entry_chase_points, exit_chase_points, status, note, created_utc, instrument, review_key) VALUES ($id, $journal, $batch, $source, $key, $grouping, $sequence, $symbol, $account, $direction, $entry, $exit, $entryPrice, $exitPrice, $quantity, $closed, $grossPoints, $averagePoints, $grossPnl, $fees, $netPnl, NULL, NULL, $pointValue, $tickSize, $initialStop, $initialTarget, $initialRiskPoints, $initialRiskCurrency, $rMultiple, $exitType, NULL, NULL, NULL, NULL, $status, $note, $created, $instrument, $reviewKey)";
        var quantity = Math.Max(1, draft.Quantity);
        var averagePoints = quantity == 0 ? 0m : draft.GrossPoints / quantity;
        var instrument = string.IsNullOrWhiteSpace(draft.Instrument) ? InstrumentCatalog.ExtractRoot(draft.Symbol) : draft.Instrument;
        var fallback = InstrumentCatalog.Resolve(instrument);
        var pointValue = draft.PointValue > 0m ? draft.PointValue : fallback.PointValue;
        var tickSize = draft.TickSize > 0m ? draft.TickSize : fallback.TickSize;
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
        command.Parameters.AddWithValue("$instrument", instrument);
        command.Parameters.AddWithValue("$reviewKey", CreateTradeReviewKey(journalId, draft.SourceType, draft.SourceKey, groupingPolicy, draft.Account, draft.Symbol, draft.Direction));
        return command.ExecuteNonQuery() > 0;
    }

    private static void InsertBar(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, Guid batchId, BarDraft bar)
    {
        var seriesKey = $"{bar.Symbol}\u001f{bar.Interval}";
        var seriesId = string.Empty;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            // Bar series are market data, not journal-owned evidence. Reuse one
            // series for every journal that imports or views the same symbol.
            find.CommandText = "SELECT id FROM bar_series WHERE symbol = $symbol AND interval = $interval ORDER BY created_utc, id LIMIT 1";
            find.Parameters.AddWithValue("$symbol", bar.Symbol);
            find.Parameters.AddWithValue("$interval", bar.Interval);
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
        var eventText = bar.EventUtc.ToString("O", CultureInfo.InvariantCulture);
        var barId = Guid.NewGuid().ToString("D");
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO bars (id, series_id, import_batch_id, event_utc, open, high, low, close, volume, number_of_trades, bid_volume, ask_volume) VALUES ($id, $series, $batch, $event, $open, $high, $low, $close, $volume, $numberOfTrades, $bidVolume, $askVolume)";
            insert.Parameters.AddWithValue("$id", barId);
            insert.Parameters.AddWithValue("$series", seriesId);
            insert.Parameters.AddWithValue("$batch", batchId.ToString("D"));
            insert.Parameters.AddWithValue("$event", eventText);
            insert.Parameters.AddWithValue("$open", NumberFormat.Decimal(bar.Open));
            insert.Parameters.AddWithValue("$high", NumberFormat.Decimal(bar.High));
            insert.Parameters.AddWithValue("$low", NumberFormat.Decimal(bar.Low));
            insert.Parameters.AddWithValue("$close", NumberFormat.Decimal(bar.Close));
            AddNullable(insert, "$volume", bar.Volume);
            AddNullable(insert, "$numberOfTrades", bar.NumberOfTrades);
            AddNullable(insert, "$bidVolume", bar.BidVolume);
            AddNullable(insert, "$askVolume", bar.AskVolume);
            if (insert.ExecuteNonQuery() == 0)
            {
                using var existing = connection.CreateCommand();
                existing.Transaction = transaction;
                existing.CommandText = "SELECT id FROM bars WHERE series_id = $series AND event_utc = $event";
                existing.Parameters.AddWithValue("$series", seriesId);
                existing.Parameters.AddWithValue("$event", eventText);
                barId = existing.ExecuteScalar() as string ?? throw new InvalidOperationException("The existing bar could not be resolved after deduplication.");
            }
        }
        using var provenance = connection.CreateCommand();
        provenance.Transaction = transaction;
        provenance.CommandText = "INSERT OR IGNORE INTO bar_imports (import_batch_id, bar_id) VALUES ($batch, $bar)";
        provenance.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        provenance.Parameters.AddWithValue("$bar", barId);
        provenance.ExecuteNonQuery();
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
        command.CommandText = "INSERT INTO trades (id, journal_id, import_batch_id, source_type, source_key, grouping_policy, sequence, symbol, account, direction, entry_utc, exit_utc, entry_price, exit_price, quantity, closed_quantity, gross_points, average_points, gross_pnl, fees, net_pnl, mae_points, mfe_points, point_value, tick_size, initial_stop_price, initial_target_price, initial_risk_points, initial_risk_currency, r_multiple, exit_type, entry_order_price, exit_order_price, entry_chase_points, exit_chase_points, status, note, created_utc, instrument, review_key) VALUES ($id, $journal, $batch, $source, $key, $grouping, $sequence, $symbol, $account, $direction, $entry, $exit, $entryPrice, $exitPrice, $quantity, $closed, $grossPoints, $averagePoints, $grossPnl, $fees, $netPnl, $mae, $mfe, $pointValue, $tickSize, $initialStop, $initialTarget, $initialRiskPoints, $initialRiskCurrency, $rMultiple, $exitType, $entryOrderPrice, $exitOrderPrice, $entryChasePoints, $exitChasePoints, $status, $note, $created, $instrument, $reviewKey)";
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
        command.Parameters.AddWithValue("$instrument", state.Instrument);
        command.Parameters.AddWithValue("$reviewKey", CreateTradeReviewKey(journalId, DerivedFillSource, state.EntrySourceKey, groupingPolicy, state.Account, state.Symbol, state.Direction));
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
                Id = Guid.Parse(reader.GetString(0)), JournalId = Guid.Parse(reader.GetString(1)), ImportBatchId = Guid.Parse(reader.GetString(2)), SourceType = reader.GetString(3), SourceKey = reader.GetString(4), ActivityType = reader.GetString(5), OrderActionSource = reader.GetString(6), EventUtc = ParseDate(reader.GetString(7)), TransactionUtc = reader.IsDBNull(8) ? null : ParseDate(reader.GetString(8)), SourceTimeText = reader.GetString(9), Symbol = reader.GetString(10), Account = reader.GetString(11), Side = reader.GetString(12), Quantity = reader.GetInt32(13), Price = ParseDecimal(reader.GetString(14)), Price2 = NullableDecimal(reader, 15), FilledQuantity = reader.IsDBNull(16) ? null : reader.GetInt32(16), OpenClose = reader.GetString(17), OrderType = reader.GetString(18), OrderStatus = reader.GetString(19), ParentOrderId = reader.GetString(20), High = NullableDecimal(reader, 21), Low = NullableDecimal(reader, 22), Note = reader.GetString(23), PositionQuantity = reader.IsDBNull(24) ? null : reader.GetInt32(24), OrderId = reader.GetString(25), ServiceOrderId = reader.GetString(26), ExchangeOrderId = reader.GetString(27), FillExecutionId = reader.GetString(28), ClientOrderId = reader.GetString(29), TimeInForce = reader.GetString(30), Username = reader.GetString(31), IsAutomated = NullableBool(reader, 32), AccountBalance = NullableDecimal(reader, 33), Fees = ParseDecimal(reader.GetString(34)), RowNumber = reader.GetInt32(35), Instrument = string.IsNullOrWhiteSpace(reader.GetString(36)) ? InstrumentCatalog.ExtractRoot(reader.GetString(10)) : reader.GetString(36), PointValue = ParsePositiveOrFallback(reader, 37, InstrumentCatalog.Resolve(reader.GetString(10)).PointValue), TickSize = ParsePositiveOrFallback(reader, 38, InstrumentCatalog.Resolve(reader.GetString(10)).TickSize)
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
        Id = Guid.Parse(reader.GetString(0)), JournalId = Guid.Parse(reader.GetString(1)), ImportBatchId = Guid.Parse(reader.GetString(2)), SourceType = reader.GetString(3), SourceKey = reader.GetString(4), OrderActionSource = reader.GetString(5), EventUtc = ParseDate(reader.GetString(6)), TransactionUtc = reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)), SourceTimeText = reader.GetString(8), Symbol = reader.GetString(9), Account = reader.GetString(10), InternalOrderId = reader.GetString(11), ServiceOrderId = reader.GetString(12), ParentOrderId = reader.GetString(13), ExchangeOrderId = reader.GetString(14), FillExecutionId = reader.GetString(15), OrderType = reader.GetString(16), OrderStatus = reader.GetString(17), Side = reader.GetString(18), OpenClose = reader.GetString(19), Price = NullableDecimal(reader, 20), Price2 = NullableDecimal(reader, 21), Quantity = reader.IsDBNull(22) ? null : reader.GetInt32(22), FilledQuantity = reader.IsDBNull(23) ? null : reader.GetInt32(23), FillPrice = NullableDecimal(reader, 24), PositionQuantity = reader.IsDBNull(25) ? null : reader.GetInt32(25), Note = reader.GetString(26), ClientOrderId = reader.GetString(27), TimeInForce = reader.GetString(28), Username = reader.GetString(29), IsAutomated = NullableBool(reader, 30), Fees = ParseDecimal(reader.GetString(31)), RowNumber = reader.GetInt32(32), Instrument = string.IsNullOrWhiteSpace(reader.GetString(33)) ? InstrumentCatalog.ExtractRoot(reader.GetString(9)) : reader.GetString(33)
    };

    private static AccountBalanceEvent ReadAccountBalanceEvent(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)), JournalId = Guid.Parse(reader.GetString(1)), ImportBatchId = Guid.Parse(reader.GetString(2)), SourceType = reader.GetString(3), SourceKey = reader.GetString(4), EventUtc = ParseDate(reader.GetString(5)), TransactionUtc = reader.IsDBNull(6) ? null : ParseDate(reader.GetString(6)), SourceTimeText = reader.GetString(7), Account = reader.GetString(8), Balance = NullableDecimal(reader, 9), Note = reader.GetString(10), RowNumber = reader.GetInt32(11)
    };

    private static AccountTransaction ReadAccountTransaction(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        JournalId = Guid.Parse(reader.GetString(1)),
        Type = ParseAccountTransactionType(reader.GetString(2)),
        EffectiveUtc = ParseDate(reader.GetString(3)),
        Amount = ParseDecimal(reader.GetString(4)),
        Note = reader.GetString(5),
        Revision = reader.GetInt32(6),
        CreatedUtc = ParseDate(reader.GetString(7)),
        UpdatedUtc = ParseDate(reader.GetString(8)),
        DeletedUtc = reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9))
    };

    private static AccountTransaction? ReadAccountTransaction(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, Guid transactionId, bool includeDeleted)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {AccountTransactionColumns} FROM account_transactions WHERE journal_id = $journal AND id = $id {(includeDeleted ? string.Empty : "AND deleted_utc IS NULL")}";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$id", transactionId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAccountTransaction(reader) : null;
    }

    private static AccountTransactionHistoryEntry ReadAccountTransactionHistory(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        JournalId = Guid.Parse(reader.GetString(1)),
        TransactionId = Guid.Parse(reader.GetString(2)),
        Revision = reader.GetInt32(3),
        Action = reader.GetString(4),
        BeforeJson = reader.GetString(5),
        AfterJson = reader.GetString(6),
        CreatedUtc = ParseDate(reader.GetString(7))
    };

    private static void InsertAccountTransaction(SqliteConnection connection, SqliteTransaction transaction, AccountTransaction entry)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO account_transactions (id, journal_id, transaction_type, effective_utc, amount, note, revision, created_utc, updated_utc, deleted_utc) VALUES ($id, $journal, $type, $effective, $amount, $note, $revision, $created, $updated, $deleted)";
        command.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
        command.Parameters.AddWithValue("$journal", entry.JournalId.ToString("D"));
        command.Parameters.AddWithValue("$type", AccountTransactionTypeValue(entry.Type));
        command.Parameters.AddWithValue("$effective", entry.EffectiveUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$amount", NumberFormat.Decimal(entry.Amount));
        command.Parameters.AddWithValue("$note", entry.Note);
        command.Parameters.AddWithValue("$revision", entry.Revision);
        command.Parameters.AddWithValue("$created", entry.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", entry.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        AddNullable(command, "$deleted", entry.DeletedUtc);
        command.ExecuteNonQuery();
    }

    private static void InsertAccountTransactionHistory(SqliteConnection connection, SqliteTransaction transaction, AccountTransaction entry, int revision, string action, string beforeJson, string afterJson, DateTimeOffset createdUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO account_transaction_history (id, journal_id, transaction_id, revision, action, before_json, after_json, created_utc) VALUES ($id, $journal, $transaction, $revision, $action, $before, $after, $created)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$journal", entry.JournalId.ToString("D"));
        command.Parameters.AddWithValue("$transaction", entry.Id.ToString("D"));
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$before", beforeJson);
        command.Parameters.AddWithValue("$after", afterJson);
        command.Parameters.AddWithValue("$created", createdUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static void ValidateAccountTransactionDraft(AccountTransactionDraft draft)
    {
        if (draft.Type is not AccountTransactionType.Deposit and not AccountTransactionType.Withdrawal)
            throw new ArgumentException("The account transaction type is invalid.", nameof(draft));
        if (draft.Amount <= 0m)
            throw new ArgumentException("Account transaction amount must be greater than zero.", nameof(draft));
        if (draft.EffectiveUtc == default)
            throw new ArgumentException("Account transaction time is required.", nameof(draft));
    }

    private static string AccountTransactionTypeValue(AccountTransactionType type) => type switch
    {
        AccountTransactionType.Deposit => "deposit",
        AccountTransactionType.Withdrawal => "withdrawal",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static AccountTransactionType ParseAccountTransactionType(string value) => value.ToLowerInvariant() switch
    {
        "deposit" => AccountTransactionType.Deposit,
        "withdrawal" => AccountTransactionType.Withdrawal,
        _ => throw new FormatException($"Unknown account transaction type '{value}'.")
    };

    private static BenchmarkPoint ReadBenchmarkPoint(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)), JournalId = Guid.Parse(reader.GetString(1)), ImportBatchId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), SeriesId = Guid.Parse(reader.GetString(3)), SourceType = reader.GetString(4), SourceKey = reader.GetString(5), Symbol = reader.GetString(6), Provider = reader.GetString(7), EventUtc = ParseDate(reader.GetString(8)), Value = ParseDecimal(reader.GetString(9)), SourceTimeText = reader.GetString(10), RowNumber = reader.GetInt32(11)
    };

    private static BenchmarkSeriesStatus ReadBenchmarkSeriesStatus(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        JournalId = Guid.Parse(reader.GetString(1)),
        Symbol = reader.GetString(2),
        Interval = reader.GetString(3),
        Provider = reader.GetString(4),
        SourceUrl = reader.GetString(5),
        CreatedUtc = ParseDate(reader.GetString(6)),
        LastFetchedUtc = reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)),
        LastAttemptedUtc = reader.IsDBNull(8) ? null : ParseDate(reader.GetString(8)),
        RequestedStart = NullableDateOnly(reader, 9),
        RequestedEnd = NullableDateOnly(reader, 10),
        LastError = reader.GetString(11),
        PointCount = Convert.ToInt32(reader.GetInt64(12), CultureInfo.InvariantCulture),
        FirstDate = NullableDateOnlyFromTimestamp(reader, 13),
        LastDate = NullableDateOnlyFromTimestamp(reader, 14)
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
        var initialRiskCurrency = initialRiskPoints.HasValue ? initialRiskPoints.Value * quantity * state.PointValue : (decimal?)null;
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
            state.PointValue,
            state.TickSize,
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

    public ImportBatch? GetImport(Guid importId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ImportColumns} FROM import_batches WHERE id = $id";
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

    private static void BackfillTradeReviewKeys(SqliteConnection connection)
    {
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT id, journal_id, source_type, source_key, grouping_policy, account, symbol, direction FROM trades WHERE review_key = '' OR review_key IS NULL";
        var rows = new List<(string Id, Guid JournalId, string SourceType, string SourceKey, string Grouping, string Account, string Symbol, string Direction)>();
        using (var reader = read.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7)));
            }
        }

        foreach (var row in rows)
        {
            var anchor = row.SourceKey;
            if (row.SourceType.Equals(DerivedFillSource, StringComparison.OrdinalIgnoreCase))
            {
                var separator = anchor.LastIndexOf(':');
                if (separator > 0 && int.TryParse(anchor[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    anchor = anchor[..separator];
            }
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE trades SET review_key = $reviewKey WHERE id = $id";
            update.Parameters.AddWithValue("$reviewKey", CreateTradeReviewKey(row.JournalId, row.SourceType, anchor, row.Grouping, row.Account, row.Symbol, row.Direction));
            update.Parameters.AddWithValue("$id", row.Id);
            update.ExecuteNonQuery();
        }
    }

    public static string CreateTradeReviewKey(Guid journalId, string sourceType, string sourceAnchor, string groupingPolicy, string account, string symbol, string direction)
    {
        static string Fold(string value) => value.Trim().ToUpperInvariant();
        var canonical = sourceType.Equals(DerivedFillSource, StringComparison.OrdinalIgnoreCase)
            ? string.Join('\u001f', "v1", "derived", journalId.ToString("D"), Fold(sourceType), sourceAnchor.Trim(), Fold(groupingPolicy), Fold(account), Fold(symbol), Fold(direction))
            : string.Join('\u001f', "v1", "direct", journalId.ToString("D"), Fold(sourceType), sourceAnchor.Trim());
        return "tfrk_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static McpAccessToken ReadMcpAccessToken(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        TokenPrefix = reader.GetString(2),
        Scopes = reader.GetString(3),
        CreatedUtc = ParseDate(reader.GetString(4)),
        LastUsedUtc = reader.IsDBNull(5) ? null : ParseDate(reader.GetString(5)),
        RevokedUtc = reader.IsDBNull(6) ? null : ParseDate(reader.GetString(6))
    };

    private static IReadOnlyList<Guid> ReadMcpTokenJournalIds(SqliteConnection connection, Guid tokenId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT journal_id FROM mcp_token_journals WHERE token_id = $token ORDER BY journal_id";
        command.Parameters.AddWithValue("$token", tokenId.ToString("D"));
        var ids = new List<Guid>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0)));
        return ids;
    }

    private static McpAccessToken WithJournalIds(McpAccessToken token, IReadOnlyList<Guid> journalIds) => new()
    {
        Id = token.Id,
        Name = token.Name,
        TokenPrefix = token.TokenPrefix,
        Scopes = token.Scopes,
        CreatedUtc = token.CreatedUtc,
        LastUsedUtc = token.LastUsedUtc,
        RevokedUtc = token.RevokedUtc,
        JournalIds = journalIds
    };

    private static Journal ReadJournal(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)), Name = reader.GetString(1), ExecutionContext = reader.GetString(2), Labels = reader.GetString(3), DescriptionMarkdown = reader.GetString(4), TimeZone = reader.GetString(5), Currency = reader.GetString(6), GroupingPolicy = reader.GetString(7), StartingEquity = NullableDecimal(reader, 8), CreatedUtc = ParseDate(reader.GetString(9))
    };

    private static string InferSourceApplication(string sourceApplication, string sourceType)
    {
        if (!string.IsNullOrWhiteSpace(sourceApplication)) return sourceApplication;
        return sourceType switch
        {
            TradeFoundryConstants.SierraFills => TradeFoundryConstants.SierraChart,
            TradeFoundryConstants.TradingViewAccount or TradeFoundryConstants.TradingViewStrategy => TradeFoundryConstants.TradingView,
            TradeFoundryConstants.BenchmarkSeries => TradeFoundryConstants.Benchmark,
            TradeFoundryConstants.OhlcvBars => TradeFoundryConstants.Ohlcv,
            TradeFoundryConstants.SierraOhlcBars => TradeFoundryConstants.SierraChart,
            _ => string.Empty
        };
    }

    private static ImportBatch ReadImport(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)), JournalId = Guid.Parse(reader.GetString(1)), FileName = reader.GetString(2), SourceApplication = InferSourceApplication(reader.IsDBNull(3) ? string.Empty : reader.GetString(3), reader.GetString(4)), SourceType = reader.GetString(4), ImportedUtc = ParseDate(reader.GetString(5)), TotalRows = reader.GetInt32(6), NewRows = reader.GetInt32(7), DuplicateRows = reader.GetInt32(8), Status = reader.GetString(9), Message = reader.GetString(10)
    };

    private static TradeReviewAnnotation ReadTradeReview(SqliteDataReader reader)
    {
        IReadOnlyList<string> tags;
        try
        {
            tags = JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            tags = Array.Empty<string>();
        }

        return new TradeReviewAnnotation
        {
            JournalId = Guid.Parse(reader.GetString(1)),
            ReviewKey = reader.GetString(2),
            Revision = reader.GetInt32(3),
            ReviewNote = reader.GetString(4),
            Setup = reader.GetString(5),
            Tags = tags,
            PlannedEntryPrice = NullableDecimal(reader, 7),
            PlannedStopPrice = NullableDecimal(reader, 8),
            PlannedTargetPrice = NullableDecimal(reader, 9),
            PlannedRiskPoints = NullableDecimal(reader, 10),
            PlannedRiskCurrency = NullableDecimal(reader, 11),
            AllInCommission = NullableDecimal(reader, 12),
            PlanAdherence = reader.GetString(13),
            ProcessRating = reader.IsDBNull(14) ? null : reader.GetInt32(14),
            Mistakes = reader.GetString(15),
            Lessons = reader.GetString(16),
            UpdatedUtc = ParseDate(reader.GetString(17))
        };
    }

    private static TradeReviewHistoryEntry ReadTradeReviewHistory(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        JournalId = Guid.Parse(reader.GetString(1)),
        ReviewKey = reader.GetString(2),
        Revision = reader.GetInt32(3),
        Action = reader.GetString(4),
        BeforeJson = reader.GetString(5),
        AfterJson = reader.GetString(6),
        Reason = reader.GetString(7),
        CreatedUtc = ParseDate(reader.GetString(8))
    };

    private static TradeReviewAttachment ReadTradeReviewAttachment(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        JournalId = Guid.Parse(reader.GetString(1)),
        ReviewKey = reader.GetString(2),
        StorageKey = reader.GetString(3),
        OriginalFileName = reader.GetString(4),
        ContentType = reader.GetString(5),
        Length = reader.GetInt64(6),
        CreatedUtc = ParseDate(reader.GetString(7))
    };

    private static DailyJournalEntry ReadDailyJournal(SqliteDataReader reader) => new()
    {
        JournalId = Guid.Parse(reader.GetString(1)),
        Date = DateOnly.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
        Revision = reader.GetInt32(3),
        Text = reader.GetString(4),
        UpdatedUtc = reader.IsDBNull(5) ? null : ParseDate(reader.GetString(5))
    };

    private static DailyJournalEntry ReadDailyJournalOrDefault(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, DateOnly date)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, journal_id, review_date, revision, journal_text, updated_utc FROM daily_review_journals WHERE journal_id = $journal AND review_date = $date";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ReadDailyJournal(reader)
            : new DailyJournalEntry { JournalId = journalId, Date = date };
    }

    private static void InsertDailyJournalHistory(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, DateOnly date, int revision, string action, string beforeJson, string afterJson, string reason, DateTimeOffset createdUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO daily_review_journal_history (id, journal_id, review_date, revision, action, before_json, after_json, reason, created_utc) VALUES ($id, $journal, $date, $revision, $action, $before, $after, $reason, $created)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$before", beforeJson);
        command.Parameters.AddWithValue("$after", afterJson);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$created", createdUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static TradeReviewAnnotation EmptyTradeReview(Guid journalId, string reviewKey, int revision = 0) => new()
    {
        JournalId = journalId,
        ReviewKey = reviewKey,
        Revision = revision
    };

    private static TradeReviewPatch NormalizeReviewPatch(TradeReviewPatch patch) => new()
    {
        ReviewNote = TrimTo(patch.ReviewNote, 4000),
        Setup = TrimTo(patch.Setup, 240),
        TagsText = string.Join(", ", ParseReviewTags(patch.TagsText)),
        PlannedEntryPrice = patch.PlannedEntryPrice,
        PlannedStopPrice = patch.PlannedStopPrice,
        PlannedTargetPrice = patch.PlannedTargetPrice,
        PlannedRiskPoints = patch.PlannedRiskPoints,
        PlannedRiskCurrency = patch.PlannedRiskCurrency,
        AllInCommission = patch.AllInCommission,
        PlanAdherence = TrimTo(patch.PlanAdherence, 32).ToLowerInvariant(),
        ProcessRating = patch.ProcessRating,
        Mistakes = TrimTo(patch.Mistakes, 2000),
        Lessons = TrimTo(patch.Lessons, 2000),
        Reason = TrimTo(patch.Reason, 500),
        ExpectedRevision = patch.ExpectedRevision
    };

    private static TradeReviewAnnotation AnnotationFromPatch(Guid journalId, string reviewKey, int revision, TradeReviewPatch patch, DateTimeOffset updatedUtc) => new()
    {
        JournalId = journalId,
        ReviewKey = reviewKey,
        Revision = revision,
        ReviewNote = patch.ReviewNote,
        Setup = patch.Setup,
        Tags = ParseReviewTags(patch.TagsText),
        PlannedEntryPrice = patch.PlannedEntryPrice,
        PlannedStopPrice = patch.PlannedStopPrice,
        PlannedTargetPrice = patch.PlannedTargetPrice,
        PlannedRiskPoints = patch.PlannedRiskPoints,
        PlannedRiskCurrency = patch.PlannedRiskCurrency,
        AllInCommission = patch.AllInCommission,
        PlanAdherence = patch.PlanAdherence,
        ProcessRating = patch.ProcessRating,
        Mistakes = patch.Mistakes,
        Lessons = patch.Lessons,
        UpdatedUtc = updatedUtc
    };

    private static TradeReviewPatch PatchFromAnnotation(TradeReviewAnnotation annotation, int expectedRevision, string reason) => new()
    {
        ReviewNote = annotation.ReviewNote,
        Setup = annotation.Setup,
        TagsText = string.Join(", ", annotation.Tags),
        PlannedEntryPrice = annotation.PlannedEntryPrice,
        PlannedStopPrice = annotation.PlannedStopPrice,
        PlannedTargetPrice = annotation.PlannedTargetPrice,
        PlannedRiskPoints = annotation.PlannedRiskPoints,
        PlannedRiskCurrency = annotation.PlannedRiskCurrency,
        AllInCommission = annotation.AllInCommission,
        PlanAdherence = annotation.PlanAdherence,
        ProcessRating = annotation.ProcessRating,
        Mistakes = annotation.Mistakes,
        Lessons = annotation.Lessons,
        Reason = reason,
        ExpectedRevision = expectedRevision
    };

    private static IReadOnlyList<string> ParseReviewTags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        return text
            .Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => TrimTo(tag, 80))
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .ToArray();
    }

    private static string TrimTo(string? value, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string SerializeReview(TradeReviewAnnotation annotation) => JsonSerializer.Serialize(annotation);

    private static TradeReviewAnnotation ReadTradeReviewOrHistoryRevision(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, string reviewKey)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT {TradeReviewColumns} FROM trade_review_annotations WHERE journal_id = $journal AND review_key = $reviewKey";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            command.Parameters.AddWithValue("$reviewKey", reviewKey);
            using var reader = command.ExecuteReader();
            if (reader.Read()) return ReadTradeReview(reader);
        }

        using var history = connection.CreateCommand();
        history.Transaction = transaction;
        history.CommandText = "SELECT MAX(revision) FROM trade_review_history WHERE journal_id = $journal AND review_key = $reviewKey";
        history.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        history.Parameters.AddWithValue("$reviewKey", reviewKey);
        var value = history.ExecuteScalar();
        return value is null or DBNull ? EmptyTradeReview(journalId, reviewKey) : EmptyTradeReview(journalId, reviewKey, Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }

    private static void InsertTradeReviewHistory(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, string reviewKey, int revision, string action, string beforeJson, string afterJson, string reason, DateTimeOffset createdUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO trade_review_history (id, journal_id, review_key, revision, action, before_json, after_json, reason, created_utc) VALUES ($id, $journal, $reviewKey, $revision, $action, $before, $after, $reason, $created)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$reviewKey", reviewKey);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$before", string.IsNullOrWhiteSpace(beforeJson) ? "{}" : beforeJson);
        command.Parameters.AddWithValue("$after", string.IsNullOrWhiteSpace(afterJson) ? "{}" : afterJson);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$created", createdUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> ReadReviewKeysForImport(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, Guid batchId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DISTINCT review_key FROM trades WHERE journal_id = $journal AND review_key <> '' AND (import_batch_id = $batch OR EXISTS (SELECT 1 FROM trade_fill_allocations a JOIN fills f ON f.id = a.fill_id WHERE a.trade_id = trades.id AND f.import_batch_id = $batch))";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        using var reader = command.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read()) keys.Add(reader.GetString(0));
        return keys;
    }

    private static void RemoveTradeReviewForSource(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, string reviewKey)
    {
        var current = ReadTradeReviewOrHistoryRevision(connection, transaction, journalId, reviewKey);
        InsertTradeReviewHistory(connection, transaction, journalId, reviewKey, current.Revision + 1, "source_removed", SerializeReview(current), "{}", "Source import was undone; review annotations were removed.", DateTimeOffset.UtcNow);

        using (var annotation = connection.CreateCommand())
        {
            annotation.Transaction = transaction;
            annotation.CommandText = "DELETE FROM trade_review_annotations WHERE journal_id = $journal AND review_key = $reviewKey";
            annotation.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            annotation.Parameters.AddWithValue("$reviewKey", reviewKey);
            annotation.ExecuteNonQuery();
        }

        using var attachments = connection.CreateCommand();
        attachments.Transaction = transaction;
        attachments.CommandText = "UPDATE trade_review_attachments SET removed_utc = $removed WHERE journal_id = $journal AND review_key = $reviewKey AND removed_utc IS NULL";
        attachments.Parameters.AddWithValue("$removed", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        attachments.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        attachments.Parameters.AddWithValue("$reviewKey", reviewKey);
        attachments.ExecuteNonQuery();
    }

    private static Trade ReadTrade(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)), JournalId = Guid.Parse(reader.GetString(1)), ImportBatchId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), SourceType = reader.GetString(3), SourceKey = reader.GetString(4), GroupingPolicy = reader.GetString(5), Sequence = reader.GetInt32(6), Symbol = reader.GetString(7), Account = reader.GetString(8), Direction = reader.GetString(9), EntryUtc = ParseDate(reader.GetString(10)), ExitUtc = reader.IsDBNull(11) ? null : ParseDate(reader.GetString(11)), EntryPrice = ParseDecimal(reader.GetString(12)), ExitPrice = reader.IsDBNull(13) ? null : ParseDecimal(reader.GetString(13)), Quantity = reader.GetInt32(14), ClosedQuantity = reader.GetInt32(15), GrossPoints = ParseDecimal(reader.GetString(16)), AveragePoints = ParseDecimal(reader.GetString(17)), GrossPnl = ParseDecimal(reader.GetString(18)), Fees = ParseDecimal(reader.GetString(19)), NetPnl = ParseDecimal(reader.GetString(20)), MaePoints = reader.IsDBNull(21) ? null : ParseDecimal(reader.GetString(21)), MfePoints = reader.IsDBNull(22) ? null : ParseDecimal(reader.GetString(22)), PointValue = ParseDecimal(reader.GetString(23)), TickSize = ParseDecimal(reader.GetString(24)), InitialStopPrice = NullableDecimal(reader, 25), InitialTargetPrice = NullableDecimal(reader, 26), InitialRiskPoints = NullableDecimal(reader, 27), InitialRiskCurrency = NullableDecimal(reader, 28), RMultiple = NullableDecimal(reader, 29), ExitType = reader.GetString(30), EntryOrderPrice = NullableDecimal(reader, 31), ExitOrderPrice = NullableDecimal(reader, 32), EntryChasePoints = NullableDecimal(reader, 33), ExitChasePoints = NullableDecimal(reader, 34), Status = reader.GetString(35), Note = reader.GetString(36), Instrument = string.IsNullOrWhiteSpace(reader.GetString(37)) ? InstrumentCatalog.ExtractRoot(reader.GetString(7)) : reader.GetString(37), ReviewKey = reader.GetString(38), HasReviewNotes = Convert.ToInt32(reader.GetValue(39), CultureInfo.InvariantCulture) != 0, HasReviewImages = Convert.ToInt32(reader.GetValue(40), CultureInfo.InvariantCulture) != 0
    };

    private static Bar ReadBar(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)), SeriesId = Guid.Parse(reader.GetString(1)), Symbol = reader.GetString(2), Interval = reader.GetString(3), EventUtc = ParseDate(reader.GetString(4)), Open = ParseDecimal(reader.GetString(5)), High = ParseDecimal(reader.GetString(6)), Low = ParseDecimal(reader.GetString(7)), Close = ParseDecimal(reader.GetString(8)), Volume = reader.IsDBNull(9) ? null : reader.GetInt64(9), NumberOfTrades = reader.IsDBNull(10) ? null : reader.GetInt64(10), BidVolume = reader.IsDBNull(11) ? null : reader.GetInt64(11), AskVolume = reader.IsDBNull(12) ? null : reader.GetInt64(12)
    };

    private static Fill ScaleFill(Fill fill, int quantity)
    {
        var fee = fill.Quantity == 0 ? 0m : fill.Fees * quantity / fill.Quantity;
        return new Fill
        {
            Id = fill.Id, JournalId = fill.JournalId, ImportBatchId = fill.ImportBatchId, SourceType = fill.SourceType, SourceKey = fill.SourceKey,
            ActivityType = fill.ActivityType, OrderActionSource = fill.OrderActionSource, EventUtc = fill.EventUtc, TransactionUtc = fill.TransactionUtc, SourceTimeText = fill.SourceTimeText, Symbol = fill.Symbol, Account = fill.Account, Side = fill.Side,
            Quantity = quantity, Price = fill.Price, Price2 = fill.Price2, FilledQuantity = fill.FilledQuantity, OpenClose = fill.OpenClose, OrderType = fill.OrderType, OrderStatus = fill.OrderStatus, ParentOrderId = fill.ParentOrderId, High = fill.High, Low = fill.Low, Note = fill.Note,
            PositionQuantity = fill.PositionQuantity, OrderId = fill.OrderId, ServiceOrderId = fill.ServiceOrderId, ExchangeOrderId = fill.ExchangeOrderId, FillExecutionId = fill.FillExecutionId, ClientOrderId = fill.ClientOrderId, TimeInForce = fill.TimeInForce, Username = fill.Username, IsAutomated = fill.IsAutomated, AccountBalance = fill.AccountBalance, Fees = fee, RowNumber = fill.RowNumber, Instrument = fill.Instrument, PointValue = fill.PointValue, TickSize = fill.TickSize
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
    private static DateOnly? NullableDateOnly(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : DateOnly.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);
    private static DateOnly? NullableDateOnlyFromTimestamp(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : DateOnly.FromDateTime(ParseDate(reader.GetString(ordinal)).UtcDateTime.Date);
    private static decimal ParsePositiveOrFallback(SqliteDataReader reader, int ordinal, decimal fallback)
    {
        if (reader.IsDBNull(ordinal)) return fallback;
        var value = ParseDecimal(reader.GetString(ordinal));
        return value > 0m ? value : fallback;
    }
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
            Symbol = fill.Symbol; Instrument = string.IsNullOrWhiteSpace(fill.Instrument) ? InstrumentCatalog.ExtractRoot(fill.Symbol) : fill.Instrument; Account = fill.Account; Direction = direction > 0 ? "Long" : "Short"; PointValue = fill.PointValue > 0m ? fill.PointValue : InstrumentCatalog.Resolve(Instrument).PointValue; TickSize = fill.TickSize > 0m ? fill.TickSize : InstrumentCatalog.Resolve(Instrument).TickSize; SignedQuantity = direction * quantity; OpenQuantity = quantity; MaxOpenQuantity = quantity; EntryCost = fill.Price * quantity; EntryUtc = fill.EventUtc; EntrySourceKey = fill.SourceKey; ImportBatchId = fill.ImportBatchId; Fees = fill.Fees; EntryReferences.Add(OrderReference.From(fill, quantity)); Notes.Add(fill.Note); AddExcursion(fill, fill.Price, direction);
        }

        public string Symbol { get; }
        public string Instrument { get; }
        public string Account { get; }
        public string Direction { get; }
        public decimal PointValue { get; }
        public decimal TickSize { get; }
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
            var feePortion = fill.Quantity == 0 ? 0m : fill.Fees * quantity / fill.Quantity;
            ClosedEntryCost += entryAverage * quantity;
            ExitCost += fill.Price * quantity;
            GrossPoints += move * quantity;
            GrossPnl += move * quantity * PointValue;
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

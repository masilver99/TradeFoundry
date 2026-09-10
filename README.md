# TradeFoundry

TradeFoundry is a local-first futures trading journal. It imports Sierra Chart Trade Activity fills and order events, TradingView account or Strategy Tester exports, benchmark daily series, and OHLCV bars for chart snippets. The phase 1 ledger is intentionally read-only: raw rows stay linked to typed source evidence and deterministic flat-to-flat trades.

## Run locally

This project targets .NET 10.

```powershell
cd C:\dev\TradeFoundry
dotnet run --urls http://127.0.0.1:5080
```

Open `http://127.0.0.1:5080`. On first run, create the local owner account, create a journal, and upload an export. The SQLite database is created at `.tradefoundry-data/journal.db` (or the path in `Storage:DataDirectory`).

## Run with Docker

```bash
docker compose up -d --build
```

The host `data/` directory is mounted into the container. Back up the database while the app is stopped, or use SQLite’s online backup tooling; keep the active database on local storage rather than SMB/NFS.

## Imports

* Sierra Chart: export the Trade Activity Log as the tab-delimited file containing `ActivityType`, `DateTime`, `TransDateTime`, `OrderActionSource`, `Symbol`, `InternalOrderID`, `ServiceOrderID`, `ParentInternalOrderID`, `OrderType`, `OrderStatus`, `Quantity`, `FilledQuantity`, `BuySell`, `Price`, `Price2`, `FillPrice`, `TradeAccount`, `OpenClose`, `PositionQuantity`, `FillExecutionServiceID`, `ExchangeOrderID`, `HighDuringPosition`, `LowDuringPosition`, `AccountBalance`, and `Note`. `Fills` rows become executions, `Orders` rows become immutable order-lifecycle events, and `Account Balance` rows become balance events. Leave the trade source timezone on **Auto-detect** for Sierra `File >> Export` files; their timestamps are UTC. Sierra `File >> Save Log As` files use the visible Sierra timezone and should use the journal timezone instead. Some Sierra simulator exports encode prices as fixed-point values (for example, `764725` for `7647.25`); TradeFoundry detects that representation from `OrderActionSource` and stores normalized prices while preserving the original row.
* TradingView account history: a generic CSV with symbol, side/action, quantity, execution price, timestamp, and an optional ID/status/fee column.
* TradingView Strategy Tester: rows grouped by `Trade #`/`Trade Number`, with entry/exit prices and times where supplied.
* Benchmark daily series: choose `Benchmark daily series` and import a CSV/TSV with a date or timestamp plus `Close`, `Adj Close`, or `Total Return` (and an optional `Symbol`/`Ticker`). The ticker defaults to `^GSPC` when it is not present.
* Sierra Chart OHLC bars: use the separate market-data importer on the Imports page. It accepts the standard `Date`, `Time`, `Open`, `High`, `Low`, `Last`, `Volume`, `NumberOfTrades`, `BidVolume`, and `AskVolume` columns from a CSV/TSV file or pasted text. Enter the CME root (`MES`, `ES`, `NQ`, and so on); contract symbols are normalized to that root. Leave the source timezone on **Auto-detect** for ordinary chart exports (the journal timezone), or choose UTC when the Sierra bar export was configured to write UTC; explicit offsets in a row take precedence. The interval is inferred from the first valid rows unless overridden (`1m` or greater). Normalized bars are shared across journals and indexed by root, interval, and UTC timestamp; the trade chart renders the stored UTC events in the journal timezone and can consolidate finer data into a selected coarser timeframe.

When the Analysis or Indicators page is opened and the journal has completed trades, TradeFoundry automatically refreshes the default `^GSPC` daily benchmark from Yahoo Finance when the cached data is missing, incomplete, or older than the configured refresh interval. The downloaded points and provider metadata are stored in the journal database, manual benchmark imports remain available as a fallback, and the `Refresh data` button forces an immediate update. Configure this behavior under the `Benchmark` section in `appsettings.json`; set `AutomaticRefresh` to `false` to keep benchmark downloads manual.

Imports are scoped to the selected journal. Sierra rows use a deterministic SHA-256 fingerprint of the complete row, so fills and order events that share an execution id remain distinct while exact duplicate rows still deduplicate safely. Re-importing a file is safe. The **Undo** action removes a batch and rebuilds derived fill-based trades. Derived trades retain point value, initial target/stop intent, initial risk, R-multiple, exit classification, entry/exit order prices, chase points, and UTC entry/exit times; `TimeInTrade` is calculated from those timestamps. Phase 1 derives fill sources flat-to-flat; the FIFO-lots choice is reserved for phase 2 while the schema already preserves the fill allocations needed for it.

## Instrument and application configuration

Settings seeds common futures instruments including MES, ES, MNQ, NQ, MYM, M2K, MCL, MGC, YM, RTY, CL, GC, and 6E. Each definition stores a default commission per contract, dollar value per point, and tick size. The Sierra Chart section stores ordered regular-expression mappings from the imported `Symbol` value to an instrument; a mapping commission override is also per contract and takes precedence over the instrument default. A blank configured commission preserves a fee reported by the source when available.

Every import batch records its trading application as well as its source format. Typed fills, order events, and derived trades retain the batch link, so the application provenance remains available from the import history and trade detail pages.

## Storage and boundaries

There is one app process and one SQLite database per self-hosted installation. Multiple standalone journals share the database and are partitioned by `journal_id`; the journal also stores execution context (`live`, `paper`, `replay`, `backtest`, or `other`) and free-form labels. SQLite WAL and a five-second busy timeout are enabled at startup.

Phase 2 will add audited manual corrections, notes, screenshots, setups, scorecards, saved views, and the read-only journal assistant. AI features should use a server-held OpenAI API key and deterministic, journal-scoped read-only tools; users should never enter ChatGPT/Codex credentials into this app.

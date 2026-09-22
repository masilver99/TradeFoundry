# TradeFoundry

TradeFoundry is a local-first futures trading journal. It currently imports Sierra Chart Trade Activity exports, but there are plans to add TradingView and NinjaTrader exports.

The GitHub repo is accepting pull requests, but the primary repo is self hosted and private. 

The code is copyrighted CC BY-NC 4.0. The code is free to modify and use as long as it attributed and not used for commercial purposes. https://creativecommons.org/licenses/by-nc/4.0/deed.en

Much of the code has been written with AI, and it has left some rough edges due to the speed of development, but these rough edges are slowly being smoothed out and the UI is slowing being polished.  

I'm already using it to view stats on my trading.

Eventually, there will be a docker image to self host and a shell application for those that want to run it locally as an app.

## Current Status

This is pre-alpha software and breaking changes are a constant refrain. 

## Features

- Imports Sierra Chart trade activity
  - Sierra Chart is famous for it's rich trade activity export since it tracks SL and TP adjustements, order cancellations, MAE, etc.
- Copious charts and statistics, including
  - Calendar view
  - Quarterly view to assist with taxes (US only)
  - Tons of performance metrics
  - Numerous charts
- Broker cost comparison
  - Compare editable per-side commission and fee schedules against journal activity
  - See projected monthly and annual cost, savings, and a side-by-side chart
- Daybook
  - This contains all your trades and allows you to edit or make changes
  - This is still in very heavy development and will probably change a fair amount
- Optional MCP Server for journal-scoped historical analysis and broker fee profile management

## Run locally

This project targets .NET 10.

```powershell
cd C:\dev\TradeFoundry
dotnet run --urls http://127.0.0.1:5080
```

Open `http://127.0.0.1:5080`. On first run, create the local owner account, create a journal, and upload an export. The SQLite database is created at `.tradefoundry-data/journal.db` (or the path in `Storage:DataDirectory`).

## Windows desktop package

The installed Windows build is a self-contained `win-x64` package with a native WPF/WebView2 shell. It keeps the existing Razor UI but displays it in a single TradeFoundry window without browser tabs or an address bar. The local server remains loopback-only at `http://127.0.0.1:5080`. The installed build uses a per-user configuration file at `%LOCALAPPDATA%\TradeFoundry\appsettings.user.json`; the installer writes the selected journal data directory there. Database files and review attachments remain outside the application install directory.

The Microsoft Edge WebView2 Runtime must be available on the Windows machine. Windows 10 and 11 systems normally already have it through Microsoft Edge; the shell shows a clear startup error if it is missing.

To create the publish payload:

~~~powershell
pwsh -File scripts\publish-windows.ps1
~~~

Install Inno Setup 7 and pass `-CompileInstaller` to compile the installer in the same run:

~~~powershell
pwsh -File scripts\publish-windows.ps1 -CompileInstaller
~~~

The script searches both standard Program Files locations. For a custom installation, pass the compiler path explicitly with `-InnoSetupPath C:\path\to\ISCC.exe`.

The installer is per-user and does not remove the selected data directory during uninstall. Re-running it reuses the previously selected data directory.

## Run with Docker

```bash
docker compose up -d --build
```

The host `data/` directory is mounted into the container. Back up the database while the app is stopped, or use SQLite’s online backup tooling; keep the active database on local storage rather than SMB/NFS.

## Local MCP server

TradeFoundry includes an optional, scoped MCP server for desktop AI clients. It is disabled by default and listens on a separate loopback-only endpoint, so the normal browser listener does not expose MCP routes. This first release is for native local runs only; Docker, LAN, and internet-reachable MCP use are unsupported.

To enable it, set `Mcp:Enabled` to `true` in local configuration (or set the `Mcp__Enabled=true` environment variable) and restart TradeFoundry. The default Streamable HTTP endpoint is `http://127.0.0.1:5081/mcp`. The host and port can be changed with `Mcp:Url`, but the address must remain a numeric loopback address.

Open Settings, create an MCP access token, and select the journals that client may read. Tokens are read-only by default; you can explicitly grant a token the separate write scope for creating and editing user-managed broker fee profiles used by the comparison page. The full `tfmcp_...` secret is displayed once; TradeFoundry stores only its SHA-256 hash and a short identifying prefix. Configure the AI client to use Streamable HTTP at the MCP endpoint with `Authorization: Bearer <token>`. Tokens can be revoked from Settings at any time and cannot use the browser session cookie.

The server publishes these deterministic tools: `list_journals`, `get_journal_overview`, `search_trades`, `get_trade_detail`, `get_trade_price_context`, `get_trading_day`, `analyze_trades`, `get_data_quality`, `list_broker_fee_profiles`, `create_broker_fee_profile`, and `update_broker_fee_profile`. The last two require the explicit write scope. It also publishes `review_trade`, `review_day`, and `review_period` prompt templates. Trades are addressed by stable `review_key` values rather than rebuildable database IDs.

MCP responses contain normalized historical evidence, calculations, explicit data gaps, and relative paths back to TradeFoundry. The server does not expose raw import records, credentials, usernames, or internal order IDs; it does not refresh benchmark data, import files, repair data, call an LLM, provide live signals, place orders, or mutate imported journal evidence. Write-scoped fee profile changes are revisioned and audited separately from imported evidence. Source notes are bounded and labeled as untrusted data. Rate and analysis-concurrency limits are configurable under `Mcp`.

MCP fee profile writes are intentionally limited to explicit user-managed broker comparison inputs. Imported evidence and deterministic trade results remain immutable; profile updates use revision checks, immutable audit history, journal scoping, and an owner-issued write scope.

## Imports

* Sierra Chart: export the Trade Activity Log as the tab-delimited file containing `ActivityType`, `DateTime`, `TransDateTime`, `OrderActionSource`, `Symbol`, `InternalOrderID`, `ServiceOrderID`, `ParentInternalOrderID`, `OrderType`, `OrderStatus`, `Quantity`, `FilledQuantity`, `BuySell`, `Price`, `Price2`, `FillPrice`, `TradeAccount`, `OpenClose`, `PositionQuantity`, `FillExecutionServiceID`, `ExchangeOrderID`, `HighDuringPosition`, `LowDuringPosition`, `AccountBalance`, and `Note`. `Fills` rows become executions, `Orders` rows become immutable order-lifecycle events, and `Account Balance` rows become balance events. Leave the trade source timezone on **Auto-detect** for Sierra `File >> Export` files; their timestamps are UTC. Sierra `File >> Save Log As` files use the visible Sierra timezone and should use the journal timezone instead. Some Sierra simulator exports encode prices as fixed-point values (for example, `764725` for `7647.25`); TradeFoundry detects that representation from `OrderActionSource` and stores normalized prices while preserving the original row.
* TradingView account history: a generic CSV with symbol, side/action, quantity, execution price, timestamp, and an optional ID/status/fee column.
* TradingView Strategy Tester: rows grouped by `Trade #`/`Trade Number`, with entry/exit prices and times where supplied.
* Benchmark daily series: choose `Benchmark daily series` and import a CSV/TSV with a date or timestamp plus `Close`, `Adj Close`, or `Total Return` (and an optional `Symbol`/`Ticker`). The ticker defaults to `^GSPC` when it is not present.
* Sierra Chart OHLC bars: use the separate market-data importer on the Imports page. It accepts the standard `Date`, `Time`, `Open`, `High`, `Low`, `Last`, `Volume`, `NumberOfTrades`, `BidVolume`, and `AskVolume` columns from a CSV/TSV file or pasted text. Enter the CME root (`MES`, `ES`, `NQ`, and so on); contract symbols are normalized to that root. Leave the source timezone on **Auto-detect** for ordinary chart exports (the journal timezone), or choose UTC when the Sierra bar export was configured to write UTC; explicit offsets in a row take precedence. The interval is inferred from the first valid rows unless overridden (`1m` or greater). Normalized bars are shared across journals and indexed by root, interval, and UTC timestamp; the trade chart renders the stored UTC events in the journal timezone and can consolidate finer data into a selected coarser timeframe.

When the Analysis or Indicators page is opened and the journal has completed trades, TradeFoundry automatically refreshes the default `^GSPC` daily benchmark from Yahoo Finance when the cached data is missing, incomplete, or older than the configured refresh interval. The downloaded points and provider metadata are stored in the journal database, manual benchmark imports remain available as a fallback, and the `Refresh data` button forces an immediate update. Configure this behavior under the `Benchmark` section in `appsettings.json`; set `AutomaticRefresh` to `false` to keep benchmark downloads manual.

Imports are scoped to the selected journal. Sierra rows use a deterministic SHA-256 fingerprint of the complete row, so fills and order events that share an execution id remain distinct while exact duplicate rows still deduplicate safely. Re-importing a file is safe. The **Undo** action removes a batch and rebuilds derived fill-based trades. Derived trades retain point value, initial target/stop intent, initial risk, R-multiple, exit classification, entry/exit order prices, chase points, and UTC entry/exit times; `TimeInTrade` is calculated from those timestamps. Phase 1 derives fill sources flat-to-flat; the FIFO-lots choice is reserved for phase 2 while the schema already preserves the fill allocations needed for it.

## Instrument and application configuration

Settings seeds common futures instruments including MES, ES, MNQ, NQ, MYM, M2K, MCL, MGC, YM, RTY, CL, GC, and 6E. Each definition stores a default all-in commission per contract, an optional fee breakdown per contract (Exchange, NFA, and Clearing / commission), dollar value per point, and tick size. When a breakdown is configured, each fill contributes its component amounts and the dashboard/reports aggregate those totals for the selected journal; changing settings later does not rewrite prior imported evidence. The Sierra Chart section stores ordered regular-expression mappings from the imported `Symbol` value to an instrument; a mapping commission override is also per contract and takes precedence over the instrument default. A blank configured commission preserves a fee reported by the source when available. Daybook review also supports per-trade Exchange, NFA, Clearing / commission, and all-in overrides; these are stored separately from imported evidence, and an all-in override takes precedence over the component total for effective fee/net P&L calculations.

Every import batch records its trading application as well as its source format. Typed fills, order events, and derived trades retain the batch link, so the application provenance remains available from the import history and the Daybook review workspace.

## Storage and boundaries

There is one app process and one SQLite database per self-hosted installation. Multiple standalone journals share the database and are partitioned by `journal_id`; the journal also stores execution context (`live`, `paper`, `replay`, `backtest`, or `other`) and free-form labels. SQLite WAL and a five-second busy timeout are enabled at startup.

Phase 2 will add audited manual corrections, notes, screenshots, setups, scorecards, saved views, and the read-only journal assistant. AI features should use a server-held OpenAI API key and deterministic, journal-scoped read-only tools; users should never enter ChatGPT/Codex credentials into this app.

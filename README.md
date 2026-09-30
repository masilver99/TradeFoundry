# TradeFoundry

TradeFoundry is a local-first futures trading journal. It currently imports Sierra Chart Trade Activity exports, but there are plans to add TradingView and NinjaTrader exports.

The GitHub repo is accepting pull requests, but the primary repo is self hosted and private. 

The code is copyrighted CC BY-NC 4.0. The code is free to modify and use as long as it attributed and not used for commercial purposes. https://creativecommons.org/licenses/by-nc/4.0/deed.en

Much of the code has been written with AI, and it has left some rough edges due to the speed of development, but these rough edges are slowly being smoothed out and the UI is slowing being polished.  

I'm already using it to view stats on my trading.

TradeFoundry can be self-hosted with Docker or installed as a Windows desktop application.

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

The Forgejo Action in `.forgejo/workflows/windows-installer.yml` runs for pushes to `main` and `master` and can also be started manually. It requires a Windows runner registered with the `windows-latest` label. Download the `TradeFoundry-Windows-Installer` artifact from the completed workflow run.

## Run with Docker

Download [compose.published.yml](compose.published.yml) into an empty deployment directory. Once the publishing workflow has completed, run:

```bash
docker compose -f compose.published.yml pull
docker compose -f compose.published.yml up -d
```

Open `http://localhost:8080` and create your owner account. This file defaults to the `main` development image, so it works before the first numbered release. It binds to localhost; for remote access, configure a reverse proxy with HTTPS and the appropriate port binding.

To pin a release, create a `.env` file beside the Compose file:

```dotenv
TRADEFOUNDRY_VERSION=0.1.0
```

Use a version that has actually been published. Available tags are `main` (newest successful main build), `sha-<full commit SHA>` (a specific commit), numbered versions such as `0.1.0`, and `latest` (most recently published stable release). Prereleases such as `0.1.0-rc.1` do not update `latest`. Numbered releases and commit tags should never be reused; pin the registry digest for an immutable deployment.

Update the version in `.env`, then run the same `pull` and `up -d` commands to upgrade. The named `tradefoundry-data` volume preserves journals, attachments, and authentication keys across container replacements. Keep the deployment directory/project name stable so Compose reuses that volume. `docker compose down -v` deletes it. Back up before upgrading; an older image may not support a database updated by a newer release.

To build from source instead:

```bash
docker compose up -d --build
```

The source-build Compose file mounts the host `data/` directory instead of a named volume. Back up the whole data directory/volume while the app is stopped, or use SQLite’s online backup tooling for the database; keep the active database on local storage rather than SMB/NFS. MCP is disabled in Docker images.

### Publishing images on Forgejo

`.forgejo/workflows/container.yml` tests and publishes Linux images to `git.shaa.one/masilver/tradefoundry` on every push to `main` and on version-tag pushes. Images include the application version and commit in .NET assembly metadata and OCI labels. The initial architecture is that of the Linux Docker runner (normally amd64); ARM64 is not built separately.

Configure these prerequisites on git.shaa.one:

1. Enable Actions for the repository and make a Linux runner with the `docker` label available. Its job environment must include Bash, Git, Node (for checkout), and Docker CLI with access to a Docker daemon. This uses the same runner convention as the existing site publishing workflow.
2. Add repository Actions secrets `REGISTRY_USERNAME` (the publishing account) and `REGISTRY_TOKEN` (a token with `read:package` and `write:package` access for the image owner). Keep credentials in Actions secrets; do not put them in Compose or the repository.
3. For anonymous downloads, the package owner `masilver` must be public and instance settings must permit anonymous package access. Forgejo package visibility follows the owning user/organization, independently of this repository's visibility. See [Forgejo package access rules](https://forgejo.org/docs/latest/user/packages/#access-restrictions). Linking the package to this repository does not make it public by itself.

Create a stable release from the desired commit on `main`:

```bash
git tag -a v0.1.0 -m "TradeFoundry 0.1.0"
git push forgejo v0.1.0
```

Use `vMAJOR.MINOR.PATCH`; prereleases may use `-alpha.N`, `-beta.N`, or `-rc.N`. Increment patch for fixes, minor for features, and major for breaking changes (during `0.x`, minor releases may break compatibility). Protect release tags against deletion or replacement. Publishing an older stable tag also moves `latest`, so publish stable releases in order. A Git tag triggers an image release; a Forgejo release page is optional.

After the workflow succeeds, verify public access from a machine without stored registry credentials:

```bash
docker pull git.shaa.one/masilver/tradefoundry:0.1.0
```

The workflow publishes images only; it does not restart any running deployments. Configure package cleanup to retain numbered releases and deployed commit tags.

## Local MCP server

TradeFoundry includes an optional, scoped MCP server for desktop AI clients. It is disabled by default and listens on a separate loopback-only endpoint, so the normal browser listener does not expose MCP routes. This first release is for native local runs only; Docker, LAN, and internet-reachable MCP use are unsupported.

To enable it, set `Mcp:Enabled` to `true` in local configuration (or set the `Mcp__Enabled=true` environment variable) and restart TradeFoundry. The default Streamable HTTP endpoint is `http://127.0.0.1:5081/mcp`. The host and port can be changed with `Mcp:Url`, but the address must remain a numeric loopback address.

Open Settings, create an MCP access token, and select the journals that client may read. Tokens are read-only by default; you can explicitly grant a token the separate write scope for creating and editing user-managed broker fee profiles used by the comparison page. The full `tfmcp_...` secret is displayed once; TradeFoundry stores only its SHA-256 hash and a short identifying prefix. Configure the AI client to use Streamable HTTP at the MCP endpoint with `Authorization: Bearer <token>`. Tokens can be revoked from Settings at any time and cannot use the browser session cookie.

The server publishes these deterministic tools: `list_journals`, `get_journal_overview`, `search_trades`, `get_trade_detail`, `get_trade_price_context`, `get_trading_day`, `analyze_trades`, `get_data_quality`, `get_market_type_probabilities`, `get_market_days`, `list_broker_fee_profiles`, `create_broker_fee_profile`, and `update_broker_fee_profile`. The market tools are read-only. The last two require the explicit write scope. It also publishes `review_trade`, `review_day`, and `review_period` prompt templates. Trades are addressed by stable `review_key` values rather than rebuildable database IDs.

MCP responses contain normalized historical evidence, calculations, explicit data gaps, and relative paths back to TradeFoundry. The server does not expose raw import records, credentials, usernames, or internal order IDs; it does not refresh benchmark data, import files, repair data, call an LLM, provide live signals, place orders, or mutate imported journal evidence. Write-scoped fee profile changes are revisioned and audited separately from imported evidence. Source notes are bounded and labeled as untrusted data. Rate and analysis-concurrency limits are configurable under `Mcp`.

MCP fee profile writes are intentionally limited to explicit user-managed broker comparison inputs. Imported evidence and deterministic trade results remain immutable; profile updates use revision checks, immutable audit history, journal scoping, and an owner-issued write scope.

## Market regime probabilities

The Market Regime page (`/journal/{journalId}/market-regime`) and the two market MCP tools use the stored intraday OHLC bars. The calculation pipeline is `MarketDayFeatureBuilder` → `MarketRegimeClassifier` → `MarketProbabilityService`; MCP and UI clients only query the resulting deterministic calculations.

The first-pass ES/MES session definition is 09:30–16:00 in `America/New_York`, with the overnight session from 16:00 through 09:30 assigned to the following RTH trade date. Directional efficiency is `abs(Close - Open) / (High - Low)`. Path efficiency is `abs(Close - Open)` divided by the open-to-first-close movement plus cumulative absolute close-to-close movement. Close location is `(Close - Low) / (High - Low)`, and normalized range is `RTH range / ATR20`. ATR20 and the volatility measure use prior completed sessions, so the current session is not included in its own trailing baseline. VWAP uses bar volume when valid and equal-weighted typical price otherwise.

Market type is one of `StrongTrend`, `WeakTrend`, or `Range`; direction is separately `Up`, `Down`, or `Neutral`. The classifier ranks directional efficiency, path efficiency, close-extreme location, VWAP persistence, countertrend excursion, and directional higher-high/lower-low structure against the accessible empirical distribution. The defaults are versioned as `market-regime-v1-percentile-components` and are configurable through `MarketRegimeOptions`; no combination enum is used. Volatility is `Low`, `Normal`, `High`, or `Extreme`, based on empirical ranks of the prior-session true-range measure at the 25th, 75th, and 90th percentiles. Insufficient warm-up history is reported as `Unknown`.

Probability results include raw counts, raw proportions, broader baselines, Wilson 95% intervals, and Beta-posterior adjusted proportions. The adjusted value uses a 20-observation empirical prior; one pseudo-observation per market-type/direction category prevents a zero-count broader cohort from producing an impossible posterior. A month-plus-day query shrinks toward the day-of-week cohort, while other calendar and premarket conditioning is removed from the broader prior. `AsOfDate` is exclusive: observations on or after that date are excluded, while earlier bars remain available for ATR and volatility warm-up. Date windows are supplied through `StartDate` and `EndDate` rather than hard-coded eras.

When the symbol is omitted or set to `ES/MES`, ES and MES are loaded as the same market population and reduced to one canonical session per trade date (preferring the series with more RTH bars, then ES). Explicit `ES` or `MES` filters remain available. Versioned raw daily features are persisted in `market_day_features`; the first query backfills the cache from intraday bars, while later queries read daily rows and classify them in memory. Bar imports record derived-cache dirty ranges, and the next query rebuilds only the affected warm-up/propagation window. `MarketDayFeatureCacheService.Rebuild` provides an idempotent full or date-window backfill entry point. No probability-combination columns are created.

## Imports

* Sierra Chart: export the Trade Activity Log as the tab-delimited file containing `ActivityType`, `DateTime`, `TransDateTime`, `OrderActionSource`, `Symbol`, `InternalOrderID`, `ServiceOrderID`, `ParentInternalOrderID`, `OrderType`, `OrderStatus`, `Quantity`, `FilledQuantity`, `BuySell`, `Price`, `Price2`, `FillPrice`, `TradeAccount`, `OpenClose`, `PositionQuantity`, `FillExecutionServiceID`, `ExchangeOrderID`, `HighDuringPosition`, `LowDuringPosition`, `AccountBalance`, and `Note`. `Fills` rows become executions, `Orders` rows become immutable order-lifecycle events, and `Account Balance` rows become balance events. Leave the trade source timezone on **Auto-detect** for Sierra `File >> Export` files; their timestamps are UTC. Sierra `File >> Save Log As` files use the visible Sierra timezone and should use the journal timezone instead. Some Sierra simulator exports encode prices as fixed-point values (for example, `764725` for `7647.25`); TradeFoundry detects that representation from `OrderActionSource` and stores normalized prices while preserving the original row.
* TradingView account history: a generic CSV with symbol, side/action, quantity, execution price, timestamp, and an optional ID/status/fee column.
* TradingView Strategy Tester: rows grouped by `Trade #`/`Trade Number`, with entry/exit prices and times where supplied.
* Benchmark daily series: choose `Benchmark daily series` and import a CSV/TSV with a date or timestamp plus `Close`, `Adj Close`, or `Total Return` (and an optional `Symbol`/`Ticker`). The ticker defaults to `^GSPC` when it is not present.
* Sierra Chart OHLC bars: use the separate market-data importer on the Imports page. It accepts the standard `Date`, `Time`, `Open`, `High`, `Low`, `Last`, `Volume`, `NumberOfTrades`, `BidVolume`, and `AskVolume` columns from a CSV/TSV file or pasted text. Enter the CME root (`MES`, `ES`, `NQ`, and so on); contract symbols are normalized to that root. Leave the source timezone on **Auto-detect** for ordinary chart exports (the journal timezone), or choose UTC when the Sierra bar export was configured to write UTC; explicit offsets in a row take precedence. The interval is inferred from the first valid rows unless overridden (`1m` or greater). Normalized bars are shared across journals and indexed by root, interval, and UTC timestamp; the trade chart renders the stored UTC events in the journal timezone and can consolidate finer data into a selected coarser timeframe.

To import trade exports automatically, configure an existing readable server-side folder under **Settings → Sources → Watched folder** for the target journal. The server scans it on startup and periodically, and watches it for new or changed `.csv`, `.tsv`, and `.txt` files. Supported Sierra Chart and TradingView trade exports use the journal's grouping policy and automatic timezone resolution. Files remain in place; unchanged files are not imported again, and a changed file is processed again. Failed files are retried after their contents change. Recent results appear in Sources settings and successful batches appear in import history. Clearing the folder path disables monitoring and keeps its recorded history.

When the Analysis or Indicators page is opened and the journal has completed trades, TradeFoundry automatically refreshes the default `^GSPC` daily benchmark from Yahoo Finance when the cached data is missing, incomplete, or older than the configured refresh interval. The downloaded points and provider metadata are stored in the journal database, manual benchmark imports remain available as a fallback, and the `Refresh data` button forces an immediate update. Configure this behavior under the `Benchmark` section in `appsettings.json`; set `AutomaticRefresh` to `false` to keep benchmark downloads manual.

Imports are scoped to the selected journal. Sierra rows use a deterministic SHA-256 fingerprint of the complete row, so fills and order events that share an execution id remain distinct while exact duplicate rows still deduplicate safely. Re-importing a file is safe. The **Undo** action removes a batch and rebuilds derived fill-based trades. Derived trades retain point value, initial target/stop intent, initial risk, R-multiple, exit classification, entry/exit order prices, chase points, and UTC entry/exit times; `TimeInTrade` is calculated from those timestamps. Phase 1 derives fill sources flat-to-flat; the FIFO-lots choice is reserved for phase 2 while the schema already preserves the fill allocations needed for it.

## Instrument and application configuration

Settings seeds common futures instruments including MES, ES, MNQ, NQ, MYM, M2K, MCL, MGC, YM, RTY, CL, GC, and 6E. Each definition stores a default all-in commission per contract, an optional fee breakdown per contract (Exchange, NFA, and Clearing / commission), dollar value per point, and tick size. When a breakdown is configured, each fill contributes its component amounts and the dashboard/reports aggregate those totals for the selected journal; changing settings later does not rewrite prior imported evidence. The Sierra Chart section stores ordered regular-expression mappings from the imported `Symbol` value to an instrument; a mapping commission override is also per contract and takes precedence over the instrument default. A blank configured commission preserves a fee reported by the source when available. Daybook review also supports per-trade Exchange, NFA, Clearing / commission, and all-in overrides; these are stored separately from imported evidence, and an all-in override takes precedence over the component total for effective fee/net P&L calculations.

Every import batch records its trading application as well as its source format. Typed fills, order events, and derived trades retain the batch link, so the application provenance remains available from the import history and the Daybook review workspace.

## Storage and boundaries

There is one app process and one SQLite database per self-hosted installation. Multiple standalone journals share the database and are partitioned by `journal_id`; the journal also stores execution context (`live`, `paper`, `replay`, `backtest`, or `other`) and free-form labels. SQLite WAL and a five-second busy timeout are enabled at startup.

Phase 2 will add audited manual corrections, notes, screenshots, setups, scorecards, saved views, and the read-only journal assistant. AI features should use a server-held OpenAI API key and deterministic, journal-scoped read-only tools; users should never enter ChatGPT/Codex credentials into this app.

# TradeFoundry

TradeFoundry is a local-first futures trading journal. It imports Sierra Chart Trade Activity fills, TradingView account or Strategy Tester exports, and OHLCV bars for chart snippets. The phase 1 ledger is intentionally read-only: raw rows stay linked to normalized fills and deterministic flat-to-flat trades.

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

* Sierra Chart: export the Trade Activity Log as the tab-delimited file containing `ActivityType`, `Quantity`, `BuySell`, `FillPrice`, `TradeAccount`, and `FillExecutionServiceID`. Only `Fills` rows become executions; order/status rows remain in raw provenance. Some Sierra simulator exports encode prices as fixed-point values (for example, `764725` for `7647.25`); TradeFoundry detects that representation from `OrderActionSource` and stores the normalized price while preserving the original row.
* TradingView account history: a generic CSV with symbol, side/action, quantity, execution price, timestamp, and an optional ID/status/fee column.
* TradingView Strategy Tester: rows grouped by `Trade #`/`Trade Number`, with entry/exit prices and times where supplied.
* OHLCV bars: a CSV/TSV containing symbol, timestamp/date, and `Open`, `High`, `Low`, `Close` (optional `Volume`). Choose an interval such as `1m` or `5m` so the trade detail page can select the matching series.

Imports are scoped to the selected journal. The service uses `FillExecutionServiceID` when Sierra provides it and a deterministic SHA-256 row fingerprint otherwise. Re-importing a file is safe. The **Undo** action removes a batch and rebuilds derived fill-based trades. Phase 1 derives fill sources flat-to-flat; the FIFO-lots choice is reserved for phase 2 while the schema already preserves the fill allocations needed for it.

## Storage and boundaries

There is one app process and one SQLite database per self-hosted installation. Multiple standalone journals share the database and are partitioned by `journal_id`; the journal also stores execution context (`live`, `paper`, `replay`, `backtest`, or `other`) and free-form labels. SQLite WAL and a five-second busy timeout are enabled at startup.

Phase 2 will add audited manual corrections, notes, screenshots, setups, scorecards, saved views, and the read-only journal assistant. AI features should use a server-held OpenAI API key and deterministic, journal-scoped read-only tools; users should never enter ChatGPT/Codex credentials into this app.

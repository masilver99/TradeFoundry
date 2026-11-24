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
- Daily Journal and Trade Review
  - Trade Review contains all your trades and allows you to edit or make changes
  - This is still in very heavy development and will probably change a fair amount
- Tags for trades and days, per journal
  - A new tag needs a confirmed name, a description, and when you know it (beforehand, during, or after); look-alike names must be confirmed as different
  - Tags can be renamed, merged (with undo), archived, and browsed by trade or day
  - The Tag Report ranks tags by the low end of their expectancy range and looks up the historical odds for up to three tags. Only tags known beforehand count as probabilities
  - Upgrading converts old free-text tags to tags that need review, after saving the converted tables to `backups/tags-migration-*.db` in the data directory
- Optional MCP Server for journal-scoped historical analysis and broker fee profile management

## Run locally

This project targets .NET 10.

```powershell
cd C:\dev\TradeFoundry
dotnet run --urls http://127.0.0.1:5080
```

Open `http://127.0.0.1:5080`. On first run, create the local owner account, create a journal, and upload an export. The SQLite database is created at `.tradefoundry-data/journal.db` (or the path in `Storage:DataDirectory`).

## Windows desktop package

The installed Windows build is a self-contained `win-x64` package with a small tray helper (`TradeFoundry.Desktop.exe`, built with Avalonia in `DesktopShell/`). Launching TradeFoundry starts the local server and opens the journal in the default browser. The tray icon's menu opens the journal, shows the server status, starts or stops the server, toggles **Start at sign-in**, opens the data folder, and quits. Clicking the Start menu shortcut again just reopens the journal. The local server remains loopback-only at `http://127.0.0.1:5080`. The installed build uses a per-user configuration file at `%LOCALAPPDATA%\TradeFoundry\appsettings.user.json`; the installer writes the selected journal data directory there. Database files and review attachments remain outside the application install directory.

The tray starts the server as `TradeFoundry.exe --desktop --embedded` and keeps its standard input open. The server shuts down gracefully when the tray stops it, and also when the tray exits or crashes, so it is never left running on its own. If the server fails to start (for example, because port 5080 is in use), the tray shows the error and recent server output.

For development, set `TradeFoundryServerPath` to a built `TradeFoundry.exe` and `TradeFoundryDesktopUrl` to another loopback URL such as `http://127.0.0.1:5090`. A non-default port gets its own single-instance names, so it does not collide with an installed copy. The `TradeFoundry: Desktop` VS Code launch configuration does this.

To create the publish payload:

~~~powershell
pwsh -File scripts\publish-windows.ps1
~~~

Install Inno Setup 7 and pass `-CompileInstaller` to compile the installer in the same run:

~~~powershell
pwsh -File scripts\publish-windows.ps1 -CompileInstaller
~~~

The script searches both standard Program Files locations. For a custom installation, pass the compiler path explicitly with `-InnoSetupPath C:\path\to\ISCC.exe`. Pass `-Version 1.2.0` to stamp a version; local builds default to `0.0.0-dev`.

The installer is per-user. Uninstall removes neither the selected data directory nor backups, and says where they are. Re-running it reuses the previously selected data directory.

### Automated builds and releases

The Forgejo Action in `.forgejo/workflows/windows-installer.yml` builds the installer on the Linux `docker` runner. No Windows runner is needed: `installer/Dockerfile` publishes `win-x64` with the .NET SDK and compiles the installer with Inno Setup running under Wine. To run the same build locally on any machine with Docker:

~~~bash
docker build -f installer/Dockerfile --target artifact --build-arg VERSION=1.2.0 -t tf-installer .
id=$(docker create tf-installer noop) && docker cp "$id:/TradeFoundry-Setup.exe" . && docker rm "$id"
~~~

- **Branch pushes do not build an installer.** To try one before tagging, start the workflow manually (**Actions → Windows installer → Run workflow**). That builds a development installer (`0.0.0-dev.<sha>`) and keeps it as a workflow artifact for 30 days. Development builds never offer updates.
- **Tag `vX.Y.Z`** (an annotated tag whose message is the changelog, [as shown below](#publishing-images-on-forgejo)) also uploads `TradeFoundry-Setup-X.Y.Z.exe` and its `.sha256` to the owner's public generic package `tradefoundry-windows`, then replaces `tradefoundry-windows/latest/latest.json`. Installed tray helpers read that manifest.
- **Tag `vX.Y.Z-rc.N`** (or `alpha`/`beta`) uploads the installer but leaves `latest.json` alone, so no one is offered it.

Publishing uses the same `REGISTRY_USERNAME` / `REGISTRY_TOKEN` secrets as the container workflow; the token needs `write:package`. Package files are public whenever the owner's profile is public, even though the repository is private. The source code stays private, but anyone with the link can download the installer.

After a stable tag replaces `latest.json`, the workflow creates a Forgejo release for the tag, with the installer link and the tag message as its changelog. Prereleases don't get one. The repository's Gotify webhook (**Settings → Webhooks**, set up the same way as michaelsilver.us's and limited to **Release** events) turns that into a phone notification. Re-running a tag's workflow leaves an existing release alone, so it won't notify twice.

### Icons

The private app and the tray use the public journal's rising-line mark in the private app's gold. The public journal keeps its green version, so the two are easy to tell apart. The tray's update variant adds a blue dot. All of these (`wwwroot/favicon.svg`, `wwwroot/favicon.ico` and `DesktopShell/Assets/TradeFoundry-update.ico`) are generated by `dotnet run --file scripts/generate-icons.cs`.

### Updates in the tray

Installed release builds check `latest.json` 30 seconds after starting and then every 12 hours. **Check for updates** in the tray menu checks on demand, and reports the result in the menu item itself. Update notices never open windows. When a newer stable version exists:

- the tray icon gains a blue dot and its tooltip names the version;
- the app shows a notice in its notifications panel (the tray passes the version to the server over its standard input). Dismissing it hides it for that version;
- the menu item becomes **Install update X.Y.Z and restart**.

Choosing that item downloads the installer and verifies its size and SHA-256 (a mismatch is refused, and the menu item says so). Then it stops the server cleanly, starts Setup with `/VERYSILENT /TFUPDATE=1`, and exits. No installer window appears. Setup waits up to 60 seconds for TradeFoundry to exit, upgrades in place (keeping the data directory), and starts the tray again with `--after-update`. The relaunched tray waits for Setup to finish before starting the server, and opens the journal.

- **Launching TradeFoundry mid-update** (for example from the Start menu) does nothing: the tray sees Setup's `TradeFoundry.Setup` mutex and exits, so it can't lock files being replaced. Setup starts it when done.
- **If the update does not finish**, Setup still starts the tray (with `--update-failed`). Its tooltip and menu item then say **Update failed. Try again…**, and clicking the item retries.

The update log is written to `%TEMP%\TradeFoundry-Update\install.log`.

The manifest URL is baked into the tray at build time (`-p:UpdateManifestUrl`). For testing, `TradeFoundryUpdateManifestUrl` overrides it and also enables update checks on development builds. Installers are not code-signed yet, so Windows SmartScreen may warn on a manual install.

## Backup and restore

**Settings > Backups** backs up the whole installation, every journal included, into one `.tfbackup` file. Backups are on from the first start: every day at 02:00, keeping the newest backup of each of the last 7 days, 4 weeks and 3 months.

### What a backup holds

- The database, copied with SQLite's online backup API (never a plain file copy), plus daily-journal images, review attachments, expense receipts and image settings.
- Publishing tokens, but only in encrypted backups.
- Not included: ChatGPT sign-ins (sign in again), MCP tokens (revoked on restore), the backup passphrase, the Backblaze B2 key, data protection keys, `appsettings.user.json` and environment settings, and the public journal site. The public site can be republished.

The site pauses while the journal is copied, about 20–30 seconds for a 3 GB journal. Pages show a "making a backup" notice and reload by themselves. Edits and autosaves made during the pause wait and are saved when it ends. Compressing, encrypting and uploading happen afterwards, with the site open.

### Where backups go

| Run as | Default folder |
|---|---|
| Windows desktop | `%LOCALAPPDATA%\TradeFoundry\Backups` |
| Docker | `/app/backups`: the `tradefoundry-backups` volume, or `./backups` for the source-build Compose file |
| `dotnet run` | `<data directory>-backups`, beside the data directory |

Choose another folder in Settings > Backups. It needs the owner password and must be outside the data directory. The page warns when the folder is on the same drive as the journal, because one disk failure would take both. Use another drive, a synced folder, or Backblaze B2. Each backup is a `.tfbackup` file plus a small `.json` file listing what it holds. Copy both files when moving a backup.

**Back up now** is on the same page, and in the tray menu. A failed backup, a failed upload, or no successful backup for two scheduled intervals shows in the notifications panel and in the tray. Backups missed while the computer was off or asleep run once, five minutes after TradeFoundry starts or wakes. Failures are retried after 5, 30 and 120 minutes. Retention deletes only this installation's scheduled backups. Manual backups, other installations' backups and the newest backup are always kept.

### Encryption

Turn on encryption with a passphrase of at least 12 characters. Keep a copy of the passphrase away from this computer, because a backup cannot be opened without it. Encrypted backups use the standard [age](https://age-encryption.org) format, so they can also be opened without TradeFoundry:

```
age -d backup.tfbackup > backup.zip
```

The passphrase is stored on this machine with its data protection keys, so scheduled backups can use it. It never goes into a backup.

### Backblaze B2

Cloud backups are always encrypted, and need an owner password. Set them up once with the [B2 command-line tool](https://www.backblaze.com/docs/cloud-storage-command-line-tools), signed in with your master key. The Backblaze website cannot create a key with exactly these permissions.

```bash
b2 account authorize
# A private bucket used only for these backups. Hidden files are erased 30 days after being hidden.
b2 bucket create --lifecycle-rule '{"daysFromHidingToDeleting": 30, "daysFromUploadingToHiding": null, "daysFromStartingToCancelingUnfinishedLargeFiles": 7, "fileNamePrefix": ""}' YOUR-BUCKET allPrivate
# A key for that bucket and prefix only, deliberately without deleteFiles.
b2 key create --bucket YOUR-BUCKET --name-prefix tradefoundry/ tradefoundry-backups listFiles,readFiles,writeFiles,listAllBucketNames
```

In PowerShell 5.1, escape the JSON's double quotes as `\"`. Do not use the website's "Keep only the last version" lifecycle preset: it erases hidden files after one day.

Enter the bucket's S3 endpoint (such as `https://s3.us-west-002.backblazeb2.com`), the bucket name, the key ID and the application key in Settings > Backups. Saving tests the connection, and so does **Test connection**. The test uploads, lists, reads and hides a probe file, then checks that the key cannot erase it permanently. If it can, the page warns you.

Each backup is saved to the folder first, then uploaded. A failed upload keeps the backup, shows a warning and is retried later without pausing the site again. You can also use **Upload again** in the history.

Retention never deletes from the bucket; it hides old backups. Hidden backups stay restorable until the lifecycle rule erases them 30 days later, so the bucket holds about three times as much as the folder. TradeFoundry warns if a backup it did not prune disappears from the bucket. That is the time to check the bucket, while hidden backups can still be restored.

### Restore

A backup restores only into an installation without journals, so nothing is ever replaced by accident.

- **New installation:** choose **Restore from a backup** on the first-run setup page.
- **Existing installation:** first use **Move current data aside and start clean** in Settings > Backups. It needs the owner password and typing "start clean". It moves the data into `set-aside/` inside the data directory, keeps your owner account, and restarts empty. Nothing is deleted: while no journal exists, the restore page offers **Put back**.

The restore page reads a backup from a folder, an uploaded file, or Backblaze B2 (with the key ID and application key, used only for that restore). For B2, **Show hidden backups** also lists pruned backups still within the recovery window. Each backup is checked in full before anything changes: every file's hash, the paths, and the database's integrity. Encrypted backups ask for their passphrase. The page then shows the backup's journals and counts, and asks for the password of the backup's owner account. After you confirm, TradeFoundry restarts once, in a few seconds, into the restored data, and you sign in with that password.

After a restore:

- Watched import folders stay paused until you check the paths and resume them.
- MCP tokens are revoked. ChatGPT needs signing in again, and so does publishing, unless the backup was encrypted.
- Backups go to the default folder. Encryption and B2 must be set up again on this machine.
- The installation gets a new id, so it never prunes the original's backups.

Outside desktop mode, restoring before setup needs a one-time code. TradeFoundry writes it to the server log (`docker compose logs tradefoundry`). This proves the visitor runs the server.

### Moving to another computer

1. Back up on the old computer, or let B2 hold the latest backup.
2. Install TradeFoundry on the new one.
3. On the first-run page, choose **Restore from a backup**.

Copying the data directory by hand also works, but only while TradeFoundry is stopped, and the copy must include the `-wal` file.

### Practice a restore

Restore one backup now and then, so you know it works before you need it. Use a separate test copy that never touches your journal: in PowerShell, from the install folder (`%LOCALAPPDATA%\Programs\TradeFoundry`), run

```powershell
$env:Storage__DataDirectory = "$env:TEMP\tradefoundry-drill"
$env:Mcp__Enabled = "false"
.\TradeFoundry.exe --urls http://127.0.0.1:5099
```

Then open `http://127.0.0.1:5099/restore`, enter the one-time code from that window, restore your newest backup, and look through a journal. Stop it with Ctrl+C and delete the `tradefoundry-drill` folder in `%TEMP%`. A test copy restored from B2 stays separate from your real installation, because it gets its own id.

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

Update the version in `.env`, then run the same `pull` and `up -d` commands to upgrade. The named `tradefoundry-data` volume preserves journals, attachments, and authentication keys across container replacements. Keep the deployment directory/project name stable so Compose reuses that volume. `docker compose down -v` deletes it, and the `tradefoundry-backups` volume with it. Back up before upgrading (**Settings > Backups > Back up now**): an older image refuses a database updated by a newer release.

To build from source instead:

```bash
docker compose up -d --build
```

The source-build Compose file mounts the host `data/` and `backups/` directories instead of named volumes. Keep the active database on local storage rather than SMB/NFS. MCP is disabled in Docker images. See [Backup and restore](#backup-and-restore).

### Trading expenses

Each journal has an Expenses page for manually recording operating costs, managing categories, attaching PDF/image receipts, and exporting filtered expenses to CSV. Expenses do not change trading P&L or account balances. New entries use the journal currency; existing entries retain their recorded currency if the journal setting changes. Totals are separated by currency without conversion.

Receipts are private files in `expense-receipts/<journal-id>/` beside `journal.db`, up to 10 MB each. TradeFoundry's backups include them; CSV export does not include receipt files. Deleting an expense hides it and its receipts while retaining audit history. Removing an individual receipt deletes its file.

### Publishing images on Forgejo

`.forgejo/workflows/container.yml` tests and publishes Linux images to `git.shaa.one/masilver/tradefoundry` on every push or merge to the repository's default branch (currently `master`) and on version-tag pushes. Other branch pushes skip publishing. Default-branch builds publish `main` (the tag used by `compose.published.yml`) and `sha-<commit>` tags. A version tag does not rebuild: it promotes the already-tested `sha-<commit>` image of the tagged commit to the numbered tag (and `latest` for stable releases). If master never built that commit, the tag gets a full build instead. The workflow can also be run manually on the default branch. Images include the application version and commit in .NET assembly metadata and OCI labels. A promoted release keeps the version of the master build it came from (`0.0.0-dev.<sha>`); its registry tag names the release. The initial architecture is that of the Linux Docker runner (normally amd64); ARM64 is not built separately.

Configure these prerequisites on git.shaa.one:

1. Enable Actions for the repository and make a Linux runner with the `docker` label available. Its job environment must include Bash, Git, Node (for checkout), and Docker CLI with access to a Docker daemon. This uses the same runner convention as the existing site publishing workflow.
2. Add repository Actions secrets `REGISTRY_USERNAME` (the publishing account) and `REGISTRY_TOKEN` (a token with `read:package` and `write:package` access for the image owner). Keep credentials in Actions secrets; do not put them in Compose or the repository.
3. For anonymous downloads, the package owner `masilver` must be public and instance settings must permit anonymous package access. Forgejo package visibility follows the owning user/organization, independently of this repository's visibility. See [Forgejo package access rules](https://forgejo.org/docs/latest/user/packages/#access-restrictions). Linking the package to this repository does not make it public by itself.

Create a stable release from the desired commit on the default branch:

```bash
git tag -a v0.1.0 --cleanup=whitespace -m "TradeFoundry 0.1.0: short summary" -m "### Added

- What changed, in words a user would recognize.

### Fixed

- What was broken and now works."
git push forgejo v0.1.0
```

The tag message becomes the **Changelog** of the Forgejo release:

- The `TradeFoundry X.Y.Z: summary` title is left out. If nothing follows it, the summary becomes the changelog.
- `Co-Authored-By` and `Signed-off-by` trailers and tag signatures are left out.
- Wrapped lines are joined. Separate paragraphs with a blank line, or use list items.
- Keep `--cleanup=whitespace`: without it, Git deletes every line starting with `#`, including headings.
- A lightweight tag (`git tag v0.1.0`) or a message with only the version still gets a release, but without a changelog, and the workflow logs a warning.
- The message is published as written and read only when the release is created. To correct it afterwards, edit the release in Forgejo rather than moving the tag.

Use `vMAJOR.MINOR.PATCH`; prereleases may use `-alpha.N`, `-beta.N`, or `-rc.N`. Increment patch for fixes, minor for features, and major for breaking changes (during `0.x`, minor releases may break compatibility). Protect release tags against deletion or replacement. Publishing an older stable tag also moves `latest`, so publish stable releases in order. A Git tag triggers an image release; a Forgejo release page is optional.

After the workflow succeeds, verify public access from a machine without stored registry credentials:

```bash
docker pull git.shaa.one/masilver/tradefoundry:0.1.0
```

The workflow publishes images only; it does not restart any running deployments. Configure package cleanup to retain numbered releases and deployed commit tags.

## Public journal website

Publish one journal's dashboard, realized equity curve, monthly calendars, daily writing, images, and compact trade summaries to the separate public reader in this repository. In TradeFoundry, open **Publishing**, configure the website, create a frozen preview, and publish it manually. See [public journal setup, Docker hosting, API, and backups](PublicJournal/README.md).

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
* NinjaTrader Trades ($): in **Trade Performance**, set *Display* to **Currency ($)**, open the *Trades* tab, right-click the grid, and export it to CSV. Each row becomes one immutable trade stored with the `source_defined` grouping policy: TradeFoundry keeps NinjaTrader's own pairing rather than regrouping positions, so a scaled position can appear as several trades and trade counts and win rate can differ from flat-to-flat positions (total P&L does not). NinjaTrader's native MAE/MFE are converted from currency to points per contract; missing excursions stay empty. Gross P&L is computed from prices and the resolved point value, and Commission, Clearing, Exchange, IP, and NFA fees are summed as the reported cost; a row whose gross minus reported costs does not match `Profit` within one cent is kept as a raw row with a warning (usually a missing instrument definition or mapping). Strategy, entry name, and exit name are preserved, and `Stop*`/`Target*`/`Close` exit names classify the exit type. Duplicates are detected from account, instrument, side, quantity, entry/exit times, and entry/exit prices plus an occurrence index for identical rows; `Trade number` and `Cum. net profit` are report-relative and ignored for identity, so re-exporting full history is safe. To replace trades NinjaTrader revised, undo the previous NinjaTrader batch first. Exports in Points/Ticks/Percent display and NinjaTrader *Executions* exports are rejected with instructions; open positions are not included in this export.
* Benchmark daily series: choose `Benchmark daily series` and import a CSV/TSV with a date or timestamp plus `Close`, `Adj Close`, or `Total Return` (and an optional `Symbol`/`Ticker`). The ticker defaults to `^GSPC` when it is not present.
* Sierra Chart OHLC bars: use the separate market-data importer on the Imports page. It accepts the standard `Date`, `Time`, `Open`, `High`, `Low`, `Last`, `Volume`, `NumberOfTrades`, `BidVolume`, and `AskVolume` columns from a CSV/TSV file or pasted text. Enter the CME root (`MES`, `ES`, `NQ`, and so on); contract symbols are normalized to that root. Leave the source timezone on **Auto-detect** for ordinary chart exports (the journal timezone), or choose UTC when the Sierra bar export was configured to write UTC; explicit offsets in a row take precedence. The interval is inferred from the first valid rows unless overridden (`1m` or greater). Normalized bars are shared across journals and indexed by root, interval, and UTC timestamp; the trade chart renders the stored UTC events in the journal timezone and can consolidate finer data into a selected coarser timeframe.

To import trade exports automatically, configure an existing readable server-side folder under **Settings → Sources → Watched folder** for the target journal. The server scans it on startup and periodically, and watches it for new or changed `.csv`, `.tsv`, and `.txt` files. Supported Sierra Chart, TradingView, and NinjaTrader Trades exports use the journal's grouping policy (NinjaTrader trades always keep their source-defined pairing) and automatic timezone resolution. Files remain in place; unchanged files are not imported again, and a changed file is processed again. Failed files are retried after their contents change. Recent results appear in Sources settings and successful batches appear in import history. Clearing the folder path disables monitoring and keeps its recorded history.

When the Analysis or Indicators page is opened and the journal has completed trades, TradeFoundry automatically refreshes the default `^GSPC` daily benchmark from Yahoo Finance when the cached data is missing, incomplete, or older than the configured refresh interval. The downloaded points and provider metadata are stored in the journal database, manual benchmark imports remain available as a fallback, and the `Refresh data` button forces an immediate update. Configure this behavior under the `Benchmark` section in `appsettings.json`; set `AutomaticRefresh` to `false` to keep benchmark downloads manual.

Imports are scoped to the selected journal. Sierra rows use a deterministic SHA-256 fingerprint of the complete row, so fills and order events that share an execution id remain distinct while exact duplicate rows still deduplicate safely. Re-importing a file is safe. The **Undo** action removes a batch and rebuilds derived fill-based trades. Derived trades retain point value, initial target/stop intent, initial risk, R-multiple, exit classification, entry/exit order prices, chase points, and UTC entry/exit times; `TimeInTrade` is calculated from those timestamps. Phase 1 derives fill sources flat-to-flat; the FIFO-lots choice is reserved for phase 2 while the schema already preserves the fill allocations needed for it.

## Instrument and application configuration

Settings seeds common futures instruments including MES, ES, MNQ, NQ, MYM, M2K, MCL, MGC, YM, RTY, CL, GC, and 6E. Each definition stores a default all-in commission per contract, an optional fee breakdown per contract (Exchange, NFA, and Clearing / commission), dollar value per point, and tick size. When a breakdown is configured, each fill contributes its component amounts and the dashboard/reports aggregate those totals for the selected journal; changing settings later does not rewrite prior imported evidence. The Sierra Chart and NinjaTrader sections store ordered regular-expression mappings from the imported symbol (Sierra `Symbol`, NinjaTrader `Instrument` such as `MES 06-23`) to an instrument; a mapping commission override is also per contract and takes precedence over the instrument default. A blank configured commission preserves a fee reported by the source when available. Trade Review also supports per-trade Exchange, NFA, Clearing / commission, and all-in overrides; these are stored separately from imported evidence, and an all-in override takes precedence over the component total for effective fee/net P&L calculations.

Every import batch records its trading application as well as its source format. Typed fills, order events, and derived trades retain the batch link, so the application provenance remains available from the import history and the Trade Review workspace.

## Storage and boundaries

There is one app process and one SQLite database per self-hosted installation. Multiple standalone journals share the database and are partitioned by `journal_id`; the journal also stores execution context (`live`, `paper`, `replay`, `backtest`, or `other`) and free-form labels. SQLite WAL and a five-second busy timeout are enabled at startup.

Phase 2 will add audited manual corrections, notes, screenshots, setups, scorecards, saved views, and the read-only journal assistant. AI features should use a server-held OpenAI API key and deterministic, journal-scoped read-only tools; users should never enter ChatGPT/Codex credentials into this app.

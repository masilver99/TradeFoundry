# Backup and restore plan

Status: decisions approved 2026-11-22. Phases 0–4 are implemented; see each phase's result.

## Summary

- A backup is a full snapshot of one installation (all journals share one database), packaged as one file.
- The site enters **maintenance mode** while the snapshot is taken, about 20-30 seconds at today's size. It
  reopens before compression, encryption and upload.
- The **web app** owns settings, schedule and engine, so desktop, Docker and `dotnet run` behave the same. The
  **tray** is a remote control: Back up now, status, and safe Quit/update during a backup.
- Destinations are a folder and Backblaze B2 through its S3 API. Cloud backups are always encrypted.
- Restore is all-or-nothing, runs only on a clean installation, and commits during an in-process host restart.

## Decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Clean journal | An installation with zero journals, archived ones included. An owner account alone is still clean. |
| 2 | Existing installations | "Move current data aside and start clean", with typed confirmation. It renames and never deletes. |
| 3 | Secrets | Publishing tokens, only in encrypted backups. Not B2 keys, the backup passphrase or ChatGPT tokens. |
| 4 | Schedule and retention | Off until configured. Then daily at 02:00, keeping 7 daily, 4 weekly and 3 monthly. |
| 5 | Cloud provider | Backblaze B2. Other S3-compatible providers are out of scope until tested. |

At 0.69 GB per backup, that retention is about 10 GB in a folder. On B2 it is about 30 GB, because pruned backups
stay recoverable for 30 more days (see Backblaze B2).

## Measurements

Taken on a scratch copy of the 11 Nov migration backup (2.96 GB, 2.35 million bars, 44 images), on the
development PC. The live databases were not opened.

| Test | Result | Consequence |
|---|---|---|
| `BackupDatabase` on the app's `Cache=Shared` connection | 66 s; every read and write blocked for 66 s | Never use the app's connection for backup |
| Same, private connection | 54 s; stalls up to 14 s | The final disk flush is also a problem |
| Stepped, private, flush every 32 MB | 18-24 s; stalls under 0.2 s | Use this method |
| `VACUUM INTO` | 27 s; 5% smaller; stalls up to 7.7 s; no progress | Rejected |
| ZIP Optimal / Fastest | 20 s, 0.68 GB / 6 s, 0.93 GB | ZIP Optimal, outside maintenance |
| 4 MB chunk deduplication after one simulated day of bars | 633 of 706 chunks changed | Rejected; random GUID keys scatter writes |
| Replace DB file with pooled connections open | Fails on Windows | Swap only with the host down |
| Same after `ClearAllPools` | Works | |
| Rebuild a minimal Kestrel host in-process on the same port | Works | Restore needs no external supervisor |
| Round trip: snapshot, ZIP, extract, check, installed app's schema init | 21 + 19 + 17 + 3.5 + 14 s; 8 table counts and all 44 file hashes matched | The design works at real size |

Not tested: B2, encryption, the maintenance gate, a restart of the full app host, Docker.

## What a backup contains

| Item | Included | Notes |
|---|---|---|
| `journal.db` (name is configurable) | Yes | Online-backup API. Never a file copy, which can lose WAL content. |
| `daily-journal-images/` | Yes | |
| `review-attachments/` | Yes | |
| `expense-receipts/` | Yes | PDFs and images |
| `image-settings/*.json` | Yes | Per-journal settings that are not in the database |
| Password hash, MCP token hashes | Yes, in the DB | MCP tokens are revoked on restore, so revoked tokens cannot come back to life |
| Publishing tokens | Encrypted backups only | Unprotected on backup, re-protected with the target's keys on restore |
| ChatGPT tokens | No | Refresh tokens rotate, so old copies are unreliable. Sign in again. |
| B2 keys, backup passphrase | No | A backup must not be able to delete the other backups |
| Data Protection keys (`keys/` in Docker, per-user DPAPI store on Windows) | No | Not portable on Windows, and unsafe beside the data they protect |
| `appsettings.user.json`, environment variables | No | Machine-specific. The manifest records app version and platform. |
| Watched source files outside the data directory | No | Imported evidence is already in the database |
| `backups/`, WAL/SHM, temporary files | No | Use an allowlist, never "zip the data directory" |
| Public journal site | No | Separate service with its own volume. It can be republished. |

Define the allowlist once, in `BackupInventory`, and have the file stores take their directory names from it.
A test populates every store and fails if a new top-level data path is neither included nor explicitly excluded.

Protected columns (`ai_connections.protected_credentials`, `journal_publishing.protected_token`, and the new
backup secrets) are blanked in the snapshot copy with `PRAGMA secure_delete=ON`, so the old values do not linger
in its free space. The live database is never modified.

### Format

```
tradefoundry-<installation>-<utc>-<id>.tfbackup        ZIP64, wrapped in age when encrypted
  manifest.json        format and schema versions, app version, size and SHA-256 of every entry, counts
  database/journal.db
  files/<allowlisted directory>/...
  secrets.json         encrypted backups only
<same name>.json       sidecar for listing: id, date, size, archive hash, versions, encrypted flag
```

The sidecar is written last. An archive without one is incomplete and is not offered for restore. Entry names
use `/` on every platform. Already-compressed images are stored, not deflated.

## Maintenance mode

Add a singleton `AppActivityGate`. Normal work takes a shared lease and backup takes the exclusive one.

| Source of work | During maintenance |
|---|---|
| Page navigation (GET) | 503 maintenance page that reloads when ready. It must not use the DB-backed layout. |
| Form posts and autosaves | Held before the body is read, then processed normally. 503 JSON after 90 s. |
| Other fetches and polls | 503 with `Retry-After` |
| `/health` (new) and static assets | Always served. `/health` returns only ready/maintenance. |
| MCP | Separate Kestrel app, so it needs its own middleware over the same gate |
| `ImportFolderMonitor` | Takes a lease per work item. Events stay queued. Importing a file is long work. |
| AI review and ChatGPT sign-in tasks | Not gated. The snapshot is one read transaction, so their writes land wholly before or after it, and they touch no data files. Holding a lease across a 4-minute review or a 5-minute sign-in would block backups for nothing. |

Three traps in the current code:

1. **Never redirect an autosave to the maintenance page.** Review and daily-journal autosave treat any OK response,
   HTML included, as saved and would silently drop the edit.
2. Autosave does not retry after a failure. Add retry with backoff for 503 and network errors.
3. The tray treats a 5xx from `/` as "not running", so it would time out or report Stopped. Move its probe to
   `/health`.

GET requests also write (benchmark refresh, feature cache, connection records), so gating only POST is not enough.

## Backup flow

1. Take the single-job lock. Check free space: about 1.3 times the database size for staging.
2. Wait up to 60 s for long work to finish **without** blocking new requests. If still busy, postpone 5 minutes.
3. Enter maintenance and drain for up to 10 s, or back out.
4. Snapshot through a new `Cache=Private;Pooling=False` connection in a read transaction: `sqlite3_backup_step`
   in 8 MB steps, destination `synchronous=OFF`, flush every 32 MB, cancellation checked between steps.
5. Copy the allowlisted files. With all writers gated, they match the database.
6. Leave maintenance in a `finally`.
7. Blank secrets, run `quick_check`, and compare referenced files with the copy. A file that was already missing
   makes the result "completed with warnings" and does not block backups.
8. Package, encrypt, read the archive back to verify it, then publish to each destination by temporary name,
   rename, sidecar last. Each destination succeeds or fails independently.
9. Record the result, apply retention, delete staging. Clean abandoned staging at startup.

Cancellation, tray Quit and update must work at every step and never leave maintenance engaged.

## Schedule and retention

- A `BackupScheduler` hosted service checks wall-clock time every 30 s, so sleep and clock changes are handled.
- Daily or weekly at a chosen time and IANA timezone (containers default to UTC). Cron syntax is unnecessary.
- A missed run happens once, 5 minutes after start or wake. A skipped DST time runs at the next valid minute and
  a repeated one runs once.
- Retry after 5, 30 and 120 minutes. A failed upload retries the finished archive without a new maintenance
  window.
- Warn in the notifications panel and tray after a failure, or when the last good backup is two intervals old.
- Retention runs per destination, only after a verified success, and only on this installation's scheduled
  backups. Manual backups and the newest good backup are never pruned.
- The default folder is outside the data directory: `%LOCALAPPDATA%\TradeFoundry\Backups`, or a separate
  `/app/backups` volume in Docker. Warn when it is on the same drive as the journal.

## Backblaze B2

Use one `IBackupDestination` (put, list, open, prune, test) with folder and B2 implementations. B2 uses
`AWSSDK.S3` against `https://s3.<region>.backblazeb2.com`. Parse the region from the endpoint and require HTTPS.

### How B2 differs from AWS

- **Every bucket is versioned.** A delete without a version id only hides the file. Space is freed by deleting the
  version or by a lifecycle rule.
- **`writeFiles` is enough to hide.** A key without `deleteFiles` is therefore not append-only: with a short
  lifecycle window, a stolen key could hide every backup and let the lifecycle erase them.
- **The master key does not work** with the S3 API.
- **SDK default checksums have been rejected** (`Unsupported header 'x-amz-checksum-crc32'`). Support was reported
  added in July 2025, but the Backblaze docs reviewed for this plan do not confirm it.

### Bucket and key, created by the owner

| Setting | Value | Why |
|---|---|---|
| Bucket | Private, used only for these backups | Lifecycle and key scope stay simple |
| `daysFromHidingToDeleting` | 30 | Recovery window after pruning, overwrite or a stolen key |
| `daysFromUploadingToHiding` | null | The app decides retention. Age alone must never hide the last backup. |
| `daysFromStartingToCancelingUnfinishedLargeFiles` | 7 | Removes uploads abandoned by a crash |
| Key capabilities | `listFiles`, `readFiles`, `writeFiles`, plus `listAllBucketNames` for SDK compatibility | Deliberately no `deleteFiles` |
| Key scope | This bucket and the `tradefoundry/` prefix | Cannot change lifecycle rules or reach other data |

Use the B2 CLI or `b2_create_key` if the web UI cannot create this exact capability set. Do not use the "Keep only
the last version" preset: it erases hidden files after one day.

### Client behavior

- Set `RequestChecksumCalculation` and `ResponseChecksumValidation` to `WHEN_REQUIRED`. Send Content-MD5 on each
  put and part, and verify our own SHA-256 whenever an archive is downloaded. An ETag is not a content hash.
- Names are `tradefoundry/<installation>/<archive>` and are never reused. Every list request passes the prefix,
  which prefix-restricted keys require.
- Use a single put up to 16 MB, otherwise 16 MB parts streamed from disk. Abort on failure (`writeFiles` allows
  it) and upload the sidecar last.
- Prune by deleting **without** a version id, sidecar first. Lifecycle erases it 30 days later.
- Restore lists visible backups by default. "Show hidden backups" uses ListObjectVersions and reads by version id.
- Warn when a backup the app did not prune is no longer visible. That is the signal to act within the 30 days.
- "Test connection" puts, lists, reads and hides a probe, then tries to delete that version. Denial is the expected
  result. Success warns that the key can permanently destroy backups.
- The secret is write-only in the UI and stored with Data Protection.
- Object Lock is optional hardening and is not required. It cannot be turned off once enabled on a bucket.

### Prove against a real bucket at the start of Phase 4

1. Single and multipart upload with AWSSDK.S3 v4, including whether payload signing needs adjusting.
2. Hide with no `deleteFiles`. The docs imply this works but also recommend both capabilities. If it fails, revisit
   retention before building on it.
3. Delete by version is denied, and a hidden version can still be listed and downloaded.
4. Abort of an interrupted multipart upload, and listing with the prefix-restricted key.

### Encryption

Prefer the [age](https://age-encryption.org) format with a passphrase, so a backup can be decrypted without
TradeFoundry. The .NET candidate, AgeSharp, is a prerelease. Before adopting it, prove a 4 GB stream in bounded
memory and decryption by the reference `age` CLI. If it fails, choose again rather than silently inventing a
format.

Scheduled runs need the passphrase stored locally under Data Protection. Enabling encryption requires typing it
twice and confirming it is saved elsewhere, because a lost PC otherwise means unreadable backups.

## Restore

Entry points are "Restore from a backup" on `/Setup` and on the empty journal page. Sources are a folder, B2
credentials entered for the restore, or an upload. The backup list comes from sidecars, not the database, because
a clean installation has no history.

1. Confirm the installation is clean. Check again at commit.
2. Check free space, then download to staging on the data volume and decrypt.
3. Validate before touching live data: format and schema not newer than the app, allowlisted paths only, no
   traversal, links or case-colliding names, sizes capped by the manifest, every hash matches.
4. Open the database untrusted (`trusted_schema=OFF`), reject triggers and views (the app defines none), run
   `quick_check` and `foreign_key_check`.
5. Show date, version, journals and counts. Ask for confirmation and the backup's owner password.
6. Migrate the **staged** copy with the normal `TradeFoundryDb` initializer, so a failed migration leaves the
   installation untouched. Revoke MCP tokens, re-protect publishing tokens, set "integrations paused".
7. Write a durable pending-restore marker and stop the host.
8. Before anything opens the database: clear pools, move the clean installation's allowlisted entries aside, move
   the staged ones into place, validate, clear the marker. Move entries individually, because the data directory
   can be a Docker mount point. Each move is idempotent, so a crash resumes or rolls back at next start.
9. Rebuild the host in the same process, invalidate old sessions and go to sign-in.
10. Show a post-restore checklist. Watched imports, schedule and publishing stay paused until confirmed, because
    saved absolute paths may be wrong and a second installation must not prune or overwrite the first one's
    backups. The restore also gets a new installation id.

Restore before setup is anonymous by nature. Desktop mode allows it, because it binds only to loopback and Phase 0
filters host names. Every other mode requires a one-time code printed to the server log, even from loopback,
because a same-host reverse proxy makes every client look local. Only the restore upload route gets a raised
size limit.

## Start clean

"Move current data aside and start clean" lives in Settings > Backups and uses the same marker, restart and
idempotent moves as restore.

1. Show what will move, offer "Back up now", and require the owner password and a typed confirmation.
2. With the host down, move the database, its WAL/SHM and the allowlisted directories to
   `<data>/set-aside/<utc>/`. Staying on the same volume makes this a rename that needs no extra space. `keys/`
   and existing `backups/` stay where they are.
3. Create a fresh database that keeps only the owner account. It counts as clean, and the site never falls back
   to anonymous setup where someone else could claim it.
4. The empty journal page offers Restore and lists set-aside copies with their size and "Put back", which is
   allowed only while the installation is still clean.

`set-aside/` is never backed up, pruned or deleted by the app.

## Settings and tray

- New **Settings > Backups**, linked from the settings nav and command palette: last and next backup, Back up
  now, schedule, retention, destinations with Test, encryption, backup list with Verify and Download, history.
  Label it as instance-wide, because settings URLs are journal-scoped.
- Tables `backup_settings`, `backup_destinations`, `backup_runs` in a new `Data/TradeFoundryDb.Backup.cs`.
- Tray: "Back up now" over the existing stdin command channel (`backup-now`), status from `/health`, and Quit or
  update asks the server to cancel a running backup before the 10-second kill timeout.

Stopping the web server from the tray was considered. It gives the same consistency, but needs a second scheduler
and settings channel, does nothing for Docker, and drops in-flight saves. Maintenance mode avoids all three.

## Security prerequisite

`AllowedHosts` is `*` and an empty owner password is allowed. By reading the code (not exploited), a malicious
website using DNS rebinding could sign in to a passwordless desktop instance. Backup would turn that into a way
to send the whole database to someone else's bucket. Before backup ships:

- Restrict desktop mode to loopback host names.
- Require the password again to change destinations or encryption, or to download a backup.
- Require a non-empty password to enable cloud backup.

## Phases

Each phase is a separate PR and is usable on its own.

### 0. Groundwork (small)
Host filtering; `/health` and the tray probe; `PRAGMA user_version` schema stamp with refusal of newer databases;
shared directory constants; autosave retry.

*Done when:* existing tests pass, a rebinding Host header is rejected, the tray works against `/health`.

### 1. Manual local backup (large)
Activity gate across web, MCP, importer, AI and sign-in; snapshot engine; archive, manifest and verify; Backups
page with Back up now.

*Done when:* a 3 GB backup has under 40 s of maintenance, an edit made during it is saved afterwards,
cancellation at each step leaves the site open, and disk-full leaves no listed partial backup.

*Result (2026-11-23):* on a scratch copy of the 3.36 GB journal the site paused for 20.8 s and the whole
backup took 55 s (0.75 GB archive). An autosave sent during the pause waited 20.8 s and saved. Every table
and all 175 files matched the source, apart from that autosave. Right after copying 3.3 GB onto the same
disk, the pause grew to 100 s: held writes then get 503 after 90 s and autosave retries them.
The folder is stored in `app_settings` and runs in `backup_runs`; destinations get their own table in
Phase 4. The Docker default folder and volumes from Phase 5 were added here, so container backups are
never written inside the container.

### 2. Restore (large)
Validation, staged migration, host restart loop and marker, post-restore pause, start clean and put back.

*Done when:* a populated fixture round-trips with every table and file equal (except the documented changes),
non-clean installations are refused, start clean followed by put back loses nothing, hostile and truncated archives are refused, killing the process at each
commit step recovers, and Windows to Docker works in both directions.

*Result (2026-11-23):* a 0.75 GB backup of the 3.36 GB journal was restored through the UI into a fresh
server-mode installation, using the one-time code from the log. Checking, unpacking and upgrading took 35–50 s
with the site running, and the restart into the restored data took 2 s. Every table, all 1,794 trades and all
175 files matched the source, apart from the documented changes. Start clean and put back then ran through the
UI on the same installation, staying signed in. Interrupted and failed commits are covered by tests, not by
killing a real process. Docker was not available, so Windows to Docker is still untested.

As built:
- The staged copy is upgraded before the preview, so the preview shows what will be restored. The backup
  owner's password is checked at commit.
- Restore and put back also move the replaced (empty) installation to `set-aside/`, as `-before-restore` and
  `-replaced`. Only copies holding journals are offered for put back.
- Cookies carry a sign-in epoch that a restore changes, because every installation signs the owner in under the
  same name. Start clean keeps the epoch, so the owner stays signed in.
- The host restarts in the same process. `/health` reports a per-host instance id, so pages can tell when the
  restart has finished.

### 3. Schedule, retention and tray (medium)
Scheduler, catch-up, retention, stale warnings, tray commands.

*Done when:* fake-clock tests cover DST, sleep and missed runs, retention can never delete the last good backup,
and Quit or update during a backup is clean.

*Result (2026-11-23):* fake-clock tests cover skipped and repeated local times, missed runs after start and
after sleep, the 5/30/120-minute retries and staleness. Retention tests cover 120 days of backups,
manual and other-installation backups and a lone backup. With a real host started as the tray starts it,
`backup-now` over stdin started a backup, and `stop` during the copy and during compression exited in under
0.5 s with no partial files, no staging and the run recorded as cancelled. The tray menu itself was built but
not clicked through.

As built:
- Scheduled backups are on by default (daily 02:00 in the machine's time zone, keep 7/4/3), so an existing
  installation makes its first backup five minutes after it starts on this version.
- An installation with no journals is never backed up on schedule, so empty backups after start clean cannot
  push real ones out of retention.
- `/health` adds `backup`: `ok`, `running` or `attention`, never the reason. The tray polls it every 30 s and
  links to `/backups`, which opens Settings > Backups.

### 4. Encryption and Backblaze B2 (medium)
B2 and age spikes, encryption, portable publishing tokens, B2 destination, restore from B2.

*Done when:* the age CLI decrypts our file, an interrupted upload leaves nothing listed, and a restore on a clean
machine works from a real B2 bucket with only the key and passphrase, pruning works without `deleteFiles`, and a
hidden backup can be restored.

*Result (2026-11-23):*
- **Spikes.** AgeSharp 0.2.0-preview.7 encrypted 4 GB in 21 s and decrypted it in 8 s, using about 300 MB at 1 GB and
  at 4 GB (scrypt's fixed cost). Against `tradefoundry-backup`, with a key holding only `listFiles`, `readFiles`,
  `writeFiles` and `listAllBucketNames` on the `tradefoundry/` prefix, all ten B2 checks passed: single and
  multipart puts with Content-MD5, abort leaving nothing listed, hide without `deleteFiles`, delete by version
  refused (403), and a hidden version downloadable by id.
- **Scratch copy of the 3.36 GB journal, through the UI.** Encryption added 13 s and checking the encrypted copy
  3 s. The 754 MB upload took 212 s once and about 16 minutes another time, so speed varies. The test connection
  reported that the key cannot erase files.
- **Restore from the bucket into fresh server-mode installations, with only the key and the passphrase.**
  Download took 17 s, decrypting, checking and upgrading 36 s, and the restart 2 s. Every table, all 175 files and
  all 1,794 trades matched, apart from the documented changes.
- **Pruning.** Two real scheduled runs with "keep 1 daily" hid the older backup in the bucket. "Show hidden backups"
  listed it, and it restored the same way.
- **The age CLI.** rage (pyrage 1.4) decrypted the real 754 MB backup in 7 s, and the ZIP inside passed its CRC
  test. The reference `age` CLI only takes a passphrase typed at a terminal, so it still needs one manual run.
- **Site pause.** It was 18 s on a warm disk and 70–77 s right after several GB had been written to the same disk,
  as in Phase 1.

As built:
- **Encryption.** The whole archive is wrapped in age with a scrypt passphrase, stored under Data Protection.
  Enabling it needs the owner password, the passphrase typed twice (at least 12 characters) and the "saved
  elsewhere" box. The ZIP is built in staging on the data drive, verified, encrypted into the folder, and the
  encrypted file is decrypted again to check it matches.
- **Restore of encrypted backups.** They are read in place through `AgeRandomAccess`, so no decrypted copy is
  written to disk. Restore asks for the passphrase after choosing a backup from a folder, a file or B2. A wrong
  passphrase asks again.
- **Publishing tokens.** They travel unprotected in `secrets.json`, inside encrypted backups only. On commit they
  are protected again with the target machine's keys.
- **Secrets that never travel.** The backup passphrase, the B2 settings and the missing-backup list are stripped
  from every snapshot and dropped on restore. Start clean keeps them, together with the schedule.
- **Destinations.** The folder is always written first. B2 is an extra destination and requires encryption and
  a non-empty owner password. Objects are stored as `tradefoundry/<installation>/<archive>` with the sidecar
  uploaded last, as single puts up to 16 MB and 16 MB parts above that. Unfinished uploads under the
  installation's prefix are aborted before each upload.
- **Failed uploads.** A failed upload keeps the backup ("completed", with the cloud status "failed"), raises a
  warning, and is retried after 5, 30 and 120 minutes from the saved archive, without a new pause. "Upload again"
  does the same on demand.
- **Cloud retention.** It runs after a successful scheduled upload and only hides objects, sidecar first.
  TradeFoundry records what it uploaded and pruned, and warns when a backup it did not prune is no longer visible.
- **Restore from B2.** It lists every installation's backups. The key is held in memory for that restore only,
  and the download's SHA-256 is checked against the sidecar.
- **Password checks.** Changing encryption or B2 needs the owner password. Download of a backup from the
  Backups page was not built.

### 5. Documentation and drill (small)
Replace the manual backup notes in both READMEs where relevant, add the Docker backup volume to both compose
files, make the installer and uninstall text mention backups, and perform one real restore drill.


## Interaction with the tags work in progress

Uncommitted tags and review-identity changes appeared in the working tree on 2026-11-22 while this plan was being
written, and were not part of the measurements. As they stand:

- The new tables are in `journal.db`, so backups include them automatically.
- The one-time tags migration writes `backups/tags-migration-*.db` inside the data directory. The allowlist
  excludes it, and user backups must use another folder so retention never touches it.
- Restoring a pre-tags backup runs that migration on the staged copy. Keep the migration's safety file when
  staging is committed, and include such a backup in the restore tests.
- An older app opening a post-tags database is exactly what the Phase 0 schema stamp must prevent, so stamp after
  that work lands.

## Code touched

| Area | Files |
|---|---|
| New | `Services/Backup/*`, `Core/BackupModels.cs`, `Data/TradeFoundryDb.Backup.cs`, `Pages/Settings/Backups.*`, `Pages/Restore.*`, maintenance middleware |
| Host | `Program.cs` (middleware, health, host loop, pending restore), `Core/DesktopRuntime.cs` |
| Writers | `Mcp/McpHostedService.cs`, `Services/ImportFolderMonitor.cs`, `Services/Ai/AiReviewService.cs`, `Services/Ai/ChatGptSignInCoordinator.cs` |
| Stores | `JournalImageFileStore` callers, `ExpenseService`, `JournalImageSettings` |
| UI | `_SettingsNav`, `CommandPaletteCatalog`, `Setup`, `wwwroot/js/site.js` |
| Desktop and deploy | `DesktopShell/App.cs`, `DesktopShell/ServerController.cs`, `Dockerfile`, both compose files, installer |

All validation follows `AGENTS.md`: isolated output, unique data directory, MCP and benchmark refresh disabled.
Test fixtures must clear watched-import paths **before** a host starts on restored data, or it will import from
the real trading folders.

## References

- [SQLite online backup API](https://www.sqlite.org/backup.html)
- [AWS SDK data integrity defaults](https://docs.aws.amazon.com/sdkref/latest/guide/feature-dataintegrity.html)
- [B2 S3-compatible API](https://www.backblaze.com/docs/cloud-storage-call-the-s3-compatible-api)
- [B2 application keys for S3](https://www.backblaze.com/docs/cloud-storage-s3-compatible-app-keys)
- [B2 delete behavior](https://www.backblaze.com/apidocs/s3-delete-object)
- [B2 lifecycle rules](https://www.backblaze.com/docs/cloud-storage-lifecycle-rules)
- [B2 Object Lock](https://www.backblaze.com/docs/cloud-storage-object-lock)
- [B2 checksum header failure report](https://github.com/nocobase/nocobase/issues/6872)
- [Report of later B2 checksum support](https://www.drupal.org/project/s3fs/issues/3513322)
- [AgeSharp](https://www.nuget.org/packages/AgeSharp)

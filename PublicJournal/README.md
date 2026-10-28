# Public journal

This separate .NET 10 Razor Pages application displays one journal's last published snapshot. It never connects to the private TradeFoundry instance or its journal database. The public reader and the private preview use the same renderer.

Everything needed to build and host the public site is in this directory:

- `App/` — the public Razor Pages application and publication API.
- `Contracts/` — the publication contract, validation, and renderer shared with TradeFoundry.
- `Dockerfile`, `.dockerignore`, `compose.yml`, and `Caddyfile` — standalone container build and HTTPS hosting.
- `compose.production.yml` and `deploy.sh` — Forgejo deployment behind the existing nginx-proxy at `trades.shaa.one`.

TradeFoundry's publishing UI, exporter, and integration tests remain in the main application. The public site builds using this directory as its Docker context; it does not need the rest of the repository.

## Local development

Set `Publishing__Token` to a randomly generated secret of at least 32 characters and `Storage__DataDirectory` to an absolute writable directory, then run:

```powershell
$env:Publishing__Token = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:Storage__DataDirectory = 'C:\temp\tradefoundry-public-data'
dotnet run --project PublicJournal/App/TradeFoundry.PublicJournal.csproj --urls http://127.0.0.1:9080
```

In TradeFoundry, open **Publishing** for the journal. Save the destination and the same secret token, create a preview, inspect it, and select **Publish this exact snapshot**. Publishing includes the entire journal history, all daily writing, and referenced daily journal images, including today's saved entry. Open trades, private trade reviews, imports, receipts, and unrelated attachments are excluded. No upload occurs until Publish is selected. Local edits require another preview and publication.

The **Title** setting supplies the public page heading instead of the private journal name. **Introduction** uses the Daily Journal formatting editor and Markdown shortcuts; its formatted HTML appears below the title and timezone. Existing plain-text introductions remain editable. Save settings and create a new preview after editing either field. Update both TradeFoundry and the public reader before publishing rich introductions; older readers reject snapshots containing the new introduction content field.

The private preview is stored beside publishing settings in `journal.db`; it contains the complete outgoing data and copied images. Tokens are protected using ASP.NET Data Protection. Preserve TradeFoundry's data-protection keys across restarts, container replacements, and restores, or re-enter the token. Preview content is not a live view of subsequent local edits. Saving settings clears the preview.

## Docker and HTTPS

From `PublicJournal/`, create an ignored `.env` containing a strong `PUBLIC_JOURNAL_TOKEN` (generate with `openssl rand -hex 32`) and, when using the included HTTPS proxy, `PUBLIC_JOURNAL_DOMAIN=journal.example.com`. The explicit Compose project name `public-journal` preserves the volume names used by the previous deployment directory.

```sh
# Local loopback or an existing reverse proxy:
docker compose up -d --build journal
# Included HTTPS proxy; point the domain's DNS to this host and expose ports 80/443:
docker compose --profile https up -d --build
```

The application is reachable at `http://127.0.0.1:9080`; the optional Caddy proxy terminates HTTPS. Its [request body limit](https://caddyserver.com/docs/caddyfile/directives/request_body) matches the application's 128 MiB limit. When using another proxy, forward to that loopback address and allow request bodies up to 128 MiB with a generous upload timeout. Remote publishing requires HTTPS. HTTP publishing is accepted only for loopback destinations. Redirects are never followed with the publishing token; configure the final URL directly. Deploying or rotating the token requires recreating the journal container and saving the same token in TradeFoundry.

The `/health` endpoint checks database availability and returns `{"status":"ok"}`. Publication logs contain snapshot IDs and revision numbers, not content or tokens. Treat container environment configuration as secret-bearing.

## Forgejo deployment to trades.shaa.one

`.forgejo/workflows/public-journal.yml` runs on publishing-related pushes to the repository's `master` branch and manual dispatch. It tests the backend in Docker, builds only the public reader for production, transfers the commit-tagged image over SSH, and deploys it in `/opt/trades` on the existing Docker host. The existing nginx-proxy and certificate companion provide HTTPS over the external `int` network; the production deployment does not start Caddy or bind host ports 80/443.

Configure these **TradeFoundry repository Actions values**:

| Kind | Name | Value |
| --- | --- | --- |
| Variable | `PUBLIC_JOURNAL_DEPLOY_USER` | SSH deployment account, normally `ubuntu` |
| Secret | `PUBLIC_JOURNAL_DEPLOY_SSH_KEY` | Dedicated deployment SSH private key authorized on the Docker host |
| Secret | `PUBLIC_JOURNAL_DEPLOY_KNOWN_HOSTS` | Verified SSH host-key entry for `trades.shaa.one` |
| Secret | `PUBLIC_JOURNAL_TOKEN` | The same publishing token saved in TradeFoundry |

Register a repository/organization Forgejo runner with the `docker` label and Docker build access. The host must have Docker Compose supporting `up --wait`, the external `int` network, and a deployment account able to create `/opt/trades` and use noninteractive `sudo docker`. `sudo` must permit the explicit `--preserve-env=IMAGE_TAG,PUBLIC_JOURNAL_TOKEN` invocation used by the deployment script.

The deployment script installs `nginx-vhost.conf` for `trades.shaa.one` into the confirmed proxy bind mount at `/home/ubuntu/docker/nginx/letsencrypt-vhost-d`, accepting 128 MiB bodies and a three-minute upload timeout. It validates and reloads nginx-proxy; the deployment account needs noninteractive sudo permission for these operations as well. Keep DNS pointed to the existing proxy host. Publishing credentials travel to the host through encrypted SSH stdin and become container environment configuration; they are not written into the repository or a deployment `.env`.

The production Compose project remains `public-journal`, with the persistent `public-journal_journal-data` volume. Deployments preserve the active publication and journal binding. Do not run `down -v`. Container readiness is checked with the app's `--health-check` mode, followed by an external HTTPS `/health` check. A failed deployment fails the workflow and retains the existing data; it does not reset the database or automatically roll back schemas. After a successful container readiness check, `/opt/trades/current-image-tag` records the activated commit tag.

For manual redeployment of a previously loaded image, use the same `deploy.sh` and supply the stored publishing token through stdin. The image archive is required by the script and is removed after deployment, so first save the desired image to `public-journal-sha-<commit>.tar.gz` in `/opt/trades`. Keep the matching Compose/script revision with any rollback procedure.

## Publication protocol v1

All `/api/v1/publication` operations require `Authorization: Bearer <token>`:

- **GET** returns `{ revision, boundJournalId, snapshotId, publishedUtc }`.
- **PUT** accepts `application/json` containing `{ expectedRevision, snapshot }`. `snapshot` follows `Contracts/Publication.cs`; image bytes use JSON base64. Uploads are limited to 128 MiB, each PNG/JPEG/WebP image to 10 MiB. The server verifies image signatures, content node types, references, and aggregate consistency before activation. Unknown fields and unsupported schema versions are rejected.
- **DELETE** accepts `{ expectedRevision, journalId, resetBinding: false }`. Unpublishing removes public data and image access but retains the journal binding. Explicitly setting `resetBinding: true` removes that binding too.

Revisions advance on activation and unpublishing. Repeating the currently active snapshot ID with identical content succeeds without another revision, even if the first response was lost. Reusing an ID with different content, a stale revision, or a different journal returns HTTP 409. Refresh status after a timeout before retrying. After another publication or unpublishing, create a fresh preview. A first publish binds the website to that journal. To switch journals, unpublish with the reset-binding checkbox from the currently bound journal, then configure and preview the new journal.

Images are staged in the incoming snapshot and committed as SQLite BLOBs in the same transaction as metadata and writing. The database contains only the active publication, its binding, and revision. Failed parsing, validation, upload, or activation preserves the previous publication. Readers load a single snapshot per request. Calendar and pagination links carry its ID and return to the latest page when that publication has changed. Old image URLs become unavailable after replacement or unpublishing. Responses use `no-store`; already downloaded information cannot be recalled from visitors.

The dashboard's month is the month of the snapshot's journal-local `asOfDate`, labeled explicitly; it does not silently advance while the snapshot is stale. Net profit and the curve exclude cash flows. Calculated balance uses TradeFoundry's opening balance and cash-flow logic, including imported-baseline fallback. Unavailable balances and undefined win rate/profit factor are labeled unavailable. There are no live quotes, unrealized results, or automatic updates.

## Backup and restore

Back up the journal volume, which contains `publication.db`, its SQLite sidecar files when present, and all public image bytes. Stop only this service before filesystem copying, or use SQLite's online backup API; copying only the live main file can miss WAL transactions. Keep the Compose project/volume identity stable. Restore while the service is stopped, preserve permissions, then restart and check `/health`, the public page, and authenticated status. Keep the publishing token separately in your secret store. The private TradeFoundry database remains a separate backup.

`docker compose down -v` deletes the journal and proxy volumes. Rebuilding a container without deleting its volumes preserves the published snapshot and binding.

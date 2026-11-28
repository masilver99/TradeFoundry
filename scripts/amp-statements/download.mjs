// Downloads every account statement PDF from the AMP Client Portal.
//
// Cloudflare blocks browsers that Playwright launches (automation flags), so this
// starts Edge as an ordinary process with a remote-debugging port and its own
// profile. You log in and finish 2FA by hand. Nothing attaches to the browser
// until a tab reaches Account Statements; then Playwright connects over CDP and
// runs every request with fetch() inside that tab, so requests carry the
// browser's own cookies, TLS fingerprint and Cloudflare clearance.
//
// Usage:
//   node download.mjs [--url https://www.ampclientportal.com] [--out <dir>]
//                     [--since 2026-01-01] [--account <number>] [--type <StatementType>]
//                     [--profile <dir>] [--port 9333] [--edge <path to msedge.exe>]

import { chromium } from 'playwright';
import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..');

const args = parseArgs(process.argv.slice(2));
const baseUrl = (args.url ?? process.env.AMP_PORTAL_URL ?? 'https://www.ampclientportal.com').replace(/\/+$/, '');
const outDir = path.resolve(args.out ?? path.join(repoRoot, '.tradefoundry-data', 'amp-statements'));
const profileDir = path.resolve(
  args.profile ?? path.join(process.env.LOCALAPPDATA ?? os.homedir(), 'TradeFoundry', 'amp-portal-profile'));
const port = Number(args.port ?? 9333);
const since = args.since ? new Date(`${args.since}T00:00:00Z`) : null;
const loginTimeoutMs = 10 * 60 * 1000;
const statementsPath = '/documentcenter/statements';
const statementsUrl = `${baseUrl}/DocumentCenter/Statements`;
const cdpUrl = `http://127.0.0.1:${port}`;

await startEdgeIfNeeded();
console.log('Log in (including 2FA) in the Edge window, then open Document Center > Account Statements.');
console.log('Waiting up to 10 minutes...');
await waitForStatementsTab();

const browser = await chromium.connectOverCDP(cdpUrl);
try {
  const page = browser.contexts().flatMap(c => c.pages())
    .find(p => pathOf(p.url()).startsWith(statementsPath));
  if (!page) fail('Lost the Account Statements tab.');
  await page.waitForSelector('input[name="__RequestVerificationToken"]', { state: 'attached' });
  console.log('Signed in.');

  let statements = await readAllStatements(page);
  console.log(`Portal lists ${statements.length} statement(s).`);

  if (since) statements = statements.filter(s => s.date && s.date >= since);
  if (args.account) statements = statements.filter(s => s.AccountNumber === args.account);
  if (args.type) statements = statements.filter(s => (s.StatementType ?? '').toLowerCase() === String(args.type).toLowerCase());

  await fs.mkdir(outDir, { recursive: true });
  let saved = 0, skipped = 0, failed = 0;
  const manifest = [];

  for (const s of statements) {
    const target = path.join(outDir, safe(s.AccountNumber || 'unknown'), fileNameFor(s));
    const entry = {
      id: s.AccountStatementId,
      account: s.AccountNumber,
      type: s.StatementType,
      date: s.date ? isoDate(s.date) : null,
      documentName: s.DocumentName,
      file: path.relative(outDir, target),
    };
    manifest.push(entry);

    if (existsSync(target)) { skipped++; continue; }

    try {
      const pdf = await fetchPdf(page, s);
      await fs.mkdir(path.dirname(target), { recursive: true });
      await fs.writeFile(target, pdf);
      saved++;
      console.log(`saved   ${entry.file}`);
      await sleep(1000); // Cloudflare rate-limits bursts (HTTP 429)
    } catch (err) {
      failed++;
      entry.error = String(err.message ?? err);
      console.warn(`FAILED  ${entry.file}: ${entry.error}`);
    }
  }

  await fs.writeFile(path.join(outDir, 'manifest.json'), JSON.stringify(manifest, null, 2));
  console.log(`\nDone. ${saved} saved, ${skipped} already present, ${failed} failed. Output: ${outDir}`);
  if (failed) process.exitCode = 1;
} finally {
  // Detach only; the Edge window stays open and signed in.
  await browser.close().catch(() => {});
}

// ---------------------------------------------------------------------------

async function startEdgeIfNeeded() {
  if (await listTabs().catch(() => null)) {
    console.log(`Reusing the Edge already listening on port ${port}.`);
    return;
  }
  const edge = args.edge ?? [
    path.join(process.env['ProgramFiles(x86)'] ?? 'C:\\Program Files (x86)', 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
    path.join(process.env.ProgramFiles ?? 'C:\\Program Files', 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
  ].find(p => existsSync(p));
  if (!edge) fail('Could not find msedge.exe; pass --edge <path>.');

  await fs.mkdir(profileDir, { recursive: true });
  const child = spawn(edge, [
    `--remote-debugging-port=${port}`,
    `--user-data-dir=${profileDir}`,
    '--no-first-run',
    '--no-default-browser-check',
    statementsUrl,
  ], { detached: true, stdio: 'ignore' });
  child.unref();

  for (let i = 0; i < 30; i++) {
    if (await listTabs().catch(() => null)) return;
    await sleep(500);
  }
  fail(`Edge did not open its debugging port ${port}.`);
}

// Polls the plain HTTP tab list; this does not attach to any page.
async function waitForStatementsTab() {
  const deadline = Date.now() + loginTimeoutMs;
  while (Date.now() < deadline) {
    const tabs = await listTabs().catch(() => []);
    if (tabs.some(t => t.type === 'page' && pathOf(t.url).startsWith(statementsPath))) {
      await sleep(2000); // let the page finish loading before attaching
      return;
    }
    await sleep(1500);
  }
  fail('Timed out waiting for the Account Statements page.');
}

async function listTabs() {
  const res = await fetch(`${cdpUrl}/json/list`);
  if (!res.ok) throw new Error(String(res.status));
  return res.json();
}

// Same request the Kendo grid makes (aspnetmvc-ajax transport), with a big page size.
async function readAllStatements(page) {
  const pageSize = 500;
  const all = [];
  for (let pageNo = 1; ; pageNo++) {
    const result = await page.evaluate(async ({ pageNo, pageSize }) => {
      const token = document.querySelector('input[name="__RequestVerificationToken"]').value;
      const body = new URLSearchParams({
        sort: 'AccountStatementDate-desc',
        page: String(pageNo),
        pageSize: String(pageSize),
        group: '',
        filter: '',
        __RequestVerificationToken: token,
      });
      const res = await fetch('/DocumentCenter/Statements?handler=ReadRecords&action=DocumentCenter&controller=Statements', {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'X-Requested-With': 'XMLHttpRequest', 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8' },
        body,
      });
      return { status: res.status, text: await res.text() };
    }, { pageNo, pageSize });

    if (result.status !== 200) fail(`ReadRecords returned HTTP ${result.status}.`);
    let body;
    try { body = JSON.parse(result.text); } catch { body = null; }
    if (!body || !Array.isArray(body.Data)) fail('ReadRecords did not return grid JSON (session expired?).');
    if (body.Errors) fail(`ReadRecords reported errors: ${JSON.stringify(body.Errors)}`);

    for (const row of body.Data) all.push({ ...row, date: parseAspNetDate(row.AccountStatementDate) });
    if (body.Data.length < pageSize || all.length >= (body.Total ?? 0)) break;
  }
  return all;
}

// The View button loads ViewDocument (an HTML fragment with a PDF viewer) into a modal.
// Try the direct preview handler first, then dig the PDF out of that fragment.
async function fetchPdf(page, s) {
  const id = encodeURIComponent(s.AccountStatementId);
  const name = encodeURIComponent(s.DocumentName ?? '');

  let directError;
  try {
    const direct = await getBytes(page, `/DocumentCenter/Statements?handler=DocumentPreview&id=${id}`);
    if (isPdf(direct)) return direct;
    directError = `DocumentPreview returned ${direct.length} bytes that are not a PDF`;
  } catch (err) {
    directError = err.message;
  }
  console.warn(`  ${directError}; trying the viewer page`);

  const viewUrl = `/DocumentCenter/Statements?handler=ViewDocument&Id=${id}&name=${name}`;
  const fragment = await getBytes(page, viewUrl);
  if (isPdf(fragment)) return fragment;

  const html = fragment ? fragment.toString('utf8') : '';

  // Inline base64 PDF ("JVBERi0" is base64 for "%PDF-").
  const b64 = html.match(/JVBERi0[A-Za-z0-9+/=\\]+/);
  if (b64) {
    const pdf = Buffer.from(b64[0].replace(/\\\//g, '/').replace(/\\/g, ''), 'base64');
    if (isPdf(pdf)) return pdf;
  }

  // A URL to the PDF inside the viewer config (file: "...", src="...", href="...").
  const candidates = new Set();
  for (const m of html.matchAll(/["'](\/?[^"'\s]*(?:handler=\w+|\.pdf)[^"'\s]*)["']/gi)) {
    candidates.add(decodeHtml(m[1]));
  }
  for (const c of candidates) {
    const bytes = await getBytes(page, new URL(c, baseUrl + viewUrl).toString());
    if (isPdf(bytes)) return bytes;
  }

  const debugFile = path.join(outDir, '_debug', `${s.AccountStatementId}.html`);
  await fs.mkdir(path.dirname(debugFile), { recursive: true });
  await fs.writeFile(debugFile, html);
  throw new Error(`no PDF found; ViewDocument response saved to ${debugFile}`);
}

// fetch() inside the portal tab; bytes come back base64-encoded.
// Retries throttling and server errors with backoff; other failures throw with the status.
async function getBytes(page, url) {
  for (let attempt = 1; ; attempt++) {
    const r = await page.evaluate(async (url) => {
      const ctl = new AbortController();
      const timer = setTimeout(() => ctl.abort(), 60000);
      try {
        const res = await fetch(url, { credentials: 'same-origin', signal: ctl.signal, headers: { 'X-Requested-With': 'XMLHttpRequest' } });
        if (!res.ok) return { status: res.status, snippet: (await res.text()).slice(0, 200) };
        const bytes = new Uint8Array(await res.arrayBuffer());
        let bin = '';
        for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
        return { status: res.status, b64: btoa(bin) };
      } catch (e) {
        return { status: 0, snippet: String(e) };
      } finally {
        clearTimeout(timer);
      }
    }, url);

    if (r.b64 !== undefined) return Buffer.from(r.b64, 'base64');
    const retryable = r.status === 0 || r.status === 429 || r.status >= 500;
    if (!retryable || attempt >= 5) {
      throw new Error(`HTTP ${r.status} for ${url}: ${r.snippet.replace(/\s+/g, ' ')}`);
    }
    const wait = 5000 * 2 ** (attempt - 1);
    console.warn(`  HTTP ${r.status}, retrying in ${wait / 1000}s...`);
    await sleep(wait);
  }
}

function isPdf(buf) {
  return !!buf && buf.length > 4 && buf.subarray(0, 5).toString('latin1') === '%PDF-';
}

function parseAspNetDate(v) {
  if (!v) return null;
  const m = /\/Date\((-?\d+)/.exec(v);
  const d = m ? new Date(Number(m[1])) : new Date(v);
  return Number.isNaN(d.getTime()) ? null : d;
}

function fileNameFor(s) {
  const date = s.date ? isoDate(s.date) : 'nodate';
  const type = safe(s.StatementType || 'Statement');
  let doc = safe(s.DocumentName || `${s.AccountStatementId}.pdf`);
  if (!doc.toLowerCase().endsWith('.pdf')) doc += '.pdf';
  return `${date}_${type}_${doc}`;
}

function isoDate(d) {
  // The portal sends midnight in its own zone; use UTC parts so the day doesn't shift.
  return d.toISOString().slice(0, 10);
}

function pathOf(url) {
  try { return new URL(url).pathname.toLowerCase(); } catch { return ''; }
}

function safe(s) {
  return String(s).replace(/[<>:"/\\|?*\x00-\x1f]+/g, '_').replace(/\s+/g, ' ').trim();
}

function decodeHtml(s) {
  return s.replace(/&amp;/g, '&').replace(/\\u0026/g, '&');
}

function sleep(ms) {
  return new Promise(r => setTimeout(r, ms));
}

function parseArgs(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (!a.startsWith('--')) continue;
    const key = a.slice(2);
    const next = argv[i + 1];
    out[key] = next && !next.startsWith('--') ? (i++, next) : true;
  }
  return out;
}

function fail(msg) {
  console.error(msg);
  process.exit(1);
}

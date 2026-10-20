# Contacts Importer (Labs) — High-Level Plan

Status: **DRAFT v0.6**. Decisions in [§17](#17-decisions-log), open questions in [§16](#16-open-questions).

⚠️ = unverified assumption, needs spike or confirmation.

## 1. Goal

Admin UI "Labs" extension that lets marketers import contacts (MB → multi-GB) into production XbyK apps, incl. SaaS, with:

- common file formats (CSV/TSV, XLSX, JSON/NDJSON, compressed)
- dedupe/merge on a marketer-selected key (default email)
- optional assignment to an existing recipient list
- opt-in/out processing behaviors (e.g. unsubscribed handling)
- full dry run before writes, results after
- consent guidance + optional consent agreement recording
- traceability of how contacts entered the app

Persona: marketer. Must follow XbyK admin UI patterns (design components, page architecture, page commands, permissions). Keep extension simple: **no custom object types in v1**.

## 2. Context / constraints

### From existing repo (context only, not a pattern)

- Upload via raw WebSocket middleware, **no auth on endpoint** (anyone could insert/delete contacts). Do not repeat.
- CSV only, fixed 7 columns, dedupe by GUID only, `BulkInsert` only (skips handlers, group rebuild), preloads all GUIDs into memory.
- Recipient list import writes `EmailSubscriptionConfirmationInfo.IsApproved = true` → silently bypasses double opt-in.
- No consent handling, no tests, never left prerelease. XbyK 31.0.1 / net8.

### From XbyK docs

- Custom contact fields: Modules → OM.Contact → Database columns (dev/admin task, not marketer).
- `IInfoProvider<ContactInfo>`: `BulkInsert` / `BulkUpdate` / `BulkDelete`. BulkInsert skips handlers + contact group recalculation.
- Auto-merge by email only on live-site actions, **not** API/admin. `IContactMergeService.MergeContactByEmail` exists. Multiple contacts can share an email.
- Recipient list = `ContactGroupInfo` (`ContactGroupIsRecipientList`) + `RecipientListSettingsInfo`. Membership = `ContactGroupMemberInfo` (manual) + `EmailSubscriptionConfirmationInfo` (`IsApproved`, date).
- Unsubscribed contacts stay in list w/ "Unsubscribed" status. ⚠️ Storage of unsubscribe/bounce state undocumented. No documented public subscribe/unsubscribe service.
- Consents: `IConsentAgreementService.Agree/Revoke/IsAgreed` usable without HTTP context. ⚠️ No bulk API, no backdating param.
- Admin uploads: only Content hub has built-in chunked upload. No generic admin upload API.
- SaaS: `AddXperienceCloudStoragePathMapping` maps `~/assets` to Azure Blob; custom paths need `AddStoragePathRegistration` (`SharedTemp`/`SharedPersistent`) or may be lost on redeploy/slot swap. Blob quota 50/250/1000 GB by plan. DB tiers small (S0–S2 on L1/L2).
- Read-only (zero-downtime) deployments block DB/blob writes, scheduled tasks, background services → imports must tolerate interruption (see §11).
- `IScheduledTask` runs once across web farm instances; overlapping run skipped.
- Request limits: Azure ~4 min request timeout; ASP.NET Core request body size limit set by app (Kestrel default ~30 MB). Chunked upload keeps every request small + short → no file splitting needed by marketer. Document limits + chunk size setting.
- No built-in contact import → no duplication.
- Risk: CDP "profiles" preview may replace contacts later.

### From Labs reference repos

- Simple Stats: XbyK 31.9.0, net10, `Kentico.Xperience.Labs.*` naming, `UIApplication` under `DIGITAL_MARKETING`, `SecondaryMenuSectionPage` sections, custom `Page<TProps>` templates, `[PageCommand]` + `usePageCommandProvider().executeCommand`, custom permissions (`UIPermission`, `UIEvaluatePermission`, `PageCommand.Permission`), `AsyncEvent` audit hooks (`AfterExportStatsEvent`), unit + integration tests (MSSQL container), signing, Labs README badge.
- Admin design components: `Dropzone`/`UploadTile`/`FileInput`, `ProgressBar`, `Callout`, `Card`, `Table`, `Dialog`/`ConfirmationDialog`, `Select`, `Switch`, `RadioGroup`, `TileSelector`, `InfoCard`, `NotificationBar`, `Layout`. **No Wizard/Stepper** → compose from Layout + Headline + Buttons.

## 3. Architecture overview

```
Browser (React template)                     Server (XbyK app, 1..n instances)
────────────────────────                     ─────────────────────────────────
1. Pick file → parse sample (first ~1k rows)
2. Mapping/options UI          ─cmd─▶ SAVE_CONFIG  → {root}/{jobId}/job.json
3. Stream-parse whole file in Web Worker:
   map → normalize → Bloom dupe check
   → gzip NDJSON → base64      ─cmd─▶ UPLOAD_CHUNK → validate → {root}/{jobId}/chunks/{n}.ndjson.gz
   (+ full-file stats)         ─cmd─▶ SAVE_PROFILE → status.json
4. Loop while not done:        ─cmd─▶ PROCESS_NEXT (phase = analyze | execute)
   show progress                        next chunk(s) in file order / ~10–20 s → checkpoint → status.json
5. Download report             ─cmd─▶ GET_REPORT   ← {root}/{jobId}/report-*.csv.gz
                                        execute: CMSActionContext.LogWebFarmTasks = false
                                        on finish: custom web farm task → clear caches
```

`{root}` = configurable storage path (appsettings), CMS.IO-backed so it can be mapped to shared storage (§11).

Principles:

1. **Everything through page commands** (auth + permission per command). No middleware, websockets, controllers, anonymous endpoints, background threads.
2. **Parse + map in browser** (D17). Only mapped columns leave the browser (GDPR data minimization). Raw file never stored on server.
3. **Browser drives processing** (D11). Each `PROCESS_NEXT` call does bounded work and returns progress. Tab closed → import pauses; reopening continues.
4. **Idempotent runs** — correctness from idempotency, not resume machinery. Checkpoint only avoids redoing work.
5. **Clean data expected** (D18) — marketer responsible for one row per key. Tool warns on likely duplicates, doesn't engineer around them. Results stay deterministic (file order, last non-empty wins).
6. **Set-based writes, no per-row API calls or global events** (D19) — no `IContactMergeService`, no `ContactInfo.Update()` per row.
7. **Server never trusts client** — validates every uploaded record (schema, field types/lengths, mapped field allow-list) before storing.
8. **Dry run = same code path as execute, writes disabled.**
9. **Never silently change consent/subscription state.** Consent-affecting behaviors explicit, off by default, attested, logged.

## 4. Job lifecycle

`Configuring → Uploading → Uploaded → Analyzing → Analyzed → Confirmed → Running ⇄ Paused → Completed | Failed | Cancelled`

- Mapping/key/transforms change after upload → re-upload required (mapping applied client-side). Behavior options (§8) change after analysis → back to `Uploaded` (re-analyze only).
- Upload resume after tab close: marketer re-selects same file (browser can't keep file handle); client verifies file fingerprint (size + hash of first/last MB) and skips uploaded chunks.
- Drift guard: execute > N hours after analysis → warn / require re-analyze.
- Cancel → stop at batch boundary; results show partial counts.
- `Paused` = no `PROCESS_NEXT` heartbeat for > N s (tab closed, laptop asleep, network loss). Derived on read, not written by a thread.
- One driver per job: `PROCESS_NEXT` carries a driver token; another tab/user may take over only when heartbeat stale (explicit "Continue import" button). Another import already running in app → **warn** (D13), don't block.
- File cleanup (PII) — see §11 "File cleanup".

## 5. Formats & client parsing (D5, D17)

All client-side, in a **Web Worker** (keeps admin UI responsive).

- **CSV/TSV**: streaming parser (e.g. Papa Parse on `File.stream()`); delimiter/quote/encoding detection w/ override, BOM, header row.
- **NDJSON**: line-by-line over `File.stream()` + `TextDecoderStream`.
- **JSON**: top-level array, streaming JSON parser (⚠️ pick lib). Nested objects flattened to dotted paths (`address.city`).
- **XLSX**: first/chosen sheet, header row, 1,048,576 row cap. ⚠️ Spike: browser streaming XLSX (zip + SAX over sheet XML, e.g. zip.js + saxes) vs full-load libs (SheetJS memory ~10× file size). Fallback: cap XLSX size (e.g. 200 MB) + tell marketer to export CSV.
- **Compressed**: `.gz` via `DecompressionStream('gzip')`; `.zip` single entry via zip lib.
- Parser abstraction in TS: `RecordSource` → async iterator of `{ rowNumber, values }`.

## 6. Normalization & upload steps (D1, D17, D18)

1. **Sample profile** (instant): first ~1k rows / few MB → columns, sample rows, fill rate, email validity → mapping suggestions (§7).
2. **Map** (§7): marketer picks which columns to import + target contact fields + dedupe key → `SAVE_CONFIG`. Unpicked columns never leave browser.
3. **Full pass**: parse → keep mapped columns → trim values → normalize key (trim + lowercase email) → Bloom filter dupe check → append to chunk buffer → flush as gzipped NDJSON chunk at ~4–8 MB. Chunks keep file order + row #.
   - Rows with missing/invalid key → not uploaded, counted + listed by row # in client-side report (§10).
   - Full-file stats (rows, invalid, likely-dupe count) sent via `SAVE_PROFILE`.
4. **Duplicate warning** (D18): Bloom filter (~12 MB for 10M keys, 1% false positive) flags keys probably seen before. Before upload confirm: "~N rows look like duplicate keys. Clean data is expected; duplicates are applied in file order (last non-empty value wins) and the preview may over-count new contacts." Download suspect row #s. Continue or cancel to clean file.
5. **Upload**: `UPLOAD_CHUNK` page command (base64 gzipped NDJSON). Chunk size configurable, must stay below app request body limit; each request seconds vs ~4 min Azure timeout. 2–3 in parallel, retry w/ backoff, resumable (server reports received chunk IDs). SHA-256 per chunk (Web Crypto), server verifies + validates records.
6. `PROCESS_NEXT` processes chunks **sequentially in file order**, batches of ~1k rows.

### Duplicate keys in file — what actually happens

- **Same batch**: server collapses rows by key in memory before lookup (cheap, exact) → applies merge rule (§7) in row order → one create/update. Prevents two creates for one key.
- **Different batches**: earlier batch creates/updates contact; later batch matches it in DB and applies merge rule again → converges to "last non-empty value wins". Correct in execute.
- **Dry run**: no writes → second occurrence in later batch also looks "new" → create count over-stated by roughly the dupe count. Covered by the Bloom warning + results showing expected vs actual.
- Removed vs bucket design: hash bucketing, TS↔C# hash parity, per-bucket browser buffers, in-file dupe policy options.

- Limits: configurable max file size (default e.g. 5 GB), storage quota warning.

## 7. Mapping & merge rule

- Marketer chooses which file columns to import and maps each to a contact field (system + custom fields from OM.Contact). Auto-suggest by name. Unpicked columns ignored + never uploaded.
- Transforms (minimal): trim all values, lowercase email, date parsing (ISO + chosen locale format).
- **Dedupe key**: any mapped field, default `ContactEmail`.
- **Merge rule** (D20), per mapped column, for existing contacts:
  - file value non-empty → **replace DB value**
  - file value empty / whitespace → depends on **"Empty values"** option (D21):
    - **Ignore** (default) → keep DB value
    - **Clear** → write NULL to DB field (shown as warning on review screen w/ count of fields that would be cleared)
  - SQL: `CASE WHEN @clearEmpty = 1 THEN NULLIF(TRIM(v.col), '') ELSE COALESCE(NULLIF(TRIM(v.col), ''), c.col) END`
  - Later: per-column override if requested.
- **Existing matches** (DB already has duplicate contacts for a key): update newest (by `ContactLastModified`) | update all matches | skip + report. Default update newest. **No `IContactMergeService`** (D19: row-by-row, N+1 SQL, global events).
- Create-only toggle: don't touch existing contacts at all.
- Where dedupe data lives:
  - Selected key field + normalization → `job.json`.
  - Key values → uploaded chunk files (temp, PII, cleaned up §11).
  - Existing-contact matching → DB, per batch. Nothing extra persisted in DB besides contacts themselves.
- Key lookup, per batch of ~1k keys:
  - SQL Server hard limit = 2,100 parameters per request; very long literal `IN` lists (10k+) can hit error 8623 / slow compile. 1k is safe either way.
  - ⚠️ Spike: does XbyK `WhereIn` parameterize or inline? Preferred: single `nvarchar(max)` JSON parameter → `WHERE ContactEmail IN (SELECT value FROM OPENJSON(@keys))` → no param limit, one plan.
  - ⚠️ Spike: is `ContactEmail` indexed? Custom fields added via Modules have no index → lookups on custom key may scan `OM_Contact`; measure on 1M+ contacts, warn in UI if non-indexed key chosen.

## 8. Processing behaviors (marketer opt-in/out)

| Behavior | Options | Default |
|---|---|---|
| New contacts | create / skip | create |
| Existing contacts | merge rule (§7) / don't update | merge rule |
| Empty values in file | ignore (keep DB value) / clear DB field | ignore |
| Multiple existing DB matches | update newest / update all / skip | update newest |
| Invalid email | skip row / import w/o email (non-repeatable, flagged) | skip row |
| Recipient list: not member | add **pending** / add **confirmed** (attest) / skip | pending |
| Recipient list: unsubscribed | keep unsubscribed / resubscribe (attest) / report only | keep |
| Recipient list: bounced | keep excluded / report | keep |
| Consent | none / blanket / row-driven column (attest) | none |
| Consent previously revoked | keep revoked / re-agree (attest) | keep |
| Import source field stamp | created only / created + updated | created only |
| Contact groups | no automatic rebuild; results screen guides marketer (§13) | — |

All choices shown on review + results screens and in audit event.

## 9. Recipient lists & consent

### Recipient lists

- Selecting a list shows persistent `Callout`: double opt-in, demonstrable consent, region-neutral note, docs link. Wording starts from [XbyK GDPR compliance docs](https://docs.kentico.com/documentation/developers-and-admins/data-protection/gdpr-compliance); Sean reviews (D14).
- "Confirmed" / "resubscribe" require checkbox attestation + `ContactsImporter.ConfirmConsent` permission. Attestation (user, time, text version) logged + in event.
- "Pending" leaves contacts unconfirmed (no email sent in v1; DOI send = backlog).
- ⚠️ Spike: exact unsubscribe/bounce storage before implementing those behaviors.

### Consent agreements (decided D4, D6)

- Optional. Marketer picks an existing XbyK consent. UI instructs them to create a **dedicated consent** mirroring the text contacts originally agreed to (e.g. "Newsletter — collected via webinar signup, 2024–2026"); don't reuse a site consent contacts never saw.
- Scope (D6): **blanket** (all rows) by default, optional **column override** (consented y/n; optional date + source columns). Rows with column = n/empty get no agreement.
- `IConsentAgreementService.Agree` per contact, batched + throttled.
- Agreement time = import time. ⚠️ Spike: backdating via `ConsentAgreementInfo` provider. Fallback: source date/source → custom contact fields (documented).
- Already agreed → skip. Revoked → keep revoked by default.
- Warn if mapped consent date older than N months (staleness).

### GDPR notes (not legal advice — needs Kentico legal review)

- **Lawful basis**: consent isn't the only Art. 6 basis. Email marketing mostly governed by ePrivacy/PECR: consent required *except* "soft opt-in" (existing customers, similar products, opt-out offered). → consent step optional.
- **Demonstrable** (Art. 7(1)): must prove who/when/how/what text. Import only *records* consent obtained elsewhere; evidence must exist in source. → dedicated consent text, date/source fields, attestation, audit event.
- **Specific + informed**: XbyK consent text must match what person saw.
- **Withdrawal wins**: defaults keep unsubscribed/revoked.
- **Staleness**: no GDPR expiry, but regulators expect refresh.
- **Double opt-in**: not in GDPR text; de facto expected in DE/AT.
- **Other regimes**: CAN-SPAM (opt-out), CASL (express consent, 2-yr implied limits) → region-neutral UI copy.

## 10. Dry run, preview, results (decided D2)

- **Preview**: sample rows as mapped contacts (Table), validation errors highlighted.
- **Client-rejected rows** (parse errors, missing/invalid key) counted in `SAVE_PROFILE`; row #s + reasons downloadable from browser right after upload (never sent to server).
- **Analyze (dry run)**: full pass, no writes:
  - Aggregates: rows read/valid/invalid, in-file dupes, create/update/unchanged/multi-match, list adds (pending/confirmed), kept-unsubscribed, resubscribed, consents recorded/skipped/revoked.
  - Field change stats for updates ("12,301 contacts: FirstName changes").
  - Row-level report (row #, outcome, reason) as downloadable gzipped CSV; CSV-injection safe. **No key/PII by default** — marketer has original file, row # locates the row. Optional "include key column" (then PII, same cleanup rules).
  - Sample before/after diffs.
- **Confirm**: `ConfirmationDialog` restating counts; type-to-confirm when resubscribe/consent behaviors on or updates > N.
- **Execute**: progress (see below), pause/cancel.

### Progress UI (D23)

Every long phase shows: what's happening, how much is done, how much is left (when known), and rate/ETA. Updated from each `PROCESS_NEXT` / `UPLOAD_CHUNK` response (no polling needed — the browser is the driver).

| Phase | Known total? | Shown |
|---|---|---|
| Parse + upload | file size yes, row total no (estimated) | bytes read / file size (%), rows parsed, est. total rows (bytes ratio), chunks uploaded / created, MB/s, ETA, rejected rows, likely dupes |
| Analyze (dry run) | yes (row total from `SAVE_PROFILE`) | rows analyzed / total (%), rows/s, ETA, running counts: new / update / unchanged / skipped / invalid |
| Execute | yes | rows processed / total (%), rows/s, ETA, running counts: created / updated / skipped / failed, list adds, consents |
| Paused / waiting | — | why (tab closed earlier, app read-only, network retry #n), last progress time, "Continue" button |

- Elapsed time + ETA from moving average of last N calls (smooth jumps).
- Components: `ProgressBar` (`completed` 0–100), `InfoCard`/`InfoCardGroup` for counts, `NotificationBar`/`Callout` for paused/warning, `NavigationBlockerDialog` when leaving page mid-run ("import will pause").
- Page title / tab title shows `%` so marketer can see progress from other tabs.
- Phase stepper header (Upload → Map → Options → Review → Run → Results) shows current step.
- **Results**: expected (dry run) vs actual, error report download.
- **Recent imports**: list built from `status.json` files (who, when, file, outcome). Full history = phase 2.

## 11. Job state, storage, web farm (decided D3, D7)

- **No custom object types.** Job state in files under configurable root:
  - `appsettings`: `ContactsImporter:StoragePath` (default `~/assets/contacts-importer`), plus max file size, retention days, batch size.
  - Files: `job.json` (config, options, attestation), `status.json` (state, checkpoint, aggregates, heartbeat, worker instance), `parts/`, reports.
  - Writes via CMS.IO (write temp + replace) so path mapping works.
- **Shared storage**: Usage guide shows custom path mapping ([storage path mapping docs](https://docs.kentico.com/documentation/developers-and-admins/api/files-api-and-cms-io/file-system-providers/storage-path-mapping)) for SaaS/web farm so chunks + status reach all instances. ⚠️ Option: `AddContactsImporter(o => o.RegisterStoragePath = true)` calls `AddStoragePathRegistration` for the user.
- **Web farm**: `CMSActionContext.LogWebFarmTasks = false` during writes (avoid per-object task flood). On completion (and per pause/cancel), send **custom web farm task** that clears cache for contact, contact group member, subscription confirmation, consent agreement objects; also clear locally. ⚠️ Confirm exact cache dummy keys.
- **Processing model** (decided D11): browser-driven `PROCESS_NEXT` loop; no scheduled task, no hosted service. Each request may hit a different instance → shared storage required for web farm/SaaS (above).

### Why no background worker / checkpoint-resume machinery

Old extension processed inside one WebSocket request: tab close, connection drop or restart killed the import with no record and no resume. New design must split work anyway (CDN cuts requests at ~100 s; multi-GB ≈ 10–30M rows ≈ minutes–hours on small DBs ⚠️ spike).

Interruption risk depends on run duration, chance of restart/scale-in during run, and how much correctness the extra machinery buys. With idempotent runs:

| Case | Planning avoids? | Covered |
|---|---|---|
| Planned deploy/restart | yes | client retries next call on another/restarted instance |
| Crash, platform maintenance, scale-in | no | same — one failed call, retried |
| Read-only deploy | yes | calls fail cleanly per batch; client backs off, continues after |
| Tab closed / laptop sleep | yes (docs) | pauses; reopen continues from checkpoint |
| Two drivers | — | driver token + heartbeat |
| Rows without dedupe key | — | **not idempotent** → skipped by default; "create without key" option flagged as non-repeatable |
| Downstream effects mid-run (automations, groups) | — | not covered by any model; document |

Usage guide: "plan imports outside deploy windows; keep tab open; large imports best run off-hours".

## 12. Tracking / audit (decided D3)

- **Custom contact field** (decided D9; dev/admin adds text field per docs). Marketer picks field from dropdown (text contact fields) on Options step and sets stamp value; default generated (e.g. `import-2026-10-20-newsletter.csv`). UI warns if no suitable field. Usable in contact group conditions.
  - Who/when/options recorded by event log + `AfterContactImportEvent`, not in the field.
  - Phase 2 "undo created contacts": field value + `ContactCreated` within job run window identifies created contacts (no 2nd field).
- **Event log** via `ILogger` (`EventId` e.g. `CONTACT_IMPORT`): start, finish, cancel, failure, attestation.
- **Audit event** (Simple Stats pattern, `IAsyncEventHandler<T>` + `AddEventHandler`):
  - `AfterContactImportEvent` — `JobID`, `UserID`, `Timestamp`, `FileName`, `FileSize`, `IsDryRun`, `Status`, options summary, aggregates, `RecipientListID`, `ConsentID` + revision, attestation text/version, `ImportSourceFieldName`.
  - Handler exceptions logged, never fail import. Usage guide gets "Audit contact imports" section + sample handler.
  - Maybe `BeforeContactImportExecuteEvent` for dev-side policy gates (cancelable?).

### Undo — what's realistic

- Undoable: **created contacts** (delete; removes their memberships/consents/activities too) — via source field, no tables.
- Not undoable without per-row snapshots: **field updates**, **resubscribes**.
- Never undoable: **merges** (destructive), **downstream effects** (emails sent, automations, group recalcs).
- → Safety = dry run + confirm, not undo.

### File cleanup (PII) (D12)

Uploaded chunks (mapped columns only) = PII on server storage. Raw file never uploaded. No background worker → cleanup is event-driven + lazy:

| Files | Deleted when |
|---|---|
| Uploaded chunks | job Completed / Cancelled / Failed (last `PROCESS_NEXT` deletes them) |
| Row report (PII-free default) | marketer dismisses job, or retention (default 7 days) |
| Row report w/ keys (opt-in) | dismiss, or short retention (default 24 h) |
| Abandoned jobs (stale heartbeat > N days) | lazy sweep whenever importer app loads / any command runs |
| `status.json` (aggregates, no PII) | retention (default 90 days) → "Recent imports" |

- "Delete import files now" button per job.
- Optional: register a scheduled task for sweep in apps where importer rarely opened (docs, not required).
- Docs: Azure Blob soft delete / backups may retain deleted files for their configured window.
- Implement `IPersonalDataCollector`/`Eraser` only if PII persists beyond job lifetime (it shouldn't).

## 13. Write path & performance

- Batches (default ~1k, configurable); transaction per batch; checkpoint after commit. `PROCESS_NEXT` runs batches until time budget (~10–20 s) used, then returns.
- Idempotency rules: match by dedupe key before write; memberships/subscriptions/consents existence-checked; field writes deterministic for same input + policy.
- Create: `BulkInsert` (one SqlBulkCopy per batch).
- Update (D19, D20): one set-based statement per batch, merge rule in SQL — e.g. `UPDATE c SET c.ContactFirstName = COALESCE(NULLIF(LTRIM(RTRIM(v.FirstName)), ''), c.ContactFirstName), …, c.ContactLastModified = @now FROM OM_Contact c JOIN OPENJSON(@rows) WITH (…) v ON c.ContactID = v.ContactID` via `ConnectionHelper`. One JSON param → no 2,100-param limit. Raw SQL bypasses Info API by design (D22) → we own `ContactLastModified` + cache clear; groups left to marketer. Validate against [stable customization guidelines](https://docs.kentico.com/documentation/developers-and-admins/customization/stable-customization-guidelines) + XbyK upgrade safety (column names from class schema, not hard-coded). Fallback: `BulkUpdate` grouped by identical value sets (poor fit for per-row values).
- Memberships + subscription confirmations: `BulkInsert` after set-based existence check.
- Consents: `IConsentAgreementService.Agree` is per-row → conflicts with D19. ⚠️ Spike: `BulkInsert` of `ConsentAgreementInfo` rows directly (also enables backdating). Check what `Agree` does beyond insert (events, cache, revision lookup).
- **Speed over per-row correctness hooks** (D22, intentional): no per-row Info API calls or global events. 1k contacts must not fire 1k events serially. All cleanup happens once, after the run:
  - Web farm cache-clear task (+ local clear).
  - **Contact groups: not rebuilt automatically.** Results screen `Callout` tells marketer to rebuild affected groups or mark them for scheduled recalculation ([docs](https://docs.kentico.com/documentation/business-users/digital-marketing/contact-groups#recalculate-contact-groups)), w/ link to Contact groups app. Recipient lists = manual membership → unaffected.
  - Typical use: PROD before go-live or before new microsite launch → no live traffic depends on immediate consistency.
  - Docs: list what's skipped (events, handlers, automation triggers on contact create/update, group recalculation) so devs know which custom handlers won't run.
- Throttle: time budget per call, optional client delay between calls, adaptive batch size → protect live site on S0/S1.
- Read-only mode: write fails → status "waiting for app to become writable", client backs off + retries.
- ⚠️ Perf targets: 1M rows analyze < 15 min, execute < 60 min on SaaS Level 2.

## 14. Admin UI & project

App: **Contacts Importer (Labs)** under Digital marketing.

- `Imports` section: recent imports list + "New import".
- Import page (custom template, step layout): Upload → Map → Options → Analyze/Review → Run → Results. Step = job state.
- Permissions: `ContactsImporter.View`, `.Import`, `.ConfirmConsent`. (`.UndoImport` phase 2.) Never rename once shipped.
- Components: Dropzone/UploadTile, ProgressBar, Callout, Card, Table, Select, RadioGroup, Switch, ConfirmationDialog, InfoCard, NotificationBar. Strings via resx.
- Package `Kentico.Xperience.Labs.ContactsImporter.Admin`; client module `xperience-admin-labs-contacts-importer`. XbyK ≥ 31.9, net10. Central packages, lock files, nullable-as-errors, Sonar.
- Tests: TS unit (parsers, mapping, normalization, Bloom dupe check), C# unit (validation, dedupe, behavior matrix), integration (MSSQL container), perf (generator → 1M/10M rows, multi-GB).
- CI/signing/release from Simple Stats.
- Docs: Usage guide (marketer), Setup (custom field, storage mapping, permissions, worker), audit event, consent/legal note.

## 15. Phasing

0. **Spike**: browser streaming XLSX + JSON parsing (memory on 1M rows); TS↔C# key normalization parity; 2 GB chunked page-command upload behind Cloudflare/SaaS; `PROCESS_NEXT` throughput (rows/s) on S1 + L2 for 1M/10M rows; set-based OPENJSON update vs `BulkUpdate`; `WhereIn` param behavior; consent `BulkInsert` vs `Agree`; key column indexes (`ContactEmail`, custom fields); unsubscribe/bounce storage (ILSpy + Dancing Goat sign-up/unsubscribe flows); consent backdating; cache keys for web farm task.
1. **MVP**: all formats (§5) parsed client-side, dedupe/merge, update policies, recipient list (pending / confirmed w/ attest / keep-unsubscribed), consent (blanket + column), profiling, full dry run + reports, resumable execute, results, recent imports, permissions, event log + `AfterContactImportEvent`, source field stamp.
2. **Phase 2** (if customer traction): custom object types (job history, import status), undo created contacts, automation trigger ("Contacts imported"), mapping presets.
3. **Backlog**: send double opt-in email to pending, non-recipient contact group assignment, API/CLI import, recurring imports from remote source.

## 16. Open questions

1. Streaming JSON + XLSX libs for browser (spike).
2. SaaS autoscale-in behavior during long imports (low impact now — browser-driven calls just retry).

## 17. Decisions log

| # | Decision | Date |
|---|---|---|
| D1 | Upload: chunked (gzip + base64) via `[PageCommand]`. No custom endpoints. | 2026-10-20 |
| D2 | Dry run: full-file analysis, same code path as execute w/ writes off. | 2026-10-20 |
| D3 | Tracking v1: custom contact field + event log + `AfterContactImportEvent`. No custom object types in v1. | 2026-10-20 |
| D4 | Consent: optional; records agreements via `IConsentAgreementService`; marketer prepares dedicated consent first. | 2026-10-20 |
| D5 | MVP formats: CSV/TSV, XLSX, JSON/NDJSON, `.gz`/`.zip`. | 2026-10-20 |
| D6 | Consent scope: blanket default + optional per-row column override. | 2026-10-20 |
| D7 | Job state: files under configurable `ContactsImporter:StoragePath`; docs explain shared-storage path mapping; `LogWebFarmTasks = false` + custom web farm cache-clear task on finish. | 2026-10-20 |
| D8 | Phase 2 (traction-gated): custom objects, undo created contacts, automation trigger, mapping presets. | 2026-10-20 |
| D9 | Source field: one marketer-selected contact field (dropdown); marketer-editable value w/ generated default; who/when via event log + global event. | 2026-10-20 |
| D10 | Repo: rewrite in place; new package `Kentico.Xperience.Labs.ContactsImporter.Admin`; deprecate old NuGet. | 2026-10-20 |
| D11 | Processing: browser-driven `PROCESS_NEXT` page command loop, time-boxed batches, idempotent runs; no background worker. (Chunk layout superseded by D18.) Rows w/o dedupe key skipped by default. | 2026-10-20 |
| D12 | PII file cleanup: event-driven + lazy sweep; row report PII-free by default. | 2026-10-20 |
| D13 | Concurrent imports: warn, don't block. | 2026-10-20 |
| D14 | Consent/DOI UI copy based on XbyK GDPR compliance docs; Sean reviews. | 2026-10-20 |
| D15 | Upload limits: chunking avoids Azure ~4 min timeout + body size limit; document limits, no manual file splitting. | 2026-10-20 |
| D16 | Unsubscribe/bounce storage: resolve in spike (ILSpy on XbyK libs + sign-up/unsubscribe flows in Dancing Goat). | 2026-10-20 |
| D17 | Parse, map + normalize in browser (Web Worker); upload only mapped columns as gzipped NDJSON; server validates every record. | 2026-10-20 |
| D18 | Clean data expected (one row per key). No hash buckets; sequential chunks in file order; in-batch dupes collapsed; Bloom filter warns on likely dupes before upload. | 2026-10-20 |
| D19 | No `IContactMergeService`, no per-row Info API updates or global events. Set-based batch writes. | 2026-10-20 |
| D20 | Merge rule per mapped column: non-empty → replace DB value; empty handled per D21. Marketer picks columns + targets. | 2026-10-20 |
| D21 | "Empty values" option: ignore (default) or clear DB field. | 2026-10-20 |
| D22 | Raw set-based SQL bypassing Info API is intentional: fast, no per-row events; cleanup after run (cache clear). Contact groups: marketer rebuilds / schedules recalculation, guided by results screen. | 2026-10-20 |
| D23 | Progress UI: done / total (or estimate), %, rate, ETA, running outcome counts, pause reason, per phase. | 2026-10-20 |

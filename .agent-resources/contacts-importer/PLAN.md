# Contacts Importer (Labs) — High-Level Plan

Status: **DRAFT v0.10** (2026-11-16). Decisions in [§17](#17-decisions-log), open questions in [§16](#16-open-questions). Current state: [STATUS.md](STATUS.md).

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

### From old repo (context only, not a pattern)

Old code (`Kentico.Xperience.Contacts.Importer`, websocket middleware, `test/Generator`, sample CSV) removed from repo when aligned to Kentico Xperience repository template; available in git history on `main` before that change.

- Upload via raw WebSocket middleware, **no auth on endpoint** (anyone could insert/delete contacts). Do not repeat.
- CSV only, fixed 7 columns, dedupe by GUID only, `BulkInsert` only (skips handlers, group rebuild), preloads all GUIDs into memory.
- Recipient list import writes `EmailSubscriptionConfirmationInfo.IsApproved = true` → silently bypasses double opt-in.
- No consent handling, no tests, never left prerelease. XbyK 31.0.1 / net8.

### From XbyK docs

- Custom contact fields: Modules → OM.Contact → Database columns (dev/admin task, not marketer).
- `IInfoProvider<ContactInfo>`: `BulkInsert` / `BulkUpdate` / `BulkDelete`. BulkInsert skips handlers + contact group recalculation.
- Auto-merge by email only on live-site actions, **not** API/admin. `IContactMergeService.MergeContactByEmail` exists. Multiple contacts can share an email.
- Recipient list = `ContactGroupInfo` (`ContactGroupIsRecipientList`) + `RecipientListSettingsInfo`. Membership = `ContactGroupMemberInfo` (manual) + `EmailSubscriptionConfirmationInfo` (`IsApproved`, date).
- **"Subscribed" rule** (verified: Xperience 31.9.3 DLL analysis; unchanged across 30.x/31.x): contact counts as subscribed when it is a list **member** AND its **newest** `EmailSubscriptionConfirmationInfo` row for that list is **approved**. Same rule drives admin "Unsubscribed" tag + "Contact is in recipient list" macro rule.
  - Double opt-in: contact added to list only after confirmation link clicked (no "pending member" state in XbyK).
  - Unsubscribe: contact stays member, stops counting (newest confirmation not approved).
  - Added by code w/o confirmation row → member but doesn't count (shows as unsubscribed).
  - Implications: confirming = insert new approved row (newest wins), not update; detect "unsubscribed" = member + newest row not approved; dry run must read newest row per contact+list.
- **Bounces** (verified from XbyK source): `EmailBounceInfo` keyed by **email address** (not contact, not list) — `EmailBounceEmailAddress`, `EmailBounceIsHardBounce`, `EmailBounceSoftBounceCount`. Upserted on detected bounce; hard sets flag, soft increments count. Bounce state is global per address, shared by every contact/list using it. Send-time exclusion (spike, 31.6.0): hard bounce OR `EmailBounceSoftBounceCount >= BouncedEmailsGlobalOptions.SoftBounceLimit` (ASP.NET option, default 5), joined on `ContactEmail` in `Proc_EmailLibrary_EmailMarketingRecipient_GenerateRecipients`. Hard flag reflects latest bounce (later soft bounce clears it). No documented public subscribe/unsubscribe service.
- Consents (spike): `Agree` only inserts one `ConsentAgreementInfo` (ContactID, ConsentID, Time = now, ConsentHash = `consent.ConsentHash`, Revoked = false) + clears `consentagreement|{contactId}|{consentId}`; no events. "Agreed" = newest row by time not revoked.
- Spike evidence: [spike-findings.md](spike-findings.md) (decompiled Kentico.Xperience.Core 31.6.0; re-check on target version, min 31.10.3).
- Admin uploads: only Content hub has built-in chunked upload. No generic admin upload API.
- SaaS: `AddXperienceCloudStoragePathMapping` maps `~/assets` to Azure Blob; custom paths need `AddStoragePathRegistration` (`SharedTemp`/`SharedPersistent`) or may be lost on redeploy/slot swap. Blob quota 50/250/1000 GB by plan. DB tiers small (S0–S2 on L1/L2).
- Read-only (zero-downtime) deployments: admin UI unavailable → no imports during deployment. Not worked around (D29); documented.
- `IScheduledTask` runs once across web farm instances; overlapping run skipped.
- Request limits: Azure ~4 min request timeout, CDN (Cloudflare) ~100 s; ASP.NET Core request body size limit set by app (Kestrel default ~30 MB). Chunked upload keeps every request small + short → no file splitting needed by marketer. Document limits + chunk size setting.
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
2. Mapping UI                  ─cmd─▶ SAVE_CONFIG  → {root}/{jobId}/job.json
3. Stream-parse whole file in Web Worker:
   map → normalize → Bloom dupe check
   → gzip NDJSON → base64      ─cmd─▶ UPLOAD_CHUNK → validate → {root}/{jobId}/chunks/{n}.ndjson.gz
   (+ full-file stats)         ─cmd─▶ SAVE_PROFILE → status.json
4. Options UI (§8)             ─cmd─▶ SAVE_CONFIG  → job.json (options)
5. Loop while not done:        ─cmd─▶ PROCESS_NEXT (phase = analyze | execute)
   show progress                        next chunk(s) in file order / ~10–20 s → checkpoint → status.json
6. Download report             ─cmd─▶ GET_REPORT   ← {root}/{jobId}/report-*.csv.gz
                                        execute Completed: built-in cache clear (creates web farm tasks; D36)
```

`{root}` = configurable storage path (appsettings), CMS.IO-backed so it can be mapped to shared storage (§11).

Also needed (command names TBD): confirm/run (records attestation → `Confirmed`, D26), cancel, delete files / dismiss job (§11 "File cleanup"). Recent imports list loads with the page (runs lazy sweep, §11).

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

UI steps (§14) ↔ states: File + Map = `Configuring`; Upload = `Uploading`/`Uploaded`; Options = `Uploaded`; Review = `Analyzing`/`Analyzed`; Run = `Confirmed`/`Running`/`Paused`; Results = `Completed`/`Failed`/`Cancelled`.

- Mapping/key/transforms change after upload → re-upload required (mapping applied client-side). Behavior options (§8) change after analysis → back to `Uploaded` (re-analyze only).
- Upload resume after tab close: marketer re-selects same file (browser can't keep file handle); client verifies file fingerprint (size + hash of first/last MB) and skips uploaded chunks.
- Drift guard: execute > N hours after analysis → warn / require re-analyze.
- Cancel → stop at batch boundary; results show partial counts. No cache clear unless `Completed` (D36, §11).
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

## 6. Normalization & upload steps (D1, D17, D18, D34)

1. **Sample profile** (instant): first ~1k rows / few MB → columns, sample rows, fill rate, email validity → mapping suggestions (§7).
2. **Map** (§7): marketer picks which columns to import + target contact fields + dedupe key → `SAVE_CONFIG`. Unpicked columns never leave browser.
3. **Full pass**: parse → keep mapped columns → trim values → normalize key (trim + lowercase email) → Bloom filter dupe check → append to chunk buffer → flush as gzipped NDJSON chunk at ~4–8 MB. Chunks keep file order + row #.
   - Rows with missing/invalid key → **always skipped** (D34): not uploaded, counted + listed by row # in client-side report (§10). No option to import them. Email = key → invalid email = invalid key → skipped.
   - Email ≠ key (e.g. custom ID key): rows w/ valid key but invalid email uploaded w/ email flagged invalid; server applies §8 "Invalid email" option at analyze/execute → option not needed before upload.
   - Full-file stats (rows, invalid, likely-dupe count) sent via `SAVE_PROFILE`.
4. **Duplicate warning** (D18): Bloom filter (~12 MB for 10M keys, 1% false positive) flags keys probably seen before. Before upload confirm: "~N rows look like duplicate keys. Clean data is expected; duplicates are applied in file order (last non-empty value wins) and the preview may over-count new contacts." Download suspect row #s. Continue or cancel to clean file.
5. **Upload**: `UPLOAD_CHUNK` page command (base64 gzipped NDJSON). Chunk size configurable, must stay below app request body limit; each request seconds vs ~4 min Azure timeout. 2–3 in parallel, retry w/ backoff, resumable (server reports received chunk IDs). SHA-256 per chunk (Web Crypto), server verifies + validates records.
6. `PROCESS_NEXT` processes chunks **sequentially in file order**, batches of ~1k rows.

- Limits: configurable max file size (default e.g. 5 GB), storage quota warning.

### Duplicate keys in file — what actually happens

- **Same batch**: server collapses rows by key in memory before lookup (cheap, exact) → applies merge rule (§7) in row order → one create/update. Prevents two creates for one key.
- **Different batches**: earlier batch creates/updates contact; later batch matches it in DB and applies merge rule again → converges to "last non-empty value wins". Correct in execute.
- **Dry run**: no writes → second occurrence in later batch also looks "new" → create count over-stated by roughly the dupe count. Covered by the Bloom warning + results showing expected vs actual.

## 7. Mapping & merge rule

- Marketer chooses which file columns to import and maps each to a contact field (system + custom fields from OM.Contact). Auto-suggest by name. Unpicked columns ignored + never uploaded.
- Transforms (minimal): trim all values, lowercase email, date parsing (ISO + chosen locale format).
- **Dedupe key**: any mapped field, default `ContactEmail`.
- **Merge rule** (D20), per mapped column, for existing contacts:
  - file value non-empty → **replace DB value**
  - file value empty / whitespace → depends on **"Empty values"** option (D21):
    - **Ignore** (default) → keep DB value
    - **Clear** → write NULL to DB field (shown as warning on review screen w/ count of fields that would be cleared)
  - SQL: `CASE WHEN @clearEmpty = 1 THEN NULLIF(LTRIM(RTRIM(v.col)), '') ELSE COALESCE(NULLIF(LTRIM(RTRIM(v.col)), ''), c.col) END`
  - Later: per-column override if requested.
- **Existing matches** (DB already has duplicate contacts for a key): update newest (by `ContactLastModified`) | update all matches | skip + report. Default update newest. **No `IContactMergeService`** (D19: row-by-row, N+1 SQL, global events).
- Create only = "Existing contacts: don't update" (§8): existing contacts untouched.
- Where dedupe data lives:
  - Selected key field + normalization → `job.json`.
  - Key values → uploaded chunk files (temp, PII, cleaned up §11).
  - Existing-contact matching → DB, per batch. Nothing extra persisted in DB besides contacts themselves.
- Key lookup, per batch of ~1k keys:
  - SQL Server hard limit = 2,100 parameters per request; very long literal `IN` lists (10k+) can hit error 8623 / slow compile. 1k is safe either way.
  - Spike: `WhereIn` w/ strings inlines escaped literals below `CMSDefaultSQLInlineLimit` (1,000), switches to table-valued param (`Type_CMS_StringTable`, nvarchar(450)) at ≥1,000. No param-limit risk. Use `WhereIn` for lookups (or `OPENJSON(@keys)` in raw SQL path). Server validates key length ≤ 450 (TVP column) + field max lengths from class schema (`ContactEmail` 254).
  - Spike: `IX_OM_Contact_ContactEmail` exists (non-unique). Custom fields added via Modules get **no index** → UI warns when custom key chosen on large contact tables; Setup docs show creating a prefixed custom index.
  - `OM_ContactGroupMember` has no unique constraint on group + contact → existence check before insert required.

## 8. Processing behaviors (marketer opt-in/out)

| Behavior | Options | Default |
|---|---|---|
| New contacts | create / skip | create |
| Existing contacts | merge rule (§7) / don't update | merge rule |
| Empty values in file | ignore (keep DB value) / clear DB field | ignore |
| Multiple existing DB matches | update newest / update all / skip | update newest |
| Invalid email (only when email ≠ dedupe key; email = key → row always skipped, §6, D34) | skip row / import w/o email (repeatable: key present; applied server-side) | skip row |
| Recipient list: not member | selecting a list = add as **confirmed** (no options; attested via Run confirm dialog) | — (no list selected = no list changes) |
| Recipient list: unsubscribed | keep unsubscribed / resubscribe (attest) / report only | keep |
| Recipient list: bounced address (XbyK send-time rule: hard bounce OR `EmailBounceSoftBounceCount >= SoftBounceLimit`, §2, D35) | add to list + report / skip list add + report | add + report (XbyK excludes at send; importer never edits `EmailBounceInfo`) |
| Consent | none / blanket / blanket + per-row column override (attest) | none |
| Consent previously revoked | keep revoked / re-agree (attest) | keep |
| Import source field stamp | created only / created + updated | created only |
| Contact groups | no automatic rebuild; results screen guides marketer (§13) | — |

(attest) = attested via Run `ConfirmationDialog` (D26, §9). All choices shown on review + results screens and in audit event.

Rows w/ missing/invalid dedupe key: always skipped client-side (§6, D34), not an option.

## 9. Recipient lists & consent

### Recipient lists

- Selecting a list shows persistent `Callout`: double opt-in, demonstrable consent, region-neutral note, docs link. Wording starts from [XbyK GDPR compliance docs](https://docs.kentico.com/documentation/developers-and-admins/data-protection/gdpr-compliance); Sean reviews (D14).
- **Attestation = Run confirm dialog** (D26): no separate checkbox. Options page shows consent `Callout` when list/resubscribe/consent chosen. On Run, `ConfirmationDialog` restates counts + consent statement ("I confirm documented consent exists for these contacts…"); clicking Run = attestation. Requires `ContactsImporter.ConfirmConsent` permission. Attestation (user, time, statement text version) logged + in `AfterContactImportEvent`.
- No "pending" or "skip" option (D24, D25): XbyK has no pending member state; member w/o approved confirmation shows as **Unsubscribed** in admin. Selecting a list always adds non-members as confirmed (membership + new approved confirmation row, dated import time, no email sent), attested via Run confirm dialog.
- Unsubscribed detection + resubscribe follow §2 "subscribed" rule. Resubscribe = insert new approved confirmation row.
- Bounce lookup: set-based join of batch emails to `EmailBounceInfo` (`ContactEmail = EmailBounceEmailAddress`, unique index). §8 option treats as bounced exactly the addresses XbyK won't email: hard OR soft ≥ `SoftBounceLimit` (D35). Dry run + results report split bounced (hard) / bounced (soft ≥ `SoftBounceLimit`, read via `IOptionsMonitor<BouncedEmailsGlobalOptions>`) / soft < limit (still sends). Importer never writes bounce rows.

### Consent agreements (decided D4, D6)

- Optional. Marketer picks an existing XbyK consent. UI instructs them to create a **dedicated consent** mirroring the text contacts originally agreed to (e.g. "Newsletter — collected via webinar signup, 2024–2026"); don't reuse a site consent contacts never saw.
- Scope (D6): **blanket** (all rows) by default, optional **column override** (consented y/n; optional date + source columns). Rows with column = n/empty get no agreement.
- Write via `BulkInsert` of `ConsentAgreementInfo` (D27) — same columns `Agree` sets (ContactID, ConsentID, Time, ConsentHash from current consent, Revoked = false). No per-contact calls.
- Agreement time: import time by default (same clock as XbyK, `IDateTimeNowService`); **backdated** from mapped consent date column when provided (Time is plain column; date validated not in future).
- `ConsentHash` pins consent text version → finalize dedicated consent text before import.
- Already agreed (newest row not revoked) → skip. Revoked → keep revoked by default; note backdated agreement older than existing revocation stays revoked (newest wins).
- After `Completed` run (D36): `CacheHelper.ClearCache("consentagreement|")` (10-min `IsAgreed` cache).
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
- **Client-rejected rows** (parse errors, missing/invalid key — always skipped, D34) counted in `SAVE_PROFILE`; row #s + reasons downloadable from browser right after upload (never sent to server).
- **Analyze (dry run)**: full pass, no writes:
  - Aggregates: rows read/valid/invalid, invalid email (skipped / imported w/o email), in-file dupes, create/update/unchanged/multi-match, list adds (confirmed), kept-unsubscribed, resubscribed, consents recorded/skipped/revoked.
  - Field change stats for updates ("12,301 contacts: FirstName changes").
  - Row-level report (row #, outcome, reason) as downloadable gzipped CSV; CSV-injection safe. **No key/PII by default** — marketer has original file, row # locates the row. Optional "include key column" (then PII, same cleanup rules).
  - Sample before/after diffs.
- **Confirm**: `ConfirmationDialog` restating counts; includes consent statement when list add / resubscribe / consent behaviors on (= attestation, D26); type-to-confirm when updates > N.
- **Execute**: progress (see below), pause/cancel.

### Progress UI (D23)

Every long phase shows: what's happening, how much is done, how much is left (when known), and rate/ETA. Updated from each `PROCESS_NEXT` / `UPLOAD_CHUNK` response (no polling needed — the browser is the driver).

| Phase | Known total? | Shown |
|---|---|---|
| Parse + upload | file size yes, row total no (estimated) | bytes read / file size (%), rows parsed, est. total rows (bytes ratio), chunks uploaded / created, MB/s, ETA, rejected rows, likely dupes |
| Analyze (dry run) | yes (row total from `SAVE_PROFILE`) | rows analyzed / total (%), rows/s, ETA, running counts: new / update / unchanged / skipped / invalid |
| Execute | yes | rows processed / total (%), rows/s, ETA, running counts: created / updated / skipped / failed, list adds, consents |
| Paused / waiting | — | why (tab closed earlier, network retry #n), last progress time, "Continue" button |

- Elapsed time + ETA from moving average of last N calls (smooth jumps).
- Components: `ProgressBar` (`completed` 0–100), `InfoCard`/`InfoCardGroup` for counts, `NotificationBar`/`Callout` for paused/warning, `NavigationBlockerDialog` when leaving page mid-run ("import will pause").
- Page title / tab title shows `%` so marketer can see progress from other tabs.
- Phase stepper header (File → Map → Upload → Options → Review → Run → Results, §14) shows current step.
- **Results**: expected (dry run) vs actual, error report download. `Cancelled`/`Failed` → `Callout`: caches not cleared, use built-in clear cache action in System application (D36).
- **Recent imports**: list built from `status.json` files (who, when, file, outcome). Full history = phase 2.

## 11. Job state, storage, web farm (decided D3, D7)

- **No custom object types.** Job state in files under configurable root:
  - `appsettings`: `ContactsImporter:StoragePath` (default `~/assets/contacts-importer`), plus max file size, chunk size, batch size, retention (see "File cleanup").
  - Files per job (`{root}/{jobId}/`): `job.json` (mapping, options, attestation), `status.json` (state, checkpoint, aggregates, heartbeat, driver token, last instance), `chunks/{n}.ndjson.gz`, `report-*.csv.gz`.
  - Writes via CMS.IO (write temp + replace) so path mapping works.
- **Shared storage**: Setup docs (§14) show custom path mapping ([storage path mapping docs](https://docs.kentico.com/documentation/developers-and-admins/api/files-api-and-cms-io/file-system-providers/storage-path-mapping)) for SaaS/web farm so chunks + status reach all instances. ⚠️ Option: `AddContactsImporter(o => o.RegisterStoragePath = true)` calls `AddStoragePathRegistration` for the user.
- **Web farm / cache** (D28, D36; replaces custom task in D7): no custom web farm task. Built-in calls already create web farm tasks:
  - `CacheHelper.TouchKeys` for `om.contact|all`, `om.contactgroupmembercontact|all`, `emaillibrary.emailsubscriptionconfirmation|all`, `cms.consentagreement|all`
  - `CacheHelper.TouchKeys("om.recipientlist|byid|{listId}")` (subscription `IsConfirmed` cache, 10 min)
  - `CacheHelper.ClearCache("consentagreement|")`
  - Optional: `ContactInfo.TYPEINFO.InvalidateAllObjects()`, `ProviderHelper.ClearHashtables`
  - `BulkInsert`/`BulkUpdate` already touch `{type}|all` + log task regardless of `LogWebFarmTasks` (flag only affects per-object API).
  - Run once, only when execute reaches `Completed` (D36). Not on pause, cancel or failure. Import not completed (cancelled, failed, abandoned) → caches may stay stale; admin uses Xperience's built-in clear cache action (System application). Documented in Usage guide + limitations (§14, D38).
- **Processing model** (decided D11): browser-driven `PROCESS_NEXT` loop; no scheduled task, no hosted service. Each request may hit a different instance → shared storage required for web farm/SaaS (above).

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
- Optional: register a scheduled task for the lazy sweep in apps where importer rarely opened (docs, not required; not a processing worker).
- Docs: Azure Blob soft delete / backups may retain deleted files for their configured window.
- Implement `IPersonalDataCollector`/`Eraser` only if PII persists beyond job lifetime (it shouldn't).

### Why no background worker / checkpoint-resume machinery

Old extension processed inside one WebSocket request: tab close, connection drop or restart killed the import with no record and no resume. New design must split work anyway (CDN cuts requests at ~100 s; multi-GB ≈ 10–30M rows ≈ minutes–hours on small DBs ⚠️ spike).

Interruption risk depends on run duration, chance of restart/scale-in during run, and how much correctness the extra machinery buys. With idempotent runs:

| Case | Planning avoids? | Covered |
|---|---|---|
| Planned deploy/restart | yes | client retries next call on another/restarted instance |
| Crash, platform maintenance, scale-in | no | same — one failed call, retried |
| Read-only deploy | yes | not supported (D29): admin UI unavailable; import stops, marketer re-runs after deploy (idempotent) |
| Tab closed / laptop sleep | yes (docs) | pauses; reopen continues from checkpoint |
| Two drivers | — | driver token + heartbeat |
| Rows without valid dedupe key | — | always skipped client-side (D34), never uploaded → runs stay idempotent |
| Downstream effects mid-run (automations, groups) | — | not covered by any model; document |

Usage guide: "don't import during deployments; keep tab open; large imports best run off-hours; import not completed → clear caches via System application".

## 12. Tracking / audit (decided D3)

- **Custom contact field** (decided D9; dev/admin adds text field per docs). Marketer picks field from dropdown (text contact fields) on Options step and sets stamp value; default generated (e.g. `import-2026-10-20-newsletter.csv`). UI warns if no suitable field. Usable in contact group conditions.
  - Who/when/options recorded by event log + `AfterContactImportEvent`, not in the field.
  - Phase 2 "undo created contacts": field value + `ContactCreated` within job run window identifies created contacts (no 2nd field).
- **Event log** via `ILogger` (`EventId` e.g. `CONTACT_IMPORT`): start, finish, cancel, failure, attestation.
- **Audit event** (Simple Stats pattern, `IAsyncEventHandler<T>` + `AddEventHandler`). Only `AfterContactImportEvent`; no before/policy-gate event (D37):
  - `AfterContactImportEvent` — `JobID`, `UserID`, `Timestamp`, `FileName`, `FileSize`, `IsDryRun`, `Status`, options summary, aggregates, `RecipientListID`, `ConsentID` + `ConsentHash`, attestation text/version, `ImportSourceFieldName`.
  - Handler exceptions logged, never fail import. Usage guide gets "Audit contact imports" section + sample handler.

### Undo — what's realistic

- Undoable: **created contacts** (delete; removes their memberships/consents/activities too) — via source field, no tables.
- Not undoable without per-row snapshots: **field updates**, **resubscribes**.
- Never undoable: **merges** (destructive), **downstream effects** (emails sent, automations, group recalcs).
- → Safety = dry run + confirm, not undo.

## 13. Write path & performance

- Batches (default ~1k, configurable); transaction per batch; checkpoint after commit. `PROCESS_NEXT` runs batches until time budget (~10–20 s) used, then returns.
- Idempotency rules: match by dedupe key before write; memberships/subscriptions/consents existence-checked; field writes deterministic for same input + policy.
- Create: `BulkInsert` (one SqlBulkCopy per batch).
- Update (D19, D20, D21): one set-based statement per batch, merge rule in SQL (full expression §7) — e.g. "Empty values = ignore": `UPDATE c SET c.ContactFirstName = COALESCE(NULLIF(LTRIM(RTRIM(v.FirstName)), ''), c.ContactFirstName), …, c.ContactLastModified = @now FROM OM_Contact c JOIN OPENJSON(@rows) WITH (…) v ON c.ContactID = v.ContactID` via `ConnectionHelper`. One JSON param → no 2,100-param limit. Raw SQL bypasses Info API by design (D22) → we own `ContactLastModified` + cache clear; groups left to marketer.
  - API: `ConnectionHelper.ExecuteNonQuery(sql, QueryDataParameters, QueryTypeEnum.SQLQuery)` (or async) inside `CMSTransactionScope`; JSON as nvarchar(max) param. Same pattern Kentico uses internally.
  - Allowed by [stable customization guidelines](https://docs.kentico.com/documentation/developers-and-admins/customization/stable-customization-guidelines): direct DB access OK "for performance optimization of operations that handle very large amounts of data", w/ test coverage for schema changes → **integration tests required** (MSSQL container, per supported XbyK version; project §14, D33). Column names from class schema (`OM.Contact` `DataClassInfo`/`FormInfo`, `[]`-quoted, allow-listed), not hard-coded. Inline SQL only — no custom procs/views (zero DB objects installed).
- Memberships + subscription confirmations: `BulkInsert` after set-based existence check (no unique constraint on membership).
- Consents: `BulkInsert` of `ConsentAgreementInfo` (D27, §9).
- **Speed over per-row correctness hooks** (D22, intentional): no per-row Info API calls or global events. 1k contacts must not fire 1k events serially. All cleanup happens once, after the run:
  - Built-in cache clear calls (§11, D28) — create web farm tasks. Only on `Completed` (D36); otherwise manual clear in System application.
  - **Contact groups: not rebuilt automatically.** Results screen `Callout` tells marketer to rebuild affected groups or mark them for scheduled recalculation ([docs](https://docs.kentico.com/documentation/business-users/digital-marketing/contact-groups#recalculate-contact-groups)), w/ link to Contact groups app. Recipient lists = manual membership → unaffected.
  - Typical use: PROD before go-live or before new microsite launch → no live traffic depends on immediate consistency.
- Throttle: time budget per call, optional client delay between calls, adaptive batch size → protect live site on S0/S1.
- ⚠️ Perf targets: 1M rows analyze < 15 min, execute < 60 min on SaaS Level 2.

### Documented choices (not risks — intentional, must be in Usage guide + Setup docs + README Limitations, §14, D38)

| Choice | Consequence developers/marketers must know |
|---|---|
| Set-based writes bypass Info API (D22) | Custom handlers on per-object Insert/Update events (`ContactInfo.TYPEINFO.Events`, `ConsentAgreementInfo` events) don't run. Type-level `BulkInsert` handlers (`SqlEvents.BulkInsert`) still run. |
| Cache cleared by type keys (D28) | Custom caches keyed only on `om.contact|byid|{id}` stay stale until expiry → depend on `om.contact|all` or accept expiry. |
| Cache cleared only on `Completed` (D36) | Paused / cancelled / failed / abandoned import → caches may be stale; admin uses built-in clear cache action (System application). |
| Rows w/o valid dedupe key skipped (D34) | Not imported, no option; row #s downloadable after upload → fix file + re-run. |
| No contact group rebuild (D22) | Marketer rebuilds groups or schedules recalculation. |
| No automation/activity triggers | Contact create/update automations don't start for imported contacts. |
| Bounced addresses not edited | Importer never writes `EmailBounceInfo`; XbyK excludes at send (hard OR soft ≥ `SoftBounceLimit`, D35). |
| No import during deployment (D29) | Admin UI unavailable while app read-only; re-run after deploy. |
| Clean data expected (D18) | Duplicate keys → last non-empty wins; dry run may over-count creates. |

## 14. Admin UI & project

App: **Contacts Importer (Labs)** under Digital marketing.

- `Imports` section: recent imports list + "New import".
- Import page (custom template, step layout): File → Map → Upload → Options → Review (analyze) → Run → Results. Step = job state (§4).
- Permissions: `ContactsImporter.View`, `.Import`, `.ConfirmConsent`. (`.UndoImport` phase 2.) Never rename once shipped.
- Components: Dropzone/UploadTile, ProgressBar, Callout, Card, Table, Select, RadioGroup, Switch, ConfirmationDialog, InfoCard, NotificationBar. Strings via resx.
- Structure from Kentico Xperience repository template (Labs family, Name `ContactsImporter`); solution `Kentico.Xperience.Labs.ContactsImporter.slnx`.
- Single package (D32): `src/Kentico.Xperience.Labs.ContactsImporter.Admin` — services (record validation, dedupe/merge, set-based writes, job storage, cache cleanup, `AfterContactImportEvent`, `AddContactsImporter`) + `UIApplication`, pages, page commands, permissions, React client (`Client/`). Template core project removed, folded in.
- Client module key `kentico-xperience-labs-contacts-importer-admin` (org `kentico`, project `xperience-labs-contacts-importer-admin`). XbyK ≥ 31.10.3 (root `Directory.Packages.props`), net10.0. Central packages, lock files, nullable-as-errors, Sonar.
- Tests (D33, .NET test project naming):
  - TS unit: parsers, mapping, normalization, Bloom dupe check.
  - C# unit: `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.Tests` (NUnit; validation, dedupe, behavior matrix).
  - Integration: `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests` (MSSQL container; set-based SQL writes, cache cleanup, per supported XbyK version — required by §13).
  - e2e: `tests/e2e` (Playwright).
  - Perf data generator: `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator` (non-packable console tool → 1M/10M rows, multi-GB, all §5 formats). Separate tool, not folded into integration tests: its files feed browser parsing/upload + SaaS throughput runs (§15 spikes), not only DB tests. Replaces old `test/Generator` (removed).
- Sample: `examples/DancingGoat` + `examples/AppHost` (Aspire).
- CI/signing/release from template (GitHub Actions CI, Azure DevOps signing + publishing for Labs).
- Version (D39): continues old package line. Old `Kentico.Xperience.Contacts.Importer` last shipped `1.0.0-prerelease-4`; `Kentico.Xperience.Labs.ContactsImporter.Admin` starts at `1.0.0-prerelease-5` (`Directory.Build.props`).
- Docs: Usage guide (marketer, `docs/Usage-Guide.md`; incl. documented choices §13, "Audit contact imports" + sample handler §12), Setup (source custom field, storage path mapping, permissions, custom key index, optional lazy-sweep scheduled task §11), consent/legal note.
- **Limitations docs (required, D38, §15 MVP)**: populate README "Limitations" (replaces placeholder under Description) + Usage guide limitations/documented choices section. Pointer list, sources:
  - §13 documented choices table (bypassed events, contact groups not recalculated, stale custom caches, no automation triggers, bounces not edited)
  - no import during deployments (D29)
  - clean data expected; duplicate keys → last non-empty wins (D18)
  - browser tab must stay open; closed → pauses (D11)
  - rows w/o valid dedupe key skipped (D34)
  - cache clear only on `Completed`; otherwise built-in clear cache in System application (D36)
  - file size / storage limits (§6, §11, D15)
  - shared storage required for web farm/SaaS (§11)

## 15. Phasing

0. **Spike**: browser streaming XLSX + JSON parsing (memory on 1M rows); TS↔C# key normalization parity; 2 GB chunked page-command upload behind Cloudflare/SaaS; `PROCESS_NEXT` throughput (rows/s) on S1 + L2 for 1M/10M rows; set-based OPENJSON update throughput (data from DataGenerator, §14). Done (see spike-findings.md): WhereIn, indexes, bounce exclusion, consent bulk insert + backdating, cache keys, raw SQL API.
1. **MVP**: all formats (§5) parsed client-side, dedupe/merge, update policies, recipient list (confirmed w/ attest; keep-unsubscribed / resubscribe; bounce option + report, D35), consent (blanket + column), profiling, full dry run + reports, resumable execute, results, recent imports, permissions, event log + `AfterContactImportEvent`, source field stamp. Definition of done includes README "Limitations" + Usage guide limitations section populated (§14, D38).
2. **Phase 2** (if customer traction): custom object types (job history, import status), undo created contacts, automation trigger ("Contacts imported"), mapping presets.
3. **Backlog**: non-recipient contact group assignment, API/CLI import, recurring imports from remote source, import suppression list from previous ESP into `EmailBounceInfo` (hard bounces).

## 16. Open questions

1. Streaming JSON + XLSX libs for browser (spike).
2. SaaS autoscale-in behavior during long imports (low impact now — browser-driven calls just retry).

## 17. Decisions log

| # | Decision | Date |
|---|---|---|
| D1 | Upload: chunked (gzip + base64) via `[PageCommand]`. No custom endpoints. | 2026-10-20 |
| D2 | Dry run: full-file analysis, same code path as execute w/ writes off. | 2026-10-20 |
| D3 | Tracking v1: custom contact field + event log + `AfterContactImportEvent`. No custom object types in v1. | 2026-10-20 |
| D4 | Consent: optional; records agreements (`BulkInsert` per D27); marketer prepares dedicated consent first. | 2026-10-20 |
| D5 | MVP formats: CSV/TSV, XLSX, JSON/NDJSON, `.gz`/`.zip`. | 2026-10-20 |
| D6 | Consent scope: blanket default + optional per-row column override. | 2026-10-20 |
| D7 | Job state: files under configurable `ContactsImporter:StoragePath`; docs explain shared-storage path mapping; cache cleanup per D28 (custom web farm task dropped). | 2026-10-20 |
| D8 | Phase 2 (traction-gated): custom objects, undo created contacts, automation trigger, mapping presets. | 2026-10-20 |
| D9 | Source field: one marketer-selected contact field (dropdown); marketer-editable value w/ generated default; who/when via event log + global event. | 2026-10-20 |
| D10 | Repo: rewrite in place; old package code removed from repo (git history); new package `Kentico.Xperience.Labs.ContactsImporter.Admin` (single package, D32); deprecate old NuGet. Version continues old line (D39). | 2026-10-20 |
| D11 | Processing: browser-driven `PROCESS_NEXT` page command loop, time-boxed batches, idempotent runs; no background worker. (Chunk layout superseded by D18.) Rows w/o dedupe key always skipped (D34). | 2026-10-20 |
| D12 | PII file cleanup: event-driven + lazy sweep; row report PII-free by default. | 2026-10-20 |
| D13 | Concurrent imports: warn, don't block. | 2026-10-20 |
| D14 | Consent/DOI UI copy based on XbyK GDPR compliance docs; Sean reviews. | 2026-10-20 |
| D15 | Upload limits: chunking avoids Azure ~4 min timeout + body size limit; document limits, no manual file splitting. | 2026-10-20 |
| D16 | Unsubscribe/bounce storage: resolve in spike (ILSpy on XbyK libs + sign-up/unsubscribe flows in Dancing Goat). Subscribed rule since found (§2, 2026-10-21); bounce storage found (`EmailBounceInfo` per address, 2026-10-22); send-time rule found (`SoftBounceLimit` option, 2026-10-22). | 2026-10-20 |
| D17 | Parse, map + normalize in browser (Web Worker); upload only mapped columns as gzipped NDJSON; server validates every record. | 2026-10-20 |
| D18 | Clean data expected (one row per key). No hash buckets; sequential chunks in file order; in-batch dupes collapsed; Bloom filter warns on likely dupes before upload. | 2026-10-20 |
| D19 | No `IContactMergeService`, no per-row Info API updates or global events. Set-based batch writes. | 2026-10-20 |
| D20 | Merge rule per mapped column: non-empty → replace DB value; empty handled per D21. Marketer picks columns + targets. | 2026-10-20 |
| D21 | "Empty values" option: ignore (default) or clear DB field. | 2026-10-20 |
| D22 | Raw set-based SQL bypassing Info API is intentional: fast, no per-row events; cleanup after run (cache clear). Contact groups: marketer rebuilds / schedules recalculation, guided by results screen. | 2026-10-20 |
| D23 | Progress UI: done / total (or estimate), %, rate, ETA, running outcome counts, pause reason, per phase. | 2026-10-20 |
| D24 | No "pending" recipient list option: add confirmed (attested) or skip. Pending/DOI-send not planned (doesn't match XbyK design). ("skip" superseded by D25.) | 2026-10-22 |
| D25 | Selecting a recipient list always adds non-members as confirmed (attested). No "skip" option. | 2026-10-22 |
| D26 | Attestation captured by Run `ConfirmationDialog` (consent statement + click = attest, logged), not a separate checkbox. | 2026-10-22 |
| D27 | Consent agreements via `BulkInsert` of `ConsentAgreementInfo` (same columns as `Agree`); optional backdating from mapped date column. | 2026-10-22 |
| D28 | No custom web farm task; built-in `CacheHelper.TouchKeys`/`ClearCache` calls (create web farm tasks) after run (only `Completed`, D36). | 2026-10-22 |
| D29 | No support for importing during deployments (admin UI unavailable when read-only); documented. Read-only workarounds removed. | 2026-10-22 |
| D30 | Bypassed events, stale custom caches, etc. are documented choices, not risks (§13 table). | 2026-10-22 |
| D31 | Repo aligned to Kentico Xperience repository template (Labs, ContactsImporter); plan applies on top of template structure. | 2026-11-16 |
| D32 | Single package `Kentico.Xperience.Labs.ContactsImporter.Admin` (services, UI, page commands, permissions, React `Client/`); template core project removed + folded in. Resolves v0.8 §16 Q3. | 2026-11-16 |
| D33 | Test projects (.NET naming): unit `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.Tests` (NUnit); integration `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests` (MSSQL container, set-based SQL writes per stable customization guidelines); e2e `tests/e2e` (Playwright); perf data generator `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator` (non-packable tool). Resolves v0.8 §16 Q4. | 2026-11-16 |
| D34 | Rows w/ missing/invalid dedupe key always skipped client-side (not uploaded, counted, row #s downloadable); no "import without key" option. Invalid email matters only when email ≠ key: "skip row (default) / import w/o email" option, repeatable, applied server-side after upload. Email = key → invalid email = invalid key → skipped. Resolves v0.9 §16 Q3. | 2026-11-16 |
| D35 | "Bounced" for §8 recipient list option = XbyK send-time rule: hard bounce OR `EmailBounceSoftBounceCount >= SoftBounceLimit` (`IOptionsMonitor<BouncedEmailsGlobalOptions>`) → exactly the addresses XbyK won't email. Report still splits hard / soft ≥ limit / soft < limit (§9). Resolves v0.9 §16 Q4. | 2026-11-16 |
| D36 | Cache clear only when import `Completed`; not on pause, cancel or failure. Not completed → admin uses Xperience's built-in clear cache action (System application); documented in Usage guide + limitations. Resolves v0.9 §16 Q5. | 2026-11-16 |
| D37 | No `BeforeContactImportExecuteEvent`; only `AfterContactImportEvent`. Resolves v0.9 §16 Q6. | 2026-11-16 |
| D38 | README "Limitations" + Usage guide limitations/documented choices section populated as part of implementation (MVP definition of done); pointer list per §14. | 2026-11-16 |
| D39 | Version continues old package line: `Kentico.Xperience.Contacts.Importer` last shipped `1.0.0-prerelease-4` → `Kentico.Xperience.Labs.ContactsImporter.Admin` starts at `1.0.0-prerelease-5` (`Directory.Build.props`). | 2026-11-16 |

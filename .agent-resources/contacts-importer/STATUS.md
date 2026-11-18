# Contacts Importer (Labs) — Status

Single source of current state. Plan: [PLAN.md](PLAN.md). Spike evidence: [spike-findings.md](spike-findings.md). Slice specs: [1](implementation-spec-1.md), [2](implementation-spec-2.md), [3](implementation-spec-3.md), [4](implementation-spec-4.md), [5](implementation-spec-5.md).

Updated: 2026-11-17

## Phase

Implementation in progress (PLAN.md DRAFT v0.10 + decisions D36a, D40–D46). Slices 1–4 committed; slices 5 + 6 in working tree. Spec 6: [implementation-spec-6.md](implementation-spec-6.md).

| Slice | Scope | State |
|---|---|---|
| 1A | Server job core | done, reviewed |
| 1B | Client import core | done, reviewed |
| 2A | Processing engine (analyze/execute, set-based writes, integration tests) | done, reviewed |
| 2B | Admin app + File/Map/Upload UI + page commands + e2e | done, reviewed |
| 3A | Options/Review/Run/Results UI, `PROCESS_NEXT` loop, paged report download, e2e | done, reviewed (command service + gate read; React components not read line by line) |
| 3B | Server hardening (field exclusions, key rule, web farm-safe status, job DB lock, upload bound, heartbeat, package targets) | done, reviewed |
| 4A | Server: recipient list adds, unsubscribed keep/resubscribe, bounce split + skip, consent agreements (blanket / column, backdating, revoked keep/re-agree), attestation, `AfterContactImportEvent` | done, reviewed (decision code read) |
| 4B | UI: consent columns (Map), list/consent options + guidance, counts, attestation dialog w/ server `ConfirmConsent` check, e2e | done, reviewed (confirm permission check read; React not read line by line) |
| 5A | Client: JSON array, XLSX (streaming, shared strings, dates, sheet select), single-entry zip (ZIP64), UI + resx, Vitest, e2e formats spec | done, reviewed (tests rerun; parsers not read line by line) |
| 5B | `DataGenerator` console tool (all formats, gz/zip, seed, dupes/invalid/empty %, key offset) + own NUnit tests + README | done, reviewed (tests rerun) |
| 5C | README Limitations, Usage guide, `docs/Setup.md`, `docs/Importing-Contacts.md` | done, reviewed (options + event sample checked vs code) |
| 6A | Flow (D50): File/Map/Upload lock after upload starts, background upload bar + details panel (`useUploadSession`, `UploadBar`), "Start a new import", header clickability (`stepViews`), resume path | done, reviewed (tests rerun; not checked by eye in browser — pane signed out) |
| 6B | Review mapping card (`MappingSummary`, `mapping-summary.ts`) | done, reviewed (tests rerun) |
| 6C | Delete imports (D51): `DELETE_IMPORT` (import page + list; replaces `DELETE_FILES`), cancel before Run / "Start a new import" delete the whole import, list "Delete" action, Results without "Delete import files now", docs | done, not reviewed (NUnit 955, Vitest 449, admin e2e 25 passed + 1 skipped) |
| 6D | Fixes after Sean's 2026-11-17 test: success color (steps, upload done), upload panel right of Options cards (sticky, one data point per line), button spacing, "Download likely duplicates", spacing in process panel; driver token made by the browser and stored before the first call (server adopts a GUID token: reload during the first call is no longer "another tab"); "Continue here" waits for the other driver / tab lock with a notice instead of doing nothing; AppHost waits for admin-client; D52 step actions (Cancel on File/Map too, Start new after upload keeps the import, Results Delete), Review spacing, Review without upload summary | done, parent (NUnit 959, Vitest 452, build, e2e typecheck; admin e2e not run, not checked by eye) |
| 7 | D53 running-import warning on Review + Run disabled + server refuses CONFIRM_IMPORT (`GET_RUNNING_IMPORT`, poll 10 s; status `PhaseStartedUtc/ByUserId`); D54 optional scheduled task `Kentico.Xperience.Labs.ContactsImporter.FileCleanup` (`ContactsImporterCleanupTask`); D57 Review "Examples of changes" (`GET_SAMPLES`, `chunks/samples-*.json`, 3 lowest rows per field and kind, cleared first); D58 adaptive execute batch size (`AdaptiveBatchSize`, `TargetBatchSeconds` 2, `MinBatchSize` 50, `Checkpoint.BatchSize`); D55 "not legal advice" removed | done, reviewed by parent (key code read; NUnit 1028, Vitest 463, build 0 warnings, format, e2e typecheck; admin e2e + integration not run, not checked by eye) |
| 8 | D60 one unfinished Run at a time (`IsUnfinishedRun`: Confirmed/Running/Paused-in-execute, no heartbeat; CONFIRM_IMPORT + PROCESS_NEXT start refuse `otherImportRunning`; app lock `Kentico.Xperience.Labs.ContactsImporter:runs` 5 s → `busy`; Review/Run callout with open-import button; stale-check QuickTip via `lastRunEndedUtc`; e2e project `admin-import-runs` workers 1); D61 state icons in imports list (`stateIcon.ts`, `StateCell.tsx`) | done, reviewed by parent (rule read; NUnit 1053, Vitest 470, build 0 warnings, format, e2e typecheck; admin e2e + integration not run, not checked by eye) |

After slice 3: all contact fields mappable again except `ContactID`, `ContactGUID`, `ContactCreated`, `ContactLastModified`; missing references skip the row (D42).

Last verification (2026-11-18, slice 8): NUnit 1053, Vitest 470, package build 0 warnings, client build, e2e typecheck, `dotnet format` clean; admin e2e + integration not run. Before (slice 7): NUnit 1028, Vitest 463, package build 0 warnings, client build, e2e typecheck, `dotnet format` clean; admin e2e + integration not run. Before (6D): parent NUnit 959, Vitest 452, client typecheck + build, e2e typecheck, `dotnet format` clean; admin e2e not rerun after 6D (process spec take-over step changed). Before (6C): parent rerun NUnit 955, Vitest 449, client typecheck + build, e2e typecheck, `dotnet format` clean. 6C agent: admin e2e 25 passed + 1 skipped (permission spec) on own instance + scratch DB (3rd run; runs 1–2 fixed: flow test timeout, Windows file lock on delete → retry). DataGenerator 26 (slice 5). Integration not rerun since slice 4.

Working end to end in admin: upload CSV/TSV/NDJSON/JSON/XLSX (.gz, single-entry .zip) → map → options → dry run → review → run → results, resume/takeover/cancel, report download. Recipient list add (subscribed, no DOI email), keep/resubscribe unsubscribed, bounce counts/skip, consent agreements, attestation on Run.

Release version: `Kentico.Xperience.Labs.ContactsImporter.Admin` `1.0.0-prerelease-5` (D39).

MVP deliverable (D38): done — README "Limitations" + `docs/Usage-Guide.md` "Limitations and documented choices".

## Components

- `src/Kentico.Xperience.Labs.ContactsImporter.Admin`
  - `ContactsImporterOptions` + `AddContactsImporter(configure?)`
  - `Jobs/` models, state machine, config validator
  - `Storage/` job store over CMS.IO (versioned status, `status.final.json`)
  - `Ingest/` key normalizer, chunk validator, ingest + complete upload (upload size bound)
  - `Schema/` mappable contact fields (`IsSupported`, `CanBeKey`)
  - `Cleanup/` chunk delete, retention, lazy sweep
  - `Processing/` processor (analyze/execute, job DB lock), batch planner, SQL builder, repository, report, aggregates, cache invalidator
  - `UIPages/` app, imports list, import page, command service, report pager, permissions
  - `Resources/` admin UI localization (en-US): `ContactsImporterResources.resx` (server, `LocalizationTarget.Server`, keys = `ImportTextKeys`, arguments via `ImportMessages`) and `ContactsImporterClientResources.resx` (client, `LocalizationTarget.Client`, read with `useText()` → `react-i18next`). Plain string keys `contactsimporter.*`; coverage tests: `Client/src/shared/resources.test.ts`, `ImportResourcesTests`. Services return `ImportMessage` (key + args); the job status stores the key in `Error` + `ErrorArguments` (older jobs: English text, shown as is). Numbers, sizes, durations, dates use the admin UI culture (`uiLocale`). Report CSV headers and reason/outcome codes stay fixed machine names.
  - `build/*.targets` — excludes default storage folder from host project Content (D40)
  - `Client/src` — `import-core`, `worker`, `upload`, `mapping`, `process`, `options`, `import` (steps), `imports-list`, `shared`
- `tests/fixtures/normalization.json` — TS↔C# parity vectors
- `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.Tests` — C# unit
- `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests` — real Xperience DB (`CONTACTSIMPORTER_INTEGRATION_SQL`; DB name must end `-integration`; setup in `IntegrationDatabase` remarks)
- `tests/e2e` — Playwright; importer specs `admin/contacts-importer*.spec.ts`
- `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator` — perf file generator (exe + own NUnit tests; README has 1M sizes/times)
- `docs/` — `Usage-Guide.md` (overview, audit event, limitations), `Setup.md` (developers), `Importing-Contacts.md` (marketers)

## Local test recipe

- e2e that executes imports: scratch copy of dev DB + own DancingGoat instance (`ASPNETCORE_ENVIRONMENT=CI`, own port, own `--artifacts-path`), so test contacts stay out of the dev DB. Job folders still land in `examples/DancingGoat/App_Data/contacts-importer` (gitignored; safe to delete).
- `appsettings.CI.json` shrinks chunk / batch / time budget / heartbeat so loops are observable.

## Open items

Decide (Sean):

- Not implemented vs PLAN: decided 2026-11-17 (D53–D59): D13 replaced by D53 (built), stale consent warning dropped (D56), samples built (D57), adaptive batch built (D58), `RegisterStoragePath` dropped (D59), scheduled task built (D54).

- Review consent / double opt-in UI texts (D14): client resx keys `contactsimporter.listoptions.*`, `contactsimporter.consentoptions.*`, `contactsimporter.consentsummary.*`, `contactsimporter.review.confirm*` (`Resources/ContactsImporterClientResources.resx`) + server resx `contactsimporter.attestation.statement` (version "1" in `Jobs/ImportAttestationPolicy.cs`; bump on en-US text change; translations attested under the same version, culture not stored).

- Invalid date in a mapped date field → row rejected client-side (alternative: server decides). Date formats `iso | dmy | mdy`; no time zone conversion.
- Upload bound = 2 × `MaxFileSizeBytes` of stored gzipped chunks.
- Source field choices = all unmapped text fields (no custom-only filter).

Fix / verify later:

- Slice 8: a paused Run blocks new Runs until continued/cancelled/deleted, or the sweep cancels it after `AbandonedJobRetentionDays` (7) without activity. Real `sp_getapplock` run lock not integration-tested. No DOM/e2e for blocker callout, open-import button, stale-check note. List state icons not seen in browser. Slice 7: Cleanup task not seen in Scheduled tasks app. Adaptive batch defaults (2 s / 50) unmeasured on SaaS; SqlException numbers untested (no public ctor). Samples: no DOM/e2e test; stale web-farm writer handled by reader dedupe (unit only).

- Slice 6: upload bar 400 ms delay, auto-open on problems, header button styling not checked by eye. No DOM test for `useUploadSession` hook. Options edited during upload stay client-side (server refuses SAVE_OPTIONS while Uploading); clicking Review in header drops unsaved option edits. Upload panel has no cancel of its own (Cancel import / Start a new import below the step, list Delete).
- Slice 6C: delete retries a locked job folder 4× (≤ 1.85 s on the request thread; Windows held `status.final.json` right after the cancel once in e2e). A chunk upload in flight during the delete can leave an orphan `chunks/` folder (no job files) → sweep deletes it after `AbandonedJobRetentionDays`. Delete refusal (`importActive`) for a job driven by another tab not covered by e2e (NUnit only).

- Slice 5: XLSX/JSON parse not measured in a real browser (Node only: 1M xlsx 5.4 s / 132 MB heap; json 2.7 s / 20 MB). XLSX sheet choice not saved with job → resume re-profiles default sheet. Deflated `.xlsx` in `.zip` inflated to a Blob per pass. UTF-16 XML parts untested; CP437 zip names read as UTF-8. Generated XLSX not opened in Excel. ZIP64 only with synthetic headers (no real > 4 GB file). Docs: `SqlEvents.BulkInsert` handlers still running + SaaS `SharedPersistent` advice + admin request body limit not verified.

- Reference fields take IDs only (country/state/status names in a file → row skipped). `ContactStateID` not checked against `ContactCountryID`. Composite / disabled FKs not checked. `GetMappableFields()` runs an uncached `sys.foreign_keys` query per call (each chunk ingest, each `PROCESS_NEXT`). `ContactBounces` / `ContactSalesForce*` writable; whether Xperience later overwrites them not investigated. Map UI does not label reference fields yet (`isReference` in DTO).
- Slice 4: e2e seeds unsubscribe/revoke via SQL script (`tests/e2e/scripts/Invoke-E2ESql.ps1`, `System.Data.SqlClient` under pwsh) — untested on Linux CI. Bounce scenarios + ConfirmConsent-denied path not in e2e (NUnit only). Throughput impact of per-batch audience query + inserts not measured. Event handlers run sync-over-async on request thread (not load-tested).
- Cancel during run: batch in flight is written + reported but not counted → results undercount by ≤ 1 batch.
- CI workflow changes (client tests, mssql service + integration DB, e2e) never executed. Permission e2e spec skipped without a viewer user.
- Release build / `dotnet pack -c Release` not run since targets file was added.
- Job DB lock: separate connections per web request inferred from decompiled code, exercised only by single-instance e2e. Real web farm / SaaS not tested. Remaining status race: a non-terminal checkpoint can be lost between instances → ≤ 1 batch reprocessed (idempotent).
- `PROCESS_NEXT` is sync on the request thread (~time budget per call per driven job).
- Chunk boundaries depend on browser gzip → cross-browser resume uses truncate-and-reupload (unit tested, not tried in a second real browser).
- JS vs .NET lowercase differ for ~55 new code points → such keys get chunk rejected.
- Field labels: fields w/o own caption (system fields) shown as English words from the column name (`ContactBusinessPhone` → "Business phone", `fieldLabel`), column name as menu second line. Not localized (Xperience gives no caption).
- Fixed 2026-11-17 (Sean testing): "Select file" button unclickable (Kentico `Dropzone` CSS `pointer-events: none` on inner divs → button moved out; e2e `selectFile` now clicks it); dev server served stale worker (inline-worker loader now registers child deps); Map + Options steps regrouped into cards (shared `Section` in `shared/ui.tsx`; Options contact rules in two columns); recipient list "(None)"; bounced default = do not add (server + client); dialogs max 640 px (`DIALOG_MAX_WIDTH`); progress views of Review/Run fade in after 400 ms (`DelayedReveal`) so small files show no flash. Admin e2e not rerun after these.
- Analysis phase has no leave-page blocker (Run has).
- Admin shows its own "Network error" toast next to the retry bar.
- CSV quote char not auto-detected; multi-member gzip untested.
- Type-to-confirm (> 1000 changes) not exercised in browser.

## Open checks (⚠️ in PLAN.md)

- §11: `RegisterStoragePath` option; SaaS: `App_Data` not blob-mapped by default → Setup docs
- §13: perf targets on SaaS L2. Local numbers (Debug, Docker SQL): analyze ~50–69k rows/s; execute create ~6–7k rows/s, update ~17k rows/s; browser parse+upload ~15 MB/s (1 GB / 8.4M rows in 67 s, memory flat)
- §15: 2 GB upload behind Cloudflare/SaaS

## Pending decisions

PLAN.md §16:

1. SaaS autoscale-in during long imports (low impact)

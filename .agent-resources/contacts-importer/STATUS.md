# Contacts Importer (Labs) — Status

Single source of current state. Plan: [PLAN.md](PLAN.md). Spike evidence: [spike-findings.md](spike-findings.md). Slice specs: [1](implementation-spec-1.md), [2](implementation-spec-2.md), [3](implementation-spec-3.md), [4](implementation-spec-4.md).

Updated: 2026-11-16

## Phase

Implementation in progress (PLAN.md DRAFT v0.10 + decisions D36a, D40–D46). Slices 1–3 committed (`428fc6a`); slice 4 in working tree.

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
| 5 | XLSX/JSON/zip parsers, DataGenerator, docs + Limitations (D38) | not started |

After slice 3: all contact fields mappable again except `ContactID`, `ContactGUID`, `ContactCreated`, `ContactLastModified`; missing references skip the row (D42).

Last full verification (parent rerun, 2026-11-17): NUnit 944 (after typed event payload), Vitest 362, integration 67 (+1 env-gated throughput skipped), admin e2e 15 passed + 1 skipped (permission spec needs viewer user), `dotnet format` clean, client + DancingGoat build OK.

Working end to end in admin: upload CSV/TSV/NDJSON(.gz) → map → options → dry run → review → run → results, resume/takeover/cancel, report download. Recipient list add (subscribed, no DOI email), keep/resubscribe unsubscribed, bounce counts/skip, consent agreements, attestation on Run.

Release version: `Kentico.Xperience.Labs.ContactsImporter.Admin` `1.0.0-prerelease-5` (D39).

MVP deliverable (D38): README "Limitations" + Usage guide limitations section populated (source list PLAN.md §14 Docs) — slice 5.

## Components

- `src/Kentico.Xperience.Labs.ContactsImporter.Admin`
  - `ContactsImporterOptions` + `AddContactsImporter(configure?)`
  - `Jobs/` models, state machine, config validator
  - `Storage/` job store over CMS.IO (versioned status, `status.final.json`)
  - `Ingest/` key normalizer, chunk validator, ingest + complete upload (upload size bound)
  - `Schema/` mappable contact fields (`IsSupported`, `CanBeKey`)
  - `Cleanup/` chunk delete, retention, lazy sweep
  - `Processing/` processor (analyze/execute, job DB lock), batch planner, SQL builder, repository, report, aggregates, cache invalidator
  - `UIPages/` app, imports list, import page, command service, report pager, permissions; `Resources/` resx
  - `build/*.targets` — excludes default storage folder from host project Content (D40)
  - `Client/src` — `import-core`, `worker`, `upload`, `mapping`, `process`, `options`, `import` (steps), `imports-list`, `shared`
- `tests/fixtures/normalization.json` — TS↔C# parity vectors
- `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.Tests` — C# unit
- `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests` — real Xperience DB (`CONTACTSIMPORTER_INTEGRATION_SQL`; DB name must end `-integration`; setup in `IntegrationDatabase` remarks)
- `tests/e2e` — Playwright; importer specs `admin/contacts-importer*.spec.ts`
- `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator` — slice 5

## Local test recipe

- e2e that executes imports: scratch copy of dev DB + own DancingGoat instance (`ASPNETCORE_ENVIRONMENT=CI`, own port, own `--artifacts-path`), so test contacts stay out of the dev DB. Job folders still land in `examples/DancingGoat/App_Data/contacts-importer` (gitignored; safe to delete).
- `appsettings.CI.json` shrinks chunk / batch / time budget / heartbeat so loops are observable.

## Open items

Decide (Sean):

- Review consent / double opt-in UI texts (D14): `Client/src/shared/strings.ts` blocks `listOptions`, `consentOptions`, `consentSummary`, `review.confirm*` + resx `contactsimporter.attestation.statement` (version "1" in `Jobs/ImportAttestationPolicy.cs`; bump on text change).

- Invalid date in a mapped date field → row rejected client-side (alternative: server decides). Date formats `iso | dmy | mdy`; no time zone conversion.
- Upload bound = 2 × `MaxFileSizeBytes` of stored gzipped chunks.
- Source field choices = all unmapped text fields (no custom-only filter).

Fix / verify later:

- Reference fields take IDs only (country/state/status names in a file → row skipped). `ContactStateID` not checked against `ContactCountryID`. Composite / disabled FKs not checked. `GetMappableFields()` runs an uncached `sys.foreign_keys` query per call (each chunk ingest, each `PROCESS_NEXT`). `ContactBounces` / `ContactSalesForce*` writable; whether Xperience later overwrites them not investigated. Map UI does not label reference fields yet (`isReference` in DTO).
- Slice 4: list/consent "not found" messages from audience resolver are English constants, not resx. e2e seeds unsubscribe/revoke via SQL script (`tests/e2e/scripts/Invoke-E2ESql.ps1`, `System.Data.SqlClient` under pwsh) — untested on Linux CI. Bounce scenarios + ConfirmConsent-denied path not in e2e (NUnit only). Throughput impact of per-batch audience query + inserts not measured. Event handlers run sync-over-async on request thread (not load-tested).
- Cancel during run: batch in flight is written + reported but not counted → results undercount by ≤ 1 batch.
- CI workflow changes (client tests, mssql service + integration DB, e2e) never executed. Permission e2e spec skipped without a viewer user.
- Release build / `dotnet pack -c Release` not run since targets file was added.
- Job DB lock: separate connections per web request inferred from decompiled code, exercised only by single-instance e2e. Real web farm / SaaS not tested. Remaining status race: a non-terminal checkpoint can be lost between instances → ≤ 1 batch reprocessed (idempotent).
- `PROCESS_NEXT` is sync on the request thread (~time budget per call per driven job).
- Chunk boundaries depend on browser gzip → cross-browser resume uses truncate-and-reupload (unit tested, not tried in a second real browser).
- JS vs .NET lowercase differ for ~55 new code points → such keys get chunk rejected.
- Standard field captions equal column names in the class → Map step shows `ContactFirstName` etc.
- Analysis phase has no leave-page blocker (Run has).
- Admin shows its own "Network error" toast next to the retry bar.
- CSV quote char not auto-detected; multi-member gzip untested.
- Type-to-confirm (> 1000 changes) not exercised in browser.

## Open checks (⚠️ in PLAN.md)

- §5: streaming JSON parser lib; browser streaming XLSX vs full-load
- §11: `RegisterStoragePath` option; SaaS: `App_Data` not blob-mapped by default → Setup docs
- §13: perf targets on SaaS L2. Local numbers (Debug, Docker SQL): analyze ~50–69k rows/s; execute create ~6–7k rows/s, update ~17k rows/s; browser parse+upload ~15 MB/s (1 GB / 8.4M rows in 67 s, memory flat)
- §15: 2 GB upload behind Cloudflare/SaaS

## Pending decisions

PLAN.md §16:

1. Streaming JSON + XLSX browser libs (spike)
2. SaaS autoscale-in during long imports (low impact)

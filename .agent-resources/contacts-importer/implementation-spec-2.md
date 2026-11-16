# Slice 2 — Processing engine + admin app (File/Map/Upload)

Plan: [PLAN.md](PLAN.md). Builds on slice 1 ([spec 1](implementation-spec-1.md), wire contract unchanged). Two parallel work items.

## Slice 1 facts to build on

- Server (`src/.../Admin`): `IImportJobStore`, `IChunkIngestService` (`Ingest`, `CompleteUpload`), `ImportJobStateMachine` (static), `IContactFieldSchemaProvider`, `IImportJobCleanupService`, `ImportJobConfigValidator`, `KeyNormalizer`, `ImportJson`, `ContactsImporterOptions`. All sync, internal.
- Client (`Client/src/import-core`): `openRecordSource`, `profileSample`, `createImportPipeline` (→ `chunks()` of `UPLOAD_CHUNK` payloads, `progress()`, `result()`), `fingerprintFile`, `BloomFilter`, contract types.
- Not yet verified in a running app: `CmsIoImportFileSystem` (incl. `~` root resolution), `ContactFieldSchemaProvider` (needs DB).
- Chunks accepted only in `Uploading`; chunk list lives in sidecar files until `CompleteUpload`.
- Server rejects whole chunk on any invalid record → client must pre-validate w/ server limits (field max lengths, chunk sizes).
- `dotnet format` IDE0072 fix inserts `throw new NotImplementedException()` into switch expressions — check its output.

## Shared rules

- Don't commit / stash / reset. Don't change `Kentico.Xperience.*` / `@kentico/*` versions.
- Both agents run in one checkout at once. Every `dotnet` build/test/run uses own `--artifacts-path` (given in task prompt) → no bin/obj lock fights. A user-owned DancingGoat process may be running from `examples/DancingGoat/bin` — don't stop it.
- `ContactsImporterServiceCollectionExtensions.cs` + test `.csproj` + root `Directory.Packages.props` + `.slnx` files: shared, additive edits only, re-read right before editing.
- Lock files: `dotnet restore --force-evaluate` after package/project reference changes.
- Report: what was built, test counts + output tail, what was not run, deviations, open questions.

## 2A — Processing engine (C#)

Owns: `src/.../Admin/Processing/**` (new), `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests/**` (new), engine unit tests in `tests/...Admin.Tests/Processing/**`. May extend slice 1 server code where needed (say what changed).

Scope (PLAN §7, §8, §10, §11, §13; recipient lists / consent / bounce = slice 4, leave seams):

- `IImportProcessor.ProcessNext(jobId, phase analyze|execute, driverToken, takeOver, ct)` → progress DTO (state, rows processed / total, aggregates, driver result). Bounded work: batches (`BatchSize`) until time budget (option, default ~15 s), checkpoint after each batch commit, heartbeat. Sequential chunks, file order. Start transitions (`Uploaded → Analyzing`, `Confirmed → Running`), finish (`Analyzed`, `Completed`), failure (`Failed` + error, no contact data in message).
- Confirm (`Analyzed → Confirmed`, drift guard: analysis older than N hours → refuse) + cancel (stop at batch boundary) operations.
- Batch: collapse in-batch dupes by key in row order (merge rule) → key lookup (`WhereIn`, or `OPENJSON`) → classify create / update / unchanged / multi-match / skipped per §8 options (new contacts, existing contacts, empty values, multiple matches, invalid email) → value conversion per field data type (bad value = row-level outcome, not failure).
- Dry run = same code path, writes off (D2).
- Create: `BulkInsert`. Update: one set-based `UPDATE … FROM OPENJSON(@rows)` per batch via `ConnectionHelper` in `CMSTransactionScope`; merge rule SQL per §7; column names from class schema, `[]`-quoted, allow-listed; sets `ContactLastModified`. No custom DB objects.
- Source field stamp (§12; created only / created + updated) when configured.
- Idempotent: re-running a batch after a crash gives same end state.
- Aggregates (typed, extend `status.aggregates` shape as needed) + field change stats + row-level report `report-*.csv.gz` (row #, outcome, reason; no key unless `reportIncludesKey`; CSV-injection safe).
- On terminal state: delete chunks (cleanup service). On execute `Completed` only: cache clear calls (§11, D28, D36). Event log via `ILogger` (start, finish, cancel, failure).
- Recompute email validity server-side (don't trust `f` flag).
- Integration tests (required, §13): real Xperience DB on local SQL Server (`localhost,1433`, container `mssql2022`; CI = service container). Investigate how to get an isolated XbyK database for tests (Kentico test base classes if any; else a dedicated database created the way `scripts/Initialize-Database.ps1` does; Labs Simple Stats repo may exist next to this repo as a reference). Must not write to the DancingGoat database `xperience-by-kentico-contacts-importer`. Cover: create, update merge rule (ignore/clear), multi-match options, in-batch + cross-batch dupes, dry run writes nothing, idempotent re-run, resume from checkpoint, custom field key, cache keys touched, `ContactFieldSchemaProvider` against real class. Add CI job/step.
- Unit tests for pure logic (collapse, classification matrix, conversion, report writer, SQL builder).
- Rough throughput number (rows/s analyze + execute, e.g. 100k rows) from an integration test or script → report.

Done: build 0 warnings, unit + integration tests green, `dotnet format --no-restore --verify-no-changes` clean.

## 2B — Admin app + File / Map / Upload (C# pages + React + e2e)

Owns: `src/.../Admin/UIPages/**`, `ContactsImporterAdminModule.cs`, `Client/**` outside `import-core/` (may fix/extend `import-core` if needed; say what changed), `tests/e2e/**`, page-command unit tests in `tests/...Admin.Tests/UIPages/**`, `examples/DancingGoat` registration (+ overlay doc per AGENTS.md), `docs/` only if setup steps change.

Scope (PLAN §3, §4, §6, §10 progress, §14):

- Replace template placeholder (page, React template, e2e spec + page object, `UICategory`). App "Contacts Importer (Labs)" under Digital marketing (`UIApplication`, Simple Stats pattern if repo available next to this one).
- Permissions `ContactsImporter.View`, `.Import` (`.ConfirmConsent` declared, used slice 4). Every page command permission-checked. Job access: creator or any user with `.Import` (decide, document).
- Pages: Imports list (recent imports from `status.json`; "New import"; continue / delete files) + import page (custom template, step header File → Map → Upload → Options → Review → Run → Results; this slice implements File, Map, Upload; later steps show placeholder). Runs lazy sweep (`SweepIfDue`).
- Page commands (names final here): load config (mappable fields, limits: chunk sizes, max file size), create job / save config (validator; `Configuring → Uploading`), upload chunk, complete upload (profile), get job (resume), cancel, delete files. Thin over slice 1 services; DTOs camelCase per contract.
- Client: Web Worker running `import-core` (webpack worker bundling must work inside the admin client module — verify in browser); File step (Dropzone, format/encoding/delimiter override, sample table); Map step (column → field selects, auto-suggest, key field, date format, validation); Upload step (full pass, 2–3 parallel uploads, retry w/ backoff, progress per §10: bytes %, rows, chunks, rate, ETA, rejected, likely dupes; duplicate warning + suspect row download; rejected rows download, client-side only; `NavigationBlockerDialog`).
- Resume: re-select file → fingerprint check → regenerate chunks; skip while sha matches stored chunk; **first mismatch (other browser → other gzip bytes/boundaries) → server deletes chunks ≥ that index, client uploads the rest**. Add needed server op.
- Admin design components only; strings localizable (resx server-side; client strings centralized).
- Verify in running app: `CmsIoImportFileSystem` works (job folder under storage root), schema provider returns fields, 4 MiB chunk request passes body size limits (document any app setting needed).
- Tests: Vitest for worker protocol/upload transport/mapping state logic; NUnit for page command logic; Playwright e2e (page objects): app visible in nav, upload small CSV end to end → job `Uploaded` w/ expected row counts, rejected rows shown, resume after reload, permission denied for user w/o `.Import` if feasible. e2e fixture CSVs in `tests/e2e/fixtures`. e2e must pass locally against a DancingGoat instance started from own artifacts path/port (CI job recipe in `.github/workflows/ci.yml`; `tests/e2e/README.md` config vars).

Done: `npm test` / `typecheck` / `build` (Client), unit tests, `dotnet format` check, e2e (`npm test` in `tests/e2e`) green.

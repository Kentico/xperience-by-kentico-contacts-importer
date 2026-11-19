# Slice 3 — Options / Review / Run / Results + hardening

Plan: [PLAN.md](PLAN.md). Builds on slices 1–2 ([spec 1](implementation-spec-1.md), [spec 2](implementation-spec-2.md)); current state [STATUS.md](STATUS.md). Two parallel work items, disjoint files.

Out of scope (slice 4): recipient lists, consent, bounces, attestation text, `ConfirmConsent`, `AfterContactImportEvent`. Options UI shows only contact options.

## Shared rules

- Don't commit / stash / reset. Don't change `Kentico.Xperience.*` / `@kentico/*` versions.
- Both agents share one checkout. Every `dotnet` build/test/run uses own `--artifacts-path` (task prompt). A user-owned DancingGoat may run from `examples/DancingGoat/bin` — don't stop it, don't test against it.
- Shared files (service registration, test `.csproj`, `ci.yml`): additive edits, re-read right before editing.
- No contact data in logs, errors, `status.json`.
- `dotnet format` IDE0072 fix inserts `throw new NotImplementedException()` in switch expressions — check its output.
- Report: built, test counts + output tails, not run, deviations, open questions.

## 3A — UI + page commands (C# pages, React, e2e)

Owns: `src/.../Admin/UIPages/**`, `Resources/**`, `Client/**`, `tests/e2e/**`, `tests/...Admin.Tests/UIPages/**`.

Server API to call (internal, sync): `IImportProcessor.ProcessNext / GetProgress / Confirm / Cancel`, `IImportReportReader` (`HasReport`, `CopyTo`, `GetDownloadFileName`), `ImportJobStateMachine.ClassifyConfigChange / ApplyConfigChange`, `ImportOptions` (§8 enums). 3B may add small things in parallel (see below) — code against what exists, don't wait.

- Page commands (all `.Import` permission, job ID from URL, inputs validated):
  - `SAVE_OPTIONS` — §8 contact options (new contacts, existing contacts, empty values, multiple matches, invalid email [only when email mapped and ≠ key], source field + value + stamp mode, `reportIncludesKey`). Allowed `Uploaded`…`Confirmed` (not while driven); after analysis → back to `Uploaded` + clear attestation (state machine rule).
  - `PROCESS_NEXT` `{phase, driverToken, takeOver}` → `ImportProgress` DTO. Must not run longer than time budget + one batch.
  - `CONFIRM_IMPORT` → `Confirm`. `CANCEL_IMPORT` → **route through `IImportProcessor.Cancel`** (replace 2B's direct transition).
  - `GET_REPORT` — paged, bounded response size (page commands only, no controller); client assembles one `.csv.gz` download. Report for 10M rows must not be buffered whole on server or sent in one response.
  - `GET_JOB` extended w/ options, progress, aggregates, analysis summary, report availability.
  - Source field choices: text contact fields from schema provider; default stamp value generated (`import-{date}-{file name}`).
- Client steps (admin design components; strings in `shared/strings.ts`; reuse `withRetry`, `RateEstimator`, `useExecuteCommand`):
  - **Options**: RadioGroups/Selects per option w/ defaults per PLAN §8; "clear" empty values shows warning; no-suitable-source-field hint.
  - **Review**: starts/continues analyze loop; progress per PLAN §10 (rows / total, %, rows/s, ETA, running counts); then aggregates (create / update / unchanged / skipped by reason / duplicates / multi-match), field change stats, cleared-fields warning, dry-run report download; "Back to options"; "Run import" → `ConfirmationDialog` restating counts (type-to-confirm when updates > threshold) → `CONFIRM_IMPORT`.
  - **Run**: execute loop; progress + running counts; pause reasons (network retry #n, taken over elsewhere, stale → "Continue import" = `takeOver`); cancel w/ confirmation; leave-page blocker ("import will pause"); `%` in document title.
  - **Results**: final state; expected (analysis) vs actual table; report download; contact groups `Callout` (rebuild / recalculation, link to Contact groups app, PLAN §13); `Cancelled`/`Failed` → cache `Callout` (D36); "Delete import files now".
  - Loop handles every `ImportProcessOutcome` (`Processed`, `PhaseFinished`, `ActiveElsewhere`, `StaleRequiresTakeOver`, `AnalysisExpired`, `Interrupted`, `TransientError`, `Failed`, `InvalidState`, `JobNotFound`).
  - Reopen job in any state → lands on right step and continues (step = state, PLAN §4).
  - Imports list: state, counts, continue / results link.
- Tests:
  - Vitest: process loop state machine (all outcomes, retry, takeover, abort), options state, report assembly.
  - NUnit: new command logic (validation, state rules, report paging).
  - Playwright e2e (page objects; unique emails per run, e.g. `e2e.{runId}.n@example.com`): full import end to end — upload → options → analyze → review counts → run → results counts; then second import of an edited file → updates + unchanged counts; dry-run report download content; cancel during run (small batch/time budget via `appsettings.CI.json` if needed); reload mid-run → continues; options change after analysis → re-analyze. Verify created contacts exist via admin UI or a DB/API check available to e2e. Existing specs keep passing.
- Run e2e against own DancingGoat instance (port ≠ user's; `ASPNETCORE_ENVIRONMENT=CI`, embedded client) on a scratch copy of the dev DB (2B recipe: `scratchpad/run-dg.ps1`), so test contacts stay out of the dev DB. Drop scratch DB + stop processes after.

Done: Client `npm test` / `typecheck` / `build`; unit tests; `dotnet format --no-restore --verify-no-changes`; e2e typecheck + full e2e green.

## 3B — Server hardening (C#)

Owns: `src/.../Admin/Schema/**`, `Jobs/**`, `Storage/**`, `Ingest/**`, `Cleanup/**`, `Processing/**`, their unit tests, `tests/...IntegrationTests/**`. Not `UIPages/`, `Client/`, `tests/e2e`.

1. **Mappable fields**: exclude fields the importer can't safely write — foreign keys / references to other objects (`ContactStatusID`, persona etc.; detect from class/type info, not only a name list), system-maintained (`ContactBounces`, `ContactSalesForce*` if present), in addition to current exclusions. Integration test against real class; list resulting fields in report.
2. **Key field rule in `ImportJobConfigValidator`**: key data type ∈ text / integer / longinteger / guid; mapped field data types limited to what the engine supports. Same messages style. Engine start check stays as backstop. Expose "can be key" + supported flag on `ContactFieldDefinition` (additive) for the UI.
3. **Web farm-safe status updates**: cancel / takeover on one instance must not be overwritten by a driver's checkpoint write on another. Options: re-read + compare driver token and state inside every checkpoint write, w/ file-level optimistic check (version counter in `status.json`, write refused when stale → `Interrupted`). Document remaining race window honestly. Tests w/ two store instances over same folder.
4. **Failed job keeps chunks when failure is before any write** (config/type problem at analyze start) so marketer fixes options without re-upload; decide + document; terminal cleanup otherwise unchanged.
5. **`MaxFileSizeBytes` server-side**: reject chunk when stored compressed bytes for the job would exceed a bound derived from max file size.
6. **Heartbeat during long batch**: batch slower than `HeartbeatStaleSeconds` must not allow takeover mid-batch (e.g. heartbeat before lookup + before write, or stale = max(setting, observed batch time ×2)). Test.
7. **Stock-template check for `~/App_Data/contacts-importer`**: create a scratch project from the Xperience template version matching 31.10.3 (`dotnet new kentico-xperience-sample-mvc` or as `update-xperience` skill does; scratch dir, not in repo), drop `job.json` + `chunks/0.json` under `App_Data/contacts-importer`, check `dotnet build` and `dotnet publish` item lists: are the files Content / published? If yes → add `buildTransitive` targets to the package excluding `App_Data\contacts-importer\**` from Content/None (default path only; documented) + verify via local pack + scratch project. Report findings either way.
8. **PLAN accuracy**: don't edit PLAN.md; report exact cache behaviour of `BulkInsert` (which keys, when) so parent can fix D36 text.

Done: unit + integration tests green (integration DB `xperience-by-kentico-contacts-importer-integration`, env `CONTACTSIMPORTER_INTEGRATION_SQL`; setup in `IntegrationDatabase` remarks); `dotnet format` check clean.

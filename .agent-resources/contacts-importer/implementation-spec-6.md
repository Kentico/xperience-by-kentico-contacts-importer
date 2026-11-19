# Slice 6 — Step flow: locked setup steps, background upload, mapping on Review

Plan: [PLAN.md](PLAN.md) §4 (lifecycle), §10 (progress UI), §14 (UI). State: [STATUS.md](STATUS.md). Builds on specs 1–5. Decided by Sean 2026-11-17 after testing (D50). Client-only unless a work item finds a server change unavoidable (stop and report first).

## Decisions (D50)

- **File, Map, Upload lock once the upload has started.** No way back to them for this import. Options and Review offer **"Start a new import"** instead: confirmation dialog → cancel this import (existing cancel command; uploaded data deleted) → open a new import (same as "New import" on the list). Options are not kept (accepted cost).
- **Background upload.** "Save and upload" on Map goes straight to **Options**. The upload runs on as before (worker + parallel chunks) and shows in a small **upload status bar** at the top of the import page (progress bar + one line: "Uploading… 42 % · 1.2 MB/s"); a toggle opens the full upload details (rows read, skipped, likely duplicates, client reports download, pause/continue, errors — today's Upload step content). When the upload completes the bar disappears and the Upload step shows as done. Small files (1–5k rows) finish at once: no bar flash (reuse `DelayedReveal` idea).
  - Problems that need the user (chunk rejected, network retry exhausted, upload paused, upload failed) **open the panel automatically** and keep the bar visible. Rejected-rows report stays downloadable from the panel after completion while the job is on Options/Review (e.g. a small "Upload: 20 rows, 2 skipped · details" line).
  - Options can be changed during the upload; **"Save and check the file" is disabled until the upload is complete**, with a hint ("Waiting for the upload to finish"). Saving options during `Uploading` stays client-local (or use the server if it already allows `SAVE_OPTIONS` in `Uploading` — check `ImportJobStateMachine`; don't change server rules).
  - Leaving the page during the upload: existing leave-page blocker / resume behaviour still applies. Reopening a job in `Uploading`: the file must be selected again to continue (browser can't keep it) — this is the one case where File is shown again, with today's resume check (same file fingerprint) and **no Map step** (mapping is fixed); the upload continues in the bar and the page goes to Options.
- **Step header**: steps are clickable only when reachable and not locked.
  - Before upload: File / Map as today (not clickable ahead of current).
  - After upload started, before Run is confirmed: File, Map, Upload = done, **not clickable**; Options and Review clickable once reached (Review only after a check exists or is running).
  - From Run confirmed on (`Confirmed`, `Running`, `Paused` in execute, terminal states): every earlier step locked / not clickable; Run shown while running; terminal → **Results is the only reachable step** (all others shown done, not clickable).
  - Use real buttons/links with `aria-current`, keyboard focus, `data-step-state` = `current | done | upcoming | locked` (e2e uses it).
- **Mapping on Review**: Review shows a compact read-only "Mapping" card: file column → contact field (readable label, `fieldLabel`), the match field (marked), date format if a date field is mapped, consent columns if mapped. Unmapped file columns are not listed (they never left the browser) — show "N columns not imported" only if the job stores that count; otherwise omit. Collapsed by default if > 8 rows, with "Show all".

## Shared rules

- Don't commit / stash / reset. No `Kentico.Xperience.*` / `@kentico/*` version changes. Own `--artifacts-path` for every `dotnet` command. Don't stop or test against the user's running Aspire DancingGoat (port 56605) or the dev DB; for e2e use your own instance + scratch DB copy (STATUS "Local test recipe", `tests/e2e/README.md`); drop it after.
- Text in `Resources/ContactsImporterClientResources.resx` (`contactsimporter.*`, plain English, same voice as existing texts); coverage test must pass. Shared UI pieces in `Client/src/shared/ui.tsx` (`Section`, `DelayedReveal`, `DIALOG_MAX_WIDTH`) — reuse.
- Shared files (`ImportTemplate.tsx`, `shared/ui.tsx`, `shared/commands.ts`, resx, `contactsImporterPage.ts`): additive edits, re-read right before editing.

## 6A — Flow, background upload, step header, "Start a new import" (React, e2e)

Owns: `Client/src/import/ImportTemplate.tsx`, `StepHeader.tsx`, `UploadStep.tsx` (→ upload panel), `OptionsStep.tsx`, `FileStep.tsx`, `MapStep.tsx` (button label/flow only), `Client/src/import/model.ts`, `Client/src/upload/**`, new components, their Vitest tests, `tests/e2e/**` except the Review mapping assertions.

1. Lift the upload session out of the step so it survives step changes (template-level hook, e.g. `useUploadSession`), keeps one driver, and cleans up on unmount / cancel.
2. Upload bar + expandable panel per D50; Map "Save and upload" → Options.
3. Options: "Save and check" gated on upload complete; "Start a new import" (Options + Review) with confirmation; remove "Back to upload" / "Change mapping" after upload.
4. Step header rules per D50 (clickable navigation for Options/Review).
5. Resume-after-reload path (`Uploading` job): File (select same file) → upload continues in bar → Options.
6. Tests: Vitest for step-lock rules (pure function, every job state) and upload-session state; e2e: update existing specs to the new flow; new spec covering upload-in-background (small file finishes → Options, Upload done), header lock rules after Run, "Start a new import". Real-click file selection stays (`selectFile`).

Done: `npm test` / `typecheck` / `build`, e2e typecheck + admin e2e green on own instance, report flow decisions you had to make.

## 6B — Mapping card on Review (React)

Owns: new `Client/src/import/MappingSummary.tsx` (+ test), the Review card placement in `ReviewStep.tsx` (Summary view only), resx keys `contactsimporter.review.mapping*`, the e2e assertion of the card (add to the review part of one existing spec via `contactsImporterPage.ts` helper `expectMappingRow(column, field)`; coordinate: re-read before editing — 6A edits the same e2e files).

1. Data from `job.mapping`, `job.keyField`, `job.dateFormat`, `job.consentMapping` + config fields (labels). Check `ReviewStep` props for what's available; extend from `ImportTemplate` with an additive prop only.
2. Uses `Section`; table via admin `Table` or the Map step row style; readable field label + column name as secondary text.
3. Vitest for the row model (labels, key mark, date format shown only with date fields, consent rows).

Done: `npm test` / `typecheck`, report. 6A runs the full e2e at the end including 6B's assertion (parent coordinates).

## 6C — Delete imports (D51, Sean 2026-11-17)

Problem: cancel before Run leaves the expected-results report + list entry and shows Results with "Delete import files now" (feels like a second cancel); list entries can't be removed (only 90-day retention).

Decisions (D51):

- **Cancel before Run** (Options, Review, resume File, "Start a new import"; states up to `Analyzed` incl. `Uploading`/`Uploaded`/`Analyzing`/paused check): deletes the **whole import** (folder: job, status, chunks, reports) → back to the imports list (or the new import for "Start a new import"). Dialog: "Cancel this import? The uploaded data and the check results are deleted. No contacts have been changed." Event log + `AfterContactImportEvent` (`Cancelled`) still raised before deletion, as today.
- **Imports list: one "Delete" row action** (replaces "Delete files now"), confirmation dialog. Ended imports (Completed / Failed / Cancelled) → whole import removed. Unfinished, not actively driven (heartbeat stale or never processing) → cancel (event) + remove. Actively running (fresh heartbeat in execute/analyze) → refused with a message (use existing stale/active rules; no takeover).
- **Cancel during Run** unchanged: Results with partial counts + actual report; delete later from the list.
- Results step: remove "Delete import files now" (deleting is on the list); keep report downloads.
- Server: new page command (e.g. `DELETE_IMPORT`) requiring `ContactsImporter.Import`; reuses cleanup's folder deletion (sweep already deletes job folders). Idempotent (already gone → ok/notFound handled). Web farm: same CMS.IO store rules as today. Keep `DELETE_FILES` only if still used; otherwise remove it and its texts/tests.
- Docs: Usage/Importing-Contacts + Setup "File cleanup" + README limitations if affected ("deleted imports leave only event log entries and audit events").

# Slice 4 — Recipient lists, consent, bounces, attestation, audit event

Plan: [PLAN.md](PLAN.md) §2 (subscribed rule, bounces, consents), §8, §9, §12; [spike-findings.md](spike-findings.md) §1, §2, §5 (31.6.0 — re-verify on 31.10.3 where relied on). State: [STATUS.md](STATUS.md). Builds on specs 1–3. Two parallel work items, disjoint files.

## Shared contract (4A + 4B)

### Which contacts

List + consent behaviours apply to every row that resolves to contact(s): created, updated, unchanged, and existing contacts matched while "Existing contacts: don't update" is on (field update and list/consent are independent). Multi-match: the contacts chosen by the multi-match option. Skipped rows (invalid value, multi-match skip, new contacts not created, …) → nothing. In-file duplicate keys → contact handled once.

### Options (`job.options`, already in `ImportOptions`)

`recipientListId` (null = no list changes), `unsubscribed` keep|resubscribe (drop `reportOnly` if it has no distinct behaviour from keep; report it), `bouncedAddress` addAndReport|skipAndReport, `consent` none|blanket|blanketWithColumnOverride, `consentId`, `revokedConsent` keep|reAgree.

### Consent columns (mapping, wire)

Consent columns are mapped at Map step (before upload), used only if consent option = `blanketWithColumnOverride`:

- `job.json`: `"consentMapping": {"agreed": "source column", "date": "source column"}` (both optional; `date` requires nothing else). Mapping change = re-upload (same rule as field mapping).
- Chunk record gets `"c": {"a": true, "d": "2025-03-01"}` — present on every record iff `consentMapping` is set. `a` required bool; `d` optional ISO date / date-time.
- Client parse of agreed: trimmed, case-insensitive `y|yes|true|1|x` → true; anything else (incl. empty) → false. Date via job `dateFormat`; unparseable non-empty date → row rejected client-side (`invalidDate`, field `consent date`).
- Server validates `c` strictly (shape, present iff mapped, date parseable). Future date → consent not recorded for the row (`consentSkippedFutureDate`), row otherwise imported.
- "Source" column (PLAN §9): not implemented — `ConsentAgreementInfo` has no column for it (⚠️ 4A verify; report).
- `blanket`: every resolved contact gets an agreement, time = import time. `blanketWithColumnOverride`: only rows w/ `a = true`; time = `d` if given else import time.

### Aggregate keys (added to `status.aggregates`; 4B labels them)

`listAdded`, `listAlreadySubscribed`, `listKeptUnsubscribed`, `listResubscribed`, `listSkippedBounced`, `listSkippedNoEmail`, `bouncedHard`, `bouncedSoftOverLimit`, `bouncedSoftUnderLimit`, `consentRecorded`, `consentAlreadyAgreed`, `consentKeptRevoked`, `consentReAgreed`, `consentNotGiven`, `consentSkippedFutureDate`. Counts = contacts. Same keys in dry run (would-be) and run. 4A may add keys; must report them.

Row report: extra reasons / columns as needed for list + consent outcome per row (4A defines, reports).

### Attestation

Needed when `recipientListId` set, or `consent` ≠ none. `Confirm` requires it then: `{userId, utc, statementVersion}` stored in `job.attestation`, cleared on options change (exists). Statement text + version constant live server-side (resx; version bumps when text changes); `GET_JOB` returns text + version + `attestationRequired`. User needs `ContactsImporter.ConfirmConsent` to confirm such a job (server-enforced). Logged (event log) at confirm.

## Shared rules

- Don't commit / stash / reset. No `Kentico.Xperience.*` / `@kentico/*` version changes.
- One checkout, two agents: own `--artifacts-path` for every `dotnet` command (task prompt). Don't stop a user-owned DancingGoat; don't test against it.
- Shared files (service registration, test `.csproj`, `ci.yml`, `Client/src/shared/commands.ts` field/DTO types): additive, re-read right before editing.
- No contact data in logs / errors / `status.json` / event payloads.
- Never silently change consent/subscription state (PLAN principle 9): defaults keep unsubscribed + revoked.
- `dotnet format` IDE0072 fix inserts `throw new NotImplementedException()` — check.

## 4A — Server (C#)

Owns: `src/.../Admin/Processing/**`, `Jobs/**`, `Ingest/**`, `Storage/**`, `Schema/**`, `Cleanup/**`, new public `Events/**`, their unit tests, `tests/...IntegrationTests/**`. Not `UIPages/`, `Client/`, `tests/e2e`.

1. **Recipient list** (PLAN §2 subscribed rule, §9): validate list exists + is recipient list at save/analyze start. Per batch, set-based, for resolved contacts w/ email:
   - not member → `ContactGroupMemberInfo` (manual membership, same values XbyK writes for manual/list add — verify by decompile on 31.10.3) + new approved `EmailSubscriptionConfirmationInfo` dated import time → `listAdded`.
   - member + newest confirmation approved → `listAlreadySubscribed`.
   - member + newest not approved / none → `keep`: nothing (`listKeptUnsubscribed`); `resubscribe`: insert new approved confirmation (`listResubscribed`).
   - bounced per send-time rule (hard OR soft ≥ `SoftBounceLimit` via `IOptionsMonitor<BouncedEmailsGlobalOptions>`; join on email): counts `bouncedHard` / `bouncedSoftOverLimit` / `bouncedSoftUnderLimit`; `skipAndReport` → no list add (`listSkippedBounced`). Never write `EmailBounceInfo`.
   - Existence checks before insert (no unique constraint on membership); idempotent re-run; inside the batch transaction + job lock. `BulkInsert`, no per-row API, no emails sent.
   - Newly created contacts of the batch included (IDs after `BulkInsert` — find a set-based way to get them, e.g. re-lookup by key).
2. **Consent** (PLAN §9, D27): validate consent exists. `BulkInsert` `ConsentAgreementInfo` (ContactID, ConsentID, Time, ConsentHash = current consent hash, Revoked = false). Newest agreement not revoked → skip (`consentAlreadyAgreed`); newest revoked → `keep` (`consentKeptRevoked`) | `reAgree` (new row, `consentReAgreed`; backdated row older than revocation stays revoked → count as kept, document). Column override per contract.
3. **Dry run** computes all of the above w/o writes (same code path).
4. **Cache** (§11, D28, D36a): on execute `Completed` add list / subscription / consent keys (`ImportCacheInvalidator` seams); verify what `BulkInsert` of these types touches itself.
5. **Wire**: `consentMapping` in job model + config validator; chunk validator `c`; `ImportProcessingContext`.
6. **Attestation**: `Confirm` enforces contract (attestation required flag computed from options; reject without); statement version constant.
7. **`AfterContactImportEvent`** (public, PLAN §12, Simple Stats `AsyncEvent`/`IAsyncEventHandler` pattern — reference repo `D:\kentico\community\xperience-by-kentico-simple-stats`): raised when analysis finishes (`IsDryRun = true`) and when a job reaches Completed / Failed / Cancelled. Payload: `JobID`, `UserID`, `Timestamp`, `FileName`, `FileSize`, `IsDryRun`, `Status`, options summary, aggregates, `RecipientListID`, `ConsentID` + `ConsentHash`, attestation (user, time, version), `ImportSourceFieldName`. Handler exceptions logged, never fail import. Raised once per transition (web farm: best effort, document). `ProcessNext` is sync — decide how to raise async event safely; document.
8. **Event log**: user ID in start/finish/cancel/failure/attestation entries.
9. Tests: unit (planner matrix for list × unsubscribed × bounce × consent × revoked), integration on real DB (create list + consent + bounce rows via API in test setup; verify subscribed rule end state w/ the same query XbyK uses or the documented rule; idempotent re-run; dry run writes nothing; backdating; event raised w/ payload; cache keys).

Done: unit + integration green (`CONTACTSIMPORTER_INTEGRATION_SQL`, DB `…-integration`), `dotnet format` check clean on own files.

## 4B — UI + commands (C# pages, React, e2e)

Owns: `src/.../Admin/UIPages/**`, `Resources/**`, `Client/**`, `tests/e2e/**`, `tests/...Admin.Tests/UIPages/**`.

1. **Map step**: consent section — optional "Consent given" + "Consent date" source columns (separate from contact field targets; a column can't be both). Saved as `consentMapping`. `import-core` mapper emits `c` per contract (+ Vitest, parity w/ server validator cases).
2. **Options step**: recipient list select (existing recipient lists; none = no list changes) + persistent consent/double opt-in `Callout` (wording from [XbyK GDPR docs](https://docs.kentico.com/documentation/developers-and-admins/data-protection/gdpr-compliance), region-neutral, docs link; text in strings file for Sean's review, D14); unsubscribed option; bounced option; consent mode + consent select + guidance to create a dedicated consent; column override only selectable when consent column mapped; revoked option. Server: list + consent choices from DB (`LOAD_CONFIG` or own command), validation in `SAVE_OPTIONS`.
3. **Review / Results**: list + consent + bounce counts (contract keys) w/ plain labels; bounce split; expected vs actual.
4. **Run confirm dialog** (D26): when attestation required, shows counts + statement text from server; confirming = attestation → `CONFIRM_IMPORT` sends `statementVersion`; server stamps user + time. Without `ConfirmConsent` permission: dialog explains, confirm disabled; server rejects anyway.
5. Imports list / results show list + consent used.
6. Tests: Vitest (options state, mapper `c`), NUnit (command validation, permission/attestation rules), Playwright e2e: import w/ recipient list → contacts listed as subscribed in the recipient list UI (or equivalent check); unsubscribed kept vs resubscribed; consent blanket → agreement visible for contact (admin UI or check available); column override; attestation dialog + permission denied path if viewer-like user feasible. Test data setup (list, consent) via admin UI steps, management API MCP is not available to tests — find a repeatable way (e.g. SQL/API seeding script in e2e global setup against the e2e DB) and document in `tests/e2e/README.md`; CI DB is fresh per run.
7. e2e on own DancingGoat instance + scratch DB copy (recipe: `scratchpad\a3a-scripts`, `scratchpad\review-slice3.ps1`). 4A's engine lands in parallel: build UI + unit tests first, run e2e once 4A reports done (parent will message).

Done: Client `npm test` / `typecheck` / `build`; unit tests; `dotnet format` check; e2e typecheck + admin e2e green.

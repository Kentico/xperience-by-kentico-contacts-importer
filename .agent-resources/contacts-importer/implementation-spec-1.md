# Slice 1 — Foundations (server job core + client import core)

Plan: [PLAN.md](PLAN.md). No UI in this slice. Two parallel work items with disjoint files; shared wire contract below.

## Slice roadmap

| Slice | Scope |
|---|---|
| 1 | 1A server job core; 1B client import core (this spec) |
| 2 | 2A processing engine (analyze/execute, set-based writes, integration tests); 2B admin app + File/Map/Upload UI + page commands + e2e |
| 3 | Options/Review/Run/Results UI, `PROCESS_NEXT` loop, reports, recent imports, e2e |
| 4 | Recipient lists, consent, bounce report, attestation, audit event, source stamp |
| 5 | XLSX/JSON/zip parsers (after lib spike), DataGenerator, docs + Limitations (D38) |

## Shared wire contract (1A + 1B must match)

JSON = camelCase everywhere.

### Chunk file `chunks/{n}.ndjson.gz`

- `n` = 0-based chunk index, file order. Gzipped UTF-8 NDJSON, `\n` separated, no BOM.
- One line per row:

```json
{"r":12,"k":"jane@example.com","v":{"ContactEmail":"jane@example.com","ContactFirstName":"Jane"},"f":["invalidEmail"]}
```

| Prop | Type | Meaning |
|---|---|---|
| `r` | int ≥ 1 | 1-based data row # in source file (header not counted) |
| `k` | string, 1–450 chars | normalized dedupe key |
| `v` | object string→string | target contact field name → trimmed raw value. Only mapped fields. Empty string allowed (= empty value, D21). Always includes key field. |
| `f` | string[]? | flags; only `"invalidEmail"` now (email ≠ key, D34). Omitted when empty. |

- `r` strictly increasing within chunk and across chunks.
- Values stay strings; server converts per field data type.

### Key normalization (parity fixture)

- Trim (Unicode whitespace, JS `String.prototype.trim` semantics). Key field = `ContactEmail` → also lowercase invariant (`toLowerCase()` / `ToLowerInvariant()`).
- Missing/empty after trim, or > 450 chars → invalid key → row skipped client-side.
- Email valid = length ≤ 254 AND matches `^[^\s@]+@[^\s@]+\.[^\s@]+$` (after normalization).
- Fixture: `tests/fixtures/normalization.json` — both TS and C# tests must load it and pass every case. Add cases, don't fork.

### `UPLOAD_CHUNK` payload (page command lands in slice 2B; service in 1A)

```json
{"jobId":"guid","index":0,"sha256":"hex of gzipped bytes","rows":1234,"data":"base64 gzipped NDJSON"}
```

Server: verify sha256, gunzip (cap decompressed size), validate every record (schema, key length, `v` keys ⊆ mapped targets from `job.json`, value length ≤ field max from class schema, `r` ordering, `rows` count) → store. Any invalid record → reject whole chunk w/ reason + row #. Re-upload of same index w/ same sha = OK (idempotent); different sha = replace only while `Uploading`.

### `job.json`

```json
{
  "jobId":"guid","createdByUserId":53,"createdUtc":"…",
  "file":{"name":"x.csv","size":123,"fingerprint":"hex"},
  "keyField":"ContactEmail",
  "mapping":[{"source":"Email","target":"ContactEmail"}],
  "dateFormat":"iso",
  "options":{ },
  "attestation":null
}
```

`options` = PLAN §8 table (typed enums, defaults per table). Recipient list / consent option props defined now, behavior in slice 4.

### `status.json`

```json
{
  "state":"Uploading",
  "chunks":[{"index":0,"sha256":"…","rows":1234}],
  "profile":{"rowsRead":0,"rowsValid":0,"rowsRejected":0,"likelyDuplicates":0,"chunkCount":0},
  "checkpoint":{"phase":"analyze","chunkIndex":0,"rowOffset":0},
  "aggregates":{ },
  "heartbeatUtc":null,"driverToken":null,"lastInstance":null,
  "updatedUtc":"…","error":null
}
```

States + transitions: PLAN §4. `Paused` derived on read (stale heartbeat), never stored.

## 1A — Server job core (C#)

Owns: `src/Kentico.Xperience.Labs.ContactsImporter.Admin/**` except `Client/` and `UIPages/`; `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.Tests/**`.

- `ContactsImporterOptions` (`ContactsImporter` config section): `StoragePath` (default `~/App_Data/contacts-importer`), `MaxFileSizeBytes`, `ChunkSizeBytes`, `BatchSize`, retention values (PLAN §11), heartbeat stale seconds. `AddContactsImporter(Action<ContactsImporterOptions>?)`.
- Job models (`job.json`, `status.json`) + System.Text.Json serialization per contract.
- `IImportJobStore`: create / read / write job + status, write/read/list/delete chunks, delete job, list jobs. CMS.IO (`CMS.IO.File`, `Directory`, `FileStream`) only; atomic write (temp + replace). jobId validated as GUID → no path traversal.
- State machine: allowed transitions (PLAN §4), incl. reset rules (mapping change → re-upload; options change after analysis → `Uploaded`). Driver token + heartbeat + derived `Paused`.
- Contact field schema provider: mappable `OM.Contact` fields (name, caption, data type, max length) from `DataClassInfo`/`FormInfo`, behind interface (fakeable). Allow-list for mapping targets.
- Record validator + chunk ingest service per contract.
- Key/email normalization (C#) + fixture test.
- File cleanup service: PLAN §11 table (delete chunks on terminal state, retention, lazy sweep of abandoned jobs). `IDateTimeNowService`/`TimeProvider` for time.
- Unit tests (NUnit): normalization fixture, validator (accept/reject matrix), state machine, store (temp dir; CMS.IO works on local paths), cleanup rules, options defaults. No DB.
- Not in scope: page commands, UI, analyze/execute, SQL writes.

Done: `dotnet build` (warnings = errors) + `dotnet test` green; `dotnet format --no-restore` clean; lock files updated if packages changed (`--force-evaluate`).

## 1B — Client import core (TypeScript)

Owns: `src/Kentico.Xperience.Labs.ContactsImporter.Admin/Client/**` (add `src/import-core/**`; don't touch `custom-layout/`), `.github/workflows/ci.yml` (add client unit test step only).

- Unit test runner (Vitest preferred; exact pinned versions like rest of `package.json`; `npm test`). Must not break webpack build / `npm run typecheck`.
- `RecordSource` abstraction → async iterator of `{ rowNumber, values }` + header/columns; byte progress.
- Parsers: CSV/TSV (streaming over `File.stream()`; delimiter/quote detection w/ override, BOM, header row, encoding override), NDJSON (line stream), `.gz` wrapper (`DecompressionStream`). JSON array / XLSX / zip = slice 5 (leave extension point).
- Sample profiler: first ~1k rows → columns, sample rows, fill rate, email validity, mapping suggestions by name (target field list passed in).
- Mapping + normalization per contract (trim, lowercase email key, date parsing ISO + chosen format → ISO output), invalid key / invalid email rules (D34).
- Bloom filter (sized for 10M keys @ 1% ≈ 12 MB; parameterized) → likely-dupe count + suspect row #s.
- Chunker: mapped rows → NDJSON → gzip (`CompressionStream`) → flush at target size; sha256 (Web Crypto) + base64; yields `UPLOAD_CHUNK` payloads + running stats (`profile` shape). Rejected-row report (row #, reason) kept client-side.
- File fingerprint (size + hash of first/last MB).
- Pipeline function usable from Web Worker and from tests (no DOM dependency beyond streams/crypto available in Node 26). Worker wiring + upload transport (parallelism/retry) = slice 2B.
- TS types for contract (`job.json` mapping, chunk record, upload payload, profile).
- Unit tests: normalization fixture, each parser (quotes, embedded newlines, delimiters, BOM, CRLF, chunk-boundary splits), bloom FP rate sanity, chunker round-trip (gunzip → records, sha matches, order/row #), profiler, rejected rows.

Done: `npm test`, `npm run typecheck`, `npm run build` green; CI step added.

## Rules for both

- Don't commit. Don't touch files outside ownership (other agent works in parallel in same checkout). Don't change `Kentico.Xperience.*` / `@kentico/*` versions.
- Follow AGENTS.md. Existing e2e (`tests/e2e`) must keep passing — template page stays until 2B.
- Report: what was built, test counts + output tail, deviations from spec/contract, open questions.

# Slice 5 — XLSX / JSON / zip parsers, DataGenerator, docs + Limitations

Plan: [PLAN.md](PLAN.md) §5 (formats), §14 (project, docs, Limitations D38), §15 (MVP definition of done, spikes). State: [STATUS.md](STATUS.md). Builds on specs 1–4. Three parallel work items, disjoint files.

## Decisions for this slice (proposed D49, Sean to confirm — resolves PLAN §16 Q1)

- **No new npm parser dependencies.** Zip entries inflated with the platform `DecompressionStream('deflate-raw')` (all evergreen browsers + Node ≥ 18); zip directory read via `File.slice`. JSON array + XLSX sheet XML parsed by small hand-written streaming tokenizers in `import-core` (same approach as the existing hand-written CSV / NDJSON readers). If a work item finds this infeasible, stop and report before adding a dependency.
- XLSX: shared strings table (`xl/sharedStrings.xml`) loaded fully before the sheet is streamed (memory ≈ unique strings); the sheet XML is streamed. Measure memory on a 1M-row file (DataGenerator) and record the number; if memory is unacceptable, propose a size cap (PLAN §5 fallback: e.g. 200 MB + "export to CSV" message).
- `.zip` = exactly one data entry (ignore directory entries and `__MACOSX/`, `.DS_Store`); entry format by its own name; more than one data entry → `ImportError` (`zipMultipleEntries`). Methods: stored (0) + deflate (8); others → `ImportError` (`unsupportedCompression`). ZIP64 supported for size/offset fields (multi-GB). Encrypted → error. `.xlsx` inside `.zip` allowed.

## Shared rules

- Don't commit / stash / reset. No `Kentico.Xperience.*` / `@kentico/*` version changes.
- One checkout, three agents: own `--artifacts-path` for every `dotnet` command. Don't stop a user-owned DancingGoat; don't test against it or the dev DB.
- Shared files (`Kentico.Xperience.Labs.ContactsImporter.slnx`, `Directory.Packages.props`, `.github/workflows/ci.yml`, `.gitignore`): additive, re-read right before editing.
- Package versions only in `Directory.Packages.props`; restore with `--force-evaluate` after adding projects / packages; commit lock files (don't commit — leave in working tree).
- Format with `dotnet format --no-restore` on own files.
- Never put contact data in logs / errors.

## 5A — Client parsers + UI (TypeScript / React, e2e)

Owns: `src/.../Admin/Client/**`, `src/.../Admin/Resources/ContactsImporterClientResources.resx`, `tests/e2e/**`.

1. `SourceFormat` += `'json' | 'xlsx'`. `detectFormat`: `.json` → json, `.xlsx` → xlsx (`.xls` / `.xlsb` / `.ods` → `unsupportedFormat` with a clear message), `.zip` → look inside. `.gz` keeps working for csv/ndjson/json (not xlsx).
2. **JSON array reader** (streaming, `RecordSource`): top-level `[ {…}, {…} ]`; same column/flatten rules as NDJSON (dotted paths, depth cap, arrays kept as JSON text, keys first seen later appended, `dynamicColumns = true`); scalars as text (numbers keep source text, `true/false`, `null` → empty). Row number = 1-based element index. Element not an object → row with `error: 'notAnObject'`; malformed JSON → fatal `ImportError` (`invalidJson`, with element index) since stream can't resync. Per-record size cap like NDJSON. Encoding detection reuses `TextRecordSource` pieces where possible.
3. **Zip reader**: central directory → single entry → stream of decompressed bytes → existing format reader (csv / ndjson / json / xlsx). `bytesRead` progress = compressed bytes of the entry. Fingerprint/resume (PLAN §4) still uses the outer file.
4. **XLSX reader**: read `xl/workbook.xml` + rels → sheet list; option `sheet` (name; default first sheet). Load `sharedStrings.xml` (rich text runs concatenated), `styles.xml` (numFmt ids → which cell styles are dates; built-in date ids 14–22, 45–47 + custom formats containing d/m/y/h/s outside quotes/brackets). Stream sheet XML: `<row r>` / `<c r t s>` / `<v>` / `<is><t>`; types `s`, `inlineStr`, `str`, `b`, `n`, `e` (errors → empty), `d`. Column letters → index (sparse cells, gaps = empty). First row = header (empty header cell → `Column N` style name, same rule as CSV if one exists). Date-styled numbers → ISO date (`yyyy-MM-dd`, or `yyyy-MM-ddTHH:mm:ss` when time part ≠ 0; 1900 date system incl. the 1900-02-29 bug; `date1904` from workbook). Other numbers → shortest round-trip text (avoid `1e+21`-style surprises for long IDs: keep the `<v>` text as is). Row number = Excel row number − header row (gaps counted). Cap 1,048,576 rows. `SourceInfo` gets `sheet` + `sheets`.
5. **UI**: File step accepts `.json,.xlsx,.zip`; format select gets JSON + Excel; sheet select when workbook has > 1 sheet (worker protocol: return sheet names with sample); CSV-only controls hidden for other formats. New error codes localized (client resx, coverage test passes). Update `contactsimporter.*` texts that list supported formats.
6. **Tests**: Vitest per reader (fixtures generated in tests — build tiny zip/xlsx bytes in test helpers with stored + deflate entries; no binaries > few KB committed), ZIP64 header parsing, shared strings, inline strings, sparse cells, dates (1900 + 1904), JSON edge cases (BOM, nested, arrays, unicode escapes, split across stream chunks at every position for a small sample), pipeline test that an XLSX and a JSON file produce the same chunks as the equivalent CSV. Playwright e2e: upload `.xlsx`, `.json`, `.zip` fixture → map → dry run review counts (generate fixtures at test time or commit small ones).
7. Memory/throughput: once 5B reports (parent messages you), run the generator for 1M-row XLSX + JSON and measure parse time + peak heap in Node (and browser if practical); report numbers.

Done: `npm test` / `typecheck` / `build`; e2e typecheck + admin e2e green on own DancingGoat instance + scratch DB copy (STATUS "Local test recipe"); `dotnet format` check.

## 5B — DataGenerator (C# console tool)

Owns: `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator/**`, its slnx entry, packages it needs (prefer none beyond BCL).

1. Non-packable net10.0 console project (`IsPackable=false`, same build props as other test projects, nullable-as-errors, lock file). Added to slnx under `/tests/`.
2. CLI: `--rows <n>` (1M / 10M typical), `--format csv|tsv|ndjson|json|xlsx`, `--compress none|gz|zip`, `--out <path>`, `--seed <int>` (deterministic output), `--duplicates <pct>` (repeat earlier keys), `--invalid-emails <pct>`, `--empty-values <pct>`, `--custom-columns <n>` (extra text columns), `--consent-columns` (agreed y/n + date), `--date-format iso|dmy|mdy`, `--existing <path>` optional: emit a second file of keys already imported (for update runs) — or simpler: `--key-offset <n>` so two runs overlap by a known amount. Columns: email, first/last name, company, job title, city, country ID?, birthday, phone, plus custom/consent.
3. Streaming writes (constant memory for 10M rows); XLSX via `System.IO.Compression.ZipArchive` + hand-written sheet XML with **inline strings** or shared strings (option `--xlsx-shared-strings`, default on, since Excel writes shared strings — the browser reader must be tested with both) and a date style for date columns; cap at 1,048,576 rows (error above). JSON = top-level array. Zip = single entry. gz via `GZipStream`.
4. Unit tests (in the existing NUnit test project only if trivial to reference; otherwise a small `--verify` self-check or tests in the generator project is fine — decide, report): determinism with seed, row counts, duplicate/invalid percentages within tolerance, XLSX opens (read back with `ZipArchive` + XML parse), zip single entry.
5. `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator/README.md`: usage examples for perf runs (1M/10M, each format), output sizes measured.

Done: build + tests green, `dotnet format` check clean, sample outputs generated for each format at 10k rows and sizes for 1M rows recorded in README.

## 5C — Docs (README Limitations, Usage guide, Setup)

Owns: `README.md`, `docs/Usage-Guide.md` (+ new `docs/*.md` pages it links), not `docs/Contributing-Setup.md` except a DataGenerator pointer line.

Ground every statement in code (option names, defaults, permissions, page names, resx texts) — read `ContactsImporterOptions`, `UIPages/**`, `Jobs/**`, `Processing/**`, `Events/**`, resx. Where code and PLAN differ, code wins; list differences in the report. Audience: marketers (usage) + developers (setup). Simple English, no legal advice (consent text: "not legal advice").

1. **README**: replace the `---Also list any limitations…---` placeholder with a short "Limitations" section (pointer list, PLAN §14 / D38 sources) linking to the Usage guide section. Description: formats list. Quick Start: permissions + storage note for web farm/SaaS.
2. **Usage guide** (overview + linked pages if long; remove the template placeholder line):
   - Setup (developer): install, `AddContactsImporter` options (each option, default, appsettings section name), storage path + SaaS/web farm path mapping (`AddStoragePathRegistration` / storage path mapping docs link; App_Data not blob-mapped on SaaS), build targets excluding storage folder, permissions (built-in View, `ContactsImporter.Import`, `ContactsImporter.ConfirmConsent`; role setup), import source custom field (create text field in Modules → OM.Contact), custom dedupe key index (SQL example with prefix), request body / chunk size vs app limits, optional scheduled lazy sweep only if code supports it (otherwise: sweep runs when the app opens / commands run).
   - Using the importer (marketer): steps File → Map → Upload → Options → Review → Run → Results; supported formats (CSV/TSV, NDJSON, JSON array, XLSX first/chosen sheet, `.gz`, single-entry `.zip`; nested JSON → dotted names; XLSX dates); dedupe key, merge rule, empty values, multi-match, invalid email; recipient lists (confirmed add, no DOI email, unsubscribed keep/resubscribe, bounces); consents (dedicated consent, blanket / column override, backdating, revoked); attestation; reports (row report columns + codes, PII-free default); pause/resume/takeover/cancel; recent imports + file cleanup/retention; contact groups after import.
   - Audit contact imports: `AfterContactImportEvent` payload + sample `IAsyncEventHandler` handler + registration (match real type names in `Events/**`).
   - Limitations / documented choices: PLAN §13 table + §14 list (all items), plus open STATUS limitations that affect users (reference fields take IDs only; results may undercount by ≤ 1 batch on cancel; XLSX memory/size note from 5A — leave a `TODO(5A)` marker the parent fills if numbers not yet known).
   - Keep existing "Administration language" section.
3. Contributing-Setup: one line + link to DataGenerator README (5B owns the README).

Done: links valid (relative paths exist), markdown renders, every option/permission/type name grep-verified in code; report list of PLAN-vs-code differences and anything unverifiable.

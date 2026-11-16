# Contacts Importer (Labs) — Status

Single source of current state. Plan: [PLAN.md](PLAN.md). Spike evidence: [spike-findings.md](spike-findings.md).

Updated: 2026-11-16

## Phase

Plan draft (PLAN.md DRAFT v0.10). Implementation not started. Phase 0 spike partly done (PLAN.md §15).

Release version: `Kentico.Xperience.Labs.ContactsImporter.Admin` `1.0.0-prerelease-5` (`Directory.Build.props`); continues old `Kentico.Xperience.Contacts.Importer` (last `1.0.0-prerelease-4`) (D39).

MVP deliverable (D38): README "Limitations" + Usage guide limitations section populated (source list PLAN.md §14 Docs).

## Components

None implemented. Repo = Kentico Xperience repository template output (Labs, `ContactsImporter`), template placeholder code only. Target layout (D32, D33; restructure in progress 2026-11-16):

- `src/Kentico.Xperience.Labs.ContactsImporter.Admin` — single package: services + admin UI + `Client/` (template core project removed, folded in)
- `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.Tests` — C# unit (NUnit)
- `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests` — MSSQL container (to create)
- `tests/Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator` — perf data generator, non-packable (to create)
- `tests/e2e` — Playwright
- `examples/DancingGoat`, `examples/AppHost`

Old importer code removed (git history on `main`).

## Open checks (⚠️ in PLAN.md)

- §5: streaming JSON parser lib
- §5: browser streaming XLSX vs full-load (memory on 1M rows)
- §11: `AddContactsImporter(o => o.RegisterStoragePath = true)` option
- §11: multi-GB run duration on small DBs (spike)
- §13: perf targets (1M rows analyze < 15 min, execute < 60 min, SaaS L2)
- §15 phase 0 remaining spikes: TS↔C# key normalization parity, 2 GB chunked upload behind Cloudflare/SaaS, `PROCESS_NEXT` + OPENJSON update throughput
- Spike findings from 31.6.0; re-check on min version 31.10.3

## Pending decisions

PLAN.md §16:

1. Streaming JSON + XLSX browser libs (spike)
2. SaaS autoscale-in during long imports (low impact)

Resolved 2026-11-16: package split → single `.Admin` package (D32); integration test project location (D33); keyless rows always skipped, invalid-email option only when email ≠ key (D34); bounced = send-time rule (D35); cache clear only on `Completed` (D36); no `BeforeContactImportExecuteEvent` (D37); limitations docs required (D38); version `1.0.0-prerelease-5` (D39).

# Contacts Importer (Labs) — Status

Single source of current state. Plan: [PLAN.md](PLAN.md). Spike evidence: [spike-findings.md](spike-findings.md).

Updated: 2026-11-16

## Phase

Plan draft (PLAN.md DRAFT v0.8). Implementation not started. Phase 0 spike partly done (PLAN.md §15).

## Components

None implemented. Repo = Kentico Xperience repository template output (Labs, `ContactsImporter`); template placeholder code only:

- `src/Kentico.Xperience.Labs.ContactsImporter` (core)
- `src/Kentico.Xperience.Labs.ContactsImporter.Admin` (admin UI + `Client/`)
- `tests/Kentico.Xperience.Labs.ContactsImporter.Tests`, `tests/e2e`
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

See PLAN.md §16 (streaming libs, autoscale-in, core/`.Admin` package split, integration test project location).

# Agent instructions

## Repository

Xperience by Kentico Labs: Contacts Importer (Kentico Labs family, library): lets marketers import contacts from CSV, Excel, and JSON files into Xperience by Kentico, with deduplication, dry runs, and recipient list assignment.

This repository was created from the Kentico repository template, the starting point for Xperience by Kentico integrations, Kentico Labs projects, example projects, and community libraries. The template supplied the first commit: build and CI setup, agent skills, the DancingGoat sample, and the Aspire host. GitHub does not sync template changes afterwards; this content is now the repository's own.

- `src/` – `Kentico.Xperience.Labs.ContactsImporter.Admin` (service registration and the Xperience administration extension, with a React client in `Client/`), published as a single NuGet package
- `tests/` – NUnit unit tests in `Kentico.Xperience.Labs.ContactsImporter.Admin.Tests`; Playwright e2e tests in `tests/e2e`
- `examples/DancingGoat` – Xperience sample site that uses the libraries; `examples/AppHost` – Aspire host that runs it locally
- `docs/` – usage guide and contributor setup
- `.agent-resources/` – agent planning docs (see below)

Skills in `.claude/skills/`:

- `setup-local` – local environment (SQL Server, database, secrets, npm, Playwright)
- `update-xperience` – Xperience version updates
- `create-pull-request` – create or update the pull request for the current branch
- `design-conventions` – DancingGoat UI and copy

## Development

- Before a commit, format with: `dotnet format --no-restore`
- `global.json` pins the exact .NET SDK (`rollForward: disable`): the lock files record SDK-implicit packages, so a different SDK fails `dotnet restore --locked-mode`. To change the SDK, edit `global.json`, run `dotnet restore --force-evaluate`, and commit both.
- Releases: `VersionPrefix` / `VersionSuffix` in `Directory.Build.props`, then the publish pipeline; see "Releasing" in `docs/Contributing-Setup.md`.
- `examples/` is excluded from formatting by `examples/.editorconfig` (`generated_code = true`)
- The Dancing Goat connection string and management API secret can be committed to source control because they are for local development only.
- Package versions live only in `Directory.Packages.props` files; lock files are committed, so restore with `--force-evaluate` after changing versions or project references.
  - Root: every version, plus the minimum Xperience version the libraries in `src/` support.
  - `examples/Directory.Packages.props`: imports the root and overrides the Xperience versions for DancingGoat, which can run a newer Xperience to check the libraries against it.

## Xperience version and DancingGoat

- `Kentico.Xperience.*` NuGet and `@kentico/*` npm versions change only through the `update-xperience` skill, which also regenerates `examples/DancingGoat` from the matching project template.
- `examples/DancingGoat` is template-generated. When you change it beyond demonstrating this repository's library (project references, settings, startup), record the change in `.claude/skills/update-xperience/references/dancinggoat-overlay.md` so updates keep it.
- `examples/DancingGoat/AGENTS.md` has instructions for the sample app.
- Run the sample with `aspire run` (`examples/AppHost`); the `aspire` MCP server lists resources, shows logs, and restarts them. Pick the run mode per invocation:
  - Plain `aspire run` when working alone: DancingGoat goes stale after edits; `aspire resource dancinggoat rebuild` refreshes it.
  - Watch when the user is testing alongside: `$env:features__defaultWatchEnabled = 'true'; aspire run` runs `dotnet watch --non-interactive`, so edits hot-reload and rude edits restart DancingGoat. The watcher owns builds and locks output; read build errors from the resource console logs.
  - Aspire settings files override the env var, so keep `features.defaultWatchEnabled` out of `aspire.config.json` and `~/.aspire`.
- Update Aspire with `aspire update` (or the grouped dependabot PR) and keep the `Aspire.AppHost.Sdk` version in `AppHost.csproj` on the same version.

## Planning docs (`.agent-resources/<feature>/`)

Each feature gets its own folder, `.agent-resources/<feature>/` (kebab-case name). Write all agent plans, research, and implementation specs for that feature there, not elsewhere in the repo. Before working on an existing feature, read its `STATUS.md`.

- `STATUS.md` – single source for the feature's current state (components, open checks, pending decisions). Update after each slice or when an item closes.
- `PLAN.md` – the feature's research, design rules, component catalog, decisions, append-only slice log. Don't track status there.
- `implementation-spec-<n>.md` – scope of slice `<n>` of the feature.

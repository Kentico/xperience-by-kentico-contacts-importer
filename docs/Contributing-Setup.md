# Contributing Setup

## Required Software

The requirements to setup, develop, and build this project are listed below.

### .NET Runtime

The exact .NET SDK version in `global.json`

- <https://dotnet.microsoft.com/en-us/download/dotnet/10.0>
- The version is pinned (`rollForward: disable`) because the committed NuGet lock files depend on it. To move to a newer SDK, change `global.json` and run `dotnet restore --force-evaluate`, then commit both.

### Node.js Runtime

- [Node.js](https://nodejs.org/en/download) LTS or newer
- [NVM for Windows](https://github.com/coreybutler/nvm-windows) or [NVM for macOS](https://github.com/nvm-sh/nvm) to manage multiple installed versions of Node.js
- See `engines` in `src/Kentico.Xperience.Labs.ContactsImporter.Admin/Client/package.json` for specific version requirements

### Database

SQL Server 2019 or newer compatible database

- [SQL Server Linux](https://learn.microsoft.com/en-us/sql/linux/install-upgrade/setup?view=sql-server-ver15)

### Email

[Mailpit](https://mailpit.axllent.org) catches DancingGoat's emails on `localhost:1025` (SMTP), with the inbox at <http://localhost:8025>. One container serves every project on the machine:

```powershell
docker run -d --name mailpit --restart unless-stopped -p 1025:1025 -p 8025:8025 axllent/mailpit
```

## Sample Project

### Database Setup

Running the sample project requires an Xperience by Kentico database. AI agents can do the whole local setup (SQL Server, database, dependencies, verification) with the `setup-local` skill. To create the database by hand, run from the repository root:

```powershell
./scripts/Initialize-Database.ps1
```

- Server, database (`xperience-by-kentico-contacts-importer`), and SQL login come from `ConnectionStrings` in `examples/DancingGoat/appsettings.json`. The default targets a Docker SQL Server on `localhost,1433` with the `sa` login; for a locally installed SQL Server pass `-ServerName` (and `-Username ""` for Windows authentication).
- The `administrator` account password is `Pass@12345`, or the `XPERIENCE_ADMIN_PASSWORD` environment variable.
- Pass `-LicenseFile <path>` (or set `XPERIENCE_LICENSE_FILE`) to install your Xperience license. Without it, add the license when you first sign in to the administration.
- `-Recreate` drops and recreates an existing database.
- The script runs [`kentico-xperience-dbmanager`](https://docs.kentico.com/documentation/developers-and-admins/installation#create-the-project-database), then moves the connection string and `CMSHashStringSalt` it writes into DancingGoat user secrets and restores `examples/DancingGoat/appsettings.json`, so there is nothing to commit. A salt already in user secrets is reused, so one machine keeps one salt. `-SaveTo` changes this (`Salt`, `AppSettings`, `None`); see the script's help.

### Run with Aspire

[Aspire](https://aspire.dev) starts everything together: DancingGoat and the admin client dev server (Proxy mode), with a link to the Mailpit inbox. Install the [Aspire CLI](https://aspire.dev/get-started/install-cli/), then from the repository root:

```powershell
aspire run
```

The dashboard links the live site and the administration. By default DancingGoat uses the database from `scripts/Initialize-Database.ps1`. To use a SQL Server container instead (Docker required; the database is created on first run), set `Sql:UseExistingServer` to `false` in `examples/AppHost` user secrets:

```powershell
dotnet user-secrets set "Sql:UseExistingServer" false --project examples/AppHost
```

Set `XPERIENCE_LICENSE_FILE` before `aspire run` to install the license into a new container database. After its first run, set the channel domains as in step 7 of the `setup-local` skill (`.claude/skills/setup-local/SKILL.md`) so emails link to the local site.

By default DancingGoat sends emails to the machine-wide Mailpit from [Email](#email), and the dashboard shows it as unhealthy when it isn't running. To start a Mailpit container for the run instead, set `Mail:UseExistingServer` to `false`:

```powershell
dotnet user-secrets set "Mail:UseExistingServer" false --project examples/AppHost
```

The root `.mcp.json` registers the Aspire MCP server (`aspire agent mcp`), so AI agents can list resources, read logs, and restart them. `aspire agent init` can also install Aspire's agent skills; note that it adds Aspire telemetry hooks to your global agent settings unless `ASPIRE_CLI_TELEMETRY_OPTOUT=true` is set.

### End-to-end tests

Playwright tests for the live site and the administration live in `tests/e2e`; they need a licensed database. See [`tests/e2e/README.md`](../tests/e2e/README.md).

To generate large import files (CSV, TSV, NDJSON, JSON, XLSX; gzipped or zipped) for performance tests, see [`tests/Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator/README.md`](../tests/Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator/README.md).

## Development Workflow

1. Create a new branch with one of the following prefixes
   - `feat/` - for new functionality
   - `refactor/` - for restructuring of existing features
   - `fix/` - for bugfixes

1. Run `dotnet format` against the `Kentico.Xperience.Labs.ContactsImporter.slnx` solution

   > use `dotnet: format` VS Code task.

1. If you changed TypeScript, run `npm run format`, `npm run lint`, and `npm run typecheck` in its folder (`npm run lint:fix` fixes what it can). CI fails on unformatted or failing code.
   - `tests/e2e`
   - `src/Kentico.Xperience.Labs.ContactsImporter.Admin/Client` – or use the `npm: format - admin client` and `npm: lint - admin client` VS Code tasks

1. Commit changes, with a commit message preferably following the [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/#summary) convention.

1. Once ready, create a PR on GitHub. The PR will need to have all comments resolved and all tests passing before it will be merged.
   - The PR should have a helpful description of the scope of changes being contributed.
   - Include screenshots or video to reflect UX or UI updates. When the UI shown in the README changes, update its screenshots with `npm run screenshots` in `tests/e2e` (see [README screenshots](../tests/e2e/README.md#readme-screenshots))
   - Indicate if new settings need to be applied when the changes are merged - locally or in other environments
   - Use the `create-pull-request` agent skill (`.claude/skills/create-pull-request/SKILL.md`, works with
     both Copilot and Claude) to push the branch and create or update the PR, with `.github/PULL_REQUEST_TEMPLATE.md`
     filled from git diff and CI evidence instead of writing the description by hand.

1. This repository uses `lf` line endings for text files. EditorConfig and Git enforce this on all platforms; no Git line-ending override is needed.

## Releasing

The package version comes from `VersionPrefix` and `VersionSuffix` in `Directory.Build.props`. A release is that version, published from `main`.

1. Set `VersionPrefix` to the new version. For a pre-release, set `VersionSuffix` to `prerelease-1` and raise the number for each further pre-release of the same version.
1. Update the version matrix in `README.md` when the supported Xperience versions change.
1. Merge the change to `main` through a pull request.
1. Run the publish pipeline for `main` (below) and confirm the packages on nuget.org.
1. Test the pre-release in a real project. When it is good, remove the `VersionSuffix` value, merge, and run the publish pipeline again for the full release.

Publish pipeline (Azure DevOps):

1. Run the `.azuredevops/pipelines/build-and-release.yaml` pipeline for `main`. It builds, signs, packs, and after approval pushes the packages to nuget.org.
1. After a successful full release, create the GitHub release and tag `v<version>` on the released commit, with generated release notes.

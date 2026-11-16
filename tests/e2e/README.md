# End-to-end tests

Playwright (TypeScript) tests against a running DancingGoat site: the live site and the Xperience administration, including this repository's admin extensions.

## Projects

| Project | Files | Purpose |
| --- | --- | --- |
| `live-site` | `live-site/*.spec.ts` | public site behavior (widgets, sections, pages) |
| `admin-setup` | `admin/auth.setup.ts` | signs in once and saves the session to `playwright/.auth/` |
| `admin-tests` | `admin/*.spec.ts` | administration behavior, starting signed in: sign-in smoke test and the Contacts Importer (`contacts-importer.spec.ts`: File, Map, Upload; `contacts-importer-run.spec.ts`: check, run, results; `contacts-importer-process.spec.ts`: options change, cancel, reload, take over) |

Page objects (`admin/*Page.ts`) hold selectors for UI this repository does not own, so an Xperience update that changes markup is fixed in one place.

Importer specs use the `test` from `admin/contactsImporterFixtures.ts`: page objects, files from `fixtures/` copied under a unique name per run, and cleanup ("Delete files" cancels the imports a test created and deletes their uploaded data; the cancelled imports stay in the list until their retention ends). Job files are written under `examples/DancingGoat/App_Data/contacts-importer` (gitignored).

The run and process specs **create and change contacts** in the database DancingGoat uses. Every run uses its own email addresses (`e2e.{runId}.{n}@example.com`), so the specs can run again, but the contacts stay: run them against a database you can throw away (CI creates a fresh one). They check the imported contacts in the Contact management application (`admin/contactsListPage.ts`). The specs that interrupt an import rely on the small importer settings of `appsettings.CI.json` (`ASPNETCORE_ENVIRONMENT=CI`); with the default settings an import of a few thousand rows finishes in one processing call.

## Requirements

- A DancingGoat database with an Xperience **license**: `./scripts/Initialize-Database.ps1 -LicenseFile <path>` (see `docs/Contributing-Setup.md`). Without a license the live site returns 500 and the administration hides custom applications.
- In the Development environment the admin client runs in Proxy mode: start `npm start` in `src/Kentico.Xperience.Labs.ContactsImporter.Admin/Client` first, or run everything with `aspire run` (see `docs/Contributing-Setup.md`).

## Run

```powershell
cd tests/e2e
npm ci
npm run install:browsers
npm test            # all projects; starts DancingGoat unless it is already running
npm run test:admin  # admin-tests only
npm run report      # open the last HTML report
```

## CI

The `e2e` job in `.github/workflows/ci.yml` runs SQL Server and Mailpit service containers, creates the database with `scripts/Initialize-Database.ps1`, builds in Release (admin client embedded, no Proxy mode), and runs DancingGoat with `ASPNETCORE_ENVIRONMENT=CI` (`examples/DancingGoat/appsettings.CI.json`, which also sets small importer chunk, batch and processing call sizes so uploads and imports take several requests). It needs the license key in the `XPERIENCE_LICENSE_KEY` secret – as a repository secret, and as a Dependabot secret for Dependabot PRs. Without it the job's steps are skipped, as for PRs from forks. The HTML report is uploaded as the `playwright-report` artifact.

## Configuration

| Variable | Default |
| --- | --- |
| `DANCING_GOAT_BASE_URL` | `applicationUrl` of the `DancingGoat` profile in `examples/DancingGoat/Properties/launchSettings.json` |
| `ADMIN_BASE_URL` | `<base URL>/admin` |
| `XPERIENCE_ADMIN_USERNAME` / `XPERIENCE_ADMIN_PASSWORD` | `administrator` / `Pass@12345` (same default as `Initialize-Database.ps1`) |
| `E2E_VIEWER_USERNAME` / `E2E_VIEWER_PASSWORD` | not set / the administrator password. A user with administration access whose role has only the **View** permission of the Contacts Importer (Labs) application. Without it the permission spec is skipped (as in CI). |
| `E2E_WEB_SERVER_COMMAND` | `dotnet run --project examples/DancingGoat` |
| `E2E_WEB_SERVER_TIMEOUT_MS` | `180000` |

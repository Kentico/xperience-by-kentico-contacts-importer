# End-to-end tests

Playwright (TypeScript) tests against a running DancingGoat site: the live site and the Xperience administration, including this repository's admin extensions.

## Projects

| Project | Files | Purpose |
| --- | --- | --- |
| `live-site` | `live-site/*.spec.ts` | public site behavior (widgets, sections, pages) |
| `admin-setup` | `admin/auth.setup.ts` | signs in once and saves the session to `playwright/.auth/` |
| `admin-import-runs` | `admin/contacts-importer-{run,process,consent,flow}.spec.ts` | the importer specs that run imports, one test at a time (`workers: 1`): only one import run can be unfinished at a time, so parallel tests would block each other. `contacts-importer-run.spec.ts`: check, run, results; `contacts-importer-process.spec.ts`: options change, cancel, reload, take over; `contacts-importer-consent.spec.ts`: recipient list, consent, consent confirmation; `contacts-importer-flow.spec.ts`: upload in the background while Options is in view, step header lock rules, "Start a new import" from Review, cancel on Options, "Delete" in the imports list |
| `admin-tests` | other `admin/*.spec.ts` | administration behavior, starting signed in: sign-in smoke test and the Contacts Importer (`contacts-importer.spec.ts`: File, Map, upload, skipped rows and likely duplicates, resume, "Start a new import", permissions; `contacts-importer-formats.spec.ts`: XLSX (sheet choice, dates), JSON array, zip, read and checked; files built at test time by `shared/fileFormats.ts`) |

Page objects (`admin/*Page.ts`) keep selectors in one place. `contactsImporterPage.ts` covers this repository's importer UI. The others cover UI this repository does not own, so an Xperience update that changes markup is fixed in one place: `adminShellPage.ts` (administration shell), `contactsListPage.ts` (Contact management), `recipientListsPage.ts` (Recipient lists: the "Email status" of a recipient), `dataProtectionPage.ts` (Data protection: consents and their agreements).

Importer specs use the `test` from `admin/contactsImporterFixtures.ts`: page objects, files from `fixtures/` copied under a unique name per run, and cleanup ("Delete" in the imports list removes every import a test created; an unfinished one is cancelled first; imported contacts stay). Job files are written under `examples/DancingGoat/App_Data/contacts-importer` (gitignored).

The run and process specs **create and change contacts** in the database DancingGoat uses. Every run uses its own email addresses (`e2e.{runId}.{n}@example.com`), so the specs can run again, but the contacts stay: run them against a database you can throw away (CI creates a fresh one). They check the imported contacts in the Contact management application (`admin/contactsListPage.ts`). The specs that interrupt an import rely on the small importer settings of `appsettings.CI.json` (`ASPNETCORE_ENVIRONMENT=CI`); with the default settings an import of a few thousand rows finishes in one processing call.

## Recipient list and consent test data

`contacts-importer-consent.spec.ts` needs a recipient list and a consent. Nothing is seeded by a separate step:

- **Recipient list**: the spec uses the recipient list of the DancingGoat sample data ("Dancing goat recipient list", part of every database `scripts/Initialize-Database.ps1` creates, also in CI); if it was renamed, the first recipient list. No recipient list fails the spec with a hint.
- **Consent**: the spec opens the Data protection application and uses the consent "E2E imported contacts consent", creating it there when it does not exist (fresh CI database). Later runs reuse it.
- **Unsubscribed contacts and withdrawn consent** cannot be produced in the administration (they come from the unsubscribe link and the website). The spec writes them to the database the way Xperience stores them, through `shared/database.ts` → `scripts/Invoke-E2ESql.ps1` (PowerShell 7 `pwsh`, `System.Data.SqlClient`). Connection string, first that is set: `E2E_SQL_CONNECTION_STRING`, `ConnectionStrings__CMSConnectionString`, the DancingGoat user secrets (not in CI), `examples/DancingGoat/appsettings.json` (CI, written there by `Initialize-Database.ps1`). It must be the database DancingGoat uses. Without a reachable database that test is skipped.

The spec subscribes contacts to the list and records consent agreements: like the other importing specs, run it against a database you can throw away.

## Requirements

- A DancingGoat database with an Xperience **license**: `./scripts/Initialize-Database.ps1 -LicenseFile <path>` (see `docs/Contributing-Setup.md`). Without a license the live site returns 500 and the administration hides custom applications.
- In the Development environment the admin client runs in Proxy mode: start `npm start` in `src/Kentico.Xperience.Labs.ContactsImporter.Admin/Client` first, or run everything with `aspire run` (see `docs/Contributing-Setup.md`).

## Run

```powershell
cd tests/e2e
npm ci
npm run install:browsers
npm test            # all projects; starts DancingGoat unless it is already running
npm run test:admin  # admin-tests and admin-import-runs
npm run report      # open the last HTML report
```

## CI

The `e2e` job in `.github/workflows/ci.yml` runs SQL Server and Mailpit service containers, creates the database with `scripts/Initialize-Database.ps1`, builds in Release (admin client embedded, no Proxy mode), and runs DancingGoat with `ASPNETCORE_ENVIRONMENT=CI` (`examples/DancingGoat/appsettings.CI.json`, which also sets small importer chunk, batch and processing call sizes so uploads and imports take several requests, and a short `HeartbeatStaleSeconds` for take-over tests). It needs the license key in the `XPERIENCE_LICENSE_KEY` secret – as a repository secret, and as a Dependabot secret for Dependabot PRs. Without it the job's steps are skipped, as for PRs from forks. The HTML report is uploaded as the `playwright-report` artifact.

## Configuration

| Variable | Default |
| --- | --- |
| `DANCING_GOAT_BASE_URL` | `applicationUrl` of the `DancingGoat` profile in `examples/DancingGoat/Properties/launchSettings.json` |
| `ADMIN_BASE_URL` | `<base URL>/admin` |
| `XPERIENCE_ADMIN_USERNAME` / `XPERIENCE_ADMIN_PASSWORD` | `administrator` / `Pass@12345` (same default as `Initialize-Database.ps1`) |
| `E2E_VIEWER_USERNAME` / `E2E_VIEWER_PASSWORD` | not set / the administrator password. A user with administration access whose role has only the **View** permission of the Contacts Importer (Labs) application. Without it the permission spec is skipped (as in CI). |
| `E2E_SQL_CONNECTION_STRING` | not set: see "Recipient list and consent test data" |
| `E2E_WEB_SERVER_COMMAND` | `dotnet run --project examples/DancingGoat` |
| `E2E_WEB_SERVER_TIMEOUT_MS` | `180000` |

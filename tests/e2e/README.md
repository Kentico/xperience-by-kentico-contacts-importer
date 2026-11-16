# End-to-end tests

Playwright (TypeScript) tests against a running DancingGoat site: the live site and the Xperience administration, including this repository's admin extensions.

## Projects

| Project | Files | Purpose |
| --- | --- | --- |
| `live-site` | `live-site/*.spec.ts` | public site behavior (widgets, sections, pages) |
| `admin-setup` | `admin/auth.setup.ts` | signs in once and saves the session to `playwright/.auth/` |
| `admin-tests` | `admin/*.spec.ts` | administration behavior, starting signed in |

Page objects (`admin/*Page.ts`) hold selectors for UI this repository does not own, so an Xperience update that changes markup is fixed in one place.

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

The `e2e` job in `.github/workflows/ci.yml` runs SQL Server and Mailpit service containers, creates the database with `scripts/Initialize-Database.ps1`, builds in Release (admin client embedded, no Proxy mode), and runs DancingGoat with `ASPNETCORE_ENVIRONMENT=CI` (`examples/DancingGoat/appsettings.CI.json`). It needs the license key in the `XPERIENCE_LICENSE_KEY` secret – as a repository secret, and as a Dependabot secret for Dependabot PRs. Without it the job's steps are skipped, as for PRs from forks. The HTML report is uploaded as the `playwright-report` artifact.

## Configuration

| Variable | Default |
| --- | --- |
| `DANCING_GOAT_BASE_URL` | `applicationUrl` of the `DancingGoat` profile in `examples/DancingGoat/Properties/launchSettings.json` |
| `ADMIN_BASE_URL` | `<base URL>/admin` |
| `XPERIENCE_ADMIN_USERNAME` / `XPERIENCE_ADMIN_PASSWORD` | `administrator` / `Pass@12345` (same default as `Initialize-Database.ps1`) |
| `E2E_WEB_SERVER_COMMAND` | `dotnet run --project examples/DancingGoat` |
| `E2E_WEB_SERVER_TIMEOUT_MS` | `180000` |

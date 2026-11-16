import fs from "node:fs";
import path from "node:path";

// Centralized test configuration. Every value has a local default and an
// environment variable override (used by CI and the Aspire AppHost).

const dancingGoatPath = path.resolve(__dirname, "../../../examples/DancingGoat");

// Default: the DancingGoat launch profile URL, so the randomized port set by
// repository setup is picked up without duplicating it here.
function launchProfileUrl(): string {
  const launchSettings = JSON.parse(
    fs.readFileSync(path.join(dancingGoatPath, "Properties/launchSettings.json"), "utf8"),
  );
  const url: string = launchSettings.profiles.DancingGoat.applicationUrl;
  return url.split(";")[0];
}

export const appBaseUrl = (process.env.DANCING_GOAT_BASE_URL ?? launchProfileUrl()).replace(/\/$/, "");

export const adminBaseUrl = process.env.ADMIN_BASE_URL ?? `${appBaseUrl}/admin`;

// scripts/Initialize-Database.ps1 creates the "administrator" account with
// XPERIENCE_ADMIN_PASSWORD, defaulting to Pass@12345.
export const adminUsername = process.env.XPERIENCE_ADMIN_USERNAME ?? "administrator";
export const adminPassword = process.env.XPERIENCE_ADMIN_PASSWORD ?? "Pass@12345";

// Storage state written by the `admin-setup` project (admin/auth.setup.ts) and
// reused by `admin-tests`, so admin specs start signed in.
export const adminStorageStatePath = path.resolve(__dirname, "../playwright/.auth/admin.json");

export const webServerCommand =
  process.env.E2E_WEB_SERVER_COMMAND ?? `dotnet run --project "${dancingGoatPath}"`;

export const webServerTimeoutMs = Number.parseInt(
  process.env.E2E_WEB_SERVER_TIMEOUT_MS ?? "180000",
  10,
);

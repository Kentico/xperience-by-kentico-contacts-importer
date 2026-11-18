import { defineConfig, devices } from "@playwright/test";
import {
  adminStorageStatePath,
  appBaseUrl,
  webServerCommand,
  webServerTimeoutMs,
} from "./shared/config";

const isCi = !!process.env.CI;

// Specs that run imports (Run step). Only one import can be unfinished at a time (PLAN D60), so
// these run one after another in their own project; the other admin specs stay parallel.
const importRunSpecs = /[\\/]admin[\\/]contacts-importer-(run|process|consent|flow)\.spec\.ts/;

export default defineConfig({
  testDir: ".",
  testIgnore: ["node_modules/**"],
  fullyParallel: true,
  forbidOnly: isCi,
  retries: isCi ? 2 : 0,
  workers: isCi ? 1 : undefined,
  reporter: [["list"], ["html", { open: "never" }]],
  use: {
    baseURL: appBaseUrl,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },

  projects: [
    {
      name: "live-site",
      use: { ...devices["Desktop Chrome"] },
      testMatch: /[\\/]live-site[\\/].*\.spec\.ts/,
    },
    {
      name: "admin-setup",
      use: { ...devices["Desktop Chrome"] },
      testMatch: /[\\/]admin[\\/]auth\.setup\.ts/,
    },
    {
      name: "admin-tests",
      use: {
        ...devices["Desktop Chrome"],
        storageState: adminStorageStatePath,
      },
      testMatch: /[\\/]admin[\\/].*\.spec\.ts/,
      testIgnore: importRunSpecs,
      dependencies: ["admin-setup"],
    },
    {
      name: "admin-import-runs",
      use: {
        ...devices["Desktop Chrome"],
        storageState: adminStorageStatePath,
      },
      testMatch: importRunSpecs,
      workers: 1,
      dependencies: ["admin-setup"],
    },
  ],

  // Starts DancingGoat unless it is already running (locally). Readiness uses
  // /admin because the live site returns 500 until a license is installed.
  webServer: {
    command: webServerCommand,
    url: `${appBaseUrl}/admin`,
    timeout: webServerTimeoutMs,
    reuseExistingServer: !isCi,
    stdout: "pipe",
  },
});

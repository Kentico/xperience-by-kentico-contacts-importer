import { defineConfig } from "@playwright/test";
import {
  adminStorageStatePath,
  appBaseUrl,
  webServerCommand,
  webServerTimeoutMs,
} from "./shared/config";

// README screenshots (images/screenshots): `npm run screenshots`. A local tool to run before a PR that
// changes UI; not part of `npm test` or CI. Captures: screenshots/*.screenshots.ts.
export default defineConfig({
  testDir: ".",
  testIgnore: ["node_modules/**"],
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"]],
  // The Contacts Importer captures import about 400,000 contacts first
  timeout: 30 * 60_000,
  use: {
    baseURL: appBaseUrl,
    browserName: "chromium",
    // Fixed size and theme so captures match between runs and contributors
    viewport: { width: 1440, height: 900 },
    deviceScaleFactor: 1,
    colorScheme: "light",
    // Content that animates in on scroll is hidden until scrolled to; with reduced motion, sites that
    // honor it (DancingGoat does) show it at once, so full-page captures include it
    reducedMotion: "reduce",
    locale: "en-US",
    trace: "retain-on-failure",
  },

  projects: [
    {
      name: "admin-setup",
      testMatch: /[\\/]admin[\\/]auth\.setup\.ts/,
    },
    {
      name: "screenshots",
      testMatch: /[\\/]screenshots[\\/].*\.screenshots\.ts/,
      use: { storageState: adminStorageStatePath },
      dependencies: ["admin-setup"],
    },
  ],

  webServer: {
    command: webServerCommand,
    url: `${appBaseUrl}/admin`,
    timeout: webServerTimeoutMs,
    reuseExistingServer: true,
    stdout: "pipe",
  },
});

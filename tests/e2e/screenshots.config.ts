import { defineConfig } from "@playwright/test";
import { appBaseUrl } from "./shared/config";

// Optional: captures the README screenshots (images/screenshots) with `npm run screenshots`.
// Not part of `npm test`: playwright.config.ts does not match scripts/. It imports contacts, so run it
// against a running DancingGoat on a database you can throw away (see scripts/capture-readme-screenshots.ts).
export default defineConfig({
  testDir: "scripts",
  testMatch: /capture-readme-screenshots\.ts$/,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"]],
  timeout: 30 * 60_000,
  use: {
    baseURL: appBaseUrl,
    browserName: "chromium",
    viewport: { width: 1440, height: 900 },
    deviceScaleFactor: 1,
    colorScheme: "light",
    trace: "retain-on-failure",
  },
});

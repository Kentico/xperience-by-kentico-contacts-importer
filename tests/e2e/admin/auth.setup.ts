import fs from "node:fs/promises";
import path from "node:path";
import { test } from "@playwright/test";
import { adminStorageStatePath } from "../shared/config";
import { signInToAdmin } from "./auth";

// Runs once (the `admin-setup` project) before `admin-tests`: signs in and saves
// the storage state so every admin spec starts authenticated.
test("authenticate admin and persist storage state", async ({ page }) => {
  await signInToAdmin(page);

  await fs.mkdir(path.dirname(adminStorageStatePath), { recursive: true });
  await page.context().storageState({ path: adminStorageStatePath });
});

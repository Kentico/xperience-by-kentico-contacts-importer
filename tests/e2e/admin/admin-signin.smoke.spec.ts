import { expect, test } from "@playwright/test";
import { AdminShellPage } from "./adminShellPage";

test.describe("admin sign-in smoke", () => {
  test("loads the administration shell for an authenticated admin", async ({ page }) => {
    const adminShell = new AdminShellPage(page);

    await adminShell.goto();
    await adminShell.expectSignedIn();

    await expect(page.locator('a[aria-label="Content hub"], a[aria-label="Settings"]').first()).toBeVisible();
  });
});

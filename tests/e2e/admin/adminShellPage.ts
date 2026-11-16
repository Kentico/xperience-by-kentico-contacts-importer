import { expect, type Page } from "@playwright/test";
import { adminBaseUrl } from "../shared/config";

// Page object for the administration shell. Selectors for the built-in admin UI
// live here so an Xperience update that changes them is fixed in one place.
export class AdminShellPage {
  constructor(private readonly page: Page) {}

  async goto(): Promise<void> {
    await this.page.goto(adminBaseUrl);
  }

  async expectSignedIn(): Promise<void> {
    await expect(this.page).toHaveURL(/\/admin/i);
    await expect.poll(async () => this.page.url().toLowerCase().includes("sign-in")).toBeFalsy();
    await expect(this.page.locator('a[aria-label="System"]')).toBeVisible({ timeout: 30_000 });
  }

  // Opens an application category in the left navigation
  async openCategory(name: string): Promise<void> {
    await this.page.locator(`button[aria-label="${name}"]`).click();
  }
}

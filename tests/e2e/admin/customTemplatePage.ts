import { expect, type Locator, type Page } from "@playwright/test";
import { adminBaseUrl } from "../shared/config";
import { AdminShellPage } from "./adminShellPage";

// The custom UI page registered by src/Kentico.Xperience.Labs.ContactsImporter.Admin:
// UIPages/CustomTemplate/CustomTemplate.cs ([UIApplication] slug and name) and
// Client/src/custom-layout/CustomLayoutTemplate.tsx (React template).
const applicationSlug = "contacts-importer";
const applicationName = "Contacts Importer";
const categoryName = "Contacts Importer";

export const defaultLabel = "Click the button to get server time.";

export class CustomTemplatePage {
  constructor(private readonly page: Page) {}

  private get heading(): Locator {
    return this.page.getByRole("heading", { level: 1 });
  }

  private get getServerTimeButton(): Locator {
    return this.page.getByRole("button", { name: "Get server time" });
  }

  async goto(): Promise<void> {
    await this.page.goto(`${adminBaseUrl}/${applicationSlug}`);
    await expect(this.heading).toBeVisible({ timeout: 30_000 });
  }

  // Navigates the way a user would, proving the module's UICategory and
  // UIApplication render in the shell, not only that the route resolves.
  async gotoViaNavigation(): Promise<void> {
    const shell = new AdminShellPage(this.page);
    await shell.goto();
    await shell.openCategory(categoryName);

    await this.page
      .locator(`a[href$="/admin/${applicationSlug}"]`)
      .filter({ hasText: applicationName })
      .first()
      .click();

    await expect(this.page).toHaveURL(new RegExp(`/admin/${applicationSlug}$`, "i"));
    await expect(this.heading).toBeVisible({ timeout: 30_000 });
  }

  async headingText(): Promise<string> {
    return (await this.heading.innerText()).trim();
  }

  async expectDefaultLabel(): Promise<void> {
    await expect(this.heading).toHaveText(defaultLabel);
  }

  async clickGetServerTime(): Promise<void> {
    await this.getServerTimeButton.click();
  }

  // The SetLabel page command returns DateTime.Now.ToString(); its format follows
  // the server culture, so only require a time component.
  async expectServerTimeLabel(): Promise<void> {
    await expect(this.heading).not.toHaveText(defaultLabel);
    await expect(this.heading).toHaveText(/\d{1,2}:\d{2}/);
  }
}

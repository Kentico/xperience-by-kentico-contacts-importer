import { expect, type Locator, type Page } from "@playwright/test";
import { adminBaseUrl } from "../shared/config";
import type { TestContact } from "./contactsImporterFixtures";

// The contact listing of the built-in "Contact management" application (not owned by this repository).
// Importer specs use it to check that imported contacts really exist with the expected values.
const contactsPath = "contact-management/contacts";

export class ContactsListPage {
  constructor(private readonly page: Page) {}

  private get searchInput(): Locator {
    return this.page.getByTestId("search-input");
  }

  // Shown instead of the table (and its search) when the listing has nothing to show
  private get emptyNotice(): Locator {
    return this.page.getByText("There are no records to display");
  }

  private row(text: string): Locator {
    return this.page.getByTestId("table-row").filter({ hasText: text });
  }

  // Opens the listing filtered to contacts whose data contains `text`. Returns false when the
  // application has no contacts at all: the listing then has no search.
  async search(text: string): Promise<boolean> {
    await this.page.goto(`${adminBaseUrl}/${contactsPath}`);
    await expect(this.searchInput.or(this.emptyNotice).first()).toBeVisible({
      timeout: 30_000,
    });
    if (!(await this.searchInput.isVisible())) {
      return false;
    }

    await this.searchInput.fill(text);
    await this.searchInput.press("Enter");
    return true;
  }

  // The contact exists once, with the first and last name of `contact`
  async expectContact(contact: TestContact): Promise<void> {
    expect(
      await this.search(contact.email),
      "the contact listing has contacts",
    ).toBe(true);
    const row = this.row(contact.email);
    await expect(row).toHaveCount(1, { timeout: 30_000 });
    await expect(row).toContainText(contact.firstName);
    await expect(row).toContainText(contact.lastName);
  }

  async expectNoContact(email: string): Promise<void> {
    if (!(await this.search(email))) {
      return;
    }

    // The listing answered the search: it shows that nothing matches
    await expect(
      this.page.getByText("We couldn't find any matches"),
    ).toBeVisible({ timeout: 30_000 });
    await expect(this.row(email)).toHaveCount(0);
  }
}

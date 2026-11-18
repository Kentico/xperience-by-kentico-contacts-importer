import { expect, type Locator, type Page } from "@playwright/test";
import { adminBaseUrl } from "../shared/config";

// The built-in "Recipient lists" application (not owned by this repository). Importer specs use it to
// find a recipient list to import into and to check what Xperience itself says about a recipient:
// the "Email status" of the Recipients listing is Receiving, Unsubscribed or Bounced.
const listsPath = "recipient-lists/list";

// Recipient list of the DancingGoat sample data; preferred when several lists exist.
const sampleListName = "Dancing goat recipient list";

export type RecipientStatus = "Receiving" | "Unsubscribed" | "Bounced";

export interface RecipientList {
  id: number;
  name: string;
}

export class RecipientListsPage {
  constructor(private readonly page: Page) {}

  private get searchInput(): Locator {
    return this.page.getByTestId("search-input");
  }

  private get emptyNotice(): Locator {
    return this.page.getByText("There are no records to display");
  }

  private recipientRow(email: string): Locator {
    return this.page.getByTestId("table-row").filter({ hasText: email });
  }

  // A recipient list to import into: the DancingGoat sample list, otherwise the first one.
  // The DancingGoat database created by scripts/Initialize-Database.ps1 has the sample list.
  async findList(): Promise<RecipientList> {
    await this.page.goto(`${adminBaseUrl}/${listsPath}`);
    const rows = this.page.getByTestId("table-row");
    await expect(rows.first().or(this.emptyNotice).first()).toBeVisible({
      timeout: 30_000,
    });
    expect(
      await rows.count(),
      "a recipient list exists (the DancingGoat sample data has one; create one in the Recipient lists application otherwise)",
    ).toBeGreaterThan(0);

    const sample = rows.filter({ hasText: sampleListName });
    const row = (await sample.count()) > 0 ? sample.first() : rows.first();
    // The row can render before its cells have text: wait for the name, an empty one would match no select option.
    const nameCell = row.getByTestId("table-cell-ContactGroupDisplayName");
    await expect(nameCell).not.toHaveText(/^\s*$/, { timeout: 30_000 });
    const name = (await nameCell.innerText()).trim();
    // The first click after the listing renders is sometimes lost: click again until the list opens.
    await expect(async () => {
      await row.click();
      await expect(this.page).toHaveURL(new RegExp(`/${listsPath}/\\d+/`), {
        timeout: 5_000,
      });
    }).toPass({ timeout: 30_000 });
    const match = new RegExp(`/${listsPath}/(\\d+)/`).exec(this.page.url());
    if (!match) {
      throw new Error(`No recipient list in ${this.page.url()}`);
    }
    const id = Number(match[1]);

    return { id, name };
  }

  // Opens the Recipients listing of the list filtered to `email`. Returns false when the list has no recipients at all.
  private async searchRecipient(
    listId: number,
    email: string,
  ): Promise<boolean> {
    await this.page.goto(`${adminBaseUrl}/${listsPath}/${listId}/subscribers`);
    await expect(this.searchInput.or(this.emptyNotice).first()).toBeVisible({
      timeout: 30_000,
    });
    if (!(await this.searchInput.isVisible())) {
      return false;
    }

    await this.searchInput.fill(email);
    await this.searchInput.press("Enter");
    return true;
  }

  // The contact is a recipient of the list, once, with the given email status
  async expectRecipient(
    listId: number,
    email: string,
    status: RecipientStatus,
  ): Promise<void> {
    expect(
      await this.searchRecipient(listId, email),
      "the recipient list has recipients",
    ).toBe(true);
    const row = this.recipientRow(email);
    await expect(row).toHaveCount(1, { timeout: 30_000 });
    await expect(
      row.getByTestId("table-cell-EmailBounceIsHardBounce"),
    ).toHaveText(status);
  }

  async expectNoRecipient(listId: number, email: string): Promise<void> {
    if (!(await this.searchRecipient(listId, email))) {
      return;
    }

    await expect(
      this.page.getByText("We couldn't find any matches"),
    ).toBeVisible({ timeout: 30_000 });
    await expect(this.recipientRow(email)).toHaveCount(0);
  }
}

import { expect, type Locator, type Page } from "@playwright/test";
import { adminBaseUrl } from "../shared/config";

// The Consents tab of the built-in "Data protection" application (not owned by this repository).
// Importer specs create their consent here, as the Options step tells marketers to, and check the
// "Consent agreements" tab of the consent: it lists contacts whose newest agreement is not revoked.
const applicationPath = "data-protection";
const consentUrlPattern = /\/data-protection\/([^/]+)\/consents\/(\d+)(\/|$)/;

export interface Consent {
  id: number;
  name: string;
  // Admin URL of the consent, without a trailing tab segment
  url: string;
}

export class DataProtectionPage {
  constructor(private readonly page: Page) {}

  private get searchInput(): Locator {
    return this.page.getByTestId("search-input");
  }

  private get emptyNotice(): Locator {
    return this.page.getByText("There are no records to display");
  }

  private row(text: string): Locator {
    return this.page.getByTestId("table-row").filter({ hasText: text });
  }

  private consentFromUrl(name: string): Consent {
    const match = consentUrlPattern.exec(this.page.url());
    if (!match) {
      throw new Error(`No consent in ${this.page.url()}`);
    }
    return {
      id: Number(match[2]),
      name,
      url: `${adminBaseUrl}/${applicationPath}/${match[1]}/consents/${match[2]}`,
    };
  }

  // Returns the consent with this name, creating it when it does not exist yet (a fresh database has no consents).
  // The consent text stays empty: an agreement only needs the consent to exist.
  async ensureConsent(name: string): Promise<Consent> {
    // The application redirects to the Consents tab of the default language.
    await this.page.goto(`${adminBaseUrl}/${applicationPath}`);
    const createButton = this.page.getByTestId("button-create");
    await expect(createButton).toBeVisible({ timeout: 30_000 });
    await expect(this.searchInput.or(this.emptyNotice).first()).toBeVisible({
      timeout: 30_000,
    });

    if (await this.searchInput.isVisible()) {
      await this.searchInput.fill(name);
      await this.searchInput.press("Enter");
      const existing = this.page
        .getByTestId("table-cell-ConsentDisplayName")
        .filter({ hasText: new RegExp(`^${escapeRegExp(name)}$`) });
      await expect(
        existing
          .first()
          .or(this.page.getByText("We couldn't find any matches"))
          .first(),
      ).toBeVisible({ timeout: 30_000 });
      if ((await existing.count()) > 0) {
        await existing.first().click();
        await expect(this.page).toHaveURL(consentUrlPattern, {
          timeout: 30_000,
        });
        return this.consentFromUrl(name);
      }
    }

    await createButton.click();
    const nameInput = this.page.locator('input[name="ConsentDisplayName"]');
    await expect(nameInput).toBeVisible({ timeout: 30_000 });
    await nameInput.fill(name);
    await this.page.getByTestId("submit-form-button").click();
    // Saving opens the new consent
    await expect(this.page).toHaveURL(consentUrlPattern, { timeout: 30_000 });
    return this.consentFromUrl(name);
  }

  // Opens the Consent agreements tab filtered to `email`. Returns false when the consent has no agreements at all.
  private async searchAgreement(
    consent: Consent,
    email: string,
  ): Promise<boolean> {
    await this.page.goto(`${consent.url}/consent-agreements`);
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

  // The contact has an agreement with the consent that is not revoked; `time` is part of the shown agreement time
  async expectAgreement(
    consent: Consent,
    email: string,
    time?: string | RegExp,
  ): Promise<void> {
    expect(
      await this.searchAgreement(consent, email),
      "the consent has agreements",
    ).toBe(true);
    const row = this.row(email);
    await expect(row).toHaveCount(1, { timeout: 30_000 });
    if (time !== undefined) {
      await expect(
        row.getByTestId("table-cell-ConsentAgreementTime"),
      ).toContainText(time);
    }
  }

  async expectNoAgreement(consent: Consent, email: string): Promise<void> {
    if (!(await this.searchAgreement(consent, email))) {
      return;
    }

    await expect(
      this.page.getByText("We couldn't find any matches"),
    ).toBeVisible({ timeout: 30_000 });
    await expect(this.row(email)).toHaveCount(0);
  }
}

function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

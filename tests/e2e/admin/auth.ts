import { expect, type Locator, type Page } from "@playwright/test";
import { adminBaseUrl, adminPassword, adminUsername } from "../shared/config";

// The Xperience sign-in form is not owned by this repository and its markup can
// change between releases, so each field matches a short list of candidate
// selectors, most specific first.
const usernameSelectors = [
  'input[name="username"]',
  'input[name="userName"]',
  'input[type="email"]',
  '[data-testid="username"] input',
];

const passwordSelectors = [
  'input[name="password"]',
  'input[type="password"]',
  '[data-testid="password"] input',
];

const submitSelectors = [
  'button[type="submit"]',
  'button:has-text("Sign in")',
  'button:has-text("Log in")',
  '[data-testid="signInButton"]',
];

// Built-in applications present in every instance; visible only when signed in.
export const adminShellSelectors = [
  'a[aria-label="System"]',
  'a[aria-label="Content hub"]',
  'a[aria-label="Settings"]',
];

export async function signInToAdmin(page: Page): Promise<void> {
  await page.goto(adminBaseUrl);

  if (await isVisibleWithin(page, adminShellSelectors, 2_000)) {
    return;
  }

  const usernameInput = await waitForAnyVisible(page, usernameSelectors, 30_000);
  const passwordInput = await waitForAnyVisible(page, passwordSelectors, 30_000);

  await usernameInput.fill(adminUsername);
  await passwordInput.fill(adminPassword);
  await (await waitForAnyVisible(page, submitSelectors, 5_000)).click();

  await expect
    .poll(
      async () => {
        if (await isVisibleWithin(page, adminShellSelectors, 1_000)) {
          return "ready";
        }
        return new URL(page.url()).pathname.toLowerCase().includes("sign") ? "sign-in" : "loading";
      },
      { timeout: 60_000, message: "Admin shell did not render after sign-in." },
    )
    .toBe("ready");
}

async function isVisibleWithin(page: Page, selectors: string[], timeoutMs: number): Promise<boolean> {
  try {
    await waitForAnyVisible(page, selectors, timeoutMs);
    return true;
  } catch {
    return false;
  }
}

async function waitForAnyVisible(page: Page, selectors: string[], timeoutMs: number): Promise<Locator> {
  const startedAt = Date.now();

  while (Date.now() - startedAt < timeoutMs) {
    for (const selector of selectors) {
      const locator = page.locator(selector).first();
      if ((await locator.count()) > 0 && (await locator.isVisible())) {
        return locator;
      }
    }
    await page.waitForTimeout(150);
  }

  throw new Error(`No visible element for [${selectors.join(", ")}].`);
}

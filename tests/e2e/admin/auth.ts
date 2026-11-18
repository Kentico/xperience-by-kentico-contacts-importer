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

export interface SignInOptions {
  username?: string;
  password?: string;
  // Elements that show the shell rendered for this user; a user with few permissions does not
  // see the built-in applications in adminShellSelectors.
  readySelectors?: string[];
}

export async function signInToAdmin(
  page: Page,
  options: SignInOptions = {},
): Promise<void> {
  const username = options.username ?? adminUsername;
  const password = options.password ?? adminPassword;
  const readySelectors = options.readySelectors ?? adminShellSelectors;

  await page.goto(adminBaseUrl);

  if (await isVisibleWithin(page, readySelectors, 2_000)) {
    return;
  }

  const usernameInput = await waitForAnyVisible(
    page,
    usernameSelectors,
    30_000,
  );
  const passwordInput = await waitForAnyVisible(
    page,
    passwordSelectors,
    30_000,
  );

  await usernameInput.fill(username);
  await passwordInput.fill(password);
  await (await waitForAnyVisible(page, submitSelectors, 5_000)).click();

  await expect
    .poll(
      async () => {
        if (await isVisibleWithin(page, readySelectors, 1_000)) {
          return "ready";
        }
        return new URL(page.url()).pathname.toLowerCase().includes("sign")
          ? "sign-in"
          : "loading";
      },
      { timeout: 60_000, message: "Admin shell did not render after sign-in." },
    )
    .toBe("ready");
}

async function isVisibleWithin(
  page: Page,
  selectors: string[],
  timeoutMs: number,
): Promise<boolean> {
  try {
    await waitForAnyVisible(page, selectors, timeoutMs);
    return true;
  } catch {
    return false;
  }
}

async function waitForAnyVisible(
  page: Page,
  selectors: string[],
  timeoutMs: number,
): Promise<Locator> {
  const locator = selectors
    .map((selector) => page.locator(selector))
    .reduce((any, next) => any.or(next))
    .filter({ visible: true })
    .first();

  await locator.waitFor({ state: "visible", timeout: timeoutMs });
  return locator;
}

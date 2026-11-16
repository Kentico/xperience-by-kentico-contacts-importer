import { expect, test } from "@playwright/test";

// Smoke coverage for the DancingGoat live site. Add specs here for widgets,
// sections, or other live-site behavior this repository's library provides.
test.describe("live site", () => {
  test("renders the home page", async ({ page }) => {
    const response = await page.goto("/");

    expect(response?.status()).toBe(200);
    await expect(page).toHaveTitle(/Dancing Goat/);
    await expect(page.getByRole("navigation", { name: "Main" })).toBeVisible();
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
  });

  test("navigates to articles from the main navigation", async ({ page }) => {
    await page.goto("/");

    await page.getByRole("navigation", { name: "Main" }).locator('a[href="/articles"]').first().click();

    await expect(page).toHaveURL(/\/articles$/);
    await expect(page.locator('a[href^="/articles/"]').first()).toBeVisible();
  });
});

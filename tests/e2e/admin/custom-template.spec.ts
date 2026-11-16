import { expect, test } from "@playwright/test";
import { CustomTemplatePage, defaultLabel } from "./customTemplatePage";

// Covers the sample admin extension end to end: custom application + UI page +
// React template calling a server-side [PageCommand].
test.describe("custom UI page template", () => {
  test("surfaces the custom application in the shell navigation", async ({ page }) => {
    const customTemplate = new CustomTemplatePage(page);

    await customTemplate.gotoViaNavigation();
    await customTemplate.expectDefaultLabel();
  });

  test("updates the label with the server time via the page command", async ({ page }) => {
    const customTemplate = new CustomTemplatePage(page);

    await customTemplate.goto();
    await customTemplate.expectDefaultLabel();
    await customTemplate.clickGetServerTime();
    await customTemplate.expectServerTimeLabel();

    expect(await customTemplate.headingText()).not.toBe(defaultLabel);
  });
});

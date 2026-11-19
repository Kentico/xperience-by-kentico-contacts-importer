import fs from "node:fs/promises";
import path from "node:path";
import type { Locator, Page } from "@playwright/test";
import { toPalettePng } from "./png-palette";

// Saves README screenshots to images/screenshots/<name>.png. Used by screenshots/*.screenshots.ts
// (npm run screenshots), not by the tests.

export const screenshotsPath = path.resolve(
  __dirname,
  "../../../images/screenshots",
);

export interface CaptureOptions {
  // View width for this capture (default: the config's 1440), e.g. for a wide table
  width?: number;
  // Capture the page's full height, up to this many pixels (default: only the view)
  fullHeight?: number;
  // Height of the image from the top (cuts off empty space below the content)
  cropHeight?: number;
  // Capture only this element, with a small margin
  element?: Locator;
}

export async function capture(
  page: Page,
  name: string,
  options: CaptureOptions = {},
): Promise<void> {
  const viewport = page.viewportSize();
  if (!viewport) {
    throw new Error("The page has no viewport size.");
  }
  const isAdmin = new URL(page.url()).pathname
    .toLowerCase()
    .startsWith("/admin");

  const width = options.width ?? viewport.width;
  if (width !== viewport.width) {
    await page.setViewportSize({ width, height: viewport.height });
  }

  // Hover states (tooltips, highlighted rows) would end up in the image
  await page.mouse.move(width - 1, viewport.height - 1);

  // The administration scrolls an inner element, not the page, so Playwright's fullPage misses the content
  // below the view: make the view as tall as that content instead. The live site uses fullPage.
  let height = viewport.height;
  let fullPage = false;
  if (options.fullHeight) {
    const innerExtra = await scrollInnerContentToTop(page);
    if (innerExtra > 0) {
      height = Math.min(viewport.height + innerExtra, options.fullHeight);
      await page.setViewportSize({ width, height });
    } else {
      height = Math.min(
        await page.evaluate(() => document.documentElement.scrollHeight),
        options.fullHeight,
      );
      fullPage = true;
    }
  } else {
    await scrollInnerContentToTop(page);
  }
  const resized =
    width !== viewport.width || (height !== viewport.height && !fullPage);

  try {
    await waitForRender(page);
    // A resize can scroll the inner element again
    await scrollInnerContentToTop(page);
    const top = isAdmin ? await adminBannerHeight(page) : 0;

    let clip = {
      x: 0,
      y: top,
      width,
      height: Math.min(height, options.cropHeight ?? height) - top,
    };
    if (options.element) {
      await options.element.scrollIntoViewIfNeeded();
      const box = await options.element.boundingBox();
      if (!box) {
        throw new Error(`The element for screenshot "${name}" is not visible.`);
      }
      const margin = 16;
      const x = Math.max(0, box.x - margin);
      const y = Math.max(top, box.y - margin);
      clip = {
        x,
        y,
        width: Math.min(box.x + box.width + margin, width) - x,
        height: Math.min(
          box.y + box.height + margin - y,
          options.cropHeight ?? Number.POSITIVE_INFINITY,
        ),
      };
    }

    const png = await page.screenshot({
      animations: "disabled",
      caret: "hide",
      fullPage,
      clip,
    });
    // Indexed PNG: the administration's background gradient makes true-color images large
    await fs.mkdir(screenshotsPath, { recursive: true });
    await fs.writeFile(
      path.join(screenshotsPath, `${name}.png`),
      toPalettePng(png),
    );
  } finally {
    if (resized) {
      await page.setViewportSize(viewport);
    }
  }
}

// Height of the banner at the top of the administration ("You are using a non-production license
// key..."), cut off from captures; 0 without one (production license). Its CSS module class is hashed,
// so only the "banner___" prefix is matched.
async function adminBannerHeight(page: Page): Promise<number> {
  return page.evaluate(() => {
    const banner = document.querySelector('[class^="banner___"]');
    const box = banner?.getBoundingClientRect();
    return box?.top === 0 ? Math.ceil(box.height) : 0;
  });
}

// Scrolls every scrolled inner element to the top and returns how much taller the tallest one's content
// is than its view.
async function scrollInnerContentToTop(page: Page): Promise<number> {
  return page.evaluate(() => {
    let extra = 0;
    for (const element of Array.from(
      document.querySelectorAll<HTMLElement>("body *"),
    )) {
      const overflowY = getComputedStyle(element).overflowY;
      if (
        (overflowY === "auto" || overflowY === "scroll") &&
        element.scrollHeight > element.clientHeight + 1
      ) {
        element.scrollTop = 0;
        extra = Math.max(extra, element.scrollHeight - element.clientHeight);
      }
    }
    window.scrollTo(0, 0);
    return extra;
  });
}

// Fonts, images (lazy ones load now: a full-page image includes them), and two frames for layout after a
// resize. Wait for the page's own content (counts, loaded data) with expect before calling capture.
async function waitForRender(page: Page): Promise<void> {
  await page.evaluate(async () => {
    await document.fonts.ready;
    for (const image of Array.from(document.images)) {
      image.loading = "eager";
    }
    await Promise.all(
      Array.from(document.images)
        .filter((image) => !image.complete)
        .map(
          (image) =>
            new Promise((resolve) => {
              image.addEventListener("load", resolve, { once: true });
              image.addEventListener("error", resolve, { once: true });
            }),
        ),
    );
    for (let frame = 0; frame < 2; frame++) {
      await new Promise(requestAnimationFrame);
    }
  });
}

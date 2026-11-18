import { execFileSync } from "node:child_process";
import fs from "node:fs/promises";
import path from "node:path";
import { expect, test, type Locator, type Page } from "@playwright/test";
import { signInToAdmin } from "../admin/auth";
import { ImportPage, ImportsListPage } from "../admin/contactsImporterPage";
import { toPalettePng } from "./png-palette";

// Captures the README screenshots into images/screenshots. Optional and slow (a few minutes); not part of
// `npm test`. Run: `npm run screenshots` with DancingGoat already running (DANCING_GOAT_BASE_URL).
//
// It imports about 400,000 fake contacts (DataGenerator, example.com/.net/.org addresses), so use a scratch
// copy of the database and an empty importer StoragePath: the imports list should show only these imports.
// Production-like importer settings (default chunk and batch sizes; a ProcessingTimeBudgetSeconds of a few
// seconds) make the Run screenshot show progress; the small settings of appsettings.CI.json also work.
//
// Imports, in order: a first import (Completed); an import of an overlapping file with empty values and
// "Clear the value" (Map, Options, Review, Run, Results screenshots; Completed); a cancelled import; a check
// only (Analyzed); a paused import. The paused import blocks other runs until it is cancelled or deleted.

const repoRoot = path.resolve(__dirname, "../../..");
const generatorProject = path.join(
  repoRoot,
  "tests/Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator",
);
const screenshotsPath = path.join(repoRoot, "images/screenshots");

interface GeneratedFile {
  name: string;
  args: string[];
}

// Number of the first generated email (DataGenerator --key-offset). Use a range without contacts in the
// database (SCREENSHOTS_KEY_BASE), so the first import only creates contacts.
const keyBase = Number.parseInt(
  process.env.SCREENSHOTS_KEY_BASE ?? "50000000",
  10,
);
const keys = (offset: number) => ["--key-offset", String(keyBase + offset)];

const files = {
  first: {
    name: "spring-newsletter-signups.csv",
    args: ["--rows", "60000", ...keys(0), "--seed", "1"],
  },
  // Overlaps the first file by 40,000 emails; 8 % empty cells, cleared on update
  update: {
    name: "crm-export-2026-11.csv",
    args: [
      "--rows",
      "150000",
      ...keys(20_000),
      "--seed",
      "2",
      "--empty-values",
      "8",
    ],
  },
  cancelled: {
    name: "trade-show-leads.csv",
    args: ["--rows", "120000", ...keys(300_000), "--seed", "3"],
  },
  analyzed: {
    name: "webinar-attendees.xlsx",
    args: [
      "--rows",
      "8000",
      ...keys(500_000),
      "--seed",
      "4",
      "--format",
      "xlsx",
    ],
  },
  paused: {
    name: "partner-referrals.csv",
    args: ["--rows", "60000", ...keys(600_000), "--seed", "5"],
  },
} satisfies Record<string, GeneratedFile>;

function rowsOf(file: GeneratedFile): number {
  return Number(file.args[file.args.indexOf("--rows") + 1]);
}

const viewport = { width: 1440, height: 900 };
// The non-production license banner at the top of the administration is cut off
const bannerHeight = 24;

// The administration scrolls an inner element, not the page: scrolls it to the top and returns how much
// taller its content is than the view.
async function scrollContentToTop(page: Page): Promise<number> {
  return page.evaluate(() => {
    let extra = 0;
    for (const element of Array.from(
      document.querySelectorAll<HTMLElement>("*"),
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
    return extra;
  });
}

interface ShotOptions {
  // View width (default 1440)
  width?: number;
  // The view is made as tall as the content, up to this height
  fullHeight?: number;
  // Height of the image from the top of the view (cuts off empty space)
  cropHeight?: number;
  // Only this element (up to cropHeight tall), with a small margin
  element?: Locator;
}

// Saves a screenshot as an indexed PNG (images/screenshots/<name>.png)
async function shot(
  page: Page,
  name: string,
  options: ShotOptions = {},
): Promise<void> {
  const width = options.width ?? viewport.width;
  await page.mouse.move(0, 0);
  const extra = await scrollContentToTop(page);
  const height = options.fullHeight
    ? Math.min(viewport.height + extra, options.fullHeight)
    : viewport.height;
  const resized = width !== viewport.width || height !== viewport.height;
  if (resized) {
    await page.setViewportSize({ width, height });
  }
  // Let counts, icons, layout and transitions settle; no single event marks that
  // eslint-disable-next-line playwright/no-wait-for-timeout
  await page.waitForTimeout(800);
  await scrollContentToTop(page);

  let clip = {
    x: 0,
    y: bannerHeight,
    width,
    height: Math.min(height, options.cropHeight ?? height) - bannerHeight,
  };
  if (options.element) {
    const box = await options.element.boundingBox();
    if (!box) {
      throw new Error("The element to capture is not visible.");
    }
    const margin = 16;
    const y = Math.max(bannerHeight, box.y - margin);
    clip = {
      x: box.x - margin,
      y,
      width: box.width + 2 * margin,
      height: Math.min(
        box.height + 2 * margin,
        options.cropHeight ?? height,
        height - y,
      ),
    };
  }

  const png = await page.screenshot({ animations: "disabled", clip });
  await fs.writeFile(
    path.join(screenshotsPath, `${name}.png`),
    toPalettePng(png),
  );
  if (resized) {
    await page.setViewportSize(viewport);
  }
}

async function uploadToOptions(
  importPage: ImportPage,
  filePath: string,
  rows: number,
): Promise<void> {
  await importPage.selectFile(filePath);
  await importPage.continueToMapping();
  await importPage.expectKeyField("Email");
  await importPage.saveAndUpload();
  await importPage.expectUploaded(rows);
  await importPage.continueToOptions();
}

async function waitForRowsProcessed(
  importPage: ImportPage,
  minimum: number,
): Promise<void> {
  await expect
    .poll(() => importPage.rowsProcessed(), {
      timeout: 300_000,
      intervals: [250],
    })
    .toBeGreaterThanOrEqual(minimum);
}

test("capture README screenshots", async ({ page }, testInfo) => {
  const dataPath = testInfo.outputPath("data");
  await fs.mkdir(dataPath, { recursive: true });
  await fs.mkdir(screenshotsPath, { recursive: true });

  const pathOf = (file: GeneratedFile) => path.join(dataPath, file.name);
  for (const file of Object.values(files)) {
    execFileSync(
      "dotnet",
      [
        "run",
        "-c",
        "Release",
        "--project",
        generatorProject,
        "--",
        ...file.args,
        "--quiet",
        "--out",
        pathOf(file),
      ],
      { stdio: "inherit" },
    );
  }

  await signInToAdmin(page);
  const importsList = new ImportsListPage(page);
  const importPage = new ImportPage(page);

  // 1. First import: creates the contacts the second import updates
  await importsList.goto();
  await importsList.startNewImport();
  await uploadToOptions(importPage, pathOf(files.first), rowsOf(files.first));
  await importPage.saveOptions();
  await importPage.expectAnalyzed(600_000);
  await importPage.runImport();
  await importPage.expectResults("Completed", 900_000);

  // 2. Update import: Map, Options, Review, Run, Results
  await importsList.goto();
  await importsList.startNewImport();
  await importPage.selectFile(pathOf(files.update));
  await importPage.continueToMapping();
  await importPage.expectKeyField("Email");
  await shot(page, "map", { fullHeight: 1110 });

  await importPage.saveAndUpload();
  await importPage.expectUploaded(rowsOf(files.update));
  await importPage.continueToOptions();
  await importPage.chooseOption("emptyValues", "clear");
  await importPage.openUploadDetails();
  await shot(page, "options");

  await importPage.saveOptions();
  await importPage.expectAnalyzed(600_000);
  await expect(
    page
      .getByTestId("contacts-importer-review-samples")
      .locator('[data-sample-kind="cleared"]')
      .first(),
  ).toBeVisible();
  await shot(page, "review");
  await shot(page, "review-examples", {
    fullHeight: 4000,
    cropHeight: 1000,
    element: page.getByTestId("contacts-importer-review-samples"),
  });

  await importPage.runImport();
  await importPage.expectStep("run");
  await waitForRowsProcessed(
    importPage,
    Math.round(rowsOf(files.update) * 0.4),
  );
  await shot(page, "run", { cropHeight: 660 });

  await importPage.expectResults("Completed", 900_000);
  await shot(page, "results", { fullHeight: 2600 });

  // 3. Cancelled import
  await importsList.goto();
  await importsList.startNewImport();
  await uploadToOptions(
    importPage,
    pathOf(files.cancelled),
    rowsOf(files.cancelled),
  );
  await importPage.saveOptions();
  await importPage.expectAnalyzed(600_000);
  await importPage.runImport();
  await waitForRowsProcessed(
    importPage,
    Math.round(rowsOf(files.cancelled) * 0.3),
  );
  await importPage.cancelRunningImport();
  await importPage.expectResults("Cancelled");

  // 4. Check only: stays Analyzed
  await importsList.goto();
  await importsList.startNewImport();
  await uploadToOptions(
    importPage,
    pathOf(files.analyzed),
    rowsOf(files.analyzed),
  );
  await importPage.saveOptions();
  await importPage.expectAnalyzed(600_000);

  // 5. Paused import
  await importsList.goto();
  await importsList.startNewImport();
  await uploadToOptions(importPage, pathOf(files.paused), rowsOf(files.paused));
  await importPage.saveOptions();
  await importPage.expectAnalyzed(600_000);
  await importPage.runImport();
  await waitForRowsProcessed(importPage, 1);
  await page.getByTestId("contacts-importer-process-pause").click();
  await importPage.expectProcessStatus("paused");

  // Imports list with every state
  // The list shows "Paused" once the heartbeat of the paused run is older than HeartbeatStaleSeconds
  await importsList.goto();
  await expect(async () => {
    await importsList.goto();
    await expect(importsList.cell(files.paused.name, "state")).toHaveText(
      "Paused",
      { timeout: 1_000 },
    );
  }).toPass({ timeout: 300_000, intervals: [5_000] });
  // Wider view: at 1440 px the last columns of the table are cut off
  await shot(page, "imports-list", { width: 1680, cropHeight: 640 });
});

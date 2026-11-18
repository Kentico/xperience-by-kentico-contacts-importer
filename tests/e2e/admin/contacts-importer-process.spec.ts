import { adminStorageStatePath } from "../shared/config";
import { contactsCsv, expect, newRunId, test, testContacts } from "./contactsImporterFixtures";
import { ContactsListPage } from "./contactsListPage";
import { ImportPage } from "./contactsImporterPage";

// Options changes, cancel, reload and take over of the check and the import. These tests create
// contacts; every run uses its own email addresses (e2e.{runId}.{n}@example.com). The imports are
// long enough to interrupt because appsettings.CI.json makes the batches and the processing calls small.

async function uploadToOptions(importPage: ImportPage, filePath: string, rows: number): Promise<void> {
  await importPage.selectFile(filePath);
  await importPage.continueToMapping();
  await importPage.expectKeyField("Email");
  await importPage.saveAndUpload();
  await importPage.expectUploaded(rows);
  await importPage.continueToOptions();
}

test.describe("contacts importer: options, cancel, reload and take over", () => {
  test("changing the options after the check runs the check again", async ({ page, importsList, importPage, uniqueFile }) => {
    const runId = newRunId();
    const rows = testContacts(runId, 1, 12);
    const file = await uniqueFile("contacts-options.csv", contactsCsv(rows));

    await importsList.goto();
    await importsList.startNewImport();
    await uploadToOptions(importPage, file.path, 12);
    await importPage.saveOptions();
    await importPage.expectAnalyzed();
    await importPage.expectCount("review-created", 12);

    // Back to the options: new contacts are not created any more
    await importPage.backToOptions();
    await expect(page.getByText("Saving changed options checks it again")).toBeVisible();
    await importPage.chooseOption("newContacts", "skip");
    await importPage.chooseOption("emptyValues", "clear");
    await expect(page.getByText("Every empty value in a mapped column deletes the value")).toBeVisible();
    await importPage.saveOptions();
    await importPage.expectAnalyzed();
    await importPage.expectCount("review-created", 0);
    await importPage.expectCount("review-skipped", 12);
    await importPage.expectCount("expected-skippedNew", 12);
    const report = await importPage.downloadReport("analyze");
    expect(report.lines).toHaveLength(13);
    expect(report.lines[1]).toMatch(/^1,skipped,\w+,$/);

    // The options are kept when the import is opened again, and unchanged options keep the check
    await page.reload();
    await importPage.expectStep("review");
    await importPage.expectAnalyzed();
    await importPage.backToOptions();
    await importPage.saveOptions();
    await importPage.expectAnalyzed(10_000);
    await importPage.expectCount("review-skipped", 12);

    // Cancelled before the run: the import is deleted and nothing was imported
    await importPage.cancelBeforeRun();
    await expect(importsList.row(file.name)).toHaveCount(0);
    await new ContactsListPage(page).expectNoContact(rows[0].email);
  });

  test("continues a running import after a reload", async ({ page, importsList, importPage, uniqueFile }) => {
    test.setTimeout(240_000);
    const runId = newRunId();
    const total = 1500;
    const rows = testContacts(runId, 1, total);
    const file = await uniqueFile("contacts-reload.csv", contactsCsv(rows));

    await importsList.goto();
    const jobId = await importsList.startNewImport();
    await uploadToOptions(importPage, file.path, total);
    await importPage.saveOptions();
    await importPage.expectAnalyzed();
    await importPage.expectCount("review-created", total);
    await importPage.runImport();

    // Wait until the import is under way
    await importPage.expectStep("run");
    await expect.poll(() => importPage.rowsProcessed(), { timeout: 60_000 }).toBeGreaterThan(0);
    expect(await importPage.rowsProcessed()).toBeLessThan(total);

    // Reload mid-run: the browser asks before leaving; the page comes back at the Run step and continues by itself
    page.on("dialog", (dialog) => void dialog.accept().catch(() => undefined));
    await page.reload();
    await expect(page).toHaveURL(new RegExp(`${jobId}$`));
    await importPage.expectStep("run");
    await importPage.expectProcessStatus("running");
    await expect(importPage.processStatus).toHaveAttribute("data-pause-reason", "");

    await importPage.expectResults("Completed");
    await importPage.expectCount("actual-rows", total);
    await importPage.expectCount("actual-created", total);
    await importPage.expectCount("actual-updated", 0);
    const report = await importPage.downloadReport("execute");
    expect(report.lines).toHaveLength(total + 1);
    expect(new Set(report.lines).size, "no row is reported twice").toBe(total + 1);

    const contacts = new ContactsListPage(page);
    await contacts.expectContact(rows[0]);
    await contacts.expectContact(rows[total - 1]);
  });

  test("an import paused with Pause stays paused when it is opened again", async ({ page, importsList, importPage, uniqueFile }) => {
    test.setTimeout(240_000);
    const runId = newRunId();
    const total = 1500;
    const rows = testContacts(runId, 1, total);
    const file = await uniqueFile("contacts-pause.csv", contactsCsv(rows));

    await importsList.goto();
    const jobId = await importsList.startNewImport();
    await uploadToOptions(importPage, file.path, total);
    await importPage.saveOptions();
    await importPage.expectAnalyzed();
    await importPage.runImport();
    await importPage.expectStep("run");
    await expect.poll(() => importPage.rowsProcessed(), { timeout: 60_000 }).toBeGreaterThan(0);

    await importPage.pauseProcessing();
    const pausedAt = await importPage.rowsProcessed();
    expect(pausedAt).toBeLessThan(total);

    // Reload: no leave question (nothing runs), and the import waits for Continue instead of continuing by itself
    await page.reload();
    await expect(page).toHaveURL(new RegExp(`${jobId}$`));
    await importPage.expectStep("run");
    await importPage.expectProcessStatus("paused");
    await expect(importPage.processStatus).toHaveAttribute("data-pause-reason", "user");
    await expect(page.getByTestId("contacts-importer-process-continue")).toBeVisible();
    await page.waitForTimeout(3_000);
    await expect(importPage.processStatus).toHaveAttribute("data-calls", "0");
    expect(await importPage.rowsProcessed()).toBe(pausedAt);

    await importPage.continueProcessing();
    await importPage.expectResults("Completed");
    await importPage.expectCount("actual-rows", total);
    await importPage.expectCount("actual-created", total);
  });

  test("cancels a running import and keeps what was imported", async ({ page, importsList, importPage, uniqueFile }) => {
    test.setTimeout(240_000);
    const runId = newRunId();
    const total = 3000;
    const rows = testContacts(runId, 1, total);
    const file = await uniqueFile("contacts-cancel.csv", contactsCsv(rows));

    await importsList.goto();
    await importsList.startNewImport();
    await uploadToOptions(importPage, file.path, total);
    await importPage.saveOptions();
    await importPage.expectAnalyzed();
    await importPage.runImport();
    await importPage.expectStep("run");
    await expect.poll(() => importPage.rowsProcessed(), { timeout: 60_000 }).toBeGreaterThan(0);

    await importPage.cancelRunningImport();
    await importPage.expectResults("Cancelled");
    await expect(importPage.resultsStatus).toHaveAttribute("data-import-started", "true");
    await expect(page.getByText("Cached contact data may be outdated")).toBeVisible();

    // Part of the file was imported; the counts say how much, and those contacts exist
    const imported = await importPage.readCount("actual-rows");
    expect(imported).toBeGreaterThan(0);
    expect(imported).toBeLessThan(total);
    await importPage.expectCount("actual-created", imported);
    await importPage.expectCount("expected-created", total);
    const report = await importPage.downloadReport("execute");
    // The report can hold one more batch than the counts: see below.
    expect(report.lines.length).toBeGreaterThanOrEqual(imported + 1);
    expect(report.lines.length).toBeLessThanOrEqual(imported + 1 + 20);

    const contacts = new ContactsListPage(page);
    await contacts.expectContact(rows[0]);
    await contacts.expectContact(rows[imported - 1]);
    // The batch that was being written when the import was cancelled is written but not counted
    // (appsettings.CI.json: 20 rows per batch); nothing after it is imported.
    await contacts.expectNoContact(rows[imported + 20].email);
    await contacts.expectNoContact(rows[total - 1].email);

    await importsList.goto();
    await expect(importsList.cell(file.name, "state")).toHaveText("Cancelled");
    await expect(importsList.cell(file.name, "contactsCreated")).toHaveText(imported.toLocaleString("en-US"));
  });

  test("another browser sees a running import and takes it over when it is left", async ({ page, browser, importsList, importPage, uniqueFile }) => {
    test.setTimeout(300_000);
    const runId = newRunId();
    const total = 3000;
    const rows = testContacts(runId, 1, total);
    const file = await uniqueFile("contacts-takeover.csv", contactsCsv(rows));

    await importsList.goto();
    const jobId = await importsList.startNewImport();
    await uploadToOptions(importPage, file.path, total);
    await importPage.saveOptions();
    await importPage.expectAnalyzed();
    await importPage.runImport();
    await importPage.expectStep("run");
    await expect.poll(() => importPage.rowsProcessed(), { timeout: 60_000 }).toBeGreaterThan(0);

    // A second browser (own session storage, so not the same driver) opens the import while it runs
    const otherContext = await browser.newContext({ storageState: adminStorageStatePath });
    try {
      const otherPage = await otherContext.newPage();
      const other = new ImportPage(otherPage);
      await other.goto(jobId);
      await other.expectStep("run");
      await other.expectProcessStatus("paused");
      await expect(other.processStatus).toHaveAttribute("data-pause-reason", "activeElsewhere");
      await expect(otherPage.getByText("is being processed in another browser tab or by another user")).toBeVisible();

      // The first browser loses its connection (its processing calls no longer arrive): the import pauses
      await page.route("**/admin/api/cmd", async (route) => {
        if ((route.request().postData() ?? "").includes("PROCESS_NEXT")) {
          await route.abort("connectionfailed");
        } else {
          await route.continue();
        }
      });
      await expect(page.getByText(/Connection problem: trying again/)).toBeVisible({ timeout: 30_000 });

      // One "Continue here": while the first browser still counts as active this browser waits (with a notice) and asks
      // again; it takes the import over once the first browser's last sign of life is old enough. (With the CI heartbeat
      // of 10 s the first browser may already count as gone here, so the notice is not asserted; Vitest covers it.)
      await other.continueProcessing();

      await other.expectResults("Completed", 240_000);
      await other.expectCount("actual-rows", total);
      await other.expectCount("actual-created", total);
      await new ContactsListPage(otherPage).expectContact(rows[total - 1]);
    } finally {
      await otherContext.close();
    }
  });
});

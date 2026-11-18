import { contactsCsv, expect, generateContactsCsv, newRunId, test, testContacts } from "./contactsImporterFixtures";

// Step flow (D50): the upload runs in the background while Options is in view, File/Map/Upload lock
// once it has started, the step header opens only reachable steps, "Start a new import" replaces going
// back. The run test creates contacts; every run uses its own email addresses (e2e.{runId}.{n}@example.com).

test.describe("contacts importer: step flow", () => {
  test("uploads in the background while the options are chosen", async ({ page, importsList, importPage, uniqueFile }) => {
    test.setTimeout(120_000);
    const runId = newRunId();
    const rows = 3000;
    // Random notes compress badly, so the file is uploaded in several chunks (small chunks in appsettings.CI.json)
    const file = await uniqueFile("contacts-background.csv", generateContactsCsv(rows, `bg${runId}`));

    // Hold the chunk uploads after the first one until the checks below are done, so the upload is still running
    // while Options shows (the bar appears only after a short delay; a few small chunks upload in about a second)
    let chunks = 0;
    let releaseChunks = (): void => undefined;
    const chunksReleased = new Promise<void>((resolve) => {
      releaseChunks = resolve;
    });
    await page.route("**/admin/api/cmd", async (route) => {
      if ((route.request().postData() ?? "").includes("UPLOAD_CHUNK") && ++chunks > 1) {
        await chunksReleased;
      }
      await route.continue().catch(() => undefined);
    });

    await importsList.goto();
    await importsList.startNewImport();
    await importPage.selectFile(file.path);
    await importPage.continueToMapping();
    await importPage.setTarget(2, "Notes");
    await importPage.expectStepState("file", "done");
    await importPage.saveAndUpload();

    // Options while uploading: the bar shows the progress, checking waits for the upload
    await importPage.expectUploadPhase("running");
    await expect(importPage.uploadBar).toHaveAttribute("data-bar-mode", "progress");
    await expect(importPage.uploadBar).toContainText("Uploading");
    await expect(page.getByText("Waiting for the upload to finish")).toBeVisible();
    await expect(page.getByTestId("contacts-importer-options-next")).toBeDisabled();
    // A new import can be started only once this upload is complete; cancelling is possible at any time
    await expect(importPage.startNewButton).toHaveCount(0);
    await expect(page.getByTestId("contacts-importer-run-cancel")).toBeVisible();
    for (const step of ["file", "map"] as const) {
      await importPage.expectStepState(step, "locked");
      await expect(importPage.stepButton(step)).toHaveCount(0);
    }
    await importPage.expectStepState("upload", "upcoming");
    await importPage.expectStepState("review", "upcoming");

    // An option chosen during the upload is kept when the upload completes
    await importPage.chooseOption("newContacts", "skip");
    expect(chunks, "the file is uploaded in more than one chunk").toBeGreaterThan(1);
    releaseChunks();
    await importPage.expectUploaded(rows);
    await page.unrouteAll({ behavior: "ignoreErrors" });
    await importPage.expectStepState("upload", "locked");
    await expect(importPage.uploadBar).toHaveAttribute("data-bar-mode", "summary");
    await expect(page.getByText("Waiting for the upload to finish")).toHaveCount(0);
    await expect(page.locator('input[type="radio"][name="contacts-importer-newContacts"][value="skip"]')).toBeChecked();

    await importPage.saveOptions();
    await importPage.expectAnalyzed();
    await importPage.expectCount("review-created", 0);
    await importPage.expectCount("expected-skippedNew", rows);
  });

  test("a small file goes to Options without showing the upload bar", async ({ importsList, importPage, uniqueFile }) => {
    const runId = newRunId();
    const file = await uniqueFile("contacts-quick.csv", contactsCsv(testContacts(runId, 1, 5)));

    await importsList.goto();
    await importsList.startNewImport();
    await importPage.selectFile(file.path);
    await importPage.continueToMapping();
    await importPage.saveAndUpload();
    await importPage.expectUploaded(5);
    // The bar never showed progress: only the summary of the finished upload is there
    await expect(importPage.uploadBar).toHaveAttribute("data-bar-mode", "summary");
    await importPage.expectStepState("upload", "locked");
    await importPage.continueToOptions();
  });

  test("the step header opens Options and Review until the run and only Results after it", async ({ page, importsList, importPage, uniqueFile }) => {
    const runId = newRunId();
    const file = await uniqueFile("contacts-header.csv", contactsCsv(testContacts(runId, 1, 6)));

    await importsList.goto();
    await importsList.startNewImport();
    await importPage.selectFile(file.path);
    await importPage.continueToMapping();
    await importPage.saveAndUpload();
    await importPage.expectUploaded(6);
    await importPage.continueToOptions();
    await importPage.saveOptions();
    await importPage.expectAnalyzed();

    // Review ↔ Options through the header
    await importPage.expectStepState("options", "done");
    await importPage.stepButton("options").click();
    await importPage.expectStep("options");
    await importPage.expectStepState("review", "done");
    await importPage.stepButton("review").click();
    await importPage.expectStep("review");
    await importPage.expectAnalyzed();

    // After the run every earlier step is locked; Results is the only step left
    await importPage.runImport();
    await importPage.expectResults("Completed");
    for (const step of ["file", "map", "upload", "options", "review", "run"] as const) {
      await importPage.expectStepState(step, "locked");
    }
    await importPage.expectStepState("results", "current");
    await expect(page.getByTestId("contacts-importer-steps").locator("button")).toHaveCount(0);
  });

  test("starts a new import from Review, keeping this one", async ({ importsList, importPage, uniqueFile }) => {
    const runId = newRunId();
    const file = await uniqueFile("contacts-start-new.csv", contactsCsv(testContacts(runId, 1, 4)));

    await importsList.goto();
    await importsList.startNewImport();
    await importPage.selectFile(file.path);
    await importPage.continueToMapping();
    await importPage.saveAndUpload();
    await importPage.expectUploaded(4);
    await importPage.continueToOptions();
    await importPage.saveOptions();
    await importPage.expectAnalyzed();

    await importPage.startNewImport();
    await importPage.expectStepState("map", "upcoming");

    await importsList.goto();
    await expect(importsList.row(file.name)).toHaveCount(1);
    await importsList.deleteImport(file.name);
  });

  test("cancelling on Options deletes the import and returns to the list", async ({ importsList, importPage, uniqueFile }) => {
    const runId = newRunId();
    const file = await uniqueFile("contacts-cancel-options.csv", contactsCsv(testContacts(runId, 1, 4)));

    await importsList.goto();
    await importsList.startNewImport();
    await importPage.selectFile(file.path);
    await importPage.continueToMapping();
    await importPage.saveAndUpload();
    await importPage.expectUploaded(4);

    await importPage.cancelBeforeRun();
    await expect(importsList.row(file.name)).toHaveCount(0);
  });

  test("deletes an unfinished and a finished import from the list", async ({ page, importsList, importPage, uniqueFile }) => {
    const runId = newRunId();
    const unfinished = await uniqueFile("contacts-delete-unfinished.csv", contactsCsv(testContacts(runId, 1, 3)));
    const finished = await uniqueFile("contacts-delete-finished.csv", contactsCsv(testContacts(runId, 4, 6)));

    // An uploaded import that waits for its options
    await importsList.goto();
    await importsList.startNewImport();
    await importPage.selectFile(unfinished.path);
    await importPage.continueToMapping();
    await importPage.saveAndUpload();
    await importPage.expectUploaded(3);

    // A finished import: Results offers the reports and Delete; this test deletes it from the list
    await importsList.goto();
    await importsList.startNewImport();
    await importPage.selectFile(finished.path);
    await importPage.continueToMapping();
    await importPage.saveAndUpload();
    await importPage.expectUploaded(3);
    await importPage.saveOptions();
    await importPage.expectAnalyzed();
    await importPage.runImport();
    await importPage.expectResults("Completed");
    await expect(page.getByTestId("contacts-importer-results-delete")).toBeVisible();

    await importsList.goto();
    await expect(importsList.cell(unfinished.name, "state")).toHaveText("Uploaded");
    await expect(importsList.cell(finished.name, "state")).toHaveText("Completed");

    // Closing the dialog keeps the import
    await importsList.openDeleteDialog(unfinished.name);
    await page.getByRole("button", { name: "Cancel", exact: true }).last().click();
    await expect(importsList.row(unfinished.name)).toHaveCount(1);

    await importsList.deleteImport(unfinished.name);
    await importsList.deleteImport(finished.name);
    await page.reload();
    await importsList.expectLoaded();
    await expect(importsList.row(unfinished.name)).toHaveCount(0);
    await expect(importsList.row(finished.name)).toHaveCount(0);
  });
});

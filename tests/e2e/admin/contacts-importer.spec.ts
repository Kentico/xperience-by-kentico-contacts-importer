import fs from "node:fs/promises";
import { viewerPassword, viewerUsername } from "../shared/config";
import { signInToAdmin } from "./auth";
import { expect, generateContactsCsv, test } from "./contactsImporterFixtures";
import { ImportPage, ImportsListPage, categoryName } from "./contactsImporterPage";

// fixtures/contacts-small.csv: 200 valid rows, then a row without email (201), a row with an invalid
// email (202), a row with an unreadable date (203) and a row that repeats the first email (204).
const small = { rowsRead: 204, rowsUploaded: 201, rowsSkipped: 3, likelyDuplicates: 1 };

test.describe("contacts importer", () => {
  test("shows the application under Digital marketing", async ({ importsList }) => {
    await importsList.gotoViaNavigation();

    await expect(importsList.headline).toBeVisible();
    await expect(importsList.newImportButton).toBeVisible();
  });

  test("uploads a CSV file end to end", async ({ importsList, importPage, uniqueFixture }) => {
    const file = await uniqueFixture("contacts-small.csv");

    await importsList.goto();
    await importsList.startNewImport();
    await importPage.expectStep("file");

    // File: the sample is read in the browser
    await importPage.selectFile(file.path);
    await expect(importPage.fileSummary).toContainText(file.name);
    await expect(importPage.fileSummary).toContainText("204 rows");
    await expect(importPage.fileSummary).toContainText("6 columns");
    await expect(importPage.sampleTable).toContainText("e2e.contact1@example.com");

    // Map: columns are suggested by name; a column without a match is not imported
    await importPage.continueToMapping();
    await importPage.expectTarget(0, "Email");
    await importPage.expectTarget(1, "First name");
    await importPage.expectTarget(2, "Last name");
    await importPage.expectTarget(3, "Company name");
    await importPage.expectTarget(4, "Birthday");
    await importPage.expectTarget(5, "Do not import");
    await importPage.expectKeyField("Email");
    await importPage.setTarget(5, "Notes");

    // Upload: runs in the background while Options is in view; the job ends Uploaded with the expected row counts
    await importPage.saveAndUpload();
    await importPage.expectUploaded(small.rowsUploaded);
    await importPage.expectCount("rows-read", small.rowsRead);
    await importPage.expectCount("rows-uploaded", small.rowsUploaded);
    await importPage.expectCount("rows-skipped", small.rowsSkipped);
    await importPage.expectCount("likely-duplicates", small.likelyDuplicates);
    await expect(importPage.uploadBar).toHaveAttribute("data-bar-mode", "summary");
    await expect(importPage.uploadBar).toContainText("Rows to import: 201");

    // The list shows the import
    await importsList.goto();
    await expect(importsList.cell(file.name, "state")).toHaveText("Uploaded");
    await expect(importsList.cell(file.name, "rows")).toHaveText(String(small.rowsUploaded));
    await expect(importsList.createdBy(file.name)).not.toBeEmpty();
  });

  test("shows skipped rows and likely duplicates with client-side downloads", async ({ page, importsList, importPage, uniqueFixture }) => {
    const file = await uniqueFixture("contacts-small.csv");

    await importsList.goto();
    await importsList.startNewImport();
    await importPage.selectFile(file.path);
    await importPage.continueToMapping();
    await importPage.saveAndUpload();
    await importPage.expectUploaded(small.rowsUploaded);

    // The client-side reports stay in the upload details after the upload is complete
    await importPage.openUploadDetails();
    await expect(page.getByText("Skipped rows: 3")).toBeVisible();
    await expect(page.getByText("No value in the match field: 1")).toBeVisible();
    await expect(page.getByText("Email address is not valid: 1")).toBeVisible();
    await expect(page.getByText("A date could not be read: 1")).toBeVisible();

    const skipped = await importPage.downloadSkippedRows();
    expect(skipped.suggestedFilename()).toMatch(/-skipped-rows\.csv$/);
    const skippedCsv = (await fs.readFile(await skipped.path(), "utf8")).replace(/^﻿/, "").trim().split(/\r\n/);
    expect(skippedCsv).toEqual([
      "row,reason,field,detail",
      "201,No value in the match field,,",
      "202,Email address is not valid,,",
      "203,A date could not be read,ContactBirthday,",
    ]);

    await expect(page.getByText("Likely duplicate rows: about 1")).toBeVisible();
    const duplicates = await importPage.downloadLikelyDuplicates();
    const duplicatesCsv = (await fs.readFile(await duplicates.path(), "utf8")).replace(/^﻿/, "").trim().split(/\r\n/);
    expect(duplicatesCsv).toEqual(["row", "204"]);
  });

  test("continues an interrupted upload after a reload", async ({ page, importsList, importPage, uniqueFile, uniqueFixture }) => {
    const rows = 6000;
    const file = await uniqueFile("contacts-resume.csv", generateContactsCsv(rows, "resume"));
    const otherFile = await uniqueFixture("contacts-other.csv");

    // Let two chunk uploads through, then cut the connection for the rest. How many chunks the file
    // has depends on the server's chunk size (appsettings.CI.json makes it small).
    let passed = 0;
    let blocked = 0;
    await page.route("**/admin/api/cmd", async (route) => {
      const isChunk = (route.request().postData() ?? "").includes("UPLOAD_CHUNK");
      if (isChunk && passed >= 2) {
        blocked++;
        await route.abort("connectionfailed");
      } else {
        if (isChunk) {
          passed++;
        }
        await route.continue();
      }
    });

    await importsList.goto();
    const jobId = await importsList.startNewImport();
    await importPage.selectFile(file.path);
    await importPage.continueToMapping();
    await importPage.setTarget(2, "Notes");
    await importPage.saveAndUpload();

    // The failed request is retried; the upload stays unfinished and its details open by themselves
    await expect.poll(() => blocked, { timeout: 30_000 }).toBeGreaterThan(0);
    await expect(page.getByText(/Connection problem/)).toBeVisible();
    await expect(importPage.uploadDetails).toBeVisible();
    await importPage.expectUploadPhase("running");
    await expect(page.getByText("Waiting for the upload to finish")).toBeVisible();
    await expect(page.getByTestId("contacts-importer-options-next")).toBeDisabled();

    // Reload mid-upload: the browser asks before leaving, the page comes back at the File step
    page.on("dialog", (dialog) => void dialog.accept().catch(() => undefined));
    await page.unrouteAll({ behavior: "ignoreErrors" });
    await page.reload();
    await expect(page).toHaveURL(new RegExp(`${jobId}$`));
    await importPage.expectStep("file");
    await expect(page.getByText("Select the file again to continue")).toBeVisible();
    await importPage.expectStepState("map", "locked");
    await importPage.expectStepState("options", "upcoming");

    // Another file is recognized by its fingerprint and cannot continue the upload; the mapping is fixed
    await importPage.selectFile(otherFile.path);
    await expect(page.getByText("This is a different file")).toBeVisible();
    await expect(importPage.resumeUploadButton).toHaveCount(0);
    await expect(page.getByTestId("contacts-importer-file-next")).toHaveCount(0);

    // The same file continues with the stored mapping, without the Map step: the upload goes on in the bar
    await importPage.selectFile(file.path);
    await importPage.resumeUploadButton.click();
    await importPage.expectStep("options");
    await importPage.expectUploaded(rows);
    await importPage.expectCount("rows-read", rows);
    await importPage.expectCount("rows-uploaded", rows);
    test.info().annotations.push({ type: "chunk uploads before the interruption", description: String(passed) });
    if (passed > 0) {
      await importPage.openUploadDetails();
      await expect(page.getByText(/(parts were|part was) already on the server/)).toBeVisible();
    }

    await importsList.goto();
    await expect(importsList.cell(file.name, "state")).toHaveText("Uploaded");
    await expect(importsList.cell(file.name, "rows")).toHaveText(rows.toLocaleString("en-US"));
  });

  test("shows an uploaded import again after a reload and can start a new import instead", async ({ page, importsList, importPage, uniqueFixture }) => {
    const file = await uniqueFixture("contacts-small.csv");

    await importsList.goto();
    const jobId = await importsList.startNewImport();
    await importPage.selectFile(file.path);
    await importPage.continueToMapping();
    await importPage.saveAndUpload();
    await importPage.expectUploaded(small.rowsUploaded);

    await page.reload();
    await importPage.expectStep("options");
    await importPage.expectUploaded(small.rowsUploaded);
    await importPage.expectCount("rows-skipped", small.rowsSkipped);
    await importPage.expectStepState("upload", "locked");

    // "Start a new import" opens a new one and keeps this one
    await importPage.startNewImport();
    expect(page.url()).not.toContain(jobId);
    await importsList.goto();
    await expect(importsList.row(file.name)).toHaveCount(1);
    await importsList.deleteImport(file.name);
    await expect(importsList.row(file.name)).toHaveCount(0);
  });
});

// Needs a user who has only the View permission of the application (README "Configuration").
test.describe("contacts importer permissions", () => {
  test.skip(!viewerUsername, "E2E_VIEWER_USERNAME is not set: no user without the Import permission to test with.");
  test.use({ storageState: { cookies: [], origins: [] } });

  test("a user without the Import permission can see imports but not change them", async ({ page }) => {
    await signInToAdmin(page, {
      username: viewerUsername!,
      password: viewerPassword,
      readySelectors: [`button[aria-label="${categoryName}"]`],
    });

    const importsList = new ImportsListPage(page);
    await importsList.goto();
    await expect(importsList.noPermissionNotice).toBeVisible();
    await expect(importsList.newImportButton).toHaveCount(0);

    // The import page explains instead of offering the steps
    const importPage = new ImportPage(page);
    const jobId = crypto.randomUUID();
    await importPage.goto(jobId);
    await expect(importPage.noPermissionNotice).toBeVisible({ timeout: 30_000 });

    // The server refuses the commands whatever the client shows
    const statuses = await page.evaluate(async (path) => {
      const html = await (await fetch("/admin")).text();
      const token = /name="xsrf-token" content="([^"]+)"/.exec(html)?.[1] ?? "";
      const send = async (commandPath: string, command: string): Promise<number> => {
        const form = new FormData();
        form.append("path", commandPath);
        form.append("command", command);
        form.append("data", "{}");
        const response = await fetch("/admin/api/cmd", { method: "POST", body: form, headers: { RequestVerificationToken: token } });
        return response.status;
      };
      return {
        list: await send("contacts-importer/imports", "LIST_IMPORTS"),
        deleteImport: await send("contacts-importer/imports", "DELETE_IMPORT"),
        loadConfig: await send(path, "LOAD_CONFIG"),
        getJob: await send(path, "GET_JOB"),
        saveConfig: await send(path, "SAVE_CONFIG"),
        uploadChunk: await send(path, "UPLOAD_CHUNK"),
        truncate: await send(path, "TRUNCATE_CHUNKS"),
        complete: await send(path, "COMPLETE_UPLOAD"),
        saveOptions: await send(path, "SAVE_OPTIONS"),
        processNext: await send(path, "PROCESS_NEXT"),
        confirm: await send(path, "CONFIRM_IMPORT"),
        getReport: await send(path, "GET_REPORT"),
        cancel: await send(path, "CANCEL_IMPORT"),
        cancelAndDelete: await send(path, "DELETE_IMPORT"),
      };
    }, `contacts-importer/imports/${jobId}`);

    expect(statuses).toEqual({
      list: 200,
      deleteImport: 403,
      loadConfig: 403,
      getJob: 403,
      saveConfig: 403,
      uploadChunk: 403,
      truncate: 403,
      complete: 403,
      saveOptions: 403,
      processNext: 403,
      confirm: 403,
      getReport: 403,
      cancel: 403,
      cancelAndDelete: 403,
    });
  });
});

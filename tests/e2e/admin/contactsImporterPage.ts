import fs from "node:fs/promises";
import { gunzipSync } from "node:zlib";
import { expect, type Download, type Locator, type Page } from "@playwright/test";
import { adminBaseUrl } from "../shared/config";
import { AdminShellPage } from "./adminShellPage";

// The "Contacts Importer (Labs)" application registered by src/Kentico.Xperience.Labs.ContactsImporter.Admin:
// UIPages/ContactsImporterApplicationPage.cs ([UIApplication] slug, name, category), ImportsListPage.cs and
// ImportPage.cs. React templates live in Client/src; their test IDs are in Client/src/shared/ui.tsx.
export const applicationSlug = "contacts-importer";
export const applicationName = "Contacts Importer (Labs)";
export const categoryName = "Digital marketing";

const importsPath = `${applicationSlug}/imports`;
const jobIdPattern = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/;

// Recent imports with "New import" (Client/src/imports-list/ImportsListTemplate.tsx).
export class ImportsListPage {
  constructor(private readonly page: Page) {}

  get newImportButton(): Locator {
    return this.page.getByTestId("contacts-importer-new-import");
  }

  get noPermissionNotice(): Locator {
    return this.page.getByTestId("contacts-importer-no-permission");
  }

  get headline(): Locator {
    return this.page.getByText("Imports", { exact: true }).last();
  }

  async goto(): Promise<void> {
    await this.page.goto(`${adminBaseUrl}/${importsPath}`);
    await this.expectLoaded();
  }

  // Opens the application from the shell navigation, as a user would
  async gotoViaNavigation(): Promise<void> {
    const shell = new AdminShellPage(this.page);
    await shell.goto();
    await shell.expectSignedIn();
    await shell.openCategory(categoryName);
    await this.page.getByRole("link", { name: applicationName }).first().click();
    await expect(this.page).toHaveURL(new RegExp(`/admin/${importsPath}$`, "i"));
    await this.expectLoaded();
  }

  async expectLoaded(): Promise<void> {
    await expect(this.newImportButton.or(this.noPermissionNotice).first()).toBeVisible({ timeout: 30_000 });
  }

  // Starts a new import and returns its job ID (part of the import page URL)
  async startNewImport(): Promise<string> {
    await this.newImportButton.click();
    await expect(this.page).toHaveURL(new RegExp(`/admin/${importsPath}/${jobIdPattern.source}$`, "i"));
    return jobIdPattern.exec(this.page.url())![0];
  }

  // Row of the import of a file; tests use unique file names
  row(fileName: string): Locator {
    return this.page.getByTestId("contacts-importer-imports").getByTestId("table-row").filter({ hasText: fileName });
  }

  cell(fileName: string, column: "state" | "rows" | "size" | "createdBy" | "contactsCreated" | "contactsUpdated"): Locator {
    return this.row(fileName).getByTestId(`table-cell-${column}`);
  }

  async openImport(fileName: string): Promise<void> {
    await this.row(fileName).getByTestId("button-open").click();
    await expect(this.page).toHaveURL(new RegExp(`/admin/${importsPath}/${jobIdPattern.source}$`, "i"));
  }

  // "Delete files": deletes the uploaded data and cancels an unfinished import
  async deleteFiles(fileName: string): Promise<void> {
    await this.row(fileName).getByTestId("button-delete").click();
    await this.page.getByRole("button", { name: "Delete files" }).last().click();
    await expect(this.cell(fileName, "state")).toHaveText("Cancelled");
  }
}

// One import: File -> Map -> Upload -> Options -> Review -> Run -> Results (Client/src/import/*.tsx).
export class ImportPage {
  constructor(private readonly page: Page) {}

  private get fileInput(): Locator {
    return this.page.getByTestId("contacts-importer-file-input");
  }

  get fileSummary(): Locator {
    return this.page.getByTestId("contacts-importer-file-summary");
  }

  get sampleTable(): Locator {
    return this.page.getByTestId("contacts-importer-sample");
  }

  get uploadStatus(): Locator {
    return this.page.getByTestId("contacts-importer-upload-status");
  }

  get resumeUploadButton(): Locator {
    return this.page.getByTestId("contacts-importer-upload-resume");
  }

  get noPermissionNotice(): Locator {
    return this.page.getByTestId("contacts-importer-no-permission");
  }

  async goto(jobId: string): Promise<void> {
    await this.page.goto(`${adminBaseUrl}/${importsPath}/${jobId}`);
  }

  // Step marked as current in the step header
  async expectStep(step: "file" | "map" | "upload" | "options" | "review" | "run" | "results"): Promise<void> {
    await expect(this.page.getByTestId("contacts-importer-steps").locator('[aria-current="step"]')).toHaveAttribute(
      "data-step",
      step,
      { timeout: 30_000 },
    );
  }

  // File step: the input is hidden behind the "Select file" button
  async selectFile(path: string): Promise<void> {
    await expect(this.fileInput).toBeAttached({ timeout: 30_000 });
    await this.fileInput.setInputFiles(path);
    await expect(this.sampleTable).toBeVisible({ timeout: 30_000 });
  }

  async continueToMapping(): Promise<void> {
    await this.page.getByTestId("contacts-importer-file-next").click();
    await this.expectStep("map");
  }

  // Map step: the contact field select of a file column, by column position
  private targetSelect(columnIndex: number): Locator {
    return this.page.getByTestId(`contacts-importer-target-${columnIndex}`);
  }

  async expectTarget(columnIndex: number, fieldName: string): Promise<void> {
    await expect(this.targetSelect(columnIndex)).toHaveValue(fieldName);
  }

  async setTarget(columnIndex: number, optionLabel: string): Promise<void> {
    await this.targetSelect(columnIndex).click();
    await this.page.getByTestId("menu-item").filter({ hasText: new RegExp(`^${escapeRegExp(optionLabel)}$`) }).click();
    await this.expectTarget(columnIndex, optionLabel);
  }

  async expectKeyField(fieldName: string): Promise<void> {
    await expect(this.page.getByTestId("contacts-importer-key-field")).toHaveValue(fieldName);
  }

  async saveAndUpload(): Promise<void> {
    await this.page.getByTestId("contacts-importer-map-next").click();
    await this.expectStep("upload");
  }

  // Upload step
  async expectUploadPhase(phase: "running" | "paused" | "failed" | "done", timeout = 60_000): Promise<void> {
    await expect(this.uploadStatus).toHaveAttribute("data-upload-phase", phase, { timeout });
  }

  async expectUploaded(rows: number): Promise<void> {
    await this.expectUploadPhase("done");
    await expect(this.uploadStatus).toHaveAttribute("data-job-state", "Uploaded");
    await expect(this.uploadStatus).toContainText(rows.toLocaleString("en-US"));
  }

  // Exact value behind a count card or a table of counts. Upload: rows-read, rows-uploaded, rows-skipped,
  // likely-duplicates. Review: review-created, review-updated, ... and expected-{count}. Run: running-{count}.
  // Results: expected-{count} (check) and actual-{count} (import); counts are the server's aggregate names.
  count(name: string): Locator {
    return this.page.getByTestId(`contacts-importer-count-${name}`).first();
  }

  async expectCount(name: string, value: number): Promise<void> {
    await expect(this.count(name)).toHaveAttribute("data-count", String(value));
  }

  async readCount(name: string): Promise<number> {
    return Number(await this.count(name).getAttribute("data-count"));
  }

  // Options step
  async continueToOptions(): Promise<void> {
    await this.page.getByTestId("contacts-importer-upload-next").click();
    await this.expectStep("options");
  }

  // Radio button of an option, e.g. ("newContacts", "skip")
  async chooseOption(name: string, value: string): Promise<void> {
    const radio = this.page.getByTestId(`contacts-importer-option-${name}-${value}`);
    await radio.click();
    await expect(this.page.locator(`input[type="radio"][name="contacts-importer-${name}"][value="${value}"]`)).toBeChecked();
  }

  // Saves the options; the Review step then checks the file (or shows the finished check)
  async saveOptions(): Promise<void> {
    await this.page.getByTestId("contacts-importer-options-next").click();
    await this.expectStep("review");
  }

  // Review and Run steps: state of the PROCESS_NEXT loop
  get processStatus(): Locator {
    return this.page.getByTestId("contacts-importer-process-status");
  }

  async expectProcessStatus(status: "running" | "pausing" | "paused" | "finished", timeout = 60_000): Promise<void> {
    await expect(this.processStatus).toHaveAttribute("data-process-status", status, { timeout });
  }

  // This tab drives the check or the import: the server answered at least one of its calls with progress
  // and the loop goes on (an answer that another tab is active pauses the loop in the same update).
  async isProcessing(): Promise<boolean> {
    const status = this.processStatus;
    if (!(await status.isVisible())) {
      return false;
    }

    return (await status.getAttribute("data-process-status")) === "running" && Number(await status.getAttribute("data-answers")) > 0;
  }

  async rowsProcessed(): Promise<number> {
    return Number(await this.processStatus.getAttribute("data-rows-processed"));
  }

  // The check (dry run) is finished and the review shows its counts
  async expectAnalyzed(timeout = 120_000): Promise<void> {
    await expect(this.processStatus).toHaveAttribute("data-job-state", "Analyzed", { timeout });
  }

  async backToOptions(): Promise<void> {
    await this.page.getByTestId("contacts-importer-review-back").click();
    await this.expectStep("options");
  }

  // "Run import" and its confirmation
  async runImport(): Promise<void> {
    await this.page.getByTestId("contacts-importer-review-run").click();
    // The dialog button has the same label as the button that opens it
    await this.page.getByRole("button", { name: "Run import" }).last().click();
  }

  async continueProcessing(): Promise<void> {
    await this.page.getByTestId("contacts-importer-process-continue").click({ timeout: 5_000 });
  }

  // Cancel in the Review and Run steps
  async cancelRunningImport(): Promise<void> {
    await this.page.getByTestId("contacts-importer-run-cancel").click();
    await this.page.getByRole("button", { name: "Cancel import" }).last().click();
    await this.expectStep("results");
  }

  // Results step
  get resultsStatus(): Locator {
    return this.page.getByTestId("contacts-importer-results-status");
  }

  async expectResults(state: "Completed" | "Cancelled" | "Failed", timeout = 180_000): Promise<void> {
    await expect(this.resultsStatus).toHaveAttribute("data-job-state", state, { timeout });
    await this.expectStep("results");
  }

  // Downloads a row report (gzipped CSV) and returns its lines without the BOM
  async downloadReport(phase: "analyze" | "execute"): Promise<{ fileName: string; lines: string[] }> {
    const started = this.page.waitForEvent("download");
    await this.page.getByTestId(`contacts-importer-download-report-${phase}`).click();
    const download = await started;
    const text = gunzipSync(await fs.readFile(await download.path())).toString("utf8");
    return { fileName: download.suggestedFilename(), lines: text.replace(/^\uFEFF/, "").trimEnd().split("\n") };
  }

  async pauseUpload(): Promise<void> {
    await this.page.getByTestId("contacts-importer-upload-pause").click();
    await this.expectUploadPhase("paused");
  }

  async downloadSkippedRows(): Promise<Download> {
    const download = this.page.waitForEvent("download");
    await this.page.getByTestId("contacts-importer-download-rejected").click();
    return download;
  }

  async downloadLikelyDuplicates(): Promise<Download> {
    const download = this.page.waitForEvent("download");
    await this.page.getByTestId("contacts-importer-download-duplicates").click();
    return download;
  }

  async cancelImport(): Promise<void> {
    await this.page.getByTestId("contacts-importer-cancel").click();
    await this.page.getByRole("button", { name: "Cancel import" }).last().click();
    await this.expectStep("results");
  }
}

function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

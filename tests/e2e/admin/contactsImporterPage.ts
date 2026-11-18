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
// Word to type before an import that changes many contacts (contactsimporter.review.confirmword, en-US)
const confirmWord = "IMPORT";
const jobIdPattern =/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/;

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

  cell(fileName: string, column: "file" | "created" | "state" | "rows" | "contactsCreated" | "contactsUpdated"): Locator {
    return this.row(fileName).getByTestId(`table-cell-${column}`);
  }

  // Second lines of the File cell (size) and the Created cell (user who created the import)
  fileSize(fileName: string): Locator {
    return this.cell(fileName, "file").getByTestId("contacts-importer-list-file-size");
  }

  createdBy(fileName: string): Locator {
    return this.cell(fileName, "created").getByTestId("contacts-importer-list-created-by");
  }

  async openImport(fileName: string): Promise<void> {
    await this.row(fileName).getByTestId("button-open").click();
    await expect(this.page).toHaveURL(new RegExp(`/admin/${importsPath}/${jobIdPattern.source}$`, "i"));
  }

  // "Delete" row action (D51): removes the whole import; an unfinished one is cancelled first
  async deleteImport(fileName: string): Promise<void> {
    await this.openDeleteDialog(fileName);
    await this.page.getByRole("button", { name: "Delete import" }).last().click();
    await expect(this.row(fileName)).toHaveCount(0);
  }

  async openDeleteDialog(fileName: string): Promise<void> {
    await this.row(fileName).getByTestId("button-delete").click();
    await expect(this.page.getByText("Delete this import?")).toBeVisible();
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

  // File step: format select (value = the format's label) and the note naming the file read from a zip
  get fileFormat(): Locator {
    return this.page.getByTestId("contacts-importer-file-format");
  }

  get fileSheet(): Locator {
    return this.page.getByTestId("contacts-importer-file-sheet");
  }

  get zipEntry(): Locator {
    return this.page.getByTestId("contacts-importer-file-zip-entry");
  }

  // File step: choose a file that cannot be read (no sample is shown)
  async selectUnreadableFile(path: string): Promise<void> {
    await expect(this.fileInput).toBeAttached({ timeout: 30_000 });
    await this.fileInput.setInputFiles(path);
  }

  // File step: worksheet of a workbook with more than one sheet; the sample is read again
  async chooseSheet(name: string): Promise<void> {
    await this.chooseFromSelect("contacts-importer-file-sheet", name);
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

  // File step: the input is hidden behind the "Select file" button; a real click opens the file chooser
  async selectFile(path: string): Promise<void> {
    const button = this.page.getByTestId("contacts-importer-file-select");
    await expect(button).toBeVisible({ timeout: 30_000 });
    const [chooser] = await Promise.all([this.page.waitForEvent("filechooser"), button.click()]);
    await chooser.setFiles(path);
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
    // Field items have a second line (the column name), so match the item's own label exactly.
    await this.page
      .getByTestId("menu-item")
      .filter({ has: this.page.getByText(optionLabel, { exact: true }) })
      .first()
      .click();
    await this.expectTarget(columnIndex, optionLabel);
  }

  // Map step: the file column that says whether (`agreed`) or when (`date`) a contact gave consent
  async setConsentColumn(kind: "agreed" | "date", column: string): Promise<void> {
    const select = this.page.getByTestId(`contacts-importer-consent-${kind}`);
    await select.click();
    await this.page.getByTestId("menu-item").filter({ hasText: new RegExp(`^${escapeRegExp(column)}$`) }).click();
    await expect(select).toHaveValue(column);
  }

  async expectKeyField(fieldName: string): Promise<void> {
    await expect(this.page.getByTestId("contacts-importer-key-field")).toHaveValue(fieldName);
  }

  // "Save and upload" goes straight to Options; the upload runs on in the upload bar
  async saveAndUpload(): Promise<void> {
    await this.page.getByTestId("contacts-importer-map-next").click();
    await this.expectStep("options");
  }

  // Step header: how a step shows (current | done | upcoming | locked) and whether it can be opened
  stepItem(step: "file" | "map" | "upload" | "options" | "review" | "run" | "results"): Locator {
    return this.page.getByTestId("contacts-importer-steps").locator(`[data-step="${step}"]`);
  }

  async expectStepState(step: "file" | "map" | "upload" | "options" | "review" | "run" | "results", state: "current" | "done" | "upcoming" | "locked"): Promise<void> {
    await expect(this.stepItem(step)).toHaveAttribute("data-step-state", state, { timeout: 30_000 });
  }

  stepButton(step: "file" | "map" | "upload" | "options" | "review" | "run" | "results"): Locator {
    return this.page.getByTestId(`contacts-importer-step-${step}`);
  }

  // Upload (in the background): hidden status element with the phase, the job state and the exact counts
  async expectUploadPhase(phase: "running" | "paused" | "failed" | "done", timeout = 60_000): Promise<void> {
    await expect(this.uploadStatus).toHaveAttribute("data-upload-phase", phase, { timeout });
  }

  async expectUploaded(rows: number): Promise<void> {
    await this.expectUploadPhase("done");
    await expect(this.uploadStatus).toHaveAttribute("data-job-state", "Uploaded");
    await expect(this.uploadStatus).toHaveAttribute("data-rows-uploaded", String(rows));
  }

  // The bar (progress, or the summary of a finished upload) and its details panel
  get uploadBar(): Locator {
    return this.page.getByTestId("contacts-importer-upload-bar");
  }

  get uploadDetails(): Locator {
    return this.page.getByTestId("contacts-importer-upload-details");
  }

  async openUploadDetails(): Promise<void> {
    if (!(await this.uploadDetails.isVisible())) {
      await this.page.getByTestId("contacts-importer-upload-details-toggle").click();
    }
    await expect(this.uploadDetails).toBeVisible();
  }

  // Options and Review once the upload is complete: opens a new import and keeps this one (no confirmation)
  get startNewButton(): Locator {
    return this.page.getByTestId("contacts-importer-start-new");
  }

  async startNewImport(): Promise<void> {
    const before = this.page.url();
    await this.startNewButton.click();
    await expect(this.page).not.toHaveURL(before);
    await expect(this.page).toHaveURL(new RegExp(`/admin/${importsPath}/${jobIdPattern.source}$`, "i"));
    await this.expectStep("file");
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

  // Options step: in view since "Save and upload"; "Save and check the file" waits for the upload
  async continueToOptions(): Promise<void> {
    await this.expectStep("options");
    await expect(this.page.getByTestId("contacts-importer-options-next")).toBeEnabled({ timeout: 60_000 });
  }

  // Radio button of an option, e.g. ("newContacts", "skip"). The label is clicked: the test ID is on a
  // wrapper that also holds the caption of the choice.
  async chooseOption(name: string, value: string): Promise<void> {
    const radio = this.page.getByTestId(`contacts-importer-option-${name}-${value}`);
    await radio.locator("label").click();
    await expect(this.page.locator(`input[type="radio"][name="contacts-importer-${name}"][value="${value}"]`)).toBeChecked();
  }

  // Options step: recipient list and consent selects, by display name
  async chooseRecipientList(name: string): Promise<void> {
    await this.chooseFromSelect("contacts-importer-recipient-list", name);
  }

  async chooseConsent(name: string): Promise<void> {
    await this.chooseFromSelect("contacts-importer-consent-select", name);
  }

  private async chooseFromSelect(testId: string, label: string): Promise<void> {
    const select = this.page.getByTestId(testId);
    await select.click();
    await this.page.getByTestId("menu-item").filter({ hasText: new RegExp(`^${escapeRegExp(label)}$`) }).click();
    await expect(select).toHaveValue(label);
  }

  // The note about double opt-in and documented consent shown while a recipient list is chosen
  get consentCallout(): Locator {
    return this.page.getByTestId("contacts-importer-consent-callout");
  }

  // Review and Results: what the import does to the recipient list and to consent (options as data attributes)
  get consentSummary(): Locator {
    return this.page.getByTestId("contacts-importer-consent-summary");
  }

  // Results: who confirmed the consent statement
  get attestationRecord(): Locator {
    return this.page.getByTestId("contacts-importer-attestation-record");
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

  // Review: the mapping card lists file `column` -> contact field (readable label, as the Map step shows it).
  // Mappings with more than 8 columns start collapsed: "Show all" is clicked first when the row is hidden.
  async expectMappingRow(column: string, field: string): Promise<void> {
    const card = this.page.getByTestId("contacts-importer-review-mapping");
    await expect(card).toBeVisible();
    const row = card.locator(`[data-mapping-column="${column}"]`);
    const showAll = this.page.getByTestId("contacts-importer-review-mapping-show-all");
    if ((await row.count()) === 0 && (await showAll.isVisible())) {
      await showAll.click();
    }
    await expect(row.locator("[data-mapping-label]")).toHaveText(field);
  }

  async backToOptions(): Promise<void> {
    await this.page.getByTestId("contacts-importer-review-back").click();
    await this.expectStep("options");
  }

  // "Run import" and its confirmation; an import that changes more than 1,000 contacts asks to type the
  // confirmation word first (CONFIRM_TYPING_THRESHOLD in Client/src/import/model.ts)
  async runImport(): Promise<void> {
    await this.openRunDialog();
    await expect(this.runDialogConfirmButton).toBeVisible();
    // The field is shown with the dialog, so it is there now or not at all
    if ((await this.confirmWordInput.count()) > 0) {
      await this.confirmWordInput.fill(confirmWord);
    }
    await this.confirmRun();
  }

  // Run dialog: field for the confirmation word (the test ID is on the input or on its wrapper)
  get confirmWordInput(): Locator {
    const field = this.page.getByTestId("contacts-importer-confirm-input");
    return field.locator("input").or(this.page.locator('input[data-testid="contacts-importer-confirm-input"]')).first();
  }

  async openRunDialog(): Promise<void> {
    await this.page.getByTestId("contacts-importer-review-run").click();
  }

  // The dialog button has the same label as the button that opens it
  get runDialogConfirmButton(): Locator {
    return this.page.getByRole("button", { name: "Run import" }).last();
  }

  async confirmRun(): Promise<void> {
    await this.runDialogConfirmButton.click();
  }

  // Run dialog of an import that changes subscriptions or consent: the statement text from the server
  get attestationStatement(): Locator {
    return this.page.getByTestId("contacts-importer-attestation-statement");
  }

  async continueProcessing(): Promise<void> {
    await this.page.getByTestId("contacts-importer-process-continue").click({ timeout: 5_000 });
  }

  // Cancel before Run (Options, Review, File waiting for the file): deletes the whole import and returns to the list
  async cancelBeforeRun(): Promise<void> {
    await this.page.getByTestId("contacts-importer-run-cancel").click();
    await expect(this.page.getByText("No contacts have been changed.")).toBeVisible();
    await this.page.getByRole("button", { name: "Cancel import" }).last().click();
    await expect(this.page).toHaveURL(new RegExp(`/admin/${importsPath}$`, "i"));
    await expect(this.page.getByTestId("contacts-importer-new-import")).toBeVisible({ timeout: 30_000 });
  }

  // Cancel in the Run step: the import stops and Results shows what was imported
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
    await this.openUploadDetails();
    await this.page.getByTestId("contacts-importer-upload-pause").click();
    await this.expectUploadPhase("paused");
  }

  // Client-side reports in the upload details
  async downloadSkippedRows(): Promise<Download> {
    await this.openUploadDetails();
    const download = this.page.waitForEvent("download");
    await this.page.getByTestId("contacts-importer-download-rejected").click();
    return download;
  }

  async downloadLikelyDuplicates(): Promise<Download> {
    await this.openUploadDetails();
    const download = this.page.waitForEvent("download");
    await this.page.getByTestId("contacts-importer-download-duplicates").click();
    return download;
  }
}

function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

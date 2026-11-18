import { expect, newRunId, test } from "./contactsImporterFixtures";
import { ImportPage } from "./contactsImporterPage";
import { DataProtectionPage, type Consent } from "./dataProtectionPage";
import { RecipientListsPage, type RecipientList } from "./recipientListsPage";
import { revokeConsent, sqlUnavailableReason, unsubscribeFromRecipientList } from "../shared/database";

// Recipient list and consent options: Map (consent columns) -> Options -> Review -> Run dialog (consent
// confirmation) -> Results, then the end state as Xperience shows it: the "Email status" of the contact in
// the recipient list and the consent agreements of the consent. Every run uses its own email addresses.
//
// Test data: the recipient list of the DancingGoat sample data (every database created by
// scripts/Initialize-Database.ps1 has it) and a consent the spec creates in the Data protection application
// on first use. See tests/e2e/README.md.

// Consent the specs record agreements for; created once per database, reused by later runs.
const consentName = "E2E imported contacts consent";

interface ConsentRow {
  email: string;
  firstName: string;
  optIn: string;
  optInDate: string;
}

function consentCsv(rows: ConsentRow[]): string {
  return `Email,First Name,Opt in,Opt in date\n${rows.map((r) => [r.email, r.firstName, r.optIn, r.optInDate].join(",")).join("\n")}\n`;
}

function rowsFor(runId: string, from: number, to: number, optIn: (n: number) => string = () => "yes"): ConsentRow[] {
  const rows: ConsentRow[] = [];
  for (let n = from; n <= to; n++) {
    rows.push({ email: `e2e.${runId}.${n}@example.com`, firstName: `First${n}`, optIn: optIn(n), optInDate: `2025-03-0${n}` });
  }
  return rows;
}

// Uploads the file, optionally with consent columns, and stops at the Options step.
async function uploadToOptions(importPage: ImportPage, filePath: string, rows: number, consentColumns: boolean): Promise<void> {
  await importPage.selectFile(filePath);
  await importPage.continueToMapping();
  // "Opt in" and "Opt in date" are not suggested as contact fields
  await importPage.expectTarget(2, "Do not import");
  await importPage.expectTarget(3, "Do not import");
  if (consentColumns) {
    await importPage.setConsentColumn("agreed", "Opt in");
    await importPage.setConsentColumn("date", "Opt in date");
  }
  await importPage.saveAndUpload();
  await importPage.expectUploaded(rows);
  await importPage.continueToOptions();
}

// Run dialog of an import that affects consent: the statement from the server, then "Run import" = confirmation.
async function runWithConfirmation(importPage: ImportPage): Promise<void> {
  await importPage.openRunDialog();
  await expect(importPage.attestationStatement).toContainText("I confirm that documented consent exists");
  await expect(importPage.attestationStatement).toHaveAttribute("data-statement-version", /.+/);
  await expect(importPage.runDialogConfirmButton).toBeEnabled();
  await importPage.confirmRun();
  await importPage.expectResults("Completed");
}

test.describe("contacts importer: recipient lists and consent", () => {
  // One worker: the tests share the consent, which the first one creates on a fresh database.
  test.describe.configure({ mode: "default", timeout: 420_000 });

  let list: RecipientList;
  let consent: Consent;

  test.beforeEach(async ({ page }) => {
    list = await new RecipientListsPage(page).findList();
    consent = await new DataProtectionPage(page).ensureConsent(consentName);
  });

  test("defaults change no recipient list and no consent", async ({ page, importsList, importPage, uniqueFile }) => {
    const runId = newRunId();
    const rows = rowsFor(runId, 1, 2);
    const file = await uniqueFile("consent-defaults.csv", consentCsv(rows));

    await importsList.goto();
    await importsList.startNewImport();
    await uploadToOptions(importPage, file.path, 2, false);

    // The safe choices are preselected
    await expect(page.getByTestId("contacts-importer-recipient-list")).toHaveValue("(None)");
    await expect(page.locator('input[name="contacts-importer-consent"][value="none"]')).toBeChecked();
    // Consent per row needs a mapped consent column
    await expect(page.locator('input[name="contacts-importer-consent"][value="blanketWithColumnOverride"]')).toBeDisabled();
    await expect(importPage.consentCallout).toHaveCount(0);

    await importPage.saveOptions();
    await importPage.expectAnalyzed();
    await expect(importPage.consentSummary).toHaveAttribute("data-recipient-list-id", "");
    await expect(importPage.consentSummary).toHaveAttribute("data-consent", "none");

    // No consent confirmation for an import that changes no subscription and no consent
    await importPage.openRunDialog();
    await expect(importPage.attestationStatement).toHaveCount(0);
    await importPage.confirmRun();
    await importPage.expectResults("Completed");
    await importPage.expectCount("actual-created", 2);
    await expect(importPage.attestationRecord).toHaveCount(0);

    await new RecipientListsPage(page).expectNoRecipient(list.id, rows[0].email);
    await new DataProtectionPage(page).expectNoAgreement(consent, rows[0].email);
  });

  test("adds contacts to a recipient list as subscribed and records consent for the rows that say yes", async ({
    page,
    importsList,
    importPage,
    uniqueFile,
  }) => {
    const runId = newRunId();
    // rows 1 and 3 say yes, rows 2 and 4 say no
    const rows = rowsFor(runId, 1, 4, (n) => (n % 2 === 1 ? "Yes" : "no"));
    const file = await uniqueFile("consent-column.csv", consentCsv(rows));

    await importsList.goto();
    await importsList.startNewImport();
    await uploadToOptions(importPage, file.path, 4, true);

    await importPage.chooseRecipientList(list.name);
    await expect(importPage.consentCallout).toBeVisible();
    await expect(importPage.consentCallout).toContainText("double opt-in");
    await expect(page.locator('input[name="contacts-importer-unsubscribed"][value="keep"]')).toBeChecked();
    await importPage.chooseOption("consent", "blanketWithColumnOverride");
    await importPage.chooseConsent(consent.name);
    await expect(page.locator('input[name="contacts-importer-revokedConsent"][value="keep"]')).toBeChecked();
    await importPage.saveOptions();

    // Review: what the import would do, as counts of contacts
    await importPage.expectAnalyzed();
    await importPage.expectCount("expected-created", 4);
    await importPage.expectCount("expected-listAdded", 4);
    await importPage.expectCount("expected-consentRecorded", 2);
    await importPage.expectCount("expected-consentNotGiven", 2);
    await expect(importPage.consentSummary).toHaveAttribute("data-recipient-list-id", String(list.id));
    await expect(importPage.consentSummary).toHaveAttribute("data-consent", "blanketWithColumnOverride");
    await expect(importPage.consentSummary).toContainText(`Recipient list: ${list.name}`);
    await expect(importPage.consentSummary).toContainText(`Consent: ${consent.name}`);
    // Checks run in a second tab, so the import page stays where it is
    const checks = await page.context().newPage();
    const lists = new RecipientListsPage(checks);
    await lists.expectNoRecipient(list.id, rows[0].email);

    await runWithConfirmation(importPage);
    await importPage.expectCount("actual-listAdded", 4);
    await importPage.expectCount("actual-consentRecorded", 2);
    await importPage.expectCount("actual-consentNotGiven", 2);
    await expect(importPage.attestationRecord).toContainText("Consent confirmed by administrator");

    // The list and consent of the import are shown in the imports list
    await importsList.goto();
    await expect(importsList.row(file.name)).toContainText(list.name);
    await expect(importsList.row(file.name)).toContainText(consent.name);

    // End state as Xperience shows it
    for (const row of rows) {
      await lists.expectRecipient(list.id, row.email, "Receiving");
    }
    const dataProtection = new DataProtectionPage(checks);
    await dataProtection.expectAgreement(consent, rows[0].email, "2025");
    await dataProtection.expectAgreement(consent, rows[2].email, "2025");
    await dataProtection.expectNoAgreement(consent, rows[1].email);
    await dataProtection.expectNoAgreement(consent, rows[3].email);
  });

  test("keeps unsubscribed contacts and withdrawn consent unless chosen otherwise", async ({ page, importsList, importPage, uniqueFile }) => {
    const sqlProblem = sqlUnavailableReason();
    test.skip(sqlProblem !== null, `Unsubscribing and withdrawing consent need the database: ${sqlProblem}`);
    const runId = newRunId();
    const rows = rowsFor(runId, 1, 3);
    const checks = await page.context().newPage();
    const lists = new RecipientListsPage(checks);
    const dataProtection = new DataProtectionPage(checks);

    // 1. Subscribe the three contacts and record their consent for every row (no consent columns)
    const first = await uniqueFile("consent-first.csv", consentCsv(rows));
    await importsList.goto();
    await importsList.startNewImport();
    await uploadToOptions(importPage, first.path, 3, false);
    await importPage.chooseRecipientList(list.name);
    await importPage.chooseOption("consent", "blanket");
    await importPage.chooseConsent(consent.name);
    await importPage.saveOptions();
    await importPage.expectAnalyzed();
    await importPage.expectCount("expected-listAdded", 3);
    await importPage.expectCount("expected-consentRecorded", 3);
    await runWithConfirmation(importPage);
    await importPage.expectCount("actual-listAdded", 3);
    await importPage.expectCount("actual-consentRecorded", 3);

    // Contacts 1 and 2 unsubscribe and withdraw their consent (what the unsubscribe link and a revocation store)
    for (const row of rows.slice(0, 2)) {
      unsubscribeFromRecipientList(row.email, list.id);
      revokeConsent(row.email, consent.id);
    }
    await lists.expectRecipient(list.id, rows[0].email, "Unsubscribed");
    await dataProtection.expectNoAgreement(consent, rows[0].email);

    // 2. The same file again with the default "keep" options: nothing changes for them
    const second = await uniqueFile("consent-second.csv", consentCsv(rows));
    await importsList.goto();
    await importsList.startNewImport();
    await uploadToOptions(importPage, second.path, 3, false);
    await importPage.chooseRecipientList(list.name);
    await importPage.chooseOption("consent", "blanket");
    await importPage.chooseConsent(consent.name);
    await importPage.saveOptions();
    await importPage.expectAnalyzed();
    await importPage.expectCount("expected-listKeptUnsubscribed", 2);
    await importPage.expectCount("expected-listAlreadySubscribed", 1);
    await importPage.expectCount("expected-listAdded", 0);
    await importPage.expectCount("expected-consentKeptRevoked", 2);
    await importPage.expectCount("expected-consentAlreadyAgreed", 1);
    await expect(importPage.consentSummary).toHaveAttribute("data-unsubscribed", "keep");
    await expect(importPage.consentSummary).toHaveAttribute("data-revoked-consent", "keep");
    await runWithConfirmation(importPage);
    await importPage.expectCount("actual-listKeptUnsubscribed", 2);
    await importPage.expectCount("actual-consentKeptRevoked", 2);

    await lists.expectRecipient(list.id, rows[0].email, "Unsubscribed");
    await lists.expectRecipient(list.id, rows[1].email, "Unsubscribed");
    await lists.expectRecipient(list.id, rows[2].email, "Receiving");
    await dataProtection.expectNoAgreement(consent, rows[0].email);
    await dataProtection.expectNoAgreement(consent, rows[1].email);
    await dataProtection.expectAgreement(consent, rows[2].email);

    // 3. Contact 2 agreed again: subscribe again and record a new agreement, for contact 2 only
    const third = await uniqueFile("consent-third.csv", consentCsv([rows[1]]));
    await importsList.goto();
    await importsList.startNewImport();
    await uploadToOptions(importPage, third.path, 1, false);
    await importPage.chooseRecipientList(list.name);
    await importPage.chooseOption("unsubscribed", "resubscribe");
    await importPage.chooseOption("consent", "blanket");
    await importPage.chooseConsent(consent.name);
    await importPage.chooseOption("revokedConsent", "reAgree");
    await importPage.saveOptions();
    await importPage.expectAnalyzed();
    await importPage.expectCount("expected-listResubscribed", 1);
    await importPage.expectCount("expected-consentReAgreed", 1);
    await expect(importPage.consentSummary).toContainText("are subscribed again");
    await runWithConfirmation(importPage);
    await importPage.expectCount("actual-listResubscribed", 1);
    await importPage.expectCount("actual-consentReAgreed", 1);

    await lists.expectRecipient(list.id, rows[1].email, "Receiving");
    await dataProtection.expectAgreement(consent, rows[1].email);
    // Contact 1 was not in the file: still unsubscribed, consent still withdrawn
    await lists.expectRecipient(list.id, rows[0].email, "Unsubscribed");
    await dataProtection.expectNoAgreement(consent, rows[0].email);
  });
});

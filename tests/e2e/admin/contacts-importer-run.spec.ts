import { contactsCsv, expect, newRunId, test, testContacts } from "./contactsImporterFixtures";
import { ContactsListPage } from "./contactsListPage";
import { ImportPage } from "./contactsImporterPage";

// Options -> Review (check) -> Run -> Results. These tests create and change contacts; every run uses
// its own email addresses (e2e.{runId}.{n}@example.com), so they can run again on the same database.

// Uploads a file with the suggested mapping and stops at the Options step.
async function uploadToOptions(importPage: ImportPage, filePath: string, rows: number): Promise<void> {
  await importPage.selectFile(filePath);
  await importPage.continueToMapping();
  await importPage.expectKeyField("Email");
  await importPage.saveAndUpload();
  await importPage.expectUploaded(rows);
  await importPage.continueToOptions();
}

test.describe("contacts importer: check, run and results", () => {
  test("imports new contacts, then a second import updates and adds contacts", async ({ page, importsList, importPage, uniqueFile }) => {
    test.setTimeout(240_000);
    const runId = newRunId();
    const contacts = new ContactsListPage(await page.context().newPage());
    const first = testContacts(runId, 1, 30);
    const firstFile = await uniqueFile("contacts-first.csv", contactsCsv(first));

    // First import: 30 new contacts with the default options
    await importsList.goto();
    await importsList.startNewImport();
    await uploadToOptions(importPage, firstFile.path, 30);
    await importPage.saveOptions();

    // Review: the check finishes and shows what the import will do
    await importPage.expectAnalyzed();
    await importPage.expectCount("review-created", 30);
    await importPage.expectCount("review-updated", 0);
    await importPage.expectMappingRow("First Name", "First name");
    await importPage.expectCount("expected-rows", 30);
    await importPage.expectCount("expected-created", 30);
    await importPage.expectCount("expected-skipped", 0);
    await contacts.expectNoContact(first[0].email);

    // The dry-run report has one line per row and no contact data
    const checkReport = await importPage.downloadReport("analyze");
    expect(checkReport.fileName).toMatch(/\.csv\.gz$/);
    expect(checkReport.lines[0]).toBe("row,outcome,reason,field");
    expect(checkReport.lines.slice(1)).toEqual(first.map((_, index) => `${index + 1},created,,`));

    // Run and results
    await importPage.runImport();
    await importPage.expectResults("Completed");
    await importPage.expectCount("expected-created", 30);
    await importPage.expectCount("actual-rows", 30);
    await importPage.expectCount("actual-created", 30);
    await importPage.expectCount("actual-updated", 0);
    await importPage.expectCount("actual-skipped", 0);
    const importReport = await importPage.downloadReport("execute");
    expect(importReport.lines.slice(1)).toEqual(first.map((_, index) => `${index + 1},created,,`));

    // The contacts exist with the imported values
    await contacts.expectContact(first[0]);
    await contacts.expectContact(first[29]);

    await importsList.goto();
    await expect(importsList.cell(firstFile.name, "state")).toHaveText("Completed");
    await expect(importsList.cell(firstFile.name, "contactsCreated")).toHaveText("30");
    await expect(importsList.cell(firstFile.name, "contactsUpdated")).toHaveText("0");

    // Second import of the edited file: 10 changed, 20 the same, 5 new
    const edited = first.map((contact, index) => (index < 10 ? { ...contact, firstName: `Changed${index + 1}` } : contact));
    const second = [...edited, ...testContacts(runId, 31, 35)];
    const secondFile = await uniqueFile("contacts-second.csv", contactsCsv(second));

    await importsList.startNewImport();
    await uploadToOptions(importPage, secondFile.path, 35);
    await importPage.saveOptions();
    await importPage.expectAnalyzed();
    await importPage.expectCount("review-created", 5);
    await importPage.expectCount("review-updated", 10);
    await importPage.expectCount("review-unchanged", 20);
    await importPage.expectCount("expected-rows", 35);
    await expect(page.getByRole("row", { name: "First name 10 0" })).toBeVisible();

    await importPage.runImport();
    await importPage.expectResults("Completed");
    await importPage.expectCount("actual-rows", 35);
    await importPage.expectCount("actual-created", 5);
    await importPage.expectCount("actual-updated", 10);
    await importPage.expectCount("actual-unchanged", 20);
    await importPage.expectCount("expected-updated", 10);
    const secondReport = await importPage.downloadReport("execute");
    expect(secondReport.lines.slice(1, 4)).toEqual(["1,updated,,", "2,updated,,", "3,updated,,"]);
    expect(secondReport.lines[12]).toBe("12,unchanged,,");
    expect(secondReport.lines[35]).toBe("35,created,,");
    await expect(page.getByText("Contact groups are not recalculated")).toBeVisible();

    // Updated, unchanged and new contacts have the expected values
    await contacts.expectContact(second[0]);
    await contacts.expectContact(second[9]);
    await contacts.expectContact(second[10]);
    await contacts.expectContact(second[34]);
    expect(second[0].firstName).toBe("Changed1");
  });
});

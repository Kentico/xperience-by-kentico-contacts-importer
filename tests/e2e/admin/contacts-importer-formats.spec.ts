import { buildXlsx, buildZip } from "../shared/fileFormats";
import { contactsCsv, expect, newRunId, test, testContacts } from "./contactsImporterFixtures";
import type { TestContact } from "./contactsImporterFixtures";
import type { ImportPage } from "./contactsImporterPage";

// XLSX, JSON array and zip files: read in the browser, mapped, uploaded and checked (dry run only:
// nothing is imported, the check counts the contacts the import would create).

const ROWS = 12;

function birthday(n: number): Date {
  return new Date(Date.UTC(1980, 0, n));
}

function isoBirthday(n: number): string {
  return birthday(n).toISOString().slice(0, 10);
}

// Map (suggested by column name) -> Upload -> Options (defaults) -> Review: the check counts every row as new.
async function uploadAndCheck(importPage: ImportPage, rows: number): Promise<void> {
  await importPage.continueToMapping();
  await importPage.expectTarget(0, "Email");
  await importPage.expectTarget(1, "First name");
  await importPage.expectKeyField("Email");
  await importPage.saveAndUpload();
  await importPage.expectUploaded(rows);
  await importPage.continueToOptions();
  await importPage.saveOptions();
  await importPage.expectAnalyzed();
  await importPage.expectCount("expected-rows", rows);
  await importPage.expectCount("review-created", rows);
  await importPage.expectCount("review-updated", 0);
  await importPage.expectCount("expected-skipped", 0);
}

function number(contact: TestContact): number {
  return Number(contact.email.split(".")[2].split("@")[0]);
}

test.describe("contacts importer: file formats", () => {
  test("reads the chosen sheet of an XLSX workbook, with dates", async ({ importsList, importPage, uniqueFile }) => {
    test.setTimeout(180_000);
    const contacts = testContacts(newRunId(), 1, ROWS);
    const workbook = buildXlsx([
      { name: "Read me", rows: [["Note"], ["The contacts are on the next sheet."]] },
      {
        name: "Contacts",
        rows: [
          ["Email", "First Name", "Last Name", "Company", "Birthday"],
          ...contacts.map((c) => [c.email, c.firstName, c.lastName, c.company, birthday(number(c))]),
        ],
      },
    ]);
    const file = await uniqueFile("contacts.xlsx", workbook);

    await importsList.goto();
    await importsList.startNewImport();
    await importPage.selectFile(file.path);
    await expect(importPage.fileFormat).toHaveValue("Excel workbook (.xlsx)");
    await expect(importPage.fileSheet).toHaveValue("Read me");
    await expect(importPage.sampleTable).toContainText("The contacts are on the next sheet.");

    await importPage.chooseSheet("Contacts");
    await expect(importPage.sampleTable).toContainText(contacts[0].email);
    await expect(importPage.sampleTable).toContainText(isoBirthday(1));
    await expect(importPage.fileSummary).toContainText(`${ROWS} rows`);
    await expect(importPage.fileSummary).toContainText("5 columns");

    await uploadAndCheck(importPage, ROWS);
  });

  test("reads a JSON array of objects", async ({ importsList, importPage, uniqueFile }) => {
    test.setTimeout(180_000);
    const contacts = testContacts(newRunId(), 1, ROWS);
    const json = JSON.stringify(
      contacts.map((c) => ({ Email: c.email, "First Name": c.firstName, "Last Name": c.lastName, Company: c.company, Address: { City: "Brno" } })),
      null,
      2,
    );
    const file = await uniqueFile("contacts.json", json);

    await importsList.goto();
    await importsList.startNewImport();
    await importPage.selectFile(file.path);
    await expect(importPage.fileFormat).toHaveValue("JSON (an array of objects)");
    await expect(importPage.sampleTable).toContainText(contacts[0].email);
    await expect(importPage.sampleTable).toContainText("Address.City");
    await expect(importPage.fileSummary).toContainText(`${ROWS} rows`);

    await uploadAndCheck(importPage, ROWS);
  });

  test("reads the one file inside a zip", async ({ importsList, importPage, uniqueFile }) => {
    test.setTimeout(180_000);
    const contacts = testContacts(newRunId(), 1, ROWS);
    const zip = buildZip([
      { name: "export/", data: "", deflate: false },
      { name: "export/contacts.csv", data: contactsCsv(contacts) },
    ]);
    const file = await uniqueFile("contacts.zip", zip);

    await importsList.goto();
    await importsList.startNewImport();
    await importPage.selectFile(file.path);
    await expect(importPage.fileFormat).toHaveValue("CSV / TSV (delimited text)");
    await expect(importPage.zipEntry).toContainText("export/contacts.csv");
    await expect(importPage.sampleTable).toContainText(contacts[0].email);
    await expect(importPage.fileSummary).toContainText(`${ROWS} rows`);

    await uploadAndCheck(importPage, ROWS);
  });

  test("explains a zip with more than one file", async ({ page, importsList, importPage, uniqueFile }) => {
    const contacts = testContacts(newRunId(), 1, 2);
    const zip = buildZip([
      { name: "a.csv", data: contactsCsv(contacts) },
      { name: "b.csv", data: contactsCsv(contacts) },
    ]);
    const file = await uniqueFile("two-files.zip", zip);

    await importsList.goto();
    await importsList.startNewImport();
    await importPage.selectUnreadableFile(file.path);
    await expect(page.getByText("The zip file holds 2 files. Put only the file to import in the zip.")).toBeVisible({ timeout: 30_000 });
  });
});

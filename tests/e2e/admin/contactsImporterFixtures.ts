import fs from "node:fs/promises";
import path from "node:path";
import { test as base } from "@playwright/test";
import { ImportPage, ImportsListPage } from "./contactsImporterPage";

const fixturesPath = path.resolve(__dirname, "../fixtures");

type ImporterFixtures = {
  importsList: ImportsListPage;
  importPage: ImportPage;
  // Copies a file from tests/e2e/fixtures under a name unique to this test run, so the test finds
  // its own import in the list and can run again. Returns the path and the unique file name.
  uniqueFixture: (fixtureName: string) => Promise<{ path: string; name: string }>;
  // Writes generated content (text or bytes) to a uniquely named file.
  uniqueFile: (baseName: string, content: string | Uint8Array) => Promise<{ path: string; name: string }>;
};

// Importer specs use this `test`: page objects plus cleanup of the imports the test created
// ("Delete" removes the import; an unfinished one is cancelled first). Imported contacts stay.
export const test = base.extend<ImporterFixtures>({
  importsList: async ({ page }, use) => {
    await use(new ImportsListPage(page));
  },

  importPage: async ({ page }, use) => {
    await use(new ImportPage(page));
  },

  uniqueFile: async ({ page }, use, testInfo) => {
    const created: string[] = [];
    const unique = `${Date.now().toString(36)}-${testInfo.workerIndex}-${testInfo.retry}`;

    await use(async (baseName, content) => {
      const extension = path.extname(baseName);
      const name = `${path.basename(baseName, extension)}-${unique}-${created.length}${extension}`;
      const filePath = testInfo.outputPath(name);
      await fs.mkdir(path.dirname(filePath), { recursive: true });
      await fs.writeFile(filePath, content);
      created.push(name);
      return { path: filePath, name };
    });

    // Cleanup must not hide the test result: an import that was never created has no row.
    const list = new ImportsListPage(page);
    for (const name of created) {
      try {
        page.removeAllListeners("dialog");
        page.on("dialog", (dialog) => void dialog.accept().catch(() => undefined));
        await page.unrouteAll({ behavior: "ignoreErrors" });
        await list.goto();
        if ((await list.row(name).count()) > 0) {
          await list.deleteImport(name);
        }
      } catch (error) {
        console.warn(`Could not clean up the import of ${name}: ${String(error)}`);
      }
    }
  },

  uniqueFixture: async ({ uniqueFile }, use) => {
    await use(async (fixtureName) => uniqueFile(fixtureName, await fs.readFile(path.join(fixturesPath, fixtureName), "utf8")));
  },
});

export { expect } from "@playwright/test";

// ID that makes the contacts of one test run unique, so tests can run again on the same database.
export function newRunId(): string {
  return `${Date.now().toString(36)}${Math.floor(Math.random() * 1296).toString(36).padStart(2, "0")}`;
}

export interface TestContact {
  email: string;
  firstName: string;
  lastName: string;
  company: string;
}

// Contacts `e2e.{runId}.{n}@example.com` for n in [from, to].
export function testContacts(runId: string, from: number, to: number): TestContact[] {
  const contacts: TestContact[] = [];
  for (let n = from; n <= to; n++) {
    contacts.push({ email: `e2e.${runId}.${n}@example.com`, firstName: `First${n}`, lastName: `Last${n}`, company: `Company ${n}` });
  }
  return contacts;
}

// CSV whose columns map to contact fields by name (Email, First Name, Last Name, Company).
export function contactsCsv(contacts: TestContact[]): string {
  const lines = ["Email,First Name,Last Name,Company"];
  for (const contact of contacts) {
    lines.push([contact.email, contact.firstName, contact.lastName, contact.company].join(","));
  }
  return `${lines.join("\n")}\n`;
}

// CSV with `rows` valid contacts. The random text keeps it from compressing well, so a file of a
// few hundred kilobytes is uploaded in several chunks when the server's chunk size is small.
export function generateContactsCsv(rows: number, tag: string): string {
  const lines = ["Email,First Name,Notes"];
  let state = 0x9e3779b9;
  const noise = (length: number): string => {
    let text = "";
    while (text.length < length) {
      state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
      text += state.toString(36);
    }
    return text.slice(0, length);
  };
  for (let i = 1; i <= rows; i++) {
    lines.push(`e2e.${tag}.${i}@example.com,First${i},${noise(80)}`);
  }
  return `${lines.join("\n")}\n`;
}

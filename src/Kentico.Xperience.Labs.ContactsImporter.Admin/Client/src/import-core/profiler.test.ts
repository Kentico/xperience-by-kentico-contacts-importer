import { describe, expect, it } from "vitest";

import type { TargetField } from "./contract";
import { openRecordSource } from "./open";
import { profileSample, suggestMapping } from "./profiler";
import { makeFile } from "./test-helpers";

const TARGETS: TargetField[] = [
  { name: "ContactEmail", caption: "Email" },
  { name: "ContactFirstName", caption: "First name" },
  { name: "ContactLastName", caption: "Last name" },
  { name: "ContactMobilePhone", caption: "Mobile phone" },
  { name: "ContactCompanyName", caption: "Company name" },
  { name: "ContactBirthday", caption: "Birthday", dataType: "datetime" },
  { name: "ContactLoyaltyTier", caption: "Loyalty tier" },
];

describe("profileSample", () => {
  it("profiles columns, sample rows and suggests a mapping", async () => {
    const csv =
      "E-mail;First Name;surname;Loyalty Tier;Internal\n" +
      "jane@example.com; Jane ;Doe;gold;x\n" +
      "not-an-email;Bob;;silver;x\n" +
      ";Cid;Novak;;x\n" +
      "DAN@EXAMPLE.COM;Dan;Brown;gold;x\n";
    const source = await openRecordSource(makeFile(csv));
    const profile = await profileSample(source, { targetFields: TARGETS });

    expect(profile.format).toBe("csv");
    expect(profile.info).toMatchObject({
      delimiter: ";",
      encoding: "utf-8",
      hasHeader: true,
    });
    expect(profile.rowsSampled).toBe(4);
    expect(profile.complete).toBe(true);
    expect(profile.estimatedTotalRows).toBe(4);
    expect(profile.parseErrors).toBe(0);
    expect(profile.raggedRows).toBe(0);
    expect(profile.bytesRead).toBe(profile.totalBytes);

    expect(profile.columns).toEqual([
      {
        index: 0,
        name: "E-mail",
        filled: 3,
        fillRate: 0.75,
        validEmails: 2,
        emailRate: 2 / 3,
        examples: ["jane@example.com", "not-an-email", "DAN@EXAMPLE.COM"],
      },
      {
        index: 1,
        name: "First Name",
        filled: 4,
        fillRate: 1,
        validEmails: 0,
        emailRate: 0,
        examples: ["Jane", "Bob", "Cid"],
      },
      {
        index: 2,
        name: "surname",
        filled: 3,
        fillRate: 0.75,
        validEmails: 0,
        emailRate: 0,
        examples: ["Doe", "Novak", "Brown"],
      },
      {
        index: 3,
        name: "Loyalty Tier",
        filled: 3,
        fillRate: 0.75,
        validEmails: 0,
        emailRate: 0,
        examples: ["gold", "silver"],
      },
      {
        index: 4,
        name: "Internal",
        filled: 4,
        fillRate: 1,
        validEmails: 0,
        emailRate: 0,
        examples: ["x"],
      },
    ]);
    expect(profile.sampleRows[0]).toEqual({
      rowNumber: 1,
      values: ["jane@example.com", " Jane ", "Doe", "gold", "x"],
    });
    expect(profile.sampleRows).toHaveLength(4);

    expect(profile.suggestions).toEqual([
      { source: "E-mail", target: "ContactEmail" },
      { source: "First Name", target: "ContactFirstName" },
      { source: "surname", target: "ContactLastName" },
      { source: "Loyalty Tier", target: "ContactLoyaltyTier" },
    ]);
  });

  it("samples only the first rows of a large file and estimates the total", async () => {
    const rows = 50_000;
    const lines = ["Email,Name"];
    for (let i = 0; i < rows; i++) {
      lines.push(
        `user${String(i).padStart(6, "0")}@example.com,Name ${String(i).padStart(6, "0")}`,
      );
    }
    const file = makeFile(lines.join("\n"), "large.csv", 16 * 1024);
    const source = await openRecordSource(file);
    const profile = await profileSample(source, {
      maxRows: 1000,
      sampleRows: 5,
    });

    expect(profile.rowsSampled).toBe(1000);
    expect(profile.complete).toBe(false);
    expect(profile.sampleRows).toHaveLength(5);
    expect(profile.columns[0]).toMatchObject({
      filled: 1000,
      fillRate: 1,
      emailRate: 1,
    });
    expect(profile.bytesRead).toBeLessThan(file.size / 10);
    expect(file.cancelled).toBe(true);
    expect(profile.estimatedTotalRows).toBeGreaterThan(rows * 0.95);
    expect(profile.estimatedTotalRows).toBeLessThan(rows * 1.05);
    expect(profile.suggestions).toEqual([]);
  });

  it("counts ragged rows and parse errors", async () => {
    const csv = await profileSample(
      await openRecordSource(makeFile("a,b\n1\n1,2\n1,2,3\n")),
    );
    expect(csv.raggedRows).toBe(2);
    expect(csv.sampleRows.map((row) => row.values)).toEqual([
      ["1", ""],
      ["1", "2"],
      ["1", "2"],
    ]);

    const ndjson = await profileSample(
      await openRecordSource(
        makeFile('{"a":"1"}\n{bad\n{"b":"x@y.co"}\n', "x.ndjson"),
      ),
    );
    expect(ndjson.parseErrors).toBe(1);
    expect(ndjson.rowsSampled).toBe(2);
    expect(ndjson.raggedRows).toBe(0);
    expect(
      ndjson.columns.map((c) => [c.name, c.filled, c.validEmails]),
    ).toEqual([
      ["a", 1, 0],
      ["b", 1, 1],
    ]);
    expect(ndjson.sampleRows.map((row) => row.values)).toEqual([
      ["1", ""],
      ["", "x@y.co"],
    ]);
  });

  it("handles an empty file", async () => {
    const profile = await profileSample(await openRecordSource(makeFile("")), {
      targetFields: TARGETS,
    });
    expect(profile).toMatchObject({
      columns: [],
      sampleRows: [],
      rowsSampled: 0,
      complete: true,
      estimatedTotalRows: 0,
      suggestions: [],
    });
  });
});

describe("suggestMapping", () => {
  const column = (name: string, emailRate = 0, filled = 10) => ({
    name,
    emailRate,
    filled,
  });

  it("matches field names, names without the Contact prefix, captions and synonyms", () => {
    expect(
      suggestMapping(
        [
          column("ContactEmail"),
          column("first_name"),
          column("LAST NAME"),
          column("Cell Phone"),
          column("Organisation"),
          column("DOB"),
          column("unknown"),
        ],
        TARGETS,
      ),
    ).toEqual([
      { source: "ContactEmail", target: "ContactEmail" },
      { source: "first_name", target: "ContactFirstName" },
      { source: "LAST NAME", target: "ContactLastName" },
      { source: "Cell Phone", target: "ContactMobilePhone" },
      { source: "Organisation", target: "ContactCompanyName" },
      { source: "DOB", target: "ContactBirthday" },
    ]);
  });

  it("uses each target once and prefers exact names over synonyms", () => {
    expect(
      suggestMapping(
        [column("mail"), column("Email"), column("e-mail")],
        TARGETS,
      ),
    ).toEqual([{ source: "Email", target: "ContactEmail" }]);
  });

  it("suggests the email field for an unnamed column full of emails", () => {
    expect(
      suggestMapping(
        [
          column("Column 1", 0.2),
          column("Column 2", 0.95),
          column("Column 3", 0.9),
        ],
        TARGETS,
      ),
    ).toEqual([{ source: "Column 2", target: "ContactEmail" }]);
    expect(suggestMapping([column("Column 1", 0.5)], TARGETS)).toEqual([]);
    expect(suggestMapping([column("Column 1", 1, 0)], TARGETS)).toEqual([]);
  });

  it("only suggests fields that are in the target list", () => {
    expect(
      suggestMapping(
        [column("email"), column("city")],
        [{ name: "ContactCity" }],
      ),
    ).toEqual([{ source: "city", target: "ContactCity" }]);
  });
});

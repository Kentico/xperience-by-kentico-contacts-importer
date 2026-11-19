import { describe, expect, it } from "vitest";

import { CONSENT_DATE_FIELD } from "./contract";
import type { ChunkRecord, ConsentMapping } from "./contract";
import { RowMapper, parseConsentGiven } from "./mapping";
import type { MapResult, RowMapperOptions } from "./mapping";
import { ImportError } from "./source";

const COLUMNS = ["Email", "Name", "Opt in", "Opt in date"];

const OPTIONS: RowMapperOptions = {
  keyField: "ContactEmail",
  mapping: [
    { source: "Email", target: "ContactEmail" },
    { source: "Name", target: "ContactFirstName" },
  ],
  fields: [
    { name: "ContactEmail", dataType: "text", maxLength: 254 },
    { name: "ContactFirstName", dataType: "text", maxLength: 100 },
  ],
};

function mapper(
  consentMapping?: ConsentMapping | null,
  extra: Partial<RowMapperOptions> = {},
  columns: readonly string[] = COLUMNS,
): RowMapper {
  return new RowMapper(columns, { ...OPTIONS, ...extra, consentMapping });
}

function map(target: RowMapper, values: string[], rowNumber = 1): MapResult {
  return target.map({ rowNumber, values });
}

function record(result: MapResult): ChunkRecord {
  if (!result.ok) {
    throw new Error(`Row was rejected: ${result.reason}`);
  }

  return result.record;
}

describe("parseConsentGiven", () => {
  // The contract's list: trimmed, any letter case. Parity with the server, which only accepts the resulting boolean.
  it.each([
    "y",
    "Y",
    "yes",
    "YES",
    "Yes",
    "true",
    "TRUE",
    "1",
    "x",
    "X",
    "  yes  ",
    "\tTrue\n",
  ])("%j means yes", (value) => {
    expect(parseConsentGiven(value)).toBe(true);
  });

  it.each([
    "",
    "   ",
    "n",
    "no",
    "false",
    "0",
    "ja",
    "oui",
    "si",
    "ok",
    "on",
    "yes!",
    "y es",
    "yess",
    "11",
    "true false",
    "agreed",
    "✓",
  ])("%j means no", (value) => {
    expect(parseConsentGiven(value)).toBe(false);
  });

  it("a missing value means no", () => {
    expect(parseConsentGiven(undefined)).toBe(false);
  });
});

describe("RowMapper consent columns", () => {
  it("adds no consent part without a consent mapping", () => {
    for (const consentMapping of [undefined, null]) {
      const result = record(
        map(mapper(consentMapping), [
          "a@example.com",
          "Ann",
          "yes",
          "2025-03-01",
        ]),
      );

      expect(result).toEqual({
        r: 1,
        k: "a@example.com",
        v: { ContactEmail: "a@example.com", ContactFirstName: "Ann" },
      });
      expect("c" in result).toBe(false);
    }
  });

  it("adds the consent part to every record when a consent mapping is set", () => {
    const target = mapper({ agreed: "Opt in", date: "Opt in date" });

    expect(
      record(map(target, ["a@example.com", "Ann", "yes", "2025-03-01"], 1)).c,
    ).toEqual({ a: true, d: "2025-03-01" });
    expect(
      record(map(target, ["b@example.com", "Bob", "no", "2025-03-02"], 2)).c,
    ).toEqual({ a: false, d: "2025-03-02" });
    expect(record(map(target, ["c@example.com", "Cid", "", ""], 3)).c).toEqual({
      a: false,
    });
    expect(
      record(map(target, ["d@example.com", "Dee", " X ", "  "], 4)).c,
    ).toEqual({ a: true });
    // a short row: the consent columns are missing
    expect(record(map(target, ["e@example.com", "Eve"], 5)).c).toEqual({
      a: false,
    });
  });

  it("never writes consent values into the contact fields", () => {
    const result = record(
      map(mapper({ agreed: "Opt in", date: "Opt in date" }), [
        "a@example.com",
        "Ann",
        "yes",
        "2025-03-01",
      ]),
    );

    expect(result.v).toEqual({
      ContactEmail: "a@example.com",
      ContactFirstName: "Ann",
    });
  });

  it("without a date column the record has no date", () => {
    const target = mapper({ agreed: "Opt in" });

    expect(
      record(map(target, ["a@example.com", "Ann", "TRUE", "2025-03-01"])).c,
    ).toEqual({ a: true });
    expect(
      record(
        map(mapper({ agreed: "Opt in", date: null }), [
          "a@example.com",
          "Ann",
          "1",
          "not a date",
        ]),
      ).c,
    ).toEqual({ a: true });
  });

  it("with only a date column every row counts as agreed (server model rule); with no column nobody does", () => {
    const target = mapper({ date: "Opt in date" });

    expect(
      record(map(target, ["a@example.com", "Ann", "no", "2025-03-01"])).c,
    ).toEqual({ a: true, d: "2025-03-01" });
    expect(record(map(target, ["a@example.com", "Ann", "no", ""])).c).toEqual({
      a: true,
    });
    expect(
      record(map(mapper({}), ["a@example.com", "Ann", "yes", "2025-03-01"])).c,
    ).toEqual({ a: false });
  });

  it("parses the consent date with the date format of the job", () => {
    const consentMapping = { agreed: "Opt in", date: "Opt in date" };

    expect(
      record(
        map(mapper(consentMapping, { dateFormat: "dmy" }), [
          "a@example.com",
          "Ann",
          "y",
          "31.01.2025",
        ]),
      ).c,
    ).toEqual({ a: true, d: "2025-01-31" });
    expect(
      record(
        map(mapper(consentMapping, { dateFormat: "mdy" }), [
          "a@example.com",
          "Ann",
          "y",
          "01/31/2025",
        ]),
      ).c,
    ).toEqual({ a: true, d: "2025-01-31" });
    expect(
      record(
        map(mapper(consentMapping), [
          "a@example.com",
          "Ann",
          "y",
          "2025-01-31T10:20:30Z",
        ]),
      ).c,
    ).toEqual({
      a: true,
      d: "2025-01-31T10:20:30Z",
    });
    expect(
      record(
        map(mapper(consentMapping, { dateFormat: "dmy" }), [
          "a@example.com",
          "Ann",
          "y",
          "31.01.2025 10:20",
        ]),
      ).c,
    ).toEqual({
      a: true,
      d: "2025-01-31T10:20:00",
    });
  });

  it("rejects a row whose consent date cannot be read, also when the row does not say yes", () => {
    const target = mapper({ agreed: "Opt in", date: "Opt in date" });

    expect(map(target, ["a@example.com", "Ann", "yes", "last spring"])).toEqual(
      { ok: false, reason: "invalidDate", field: CONSENT_DATE_FIELD },
    );
    expect(map(target, ["a@example.com", "Ann", "no", "2025-02-30"])).toEqual({
      ok: false,
      reason: "invalidDate",
      field: "consent date",
    });
    // day-first value while the job reads ISO dates only
    expect(map(target, ["a@example.com", "Ann", "yes", "31.01.2025"])).toEqual({
      ok: false,
      reason: "invalidDate",
      field: "consent date",
    });
  });

  it("reports an unusable key before a bad consent date", () => {
    const target = mapper({ agreed: "Opt in", date: "Opt in date" });

    expect(map(target, ["", "Ann", "yes", "last spring"])).toEqual({
      ok: false,
      reason: "missingKey",
    });
    expect(map(target, ["not-an-email", "Ann", "yes", "last spring"])).toEqual({
      ok: false,
      reason: "invalidEmailKey",
    });
  });

  it("keeps the invalid email flag next to the consent part", () => {
    const target = new RowMapper(["Id", "Email", "Opt in"], {
      keyField: "CustomerID",
      mapping: [
        { source: "Id", target: "CustomerID" },
        { source: "Email", target: "ContactEmail" },
      ],
      consentMapping: { agreed: "Opt in" },
    });

    expect(record(map(target, ["7", "nope", "yes"]))).toEqual({
      r: 1,
      k: "7",
      v: { CustomerID: "7", ContactEmail: "nope" },
      f: ["invalidEmail"],
      c: { a: true },
    });
  });

  it("refuses a consent column that is missing in the file", () => {
    expect(() => mapper({ agreed: "Consent" })).toThrow(ImportError);
    expect(() => mapper({ agreed: "Opt in", date: "When" })).toThrow(/When/);
  });

  it("refuses a consent column that is also a contact field column, and the same column twice", () => {
    expect(() => mapper({ agreed: "Name" })).toThrow(
      /cannot also be a consent column/,
    );
    expect(() => mapper({ agreed: "Opt in", date: "Email" })).toThrow(
      /cannot also be a consent column/,
    );
    expect(() => mapper({ agreed: "Opt in", date: "Opt in" })).toThrow(
      /two different columns/,
    );
  });

  it("finds consent columns that appear later in a source with dynamic columns (NDJSON)", () => {
    const columns = ["Email", "Name"];
    const target = new RowMapper(
      columns,
      { ...OPTIONS, consentMapping: { agreed: "Opt in", date: "Opt in date" } },
      true,
    );

    expect(record(map(target, ["a@example.com", "Ann"])).c).toEqual({
      a: false,
    });

    columns.push("Opt in", "Opt in date");
    expect(
      record(map(target, ["b@example.com", "Bob", "yes", "2025-03-01"], 2)).c,
    ).toEqual({ a: true, d: "2025-03-01" });
  });

  it("serializes to the wire shape the server validates", () => {
    const target = mapper({ agreed: "Opt in", date: "Opt in date" });

    expect(
      JSON.stringify(
        record(map(target, ["a@example.com", "Ann", "yes", "2025-03-01"])),
      ),
    ).toBe(
      '{"r":1,"k":"a@example.com","v":{"ContactEmail":"a@example.com","ContactFirstName":"Ann"},"c":{"a":true,"d":"2025-03-01"}}',
    );
    expect(
      JSON.stringify(record(map(target, ["a@example.com", "Ann", "", ""]))),
    ).toBe(
      '{"r":1,"k":"a@example.com","v":{"ContactEmail":"a@example.com","ContactFirstName":"Ann"},"c":{"a":false}}',
    );
  });
});

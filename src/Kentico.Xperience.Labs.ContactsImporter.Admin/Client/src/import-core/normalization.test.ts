import { describe, expect, it } from "vitest";

// TS <-> C# parity vectors, shared with tests/Kentico.Xperience.Labs.ContactsImporter.Admin.Tests
import fixture from "../../../../../tests/fixtures/normalization.json";
import {
  checkKey,
  isValidEmail,
  normalizeEmail,
  normalizeKey,
  parseDateToIso,
} from "./normalization";

describe("normalization parity fixture", () => {
  it("has cases", () => {
    expect(fixture.keyCases.length).toBeGreaterThan(0);
    expect(fixture.emailCases.length).toBeGreaterThan(0);
    expect(fixture.lengthCases.length).toBeGreaterThan(0);
  });

  it.each(fixture.keyCases.map((c, i) => [i, c] as const))(
    "keyCases[%i]",
    (_, { keyField, input, normalizedKey }) => {
      expect(normalizeKey(keyField, input)).toBe(normalizedKey);
    },
  );

  it.each(fixture.emailCases.map((c, i) => [i, c] as const))(
    "emailCases[%i]",
    (_, { input, valid }) => {
      expect(isValidEmail(normalizeEmail(input))).toBe(valid);
    },
  );

  it.each(fixture.lengthCases.map((c, i) => [i, c] as const))(
    "lengthCases[%i]",
    (_, c) => {
      const suffix =
        "domain" in c && typeof c.domain === "string" ? c.domain : "";
      const build = (length: number) =>
        c.repeatChar.repeat(length - suffix.length) + suffix;
      const valid = build(c.validLength);
      const invalid = build(c.invalidLength);
      expect(valid).toHaveLength(c.validLength);
      expect(invalid).toHaveLength(c.invalidLength);

      expect(normalizeKey(c.keyField, valid)).toBe(valid);
      expect(normalizeKey(c.keyField, invalid)).toBeNull();
      if (suffix) {
        expect(isValidEmail(valid)).toBe(true);
        expect(isValidEmail(invalid)).toBe(false);
      }
    },
  );
});

describe("checkKey", () => {
  it("reports why a key is unusable", () => {
    expect(checkKey("ContactEmail", "  ")).toEqual({
      ok: false,
      reason: "missingKey",
    });
    expect(checkKey("ContactEmail", "nope")).toEqual({
      ok: false,
      reason: "invalidEmailKey",
    });
    expect(checkKey("CustomerID", "x".repeat(451))).toEqual({
      ok: false,
      reason: "keyTooLong",
    });
    expect(checkKey("CustomerID", " Ab ")).toEqual({ ok: true, key: "Ab" });
  });

  it("lowercases only the email key", () => {
    expect(normalizeKey("ContactEmail", "A@B.Co")).toBe("a@b.co");
    expect(normalizeKey("ContactLastName", "Smith")).toBe("Smith");
  });
});

describe("parseDateToIso", () => {
  it.each([
    ["2024-02-29", "2024-02-29"],
    ["2024-02-29T13:05", "2024-02-29T13:05:00"],
    ["2024-02-29 13:05:09", "2024-02-29T13:05:09"],
    ["2024-02-29T13:05:09.123Z", "2024-02-29T13:05:09.123Z"],
    ["2024-02-29T13:05:09+0200", "2024-02-29T13:05:09+02:00"],
    ["2024-02-29T13:05:09-05:30", "2024-02-29T13:05:09-05:30"],
  ])("iso: %s -> %s", (input, expected) => {
    expect(parseDateToIso(input, "iso")).toBe(expected);
    expect(parseDateToIso(input, "dmy")).toBe(expected);
  });

  it.each([
    ["dmy", "31.1.2024", "2024-01-31"],
    ["dmy", "01/02/2024", "2024-02-01"],
    ["dmy", "1-2-2024 7:05", "2024-02-01T07:05:00"],
    ["mdy", "1/31/2024", "2024-01-31"],
    ["mdy", "01/02/2024", "2024-01-02"],
    ["mdy", "12/31/1999 23:59:58", "1999-12-31T23:59:58"],
  ] as const)("%s: %s -> %s", (format, input, expected) => {
    expect(parseDateToIso(input, format)).toBe(expected);
  });

  it.each([
    ["iso", "2023-02-29"],
    ["iso", "2024-13-01"],
    ["iso", "2024-01-01T24:00"],
    ["iso", "31.1.2024"],
    ["iso", "yesterday"],
    ["iso", "20240131"],
    ["dmy", "1/31/2024"],
    ["mdy", "31/1/2024"],
    ["dmy", "31.4.2024"],
    ["dmy", "1.1.24"],
    ["dmy", "1.1.2024 25:00"],
  ] as const)("%s rejects %s", (format, input) => {
    expect(parseDateToIso(input, format)).toBeNull();
  });
});

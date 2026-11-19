import { describe, expect, it } from "vitest";

import {
  chunkSizes,
  fieldLabel,
  isFinished,
  stepForState,
  toOpenOptions,
  toTargetFields,
} from "../import/model";

import type { JobState, LimitsDto } from "./commands";
import {
  createFormatters,
  formatBytes,
  formatDateTime,
  formatDuration,
  formatNumber,
  formatPercent,
  localizeParams,
} from "./format";
import {
  csvValue,
  rejectReasonKey,
  rejectedRowsCsv,
  reportFileName,
  suspectRowsCsv,
} from "./reports";
import { enText } from "./test-resources";

const LIMITS: LimitsDto = {
  maxFileSizeBytes: 5 * 1024 ** 3,
  chunkSizeBytes: 4 * 1024 * 1024,
  maxChunkCompressedBytes: 8 * 1024 * 1024,
  maxChunkUncompressedBytes: 128 * 1024 * 1024,
  maxNonTextValueLength: 256,
  maxKeyLength: 450,
  parallelUploads: 3,
};

describe("localizeParams", () => {
  it("formats numbers in the culture and keeps count a number for the plural form", () => {
    expect(
      localizeParams(
        { rows: 1234567, count: 1234567, name: "x" },
        createFormatters("en-US"),
      ),
    ).toEqual({
      rows: "1,234,567",
      count: 1234567,
      name: "x",
    });
    expect(
      localizeParams({ rows: 1234567 }, createFormatters("de-DE")),
    ).toEqual({ rows: "1.234.567" });
  });
});

describe("formatters", () => {
  it("formats bytes in binary units", () => {
    expect(formatBytes(0, "en-US")).toBe("0 B");
    expect(formatBytes(1536, "en-US")).toBe("1.5 KB");
    expect(formatBytes(4 * 1024 * 1024, "en-US")).toBe("4 MB");
    expect(formatBytes(250 * 1024 * 1024, "en-US")).toBe("250 MB");
    expect(formatBytes(5 * 1024 ** 3, "en-US")).toBe("5 GB");
  });

  it("formats numbers, percentages and durations", () => {
    expect(formatNumber(1234567, "en-US")).toBe("1,234,567");
    expect(formatPercent(3.46, "en-US")).toBe("3.5");
    expect(formatPercent(42.4, "en-US")).toBe("42");
    expect(formatPercent(0, "en-US")).toBe("0");
    expect(formatDuration(8, "en-US")).toBe("8 sec");
    expect(formatDuration(75, "en-US")).toBe("1 min, 15 sec");
    expect(formatDuration(3720, "en-US")).toBe("1 hr, 2 min");
  });

  it("formats in the culture it is given", () => {
    expect(formatNumber(1234567, "de-DE")).toBe("1.234.567");
    expect(formatBytes(1536, "de-DE")).toBe("1,5 KB");
    expect(formatPercent(3.46, "cs-CZ")).toBe("3,5");
    expect(formatDuration(75, "de-DE")).toBe("1 Min., 15 Sek.");
    expect(formatDateTime("2026-11-16T12:00:00Z", "de-DE")).not.toBe(
      formatDateTime("2026-11-16T12:00:00Z", "en-US"),
    );
  });

  it("reads server timestamps as UTC, with or without a zone", () => {
    const withZone = formatDateTime("2026-11-16T12:00:00Z", "en-US");

    expect(formatDateTime("2026-11-16T12:00:00", "en-US")).toBe(withZone);
    expect(formatDateTime("not a date", "en-US")).toBe("not a date");
  });
});

describe("reports", () => {
  it("builds the skipped rows CSV from row numbers and reasons only, with fixed column names", () => {
    const csv = rejectedRowsCsv(
      [
        { rowNumber: 3, reason: "missingKey" },
        { rowNumber: 9, reason: "valueTooLong", field: "ContactFirstName" },
        { rowNumber: 12, reason: "parseError", detail: "unterminatedQuote" },
      ],
      (reason) => enText(rejectReasonKey(reason)),
    );

    expect(csv).toBe(
      [
        "row,reason,field,detail",
        "3,No value in the match field,,",
        "9,A value is too long for its field,ContactFirstName,",
        "12,Row could not be parsed,,unterminatedQuote",
        "",
      ].join("\r\n"),
    );
  });

  it("builds the likely duplicates CSV", () => {
    expect(suspectRowsCsv([5, 9])).toBe("row\r\n5\r\n9\r\n");
  });

  it("quotes CSV values and neutralizes formulas", () => {
    expect(csvValue("plain")).toBe("plain");
    expect(csvValue("a,b")).toBe('"a,b"');
    expect(csvValue('say "hi"')).toBe('"say ""hi"""');
    expect(csvValue("=SUM(A1)")).toBe("'=SUM(A1)");
    expect(csvValue("+1")).toBe("'+1");
    expect(csvValue("@x")).toBe("'@x");
  });

  it("names report files after the source file", () => {
    expect(reportFileName("contacts.csv", "skipped-rows")).toBe(
      "contacts-skipped-rows.csv",
    );
    expect(reportFileName("my list (final).csv.gz", "likely-duplicates")).toBe(
      "my-list-final--likely-duplicates.csv",
    );
    expect(reportFileName("", "skipped-rows")).toBe("import-skipped-rows.csv");
  });
});

describe("import model", () => {
  it("maps every job state to a step", () => {
    const expected: Record<JobState, string> = {
      Configuring: "file",
      Uploading: "options",
      Uploaded: "options",
      Analyzing: "review",
      Analyzed: "review",
      Confirmed: "run",
      Running: "run",
      Paused: "run",
      Completed: "results",
      Failed: "results",
      Cancelled: "results",
    };

    for (const [state, step] of Object.entries(expected)) {
      expect(stepForState(state as JobState)).toBe(step);
    }
    // A paused job is at the step of the phase that was running.
    expect(stepForState("Paused", "analyze")).toBe("review");
    expect(stepForState("Paused", "execute")).toBe("run");
    expect(isFinished("Cancelled")).toBe(true);
    expect(isFinished("Uploaded")).toBe(false);
  });

  it("turns source settings into open options, leaving detection on for empty values", () => {
    expect(
      toOpenOptions({
        format: "",
        encoding: "",
        delimiter: "",
        hasHeader: true,
        sheet: "",
      }),
    ).toEqual({ hasHeader: true });
    expect(
      toOpenOptions({
        format: "csv",
        encoding: "windows-1252",
        delimiter: ";",
        hasHeader: false,
        sheet: "Q1",
      }),
    ).toEqual({
      format: "csv",
      encoding: "windows-1252",
      delimiter: ";",
      hasHeader: false,
      sheet: "Q1",
    });
  });

  it("gives the import core the value length limits the server enforces", () => {
    const fields = toTargetFields(
      [
        {
          name: "ContactEmail",
          caption: "Email",
          dataType: "text",
          maxLength: 254,
          isSupported: true,
          canBeKey: true,
        },
        {
          name: "ContactNotes",
          caption: "ContactNotes",
          dataType: "longtext",
          maxLength: null,
          isSupported: true,
          canBeKey: true,
        },
        {
          name: "ContactBirthday",
          caption: "ContactBirthday",
          dataType: "datetime",
          maxLength: null,
          isSupported: true,
          canBeKey: true,
        },
        {
          name: "ContactGender",
          caption: "ContactGender",
          dataType: "integer",
          maxLength: null,
          isSupported: true,
          canBeKey: true,
        },
      ],
      LIMITS,
    );

    expect(fields).toEqual([
      {
        name: "ContactEmail",
        caption: "Email",
        dataType: "text",
        maxLength: 254,
      },
      { name: "ContactNotes", caption: "ContactNotes", dataType: "longtext" },
      {
        name: "ContactBirthday",
        caption: "ContactBirthday",
        dataType: "datetime",
        maxLength: 256,
      },
      {
        name: "ContactGender",
        caption: "ContactGender",
        dataType: "integer",
        maxLength: 256,
      },
    ]);
  });

  it("keeps chunk sizes within the server limits", () => {
    expect(chunkSizes(LIMITS)).toEqual({
      targetChunkBytes: 4 * 1024 * 1024,
      maxChunkUncompressedBytes: 32 * 1024 * 1024,
    });
    expect(
      chunkSizes({ ...LIMITS, maxChunkUncompressedBytes: 1024 })
        .maxChunkUncompressedBytes,
    ).toBe(1024);
  });

  it("labels fields with caption and name when they differ", () => {
    expect(fieldLabel({ name: "ContactEmail", caption: "Email address" })).toBe(
      "Email address",
    );
    expect(fieldLabel({ name: "ContactEmail", caption: "ContactEmail" })).toBe(
      "Email",
    );
    expect(fieldLabel({ name: "ContactBusinessPhone", caption: "" })).toBe(
      "Business phone",
    );
    expect(
      fieldLabel({ name: "ContactCountryID", caption: "ContactCountryID" }),
    ).toBe("Country ID");
    expect(fieldLabel({ name: "Contact", caption: "" })).toBe("Contact");
    expect(fieldLabel({ name: "LoyaltyTier", caption: "LoyaltyTier" })).toBe(
      "Loyalty tier",
    );
  });
});

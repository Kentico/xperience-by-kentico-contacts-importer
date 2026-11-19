import { describe, expect, it } from "vitest";

import type { FieldDto } from "../shared/commands";

import {
  MAPPING_SUMMARY_COLLAPSED_ROWS,
  canCollapseMapping,
  mappingSummary,
  visibleMappingRows,
} from "./mapping-summary";
import type { MappingSummaryJob } from "./mapping-summary";

function field(name: string, caption: string, dataType = "text"): FieldDto {
  return {
    name,
    caption,
    dataType,
    maxLength: null,
    isSupported: true,
    canBeKey: true,
  };
}

const FIELDS: FieldDto[] = [
  field("ContactEmail", "ContactEmail"),
  field("ContactFirstName", "First name"),
  field("ContactBusinessPhone", "ContactBusinessPhone"),
  field("ContactBirthday", "Birthday", "datetime"),
  field("CustomerID", "Customer number"),
];

function job(overrides: Partial<MappingSummaryJob> = {}): MappingSummaryJob {
  return {
    mapping: [
      { source: "Email", target: "ContactEmail" },
      { source: "First Name", target: "ContactFirstName" },
      { source: "Phone", target: "ContactBusinessPhone" },
    ],
    keyField: "ContactEmail",
    dateFormat: "dmy",
    consentMapping: null,
    ...overrides,
  };
}

describe("mappingSummary", () => {
  it("lists the imported columns in file order with readable field labels", () => {
    const summary = mappingSummary(job(), FIELDS);

    expect(summary.rows).toEqual([
      { column: "Email", field: "ContactEmail", label: "Email", isKey: true },
      {
        column: "First Name",
        field: "ContactFirstName",
        label: "First name",
        isKey: false,
      },
      {
        column: "Phone",
        field: "ContactBusinessPhone",
        label: "Business phone",
        isKey: false,
      },
    ]);
  });

  it("marks the match field and gives its label", () => {
    const summary = mappingSummary(
      job({
        keyField: "CustomerID",
        mapping: [
          { source: "Id", target: "CustomerID" },
          { source: "Email", target: "ContactEmail" },
        ],
      }),
      FIELDS,
    );

    expect(
      summary.rows.filter((row) => row.isKey).map((row) => row.column),
    ).toEqual(["Id"]);
    expect(summary.key).toEqual({
      field: "CustomerID",
      label: "Customer number",
    });
  });

  it("has no match field when the job has none", () => {
    expect(mappingSummary(job({ keyField: "" }), FIELDS).key).toBeNull();
  });

  it("shows the field name for a field the configuration does not know", () => {
    const summary = mappingSummary(
      job({ mapping: [{ source: "X", target: "GoneField" }] }),
      FIELDS,
    );

    expect(summary.rows[0]).toMatchObject({
      field: "GoneField",
      label: "GoneField",
    });
  });

  it("skips entries without a target", () => {
    const summary = mappingSummary(
      job({
        mapping: [
          { source: "Email", target: "ContactEmail" },
          { source: "Notes", target: "" },
        ],
      }),
      FIELDS,
    );

    expect(summary.rows.map((row) => row.column)).toEqual(["Email"]);
  });

  it("shows the date format only when a date field is mapped", () => {
    expect(mappingSummary(job(), FIELDS).dateFormat).toBeNull();

    const withDate = job({
      mapping: [
        ...job().mapping,
        { source: "Born", target: "ContactBirthday" },
      ],
    });
    expect(mappingSummary(withDate, FIELDS).dateFormat).toBe("dmy");
  });

  it("shows the date format when the consent date column is mapped", () => {
    const summary = mappingSummary(
      job({ consentMapping: { agreed: "Opt in", date: "Opt in date" } }),
      FIELDS,
    );

    expect(summary.dateFormat).toBe("dmy");
  });

  it("gives the consent columns when mapped", () => {
    expect(mappingSummary(job(), FIELDS)).toMatchObject({
      consentAgreed: null,
      consentDate: null,
    });
    expect(
      mappingSummary(
        job({ consentMapping: { agreed: "Opt in", date: null } }),
        FIELDS,
      ),
    ).toMatchObject({ consentAgreed: "Opt in", consentDate: null });
    expect(
      mappingSummary(
        job({ consentMapping: { agreed: "Opt in", date: "When" } }),
        FIELDS,
      ),
    ).toMatchObject({ consentAgreed: "Opt in", consentDate: "When" });
  });
});

describe("visibleMappingRows", () => {
  const rows = (amount: number) =>
    mappingSummary(
      job({
        mapping: Array.from({ length: amount }, (_, index) => ({
          source: `Column ${index}`,
          target: "ContactFirstName",
        })),
      }),
      FIELDS,
    ).rows;

  it("shows every row up to the limit", () => {
    const few = rows(MAPPING_SUMMARY_COLLAPSED_ROWS);

    expect(canCollapseMapping(few)).toBe(false);
    expect(visibleMappingRows(few, false)).toHaveLength(
      MAPPING_SUMMARY_COLLAPSED_ROWS,
    );
  });

  it("collapses more rows until expanded", () => {
    const many = rows(MAPPING_SUMMARY_COLLAPSED_ROWS + 3);

    expect(canCollapseMapping(many)).toBe(true);
    expect(visibleMappingRows(many, false).map((row) => row.column)).toEqual(
      many.slice(0, MAPPING_SUMMARY_COLLAPSED_ROWS).map((row) => row.column),
    );
    expect(visibleMappingRows(many, true)).toHaveLength(
      MAPPING_SUMMARY_COLLAPSED_ROWS + 3,
    );
  });
});

import { isDateDataType } from "../import-core";
import type { DateFormat } from "../import-core";
import type { FieldDto, JobDto } from "../shared/commands";

import { fieldLabel } from "./model";

/** The part of the job the Review mapping card is built from. */
export type MappingSummaryJob = Pick<
  JobDto,
  "mapping" | "keyField" | "dateFormat" | "consentMapping"
>;

/** One imported file column and the contact field it goes to. */
export interface MappingSummaryRow {
  readonly column: string;
  /** Column name of the contact field. */
  readonly field: string;
  /** Readable name of the contact field (`fieldLabel`); the field name when the field is not known. */
  readonly label: string;
  /** The field existing contacts are matched by. */
  readonly isKey: boolean;
}

export interface MappingSummary {
  /** Imported columns in file order. Columns that are not imported are not stored, so not listed. */
  readonly rows: readonly MappingSummaryRow[];
  /** The match field; `null` when the job has none. */
  readonly key: { readonly field: string; readonly label: string } | null;
  /** Date format of the file; `null` when no mapped column is read as a date. */
  readonly dateFormat: DateFormat | null;
  /** Consent columns; `null` when not mapped. */
  readonly consentAgreed: string | null;
  readonly consentDate: string | null;
}

/** Rows shown before "Show all". */
export const MAPPING_SUMMARY_COLLAPSED_ROWS = 8;

/** Read-only summary of the saved mapping for the Review step. */
export function mappingSummary(
  job: MappingSummaryJob,
  fields: readonly FieldDto[],
): MappingSummary {
  const byName = new Map(fields.map((field) => [field.name, field]));
  const labelOf = (name: string) => {
    const field = byName.get(name);

    return field ? fieldLabel(field) : name;
  };
  const rows = job.mapping
    .filter((entry) => entry.target !== "")
    .map((entry) => ({
      column: entry.source,
      field: entry.target,
      label: labelOf(entry.target),
      isKey: entry.target === job.keyField,
    }));
  const consentAgreed = job.consentMapping?.agreed || null;
  const consentDate = job.consentMapping?.date || null;
  // Same rule as the Map step: the date format applies to date fields and to the consent date column.
  const readsDates =
    consentDate !== null ||
    rows.some((row) => isDateDataType(byName.get(row.field)?.dataType));

  return {
    rows,
    key:
      job.keyField !== ""
        ? { field: job.keyField, label: labelOf(job.keyField) }
        : null,
    dateFormat: readsDates ? job.dateFormat : null,
    consentAgreed,
    consentDate,
  };
}

/** Rows to show: all when expanded or few, otherwise the first `MAPPING_SUMMARY_COLLAPSED_ROWS`. */
export function visibleMappingRows(
  rows: readonly MappingSummaryRow[],
  expanded: boolean,
): readonly MappingSummaryRow[] {
  return expanded || rows.length <= MAPPING_SUMMARY_COLLAPSED_ROWS
    ? rows
    : rows.slice(0, MAPPING_SUMMARY_COLLAPSED_ROWS);
}

/** The card offers "Show all" / "Show fewer". */
export function canCollapseMapping(
  rows: readonly MappingSummaryRow[],
): boolean {
  return rows.length > MAPPING_SUMMARY_COLLAPSED_ROWS;
}

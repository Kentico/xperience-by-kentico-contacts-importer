import { EMAIL_FIELD } from "./contract";
import type { FieldMapping, TargetField } from "./contract";
import { isValidEmail, normalizeEmail } from "./normalization";
import type { RecordSource, SourceFormat, SourceInfo } from "./source";

export interface ProfileOptions {
  /** Rows to profile. Default 1000. */
  maxRows?: number;
  /** Rows returned for the preview table. Default 20. */
  sampleRows?: number;
  /** Mappable contact fields (from the server) used for mapping suggestions. */
  targetFields?: readonly TargetField[];
}

export interface ColumnProfile {
  index: number;
  name: string;
  /** Sampled rows with a non-empty (after trim) value. */
  filled: number;
  /** `filled` / rows sampled, 0-1. */
  fillRate: number;
  /** Non-empty values that are valid emails. */
  validEmails: number;
  /** `validEmails` / `filled`, 0-1 (0 when the column is empty). */
  emailRate: number;
  /** Up to 3 distinct non-empty values. */
  examples: string[];
}

export interface SampleRow {
  rowNumber: number;
  /** Raw values, padded / cut to the column count. */
  values: string[];
}

export interface SampleProfile {
  format: SourceFormat;
  info: SourceInfo;
  columns: ColumnProfile[];
  sampleRows: SampleRow[];
  /** Rows profiled (parse errors excluded). */
  rowsSampled: number;
  /** Sampled rows that could not be parsed. */
  parseErrors: number;
  /** Sampled rows whose value count differs from the column count. */
  raggedRows: number;
  /** True when the sample covers the whole file. */
  complete: boolean;
  bytesRead: number;
  totalBytes: number;
  /** Row count of the file: exact when `complete`, otherwise estimated from bytes per row. */
  estimatedTotalRows: number;
  /** Suggested mapping (see `suggestMapping`). */
  suggestions: FieldMapping[];
}

const MAX_EXAMPLES = 3;
const EMAIL_COLUMN_MIN_RATE = 0.8;

/** Well-known contact fields -> source column names that usually mean the same thing (simplified form). */
const SYNONYMS: Readonly<Record<string, readonly string[]>> = {
  ContactEmail: ["email", "emailaddress", "mail", "emailadresse"],
  ContactFirstName: ["firstname", "first", "givenname", "forename", "fname"],
  ContactLastName: ["lastname", "last", "surname", "familyname", "lname"],
  ContactMiddleName: ["middlename", "middle"],
  ContactBusinessPhone: [
    "phone",
    "phonenumber",
    "businessphone",
    "telephone",
    "tel",
    "workphone",
  ],
  ContactMobilePhone: [
    "mobile",
    "mobilephone",
    "cell",
    "cellphone",
    "mobilenumber",
  ],
  ContactCompanyName: [
    "company",
    "companyname",
    "organization",
    "organisation",
    "account",
  ],
  ContactJobTitle: ["jobtitle", "title", "position", "role"],
  ContactAddress1: [
    "address",
    "address1",
    "street",
    "streetaddress",
    "addressline1",
  ],
  ContactCity: ["city", "town"],
  ContactZIP: ["zip", "zipcode", "postalcode", "postcode"],
  ContactBirthday: ["birthday", "birthdate", "dateofbirth", "dob"],
  ContactGender: ["gender", "sex"],
  ContactNotes: ["notes", "note", "comment", "comments"],
};

/** Lowercase letters and digits only: `E-mail Address` -> `emailaddress`. */
function simplify(name: string): string {
  return name.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, "");
}

/**
 * Suggests a target field for source columns by name: the field name, the name without the
 * `Contact` prefix, the caption, then common synonyms. A column with mostly valid emails is
 * suggested for `ContactEmail` when no column name matched it. Each target and each column is
 * used at most once; columns without a match are left out.
 */
export function suggestMapping(
  columns: readonly Pick<ColumnProfile, "name" | "emailRate" | "filled">[],
  targetFields: readonly TargetField[],
): FieldMapping[] {
  const exact = new Map<string, string>();
  const loose = new Map<string, string>();
  const register = (
    map: Map<string, string>,
    alias: string,
    target: string,
  ) => {
    const key = simplify(alias);
    if (key.length > 0 && !map.has(key)) {
      map.set(key, target);
    }
  };

  for (const field of targetFields) {
    register(exact, field.name, field.name);
    if (/^contact./i.test(field.name)) {
      register(exact, field.name.slice("contact".length), field.name);
    }
    if (field.caption) {
      register(exact, field.caption, field.name);
    }
  }
  for (const field of targetFields) {
    for (const synonym of SYNONYMS[field.name] ?? []) {
      register(loose, synonym, field.name);
    }
  }

  const byColumn = new Map<string, string>();
  const usedTargets = new Set<string>();
  for (const aliases of [exact, loose]) {
    for (const column of columns) {
      if (byColumn.has(column.name)) {
        continue;
      }

      const target = aliases.get(simplify(column.name));
      if (target !== undefined && !usedTargets.has(target)) {
        byColumn.set(column.name, target);
        usedTargets.add(target);
      }
    }
  }

  if (
    !usedTargets.has(EMAIL_FIELD) &&
    targetFields.some((f) => f.name === EMAIL_FIELD)
  ) {
    let best: (typeof columns)[number] | undefined;
    for (const column of columns) {
      if (
        !byColumn.has(column.name) &&
        column.filled > 0 &&
        column.emailRate >= EMAIL_COLUMN_MIN_RATE &&
        (!best || column.emailRate > best.emailRate)
      ) {
        best = column;
      }
    }
    if (best) {
      byColumn.set(best.name, EMAIL_FIELD);
    }
  }

  const suggestions: FieldMapping[] = [];
  for (const column of columns) {
    const target = byColumn.get(column.name);
    if (target !== undefined) {
      suggestions.push({ source: column.name, target });
    }
  }

  return suggestions;
}

/**
 * Profiles the first rows of a source (and consumes it: open a new source for the full pass).
 * Reads only as much of the file as the sample needs.
 */
export async function profileSample(
  source: RecordSource,
  options: ProfileOptions = {},
): Promise<SampleProfile> {
  const maxRows = options.maxRows ?? 1000;
  const maxSampleRows = options.sampleRows ?? 20;
  const filled: number[] = [];
  const validEmails: number[] = [];
  const examples: string[][] = [];
  const sampleRows: SampleRow[] = [];
  let rowsSampled = 0;
  let rowsSeen = 0;
  let parseErrors = 0;
  let raggedRows = 0;
  let complete = true;
  let bytesRead = 0;

  for await (const batch of source.batches()) {
    rowsSeen += batch.length;
    bytesRead = source.bytesRead;

    for (const row of batch) {
      if (rowsSampled + parseErrors >= maxRows) {
        break;
      }
      if (row.error) {
        parseErrors++;
        continue;
      }

      rowsSampled++;
      const values = row.values;
      const columnCount = source.columns.length;
      if (values.length !== columnCount && !source.dynamicColumns) {
        raggedRows++;
      }
      for (let i = 0; i < columnCount; i++) {
        const value = (values[i] ?? "").trim();
        if (value.length === 0) {
          continue;
        }

        filled[i] = (filled[i] ?? 0) + 1;
        if (isValidEmail(normalizeEmail(value))) {
          validEmails[i] = (validEmails[i] ?? 0) + 1;
        }
        const columnExamples = (examples[i] ??= []);
        if (
          columnExamples.length < MAX_EXAMPLES &&
          !columnExamples.includes(value)
        ) {
          columnExamples.push(value);
        }
      }
      if (sampleRows.length < maxSampleRows) {
        sampleRows.push({ rowNumber: row.rowNumber, values });
      }
    }

    if (rowsSampled + parseErrors >= maxRows) {
      complete = false;
      break;
    }
  }

  const names = source.columns;
  const columns: ColumnProfile[] = names.map((name, index) => {
    const columnFilled = filled[index] ?? 0;
    const columnEmails = validEmails[index] ?? 0;

    return {
      index,
      name,
      filled: columnFilled,
      fillRate: rowsSampled > 0 ? columnFilled / rowsSampled : 0,
      validEmails: columnEmails,
      emailRate: columnFilled > 0 ? columnEmails / columnFilled : 0,
      examples: examples[index] ?? [],
    };
  });
  for (const row of sampleRows) {
    row.values = names.map((_, i) => row.values[i] ?? "");
  }

  const totalBytes = source.totalBytes;
  const estimatedTotalRows =
    complete || bytesRead === 0
      ? rowsSeen
      : Math.max(rowsSeen, Math.round((rowsSeen / bytesRead) * totalBytes));

  return {
    format: source.format,
    info: source.info,
    columns,
    sampleRows,
    rowsSampled,
    parseErrors,
    raggedRows,
    complete,
    bytesRead,
    totalBytes,
    estimatedTotalRows,
    suggestions: options.targetFields
      ? suggestMapping(columns, options.targetFields)
      : [],
  };
}

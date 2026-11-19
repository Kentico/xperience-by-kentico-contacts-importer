/**
 * Wire contract shared with the server (see .agent-resources/contacts-importer/implementation-spec-1.md).
 * JSON is camelCase everywhere. Keep in sync with the C# job models.
 */

/** Contact field that holds the email address; lowercased when used as the dedupe key. */
export const EMAIL_FIELD = "ContactEmail";

/** Maximum length of a normalized dedupe key (server table-valued parameter column is nvarchar(450)). */
export const MAX_KEY_LENGTH = 450;

/** Maximum length of an email address. */
export const MAX_EMAIL_LENGTH = 254;

/** Maximum size of one chunk record (one NDJSON line, UTF-8); the server rejects chunks with longer records. */
export const MAX_RECORD_BYTES = 1024 * 1024;

/** Only flag defined so far: email is invalid and is not the dedupe key (D34). */
export const INVALID_EMAIL_FLAG = "invalidEmail";

export type RecordFlag = typeof INVALID_EMAIL_FLAG;

/** One line of a `chunks/{n}.ndjson.gz` file. */
export interface ChunkRecord {
  /** 1-based data row number in the source file (header not counted). */
  r: number;
  /** Normalized dedupe key, 1-450 chars. */
  k: string;
  /** Target contact field name -> trimmed value. Mapped fields only; always includes the key field. */
  v: Record<string, string>;
  /** Flags; omitted when empty. */
  f?: RecordFlag[];
  /** Consent columns of the row; on every record when the job has a `consentMapping`, otherwise never. */
  c?: ConsentRecord;
}

/** Consent columns of one row (`ChunkRecord.c`). */
export interface ConsentRecord {
  /**
   * The "consent given" column says yes; false when it holds anything else or nothing. When only a
   * consent date column is mapped, every row counts as consent given (same rule as the server model).
   */
  a: boolean;
  /** Consent date as ISO 8601 date or date-time; omitted when the column is not mapped or the value is empty. */
  d?: string;
}

/**
 * `job.json` consent mapping: source columns that say whether and when the contact of a row gave
 * consent. Not contact fields; used only with the consent option "column override".
 */
export interface ConsentMapping {
  /** Source column that says whether consent was given. Not set: every row counts as consent given. */
  agreed?: string | null;
  /** Source column with the date consent was given. */
  date?: string | null;
}

/** Name used for the consent date column where a target field name is reported (rejected rows). */
export const CONSENT_DATE_FIELD = "consent date";

/** Values of the "consent given" column that mean yes (after trim, any letter case). Everything else is no. */
export const CONSENT_YES_VALUES: readonly string[] = [
  "y",
  "yes",
  "true",
  "1",
  "x",
];

/** `UPLOAD_CHUNK` page command payload. */
export interface UploadChunkPayload {
  jobId: string;
  /** 0-based chunk index, file order. */
  index: number;
  /** Lowercase hex SHA-256 of the gzipped bytes. */
  sha256: string;
  /** Number of records in the chunk. */
  rows: number;
  /** Base64 of the gzipped NDJSON. */
  data: string;
}

/** `job.json` mapping entry: source column name -> target contact field name. */
export interface FieldMapping {
  source: string;
  target: string;
}

/**
 * Date format of the source values for date/datetime target fields.
 * ISO 8601 input is always accepted; `dmy` / `mdy` add day-first / month-first input.
 */
export type DateFormat = "iso" | "dmy" | "mdy";

/** `job.json` file descriptor. */
export interface JobFile {
  name: string;
  size: number;
  fingerprint: string;
}

/** `status.json` profile, also sent with `SAVE_PROFILE`. */
export interface ImportProfile {
  rowsRead: number;
  rowsValid: number;
  rowsRejected: number;
  likelyDuplicates: number;
  chunkCount: number;
}

/** Mappable contact field, as provided by the server schema provider. */
export interface TargetField {
  /** Field (column) name, e.g. `ContactEmail`. */
  name: string;
  caption?: string;
  /** Xperience data type, e.g. `text`, `date`, `datetime`. */
  dataType?: string;
  /** Maximum value length for text fields. */
  maxLength?: number;
}

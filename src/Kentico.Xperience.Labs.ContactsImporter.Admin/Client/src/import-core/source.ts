/** Formats with a parser. A `.zip` is a container of one file in one of these formats. */
export type SourceFormat = "csv" | "ndjson" | "json" | "xlsx";

/** Why a row could not be parsed. Such rows are yielded (with empty values) so callers can report them. */
export type RowErrorCode = "unterminatedQuote" | "invalidJson" | "notAnObject";

export interface SourceRow {
  /** 1-based data row number in the source file. The header is not counted; blank lines are. */
  rowNumber: number;
  /**
   * Raw values by column index (see `RecordSource.columns`). May be shorter or longer than
   * `columns` for ragged rows; read missing values as empty.
   */
  values: string[];
  /** Set when the row could not be parsed. */
  error?: RowErrorCode;
}

export interface SourceInfo {
  /** Encoding label used to decode the file. */
  encoding: string;
  /** Whether the file was gzip-compressed. */
  compressed: boolean;
  /** CSV only: delimiter in use. */
  delimiter?: string;
  /** CSV only: whether the delimiter was detected (false = override or fallback). */
  delimiterDetected?: boolean;
  /** CSV only: whether the first record is a header. */
  hasHeader?: boolean;
  /** Name of the file read from a `.zip` container. */
  zipEntry?: string;
  /** XLSX only: the sheet being read. */
  sheet?: string;
  /** XLSX only: names of all worksheets, in workbook order. */
  sheets?: string[];
}

/**
 * Streaming, single-pass reader of tabular records. Open a new source for another pass.
 * Rows are produced only as fast as the consumer pulls them, so memory use stays bounded.
 */
export interface RecordSource {
  readonly format: SourceFormat;
  /** Column names; values of a row are aligned with this list by index. */
  readonly columns: readonly string[];
  /** True when `columns` can grow while rows are read (NDJSON: keys first seen in later rows). */
  readonly dynamicColumns: boolean;
  /** Size of the file in bytes. */
  readonly totalBytes: number;
  /**
   * Bytes of the file read so far (compressed bytes for `.gz`); compare with `totalBytes` for
   * progress. Zip entries and XLSX sheets scale the compressed bytes read of the entry to the file size.
   */
  readonly bytesRead: number;
  readonly info: SourceInfo;
  /** Rows in file order, one batch per decoded stream chunk. Can be iterated once. */
  batches(): AsyncGenerator<SourceRow[], void, void>;
  /** Rows in file order. Can be iterated once (shares the pass with `batches`). */
  rows(): AsyncGenerator<SourceRow, void, void>;
  /** Stops reading and releases the underlying stream. */
  cancel(): Promise<void>;
}

/** The subset of `File` / `Blob` the import core needs. A browser or Node `File` satisfies it. */
export interface FileLike {
  readonly name?: string;
  readonly size: number;
  stream(): ReadableStream<Uint8Array>;
  slice(start?: number, end?: number): { arrayBuffer(): Promise<ArrayBuffer> };
}

export type ImportErrorCode =
  | "unsupportedFormat"
  | "unsupportedEncoding"
  | "recordTooLarge"
  | "invalidConfig"
  | "sourceConsumed"
  /** JSON array: the file is not valid JSON (`item`: 1-based array item where it broke). */
  | "invalidJson"
  /** JSON array: the file does not start with `[` (e.g. NDJSON saved as `.json`). */
  | "jsonNotArray"
  /** The zip file is damaged or not a zip file. */
  | "invalidZip"
  /** The zip holds no data file. */
  | "zipEmpty"
  /** The zip holds more than one data file (`count`). */
  | "zipMultipleEntries"
  /** The zip entry is encrypted. */
  | "zipEncrypted"
  /** The zip entry uses a compression method other than stored / deflate (`method`). */
  | "unsupportedCompression"
  /** The file is not a readable XLSX workbook. */
  | "invalidXlsx"
  /** XLSX: the chosen sheet does not exist (`sheet`). */
  | "sheetNotFound"
  /** XLSX: more data rows than Excel allows (`maxRows`). */
  | "tooManyRows";

/**
 * Values that describe an `ImportError`: `encoding` (`unsupportedEncoding`); `record` (CSV),
 * `line` (NDJSON) or `item` (JSON array) and `maxChars` (`recordTooLarge`); `extension`
 * (`unsupportedFormat` of a known but unsupported type); `item` (`invalidJson`); `count`
 * (`zipMultipleEntries`); `method` (`unsupportedCompression`); `sheet` (`sheetNotFound`);
 * `maxRows` (`tooManyRows`). Never values of the file's rows.
 */
export type ImportErrorParams = Readonly<
  Partial<Record<string, string | number>>
>;

/**
 * Fatal error of the import core (as opposed to a rejected row). `message` is for developers
 * (English, for logs and tests); the UI shows its own text for `code` and `params`.
 */
export class ImportError extends Error {
  readonly code: ImportErrorCode;
  readonly params: ImportErrorParams;

  constructor(
    code: ImportErrorCode,
    message: string,
    params: ImportErrorParams = {},
  ) {
    super(message);
    this.name = "ImportError";
    this.code = code;
    this.params = params;
  }
}

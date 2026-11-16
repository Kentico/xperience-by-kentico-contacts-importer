/** Formats with a parser in this slice. JSON array / XLSX / zip arrive in slice 5 (see `openRecordSource`). */
export type SourceFormat = 'csv' | 'ndjson';

/** Why a row could not be parsed. Such rows are yielded (with empty values) so callers can report them. */
export type RowErrorCode = 'unterminatedQuote' | 'invalidJson' | 'notAnObject';

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
  /** Bytes of the file read so far (compressed bytes for `.gz`); compare with `totalBytes` for progress. */
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
  | 'unsupportedFormat'
  | 'unsupportedEncoding'
  | 'recordTooLarge'
  | 'invalidConfig'
  | 'sourceConsumed';

/** Fatal error of the import core (as opposed to a rejected row). */
export class ImportError extends Error {
  readonly code: ImportErrorCode;

  constructor(code: ImportErrorCode, message: string) {
    super(message);
    this.name = 'ImportError';
    this.code = code;
  }
}

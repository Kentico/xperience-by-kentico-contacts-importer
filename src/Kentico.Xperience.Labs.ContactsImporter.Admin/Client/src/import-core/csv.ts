import { ImportError } from './source';
import type { RowErrorCode, SourceRow } from './source';
import type { TextFormatReader } from './text-source';

export const DELIMITER_CANDIDATES: readonly string[] = [',', ';', '\t', '|'];

/** Default cap on the characters of one record; a larger record means a broken file (unbalanced quote). */
export const DEFAULT_MAX_RECORD_CHARS = 1_048_576;

export interface CsvRawRecord {
  /** 0-based index of the record in the file; blank lines are counted but not emitted. */
  index: number;
  fields: string[];
  error?: RowErrorCode;
}

export interface CsvParserOptions {
  /** Single character. Default `,`. */
  delimiter?: string;
  /** Single character, or an empty string to turn quoting off. Default `"`. */
  quote?: string;
  maxRecordChars?: number;
}

/**
 * Incremental CSV parser (RFC 4180, lenient). Text can be pushed in pieces split at any position.
 * - Records end with CRLF, LF or CR; line breaks and delimiters inside quoted fields are data.
 * - A quote opens a quoted field only at the start of a field; `""` inside is a literal quote.
 * - Blank lines are skipped (still counted in `index`).
 */
export class CsvParser {
  private readonly delimiterCode: number;
  private readonly quote: string;
  private readonly quoteCode: number;
  private readonly maxRecordChars: number;

  private field = '';
  private row: string[] = [];
  private rowChars = 0;
  private inQuotes = false;
  /** The current field started with a quote (stays set after the closing quote). */
  private fieldQuoted = false;
  /** A quote ended the previous piece inside a quoted field: closing or first half of `""`. */
  private quotePending = false;
  /** A CR ended the previous piece: a leading LF belongs to the same line break. */
  private afterCarriageReturn = false;
  private index = 0;

  constructor(options: CsvParserOptions = {}) {
    const delimiter = options.delimiter ?? ',';
    const quote = options.quote ?? '"';
    if (delimiter.length !== 1) {
      throw new ImportError('invalidConfig', 'CSV delimiter must be a single character.');
    }
    if (quote.length > 1 || quote === delimiter) {
      throw new ImportError(
        'invalidConfig',
        'CSV quote must be a single character different from the delimiter.',
      );
    }

    this.delimiterCode = delimiter.charCodeAt(0);
    this.quote = quote;
    this.quoteCode = quote.length === 1 ? quote.charCodeAt(0) : -1;
    this.maxRecordChars = options.maxRecordChars ?? DEFAULT_MAX_RECORD_CHARS;
  }

  /** Parses the next piece of text and appends the completed records to `out`. */
  push(text: string, out: CsvRawRecord[]): void {
    const length = text.length;
    let i = 0;
    let start = 0;

    if (this.quotePending && length > 0) {
      this.quotePending = false;
      if (text.charCodeAt(0) === this.quoteCode) {
        this.field += this.quote;
        i = 1;
        start = 1;
      } else {
        this.inQuotes = false;
      }
    }
    if (this.afterCarriageReturn && length > 0) {
      this.afterCarriageReturn = false;
      if (text.charCodeAt(0) === 10) {
        i = 1;
        start = 1;
      }
    }

    while (i < length) {
      if (this.inQuotes) {
        const quoteAt = text.indexOf(this.quote, i);
        if (quoteAt === -1) {
          i = length;
          break;
        }

        this.field += text.slice(start, quoteAt);
        if (quoteAt + 1 === length) {
          this.quotePending = true;
          i = length;
          start = length;
          break;
        }
        if (text.charCodeAt(quoteAt + 1) === this.quoteCode) {
          this.field += this.quote;
          i = quoteAt + 2;
        } else {
          this.inQuotes = false;
          i = quoteAt + 1;
        }
        start = i;
        continue;
      }

      const code = text.charCodeAt(i);
      if (code === this.delimiterCode) {
        this.endField(text.slice(start, i));
        i++;
        start = i;
      } else if (code === 10 || code === 13) {
        this.field += text.slice(start, i);
        this.endRecord(out);
        i++;
        if (code === 13) {
          if (i === length) {
            this.afterCarriageReturn = true;
          } else if (text.charCodeAt(i) === 10) {
            i++;
          }
        }
        start = i;
      } else if (
        code === this.quoteCode &&
        i === start &&
        this.field.length === 0 &&
        !this.fieldQuoted
      ) {
        this.inQuotes = true;
        this.fieldQuoted = true;
        i++;
        start = i;
      } else {
        i++;
      }
    }

    if (start < length) {
      this.field += text.slice(start, length);
    }
    if (this.rowChars + this.field.length > this.maxRecordChars) {
      throw new ImportError(
        'recordTooLarge',
        `Record ${this.index + 1} is larger than ${this.maxRecordChars} characters. ` +
          'The file probably has an unbalanced quote or the wrong delimiter.',
        { record: this.index + 1, maxChars: this.maxRecordChars },
      );
    }
  }

  /** Signals the end of input and appends the last record, if any, to `out`. */
  end(out: CsvRawRecord[]): void {
    this.afterCarriageReturn = false;
    if (this.quotePending) {
      this.quotePending = false;
      this.inQuotes = false;
    }

    if (this.inQuotes) {
      this.inQuotes = false;
      this.endRecord(out, 'unterminatedQuote');
    } else if (this.row.length > 0 || this.field.length > 0 || this.fieldQuoted) {
      this.endRecord(out);
    }
  }

  private endField(tail: string): void {
    const value = this.field + tail;
    this.row.push(value);
    this.rowChars += value.length;
    this.field = '';
    this.fieldQuoted = false;
  }

  private endRecord(out: CsvRawRecord[], error?: RowErrorCode): void {
    const blank =
      this.row.length === 0 && this.field.length === 0 && !this.fieldQuoted;
    if (!blank) {
      this.row.push(this.field);
      const record: CsvRawRecord = { index: this.index, fields: this.row };
      if (error) {
        record.error = error;
      }
      out.push(record);
    }

    this.index++;
    this.row = [];
    this.rowChars = 0;
    this.field = '';
    this.fieldQuoted = false;
  }
}

export interface DetectDelimiterOptions {
  candidates?: readonly string[];
  quote?: string;
  /** The sample is the whole file (the last record is complete even without a line break). */
  complete?: boolean;
  /** Records to look at. Default 50. */
  maxRecords?: number;
}

/**
 * Picks the delimiter that splits the sample records into the most consistent number (> 1) of
 * fields; ties go to more fields, then candidate order. Returns `null` when no candidate splits.
 */
export function detectDelimiter(
  sample: string,
  options: DetectDelimiterOptions = {},
): string | null {
  const candidates = options.candidates ?? DELIMITER_CANDIDATES;
  const maxRecords = options.maxRecords ?? 50;
  let best: string | null = null;
  let bestConsistency = 0;
  let bestFields = 0;

  for (const delimiter of candidates) {
    const parser = new CsvParser({
      delimiter,
      quote: options.quote,
      maxRecordChars: Number.MAX_SAFE_INTEGER,
    });
    const records: CsvRawRecord[] = [];
    parser.push(sample, records);
    if (options.complete || records.length === 0) {
      parser.end(records);
    }
    if (records.length === 0) {
      continue;
    }

    const counts = new Map<number, number>();
    const considered = Math.min(records.length, maxRecords);
    for (let i = 0; i < considered; i++) {
      const fields = records[i].fields.length;
      counts.set(fields, (counts.get(fields) ?? 0) + 1);
    }

    let modeFields = 0;
    let modeCount = 0;
    for (const [fields, count] of counts) {
      if (count > modeCount || (count === modeCount && fields > modeFields)) {
        modeFields = fields;
        modeCount = count;
      }
    }
    if (modeFields < 2) {
      continue;
    }

    const consistency = modeCount / considered;
    if (
      consistency > bestConsistency ||
      (consistency === bestConsistency && modeFields > bestFields)
    ) {
      best = delimiter;
      bestConsistency = consistency;
      bestFields = modeFields;
    }
  }

  return best;
}

/** Trims header names, names empty ones `Column N` and makes duplicates unique (`Email (2)`). */
export function normalizeHeaders(fields: readonly string[]): string[] {
  const used = new Set<string>();

  return fields.map((field, i) => {
    const base = field.trim() || `Column ${i + 1}`;
    let name = base;
    for (let n = 2; used.has(name); n++) {
      name = `${base} (${n})`;
    }
    used.add(name);

    return name;
  });
}

export interface CsvReaderOptions {
  /** Delimiter override; detected from the sample when not set. */
  delimiter?: string;
  /** Used when detection finds nothing (e.g. single-column file). Default `,`. */
  fallbackDelimiter?: string;
  /** Quote character; empty string turns quoting off. Default `"`. */
  quote?: string;
  /** First non-blank record is the header. Default true. */
  hasHeader?: boolean;
  maxRecordChars?: number;
}

/** CSV/TSV format reader: dialect detection, header handling and row numbering on top of `CsvParser`. */
export class CsvReader implements TextFormatReader {
  readonly format = 'csv' as const;
  readonly dynamicColumns = false;
  columns: string[] = [];
  columnsReady = false;
  delimiter = ',';
  delimiterDetected = false;
  readonly hasHeader: boolean;

  private readonly options: CsvReaderOptions;
  private parser: CsvParser | undefined;
  private headerIndex = -1;
  private readonly raw: CsvRawRecord[] = [];

  constructor(options: CsvReaderOptions = {}) {
    this.options = options;
    this.hasHeader = options.hasHeader ?? true;
  }

  init(sample: string, complete: boolean): void {
    if (this.options.delimiter !== undefined) {
      this.delimiter = this.options.delimiter;
    } else {
      const detected = detectDelimiter(sample, {
        quote: this.options.quote,
        complete,
      });
      this.delimiterDetected = detected !== null;
      this.delimiter = detected ?? this.options.fallbackDelimiter ?? ',';
    }

    this.parser = new CsvParser({
      delimiter: this.delimiter,
      quote: this.options.quote,
      maxRecordChars: this.options.maxRecordChars,
    });
  }

  push(text: string, out: SourceRow[]): void {
    this.requireParser().push(text, this.raw);
    this.drain(out);
  }

  end(out: SourceRow[]): void {
    this.requireParser().end(this.raw);
    this.drain(out);
    this.columnsReady = true;
  }

  private requireParser(): CsvParser {
    if (!this.parser) {
      throw new ImportError('invalidConfig', 'CSV reader used before init.');
    }

    return this.parser;
  }

  private drain(out: SourceRow[]): void {
    const raw = this.raw;
    for (let i = 0; i < raw.length; i++) {
      const record = raw[i];
      if (!this.columnsReady) {
        this.columnsReady = true;
        if (this.hasHeader) {
          this.columns = normalizeHeaders(record.fields);
          this.headerIndex = record.index;
          continue;
        }
        this.columns = record.fields.map((_, n) => `Column ${n + 1}`);
      }

      const row: SourceRow = {
        rowNumber: record.index - this.headerIndex,
        values: record.fields,
      };
      if (record.error) {
        row.error = record.error;
      }
      out.push(row);
    }
    raw.length = 0;
  }
}

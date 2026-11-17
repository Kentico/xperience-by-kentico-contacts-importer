import { DEFAULT_MAX_RECORD_CHARS } from './csv';
import { ImportError } from './source';
import type { SourceRow } from './source';
import type { TextFormatReader } from './text-source';

const MAX_FLATTEN_DEPTH = 16;

export interface NdjsonReaderOptions {
  /** Cap on the characters of one line. */
  maxRecordChars?: number;
}

/**
 * NDJSON / JSON Lines reader: one JSON object per line (LF or CRLF).
 * - Columns are the object keys in first-seen order; keys first seen in later rows are appended.
 * - Nested objects are flattened to dotted paths (`address.city`); arrays are kept as JSON text.
 * - Row number = line number (blank lines are skipped but counted).
 * - A line that is not a JSON object is yielded with `error` set.
 */
export class NdjsonReader implements TextFormatReader {
  readonly format = 'ndjson' as const;
  readonly dynamicColumns = true;
  readonly columns: string[] = [];
  columnsReady = false;

  private readonly maxRecordChars: number;
  private readonly columnIndex = new Map<string, number>();
  private carry = '';
  private line = 0;

  constructor(options: NdjsonReaderOptions = {}) {
    this.maxRecordChars = options.maxRecordChars ?? DEFAULT_MAX_RECORD_CHARS;
  }

  init(): void {
    // no dialect to detect
  }

  push(text: string, out: SourceRow[]): void {
    let start = 0;
    let newline = text.indexOf('\n');
    while (newline !== -1) {
      const piece = text.slice(start, newline);
      const line = this.carry.length > 0 ? this.carry + piece : piece;
      this.carry = '';
      this.parseLine(line, out);
      start = newline + 1;
      newline = text.indexOf('\n', start);
    }

    if (start < text.length) {
      this.carry += start === 0 ? text : text.slice(start);
    }
    if (this.carry.length > this.maxRecordChars) {
      throw new ImportError(
        'recordTooLarge',
        `Line ${this.line + 1} is larger than ${this.maxRecordChars} characters.`,
        { line: this.line + 1, maxChars: this.maxRecordChars },
      );
    }
  }

  end(out: SourceRow[]): void {
    if (this.carry.length > 0) {
      const line = this.carry;
      this.carry = '';
      this.parseLine(line, out);
    }
    this.columnsReady = true;
  }

  private parseLine(line: string, out: SourceRow[]): void {
    this.line++;
    const text = line.trim();
    if (text.length === 0) {
      return;
    }

    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch {
      out.push({ rowNumber: this.line, values: [], error: 'invalidJson' });
      this.columnsReady = true;

      return;
    }
    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
      out.push({ rowNumber: this.line, values: [], error: 'notAnObject' });
      this.columnsReady = true;

      return;
    }

    const values: string[] = new Array<string>(this.columns.length).fill('');
    this.flatten(parsed as Record<string, unknown>, '', values, 0);
    out.push({ rowNumber: this.line, values });
    this.columnsReady = true;
  }

  private flatten(
    object: Record<string, unknown>,
    prefix: string,
    values: string[],
    depth: number,
  ): void {
    for (const key of Object.keys(object)) {
      const value = object[key];
      const name = prefix + key;
      if (
        value !== null &&
        typeof value === 'object' &&
        !Array.isArray(value) &&
        depth < MAX_FLATTEN_DEPTH
      ) {
        this.flatten(value as Record<string, unknown>, `${name}.`, values, depth + 1);
        continue;
      }

      let index = this.columnIndex.get(name);
      if (index === undefined) {
        index = this.columns.length;
        this.columns.push(name);
        this.columnIndex.set(name, index);
      }
      while (values.length < index) {
        values.push('');
      }
      values[index] = toText(value);
    }
  }
}

function toText(value: unknown): string {
  if (value === null || value === undefined) {
    return '';
  }
  if (typeof value === 'string') {
    return value;
  }
  if (typeof value === 'object') {
    return JSON.stringify(value);
  }

  return String(value);
}

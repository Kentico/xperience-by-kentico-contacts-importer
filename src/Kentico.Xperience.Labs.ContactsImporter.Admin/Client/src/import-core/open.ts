import { CsvReader } from './csv';
import { NdjsonReader } from './ndjson';
import { ImportError } from './source';
import type { FileLike, RecordSource, SourceFormat } from './source';
import { TextRecordSource } from './text-source';

export interface OpenSourceOptions {
  /** Format override; detected from the file name when not set. */
  format?: SourceFormat;
  /** Gzip override; detected from the gzip magic bytes when not set. */
  compressed?: boolean;
  /** Encoding label for `TextDecoder` (e.g. `windows-1252`). Default `auto`: BOM, then UTF-8 validity. */
  encoding?: string;
  /** CSV: delimiter override (single character). Detected from `, ; tab |` when not set. */
  delimiter?: string;
  /** CSV: quote character; empty string turns quoting off. Default `"`. */
  quote?: string;
  /** CSV: first record is the header. Default true. */
  hasHeader?: boolean;
  /** Cap on the characters of one record / line. Default 1,048,576. */
  maxRecordChars?: number;
  /** Bytes read up front for encoding and delimiter detection. Default 64 KiB. */
  sampleBytes?: number;
}

const CSV_EXTENSIONS = new Set(['csv', 'tsv', 'tab', 'txt']);
const NDJSON_EXTENSIONS = new Set(['ndjson', 'jsonl']);

function extensionOf(name: string): string {
  const dot = name.lastIndexOf('.');

  return dot === -1 ? '' : name.slice(dot + 1).toLowerCase();
}

/** File name without a trailing `.gz`. */
function innerName(name: string): string {
  return extensionOf(name) === 'gz' ? name.slice(0, -3) : name;
}

/** Format by file name (`.gz` ignored), or `null` when the extension has no parser. */
export function detectFormat(fileName: string): SourceFormat | null {
  const extension = extensionOf(innerName(fileName));
  if (CSV_EXTENSIONS.has(extension)) {
    return 'csv';
  }
  if (NDJSON_EXTENSIONS.has(extension)) {
    return 'ndjson';
  }

  return null;
}

/** Whether the file starts with the gzip magic bytes. */
export async function isGzip(file: FileLike): Promise<boolean> {
  if (file.size < 2) {
    return false;
  }

  const head = new Uint8Array(await file.slice(0, 2).arrayBuffer());

  return head[0] === 0x1f && head[1] === 0x8b;
}

/**
 * Opens a streaming record source for a file. Resolves once the columns are known.
 *
 * Extension point (slice 5): JSON array, XLSX and zip get their own `RecordSource`
 * implementations and a branch here; callers only depend on `RecordSource`.
 */
export async function openRecordSource(
  file: FileLike,
  options: OpenSourceOptions = {},
): Promise<RecordSource> {
  const name = file.name ?? '';
  const format = options.format ?? detectFormat(name);
  if (!format) {
    throw new ImportError(
      'unsupportedFormat',
      `The format of '${name}' is not supported. Supported: CSV, TSV, NDJSON, optionally gzipped.`,
    );
  }

  const textOptions = {
    encoding: options.encoding,
    compressed: options.compressed ?? (await isGzip(file)),
    sampleBytes: options.sampleBytes,
  };

  if (format === 'ndjson') {
    return TextRecordSource.open(
      file,
      new NdjsonReader({ maxRecordChars: options.maxRecordChars }),
      textOptions,
    );
  }

  const extension = extensionOf(innerName(name));
  const reader = new CsvReader({
    delimiter: options.delimiter,
    fallbackDelimiter: extension === 'tsv' || extension === 'tab' ? '\t' : ',',
    quote: options.quote,
    hasHeader: options.hasHeader,
    maxRecordChars: options.maxRecordChars,
  });

  return TextRecordSource.open(file, reader, textOptions, () => ({
    delimiter: reader.delimiter,
    delimiterDetected: reader.delimiterDetected,
    hasHeader: reader.hasHeader,
  }));
}

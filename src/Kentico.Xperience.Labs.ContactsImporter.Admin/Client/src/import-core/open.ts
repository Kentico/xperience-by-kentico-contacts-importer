import { CsvReader } from "./csv";
import { JsonArrayReader } from "./json";
import { NdjsonReader } from "./ndjson";
import { ImportError } from "./source";
import type {
  FileLike,
  RecordSource,
  SourceFormat,
  SourceInfo,
  SourceRow,
} from "./source";
import { TextRecordSource } from "./text-source";
import type { TextFormatReader, TextSourceOptions } from "./text-source";
import { XlsxRecordSource } from "./xlsx";
import { ZipArchive, baseName, hasZipSignature, singleDataEntry } from "./zip";

export interface OpenSourceOptions {
  /** Format override (of the file inside a `.zip`); detected from the file name when not set. */
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
  /** XLSX: worksheet name. Default: the first visible worksheet. */
  sheet?: string;
  /** Cap on the characters of one record / line / JSON array item. Default 1,048,576. */
  maxRecordChars?: number;
  /** Bytes read up front for encoding and delimiter detection. Default 64 KiB. */
  sampleBytes?: number;
}

const CSV_EXTENSIONS = new Set(["csv", "tsv", "tab", "txt"]);
const NDJSON_EXTENSIONS = new Set(["ndjson", "jsonl"]);
/** Spreadsheet formats people try that have no reader: a clear "save as .xlsx or CSV" message. */
const UNSUPPORTED_SPREADSHEETS = new Set([
  "xls",
  "xlsb",
  "xlsm",
  "ods",
  "numbers",
]);
/** Containers that cannot be nested in a `.zip`. */
const NESTED_CONTAINERS = new Set(["zip", "gz", "7z", "rar"]);

function extensionOf(name: string): string {
  const dot = name.lastIndexOf(".");

  return dot === -1 ? "" : name.slice(dot + 1).toLowerCase();
}

/** File name without a trailing `.gz`. */
function innerName(name: string): string {
  return extensionOf(name) === "gz" ? name.slice(0, -3) : name;
}

/**
 * Format by file name (`.gz` ignored), or `null` when the extension has no parser. A `.zip` is a
 * container: its format is the format of the file inside (see `openRecordSource`).
 */
export function detectFormat(fileName: string): SourceFormat | null {
  const extension = extensionOf(innerName(fileName));
  if (CSV_EXTENSIONS.has(extension)) {
    return "csv";
  }
  if (NDJSON_EXTENSIONS.has(extension)) {
    return "ndjson";
  }
  if (extension === "json") {
    return "json";
  }
  if (extension === "xlsx") {
    return "xlsx";
  }

  return null;
}

async function readHead(file: FileLike, length: number): Promise<Uint8Array> {
  if (file.size === 0) {
    return new Uint8Array(0);
  }

  return new Uint8Array(
    await file.slice(0, Math.min(length, file.size)).arrayBuffer(),
  );
}

function startsWithGzip(head: Uint8Array): boolean {
  return head.length >= 2 && head[0] === 0x1f && head[1] === 0x8b;
}

/** Whether the file starts with the gzip magic bytes. */
export async function isGzip(file: FileLike): Promise<boolean> {
  return startsWithGzip(await readHead(file, 2));
}

function unsupported(name: string): ImportError {
  const extension = extensionOf(innerName(name));
  if (UNSUPPORTED_SPREADSHEETS.has(extension)) {
    return new ImportError(
      "unsupportedFormat",
      `'.${extension}' files are not supported. Save the sheet as .xlsx or CSV.`,
      { extension },
    );
  }

  return new ImportError(
    "unsupportedFormat",
    `The format of '${name}' is not supported. Supported: CSV, TSV, NDJSON, JSON, XLSX; text formats optionally gzipped; any of them in a zip.`,
  );
}

/** An OOXML package (XLSX) rather than a zip of data files. */
function isOfficePackage(archive: ZipArchive): boolean {
  return archive.find("[Content_Types].xml") !== undefined;
}

/**
 * Opens a streaming record source for a file. Resolves once the columns are known.
 * - CSV / TSV, NDJSON, JSON array: text readers, optionally gzipped.
 * - XLSX: zip package read with random access (`File.slice`), sheet streamed.
 * - `.zip` (detected from the zip signature): exactly one data file inside, read in its own
 *   format (`format` overrides it); progress counts the compressed bytes of that file.
 */
export async function openRecordSource(
  file: FileLike,
  options: OpenSourceOptions = {},
): Promise<RecordSource> {
  const name = file.name ?? "";
  const head = await readHead(file, 4);

  if (hasZipSignature(head)) {
    const archive = await ZipArchive.open(file);
    if (isOfficePackage(archive)) {
      return XlsxRecordSource.open(file, { sheet: options.sheet });
    }

    return openZipEntry(file, archive, options);
  }

  const format = options.format ?? detectFormat(name);
  if (!format) {
    throw unsupported(name);
  }

  const compressed = options.compressed ?? startsWithGzip(head);
  if (format === "xlsx") {
    if (compressed) {
      throw new ImportError(
        "unsupportedFormat",
        "Gzipped XLSX files are not supported. Use the .xlsx file as is (it is already compressed).",
      );
    }
    throw new ImportError(
      "invalidXlsx",
      "The file is not an Excel workbook (.xlsx).",
    );
  }

  return openText(file, name, format, options, {
    encoding: options.encoding,
    compressed,
    sampleBytes: options.sampleBytes,
  });
}

function openText(
  file: FileLike,
  name: string,
  format: Exclude<SourceFormat, "xlsx">,
  options: OpenSourceOptions,
  textOptions: TextSourceOptions,
): Promise<TextRecordSource> {
  if (format === "ndjson" || format === "json") {
    const reader: TextFormatReader =
      format === "ndjson"
        ? new NdjsonReader({ maxRecordChars: options.maxRecordChars })
        : new JsonArrayReader({ maxRecordChars: options.maxRecordChars });

    return TextRecordSource.open(file, reader, textOptions);
  }

  const extension = extensionOf(innerName(name));
  const reader = new CsvReader({
    delimiter: options.delimiter,
    fallbackDelimiter: extension === "tsv" || extension === "tab" ? "\t" : ",",
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

async function openZipEntry(
  file: FileLike,
  archive: ZipArchive,
  options: OpenSourceOptions,
): Promise<RecordSource> {
  const entry = singleDataEntry(archive);
  const entryName = baseName(entry.name);
  if (NESTED_CONTAINERS.has(extensionOf(entryName))) {
    throw new ImportError(
      "unsupportedFormat",
      "The file inside the zip is compressed again. Put the data file itself in the zip.",
    );
  }

  const format = options.format ?? detectFormat(entryName);
  if (!format) {
    throw unsupported(entryName);
  }

  if (format === "xlsx") {
    // Random access needs the workbook as a file of its own: stored = a view, deflated = inflated once.
    return XlsxRecordSource.open(await archive.entryFile(entry), {
      sheet: options.sheet,
      totalBytes: file.size,
      zipEntry: entry.name,
    });
  }

  let compressedRead = 0;
  const stream = await archive.stream(entry, (bytes) => {
    compressedRead += bytes;
  });
  const entryFile: FileLike = {
    name: entryName,
    size: entry.uncompressedSize,
    stream: () => stream,
    slice: () => {
      throw new ImportError(
        "invalidConfig",
        "A zip entry stream has no random access.",
      );
    },
  };
  const inner = await openText(entryFile, entryName, format, options, {
    encoding: options.encoding,
    compressed: false,
    sampleBytes: options.sampleBytes,
  });

  return new ZipEntrySource(
    inner,
    file.size,
    entry.compressedSize,
    entry.name,
    () => compressedRead,
  );
}

/**
 * A text source over a zip entry. Progress is the compressed bytes read of the entry, scaled to
 * the zip file size so it compares with `totalBytes` like any other source.
 */
class ZipEntrySource implements RecordSource {
  readonly totalBytes: number;
  private readonly inner: RecordSource;
  private readonly entrySize: number;
  private readonly entryName: string;
  private readonly compressedRead: () => number;

  constructor(
    inner: RecordSource,
    totalBytes: number,
    entrySize: number,
    entryName: string,
    compressedRead: () => number,
  ) {
    this.inner = inner;
    this.totalBytes = totalBytes;
    this.entrySize = entrySize;
    this.entryName = entryName;
    this.compressedRead = compressedRead;
  }

  get format(): SourceFormat {
    return this.inner.format;
  }

  get columns(): readonly string[] {
    return this.inner.columns;
  }

  get dynamicColumns(): boolean {
    return this.inner.dynamicColumns;
  }

  get bytesRead(): number {
    if (this.entrySize === 0) {
      return this.totalBytes;
    }

    return Math.min(
      this.totalBytes,
      Math.round((this.compressedRead() / this.entrySize) * this.totalBytes),
    );
  }

  get info(): SourceInfo {
    return { ...this.inner.info, zipEntry: this.entryName };
  }

  batches(): AsyncGenerator<SourceRow[], void, void> {
    return this.inner.batches();
  }

  rows(): AsyncGenerator<SourceRow, void, void> {
    return this.inner.rows();
  }

  cancel(): Promise<void> {
    return this.inner.cancel();
  }
}

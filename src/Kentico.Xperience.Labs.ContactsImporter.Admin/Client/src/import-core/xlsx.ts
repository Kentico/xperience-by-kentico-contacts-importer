import { normalizeHeaders } from "./csv";
import { ImportError } from "./source";
import type { FileLike, RecordSource, SourceInfo, SourceRow } from "./source";
import { XmlTokenizer, decodeOoxmlEscapes, parseXmlAttributes } from "./xml";
import type { XmlAttributes, XmlHandler } from "./xml";
import { ZipArchive } from "./zip";
import type { ZipEntry } from "./zip";

/** Most rows an Excel worksheet can have. */
export const XLSX_MAX_ROWS = 1_048_576;

export interface XlsxSourceOptions {
  /** Worksheet name; default: the first visible worksheet. */
  sheet?: string;
  /** Size progress is reported against (the outer file when the workbook is inside a zip). Default: file size. */
  totalBytes?: number;
  /** Name of the workbook inside a zip container. */
  zipEntry?: string;
}

/** Built-in number formats that show dates / times (ECMA-376 §18.8.30, incl. the East Asian ones). */
const BUILTIN_DATE_FORMATS = new Set([
  14, 15, 16, 17, 18, 19, 20, 21, 22, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36,
  45, 46, 47, 50, 51, 52, 53, 54, 55, 56, 57, 58,
]);

/**
 * Whether a number format code shows a date or time: it has `d`, `m`, `y`, `h` or `s` outside
 * quoted text, `[...]` sections (colors, locales, conditions) and escaped characters.
 */
export function isDateFormatCode(code: string): boolean {
  const section = code
    .replace(/"[^"]*"/g, "")
    .replace(/\[[^\]]*\]/g, "")
    .replace(/\\./g, "")
    .replace(/[_*]./g, "")
    .split(";")[0]
    .replace(/general/gi, "");

  return /[dmyhs]/i.test(section);
}

function pad(value: number, length = 2): string {
  return String(value).padStart(length, "0");
}

const DAY_MS = 86_400_000;
const EPOCH_1900 = Date.UTC(1899, 11, 30);
const EPOCH_1900_EARLY = Date.UTC(1899, 11, 31);
const EPOCH_1904 = Date.UTC(1904, 0, 1);
/** Serial of 9999-12-31 in the 1900 date system. */
const MAX_SERIAL = 2_958_465;

/**
 * Excel date serial -> `yyyy-MM-dd`, or `yyyy-MM-ddTHH:mm:ss` when it has a time (rounded to the
 * second). 1900 date system: serial 1 = 1900-01-01 and serial 60 is Excel's 1900-02-29 (a day
 * that does not exist, kept as Excel shows it); 1904 system: serial 0 = 1904-01-01.
 * Returns `null` for values that are no date (negative, too large, not a number).
 */
export function excelSerialToIso(
  serial: number,
  date1904: boolean,
): string | null {
  if (!Number.isFinite(serial) || serial < 0 || serial > MAX_SERIAL) {
    return null;
  }

  const totalSeconds = Math.round(serial * 86_400);
  const days = Math.floor(totalSeconds / 86_400);
  const seconds = totalSeconds - days * 86_400;
  const time =
    seconds === 0
      ? ""
      : `T${pad(Math.floor(seconds / 3600))}:${pad(Math.floor((seconds % 3600) / 60))}:${pad(seconds % 60)}`;

  let date: string;
  if (date1904) {
    date = isoDate(EPOCH_1904 + days * DAY_MS);
  } else if (days === 60) {
    date = "1900-02-29";
  } else {
    date = isoDate((days < 60 ? EPOCH_1900_EARLY : EPOCH_1900) + days * DAY_MS);
  }

  return date + time;
}

function isoDate(ms: number): string {
  const d = new Date(ms);

  return `${pad(d.getUTCFullYear(), 4)}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`;
}

/** 0-based column index of a cell reference (`B7` -> 1); -1 when it has no letters. */
export function columnIndex(reference: string): number {
  let index = 0;
  let i = 0;
  for (; i < reference.length; i++) {
    const code = reference.charCodeAt(i) | 0x20; // lowercase
    if (code < 0x61 || code > 0x7a) {
      break;
    }
    index = index * 26 + (code - 0x60);
  }

  return i === 0 ? -1 : index - 1;
}

function invalidXlsx(message: string): ImportError {
  return new ImportError("invalidXlsx", message);
}

/** Text decoder for an XML part: UTF-16 by byte order mark, otherwise UTF-8. */
function decoderFor(head: Uint8Array): TextDecoder {
  if (head.length >= 2 && head[0] === 0xff && head[1] === 0xfe) {
    return new TextDecoder("utf-16le");
  }
  if (head.length >= 2 && head[0] === 0xfe && head[1] === 0xff) {
    return new TextDecoder("utf-16be");
  }

  return new TextDecoder("utf-8");
}

async function readXmlText(
  archive: ZipArchive,
  entry: ZipEntry,
): Promise<string> {
  const bytes = await archive.readBytes(entry);

  return decoderFor(bytes).decode(bytes);
}

/** Streams an XML part through a handler (for parts that can be large, e.g. shared strings). */
async function streamXml(
  archive: ZipArchive,
  entry: ZipEntry,
  handler: XmlHandler,
): Promise<void> {
  const reader = (await archive.stream(entry)).getReader();
  const tokenizer = new XmlTokenizer(handler);
  let decoder: TextDecoder | undefined;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) {
      break;
    }
    decoder ??= decoderFor(value);
    tokenizer.push(decoder.decode(value, { stream: true }));
  }
  if (decoder) {
    tokenizer.push(decoder.decode());
  }
  tokenizer.end();
}

/** Calls `onOpen` for every start tag of a small, fully read XML text. */
function scanTags(
  text: string,
  onOpen: (name: string, attributes: XmlAttributes) => void,
): void {
  const tokenizer = new XmlTokenizer({
    open: (name, raw) => {
      onOpen(name, parseXmlAttributes(raw));
    },
    close: () => undefined,
    text: () => undefined,
  });
  tokenizer.push(text);
  tokenizer.end();
}

interface Relationship {
  readonly type: string;
  readonly target: string;
}

/** Folder of a part path, with a trailing slash (`xl/workbook.xml` -> `xl/`). */
function folderOf(path: string): string {
  const slash = path.lastIndexOf("/");

  return slash === -1 ? "" : path.slice(0, slash + 1);
}

/** Resolves a relationship target against the folder of its source part. */
function resolvePart(folder: string, target: string): string {
  const parts = (
    target.startsWith("/") ? target.slice(1) : folder + target
  ).split("/");
  const result: string[] = [];
  for (const part of parts) {
    if (part === "..") {
      result.pop();
    } else if (part !== "." && part !== "") {
      result.push(part);
    }
  }

  return result.join("/");
}

async function readRelationships(
  archive: ZipArchive,
  partPath: string,
): Promise<Map<string, Relationship>> {
  const relsPath = `${folderOf(partPath)}_rels/${partPath.slice(folderOf(partPath).length)}.rels`;
  const entry = archive.find(relsPath);
  const result = new Map<string, Relationship>();
  if (!entry) {
    return result;
  }

  const folder = folderOf(partPath);
  scanTags(await readXmlText(archive, entry), (name, attributes) => {
    if (
      name === "Relationship" &&
      attributes.Id &&
      attributes.Target &&
      attributes.TargetMode !== "External"
    ) {
      result.set(attributes.Id, {
        type: attributes.Type ?? "",
        target: resolvePart(folder, attributes.Target),
      });
    }
  });

  return result;
}

interface Workbook {
  readonly sheets: { name: string; path: string; hidden: boolean }[];
  readonly date1904: boolean;
  readonly sharedStringsPath: string | undefined;
  readonly stylesPath: string | undefined;
}

async function readWorkbook(archive: ZipArchive): Promise<Workbook> {
  const rootRels = await readRelationships(archive, "");
  let workbookPath = "xl/workbook.xml";
  for (const rel of rootRels.values()) {
    if (rel.type.endsWith("/officeDocument")) {
      workbookPath = rel.target;
    }
  }

  const workbookEntry = archive.find(workbookPath);
  if (!workbookEntry) {
    throw invalidXlsx("The file is not an Excel workbook (no workbook part).");
  }

  const rels = await readRelationships(archive, workbookPath);
  const sheets: Workbook["sheets"] = [];
  let date1904 = false;
  scanTags(await readXmlText(archive, workbookEntry), (name, attributes) => {
    if (name === "workbookPr") {
      const value = (attributes.date1904 ?? "").toLowerCase();
      date1904 = value === "1" || value === "true";
    } else if (name === "sheet") {
      const rel = attributes.id ? rels.get(attributes.id) : undefined;
      // chartsheets / dialog sheets have no cells
      if (
        rel &&
        rel.type.endsWith("/worksheet") &&
        attributes.name !== undefined
      ) {
        sheets.push({
          name: attributes.name,
          path: rel.target,
          hidden: (attributes.state ?? "visible") !== "visible",
        });
      }
    }
  });

  let sharedStringsPath: string | undefined;
  let stylesPath: string | undefined;
  for (const rel of rels.values()) {
    if (rel.type.endsWith("/sharedStrings")) {
      sharedStringsPath = rel.target;
    } else if (rel.type.endsWith("/styles")) {
      stylesPath = rel.target;
    }
  }

  return { sheets, date1904, sharedStringsPath, stylesPath };
}

/** Per cell style index (`s`): whether its number format is a date / time. */
async function readDateStyles(
  archive: ZipArchive,
  path: string | undefined,
): Promise<boolean[]> {
  const entry = path ? archive.find(path) : undefined;
  if (!entry) {
    return [];
  }

  const customFormats = new Map<number, string>();
  const styles: boolean[] = [];
  let inCellXfs = false;
  const tokenizer = new XmlTokenizer({
    open: (name, raw, selfClosing) => {
      if (name === "numFmt") {
        const attributes = parseXmlAttributes(raw);
        customFormats.set(
          Number(attributes.numFmtId),
          attributes.formatCode ?? "",
        );
      } else if (name === "cellXfs") {
        inCellXfs = !selfClosing;
      } else if (name === "xf" && inCellXfs) {
        const id = Number(parseXmlAttributes(raw).numFmtId ?? 0);
        const custom = customFormats.get(id);
        styles.push(
          custom !== undefined
            ? isDateFormatCode(custom)
            : BUILTIN_DATE_FORMATS.has(id),
        );
      }
    },
    close: (name) => {
      if (name === "cellXfs") {
        inCellXfs = false;
      }
    },
    text: () => undefined,
  });
  tokenizer.push(await readXmlText(archive, entry));
  tokenizer.end();

  return styles;
}

/**
 * Shared strings table, fully in memory (memory ≈ the unique strings of the workbook). Rich text
 * runs are joined; phonetic runs (`rPh`) are left out.
 */
async function readSharedStrings(
  archive: ZipArchive,
  path: string | undefined,
): Promise<string[]> {
  const entry = path ? archive.find(path) : undefined;
  if (!entry) {
    return [];
  }

  const strings: string[] = [];
  let current = "";
  let inItem = false;
  let phonetic = 0;
  let capture = false;
  await streamXml(archive, entry, {
    open: (name, _raw, selfClosing) => {
      if (selfClosing) {
        if (name === "si") {
          inItem = true;
          current = "";
        }

        return;
      }
      if (name === "si") {
        inItem = true;
        current = "";
      } else if (name === "rPh") {
        phonetic++;
      } else if (name === "t" && inItem && phonetic === 0) {
        capture = true;
      }
    },
    close: (name) => {
      if (name === "t") {
        capture = false;
      } else if (name === "rPh") {
        phonetic--;
      } else if (name === "si") {
        strings.push(decodeOoxmlEscapes(current));
        current = "";
        inItem = false;
      }
    },
    text: (text) => {
      if (capture) {
        current += text;
      }
    },
  });

  return strings;
}

/** Excel's own `TRUE` / `FALSE` text for boolean cells. */
function booleanText(value: string): string {
  return value === "1" ? "TRUE" : value === "0" ? "FALSE" : value;
}

/** Parses `<sheetData>` into rows: sparse cells, shared / inline strings, dates. */
class SheetHandler implements XmlHandler {
  columns: string[] = [];
  columnsReady = false;
  out: SourceRow[] = [];

  private readonly strings: readonly string[];
  private readonly dateStyles: readonly boolean[];
  private readonly date1904: boolean;
  private headerRow = -1;
  private row = 0;
  /** Sparse: a column without a `<c>` element is a hole. */
  private cells: (string | undefined)[] = [];
  private column = -1;
  private cellType = "";
  private cellStyle = 0;
  private value = "";
  private capture = false;
  private inInlineString = false;
  private phonetic = 0;

  constructor(
    strings: readonly string[],
    dateStyles: readonly boolean[],
    date1904: boolean,
  ) {
    this.strings = strings;
    this.dateStyles = dateStyles;
    this.date1904 = date1904;
  }

  open(name: string, raw: string, selfClosing: boolean): void {
    switch (name) {
      case "row": {
        const r = Number(parseXmlAttributes(raw).r);
        this.row = Number.isInteger(r) && r > 0 ? r : this.row + 1;
        this.cells = [];
        this.column = -1;
        break;
      }
      case "c": {
        const attributes = parseXmlAttributes(raw);
        const reference = attributes.r;
        const column = reference ? columnIndex(reference) : -1;
        this.column = column >= 0 ? column : this.column + 1;
        this.cellType = attributes.t ?? "n";
        this.cellStyle = Number(attributes.s ?? 0);
        this.value = "";
        break;
      }
      case "v":
        this.capture = !selfClosing;
        break;
      case "is":
        this.inInlineString = !selfClosing;
        break;
      case "rPh":
        if (!selfClosing) {
          this.phonetic++;
        }
        break;
      case "t":
        this.capture =
          this.inInlineString && this.phonetic === 0 && !selfClosing;
        break;
      default:
        break;
    }
  }

  close(name: string): void {
    switch (name) {
      case "v":
      case "t":
        this.capture = false;
        break;
      case "is":
        this.inInlineString = false;
        break;
      case "rPh":
        this.phonetic = Math.max(0, this.phonetic - 1);
        break;
      case "c": {
        const text = this.cellText();
        if (text !== "") {
          this.cells[this.column] = text;
        }
        break;
      }
      case "row":
        this.endRow();
        break;
      default:
        break;
    }
  }

  text(text: string): void {
    if (this.capture) {
      this.value += text;
    }
  }

  end(): void {
    this.columnsReady = true;
  }

  private cellText(): string {
    const value = this.value;
    if (value === "") {
      return "";
    }
    switch (this.cellType) {
      case "s":
        return this.strings[Number(value)] ?? "";
      case "inlineStr":
      case "str":
        return decodeOoxmlEscapes(value);
      case "b":
        return booleanText(value);
      case "e":
        return "";
      case "d":
        return value.replace(/T00:00:00(\.0+)?Z?$/, "");
      default: {
        if (!this.dateStyles[this.cellStyle]) {
          return value;
        }

        return excelSerialToIso(Number(value), this.date1904) ?? value;
      }
    }
  }

  private endRow(): void {
    const cells = this.cells;
    this.cells = [];
    if (cells.length === 0) {
      return;
    }

    if (!this.columnsReady) {
      const header: string[] = [];
      for (const cell of cells) {
        header.push(cell ?? "");
      }
      this.columns = normalizeHeaders(header);
      this.headerRow = this.row;
      this.columnsReady = true;

      return;
    }

    if (this.row > XLSX_MAX_ROWS) {
      throw new ImportError(
        "tooManyRows",
        `The sheet has more than ${XLSX_MAX_ROWS} rows.`,
        {
          maxRows: XLSX_MAX_ROWS,
        },
      );
    }

    const length = Math.max(this.columns.length, cells.length);
    const values = new Array<string>(length);
    for (let i = 0; i < length; i++) {
      values[i] = cells[i] ?? "";
    }
    this.out.push({ rowNumber: this.row - this.headerRow, values });
  }
}

/**
 * Streaming XLSX record source: workbook, relationships, styles and the shared strings table are
 * read first; the worksheet XML is then streamed (inflated in pieces) and parsed into rows.
 * - First row with a value = header (empty names -> `Column N`, duplicates numbered, as for CSV).
 * - Row number = Excel row number minus the header's row number (gaps count; empty rows skipped).
 * - Date-formatted numbers -> ISO date (`yyyy-MM-dd[THH:mm:ss]`); other numbers keep their stored text.
 */
export class XlsxRecordSource implements RecordSource {
  readonly format = "xlsx" as const;
  readonly dynamicColumns = false;
  readonly totalBytes: number;

  private readonly handler: SheetHandler;
  private readonly sheetBytes: number;
  private readonly sheetInfo: Pick<SourceInfo, "sheet" | "sheets" | "zipEntry">;
  private reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
  private readonly tokenizer: XmlTokenizer;
  private decoder: TextDecoder | undefined;
  private sheetRead = 0;
  private pending: SourceRow[] = [];
  private eof = false;
  private consumed = false;
  private cancelled = false;

  private constructor(
    handler: SheetHandler,
    totalBytes: number,
    sheetBytes: number,
    sheetInfo: Pick<SourceInfo, "sheet" | "sheets" | "zipEntry">,
  ) {
    this.handler = handler;
    this.totalBytes = totalBytes;
    this.sheetBytes = sheetBytes;
    this.sheetInfo = sheetInfo;
    this.tokenizer = new XmlTokenizer(handler);
  }

  /** Opens the workbook and reads the sheet far enough to know the columns. */
  static async open(
    file: FileLike,
    options: XlsxSourceOptions = {},
  ): Promise<XlsxRecordSource> {
    let archive: ZipArchive;
    try {
      archive = await ZipArchive.open(file);
    } catch (error) {
      if (error instanceof ImportError && error.code === "invalidZip") {
        throw invalidXlsx("The file is not an Excel workbook (.xlsx).");
      }
      throw error;
    }

    const workbook = await readWorkbook(archive);
    if (workbook.sheets.length === 0) {
      throw invalidXlsx("The workbook has no worksheets.");
    }

    let sheet;
    if (options.sheet !== undefined && options.sheet !== "") {
      const wanted = options.sheet;
      sheet =
        workbook.sheets.find((s) => s.name === wanted) ??
        workbook.sheets.find(
          (s) => s.name.toLowerCase() === wanted.toLowerCase(),
        );
      if (!sheet) {
        throw new ImportError(
          "sheetNotFound",
          `The workbook has no sheet '${wanted}'.`,
          { sheet: wanted },
        );
      }
    } else {
      sheet = workbook.sheets.find((s) => !s.hidden) ?? workbook.sheets[0];
    }

    const sheetEntry = archive.find(sheet.path);
    if (!sheetEntry) {
      throw invalidXlsx(
        `The workbook part of sheet '${sheet.name}' is missing.`,
      );
    }

    const [dateStyles, strings] = [
      await readDateStyles(archive, workbook.stylesPath),
      await readSharedStrings(
        archive,
        workbook.sharedStringsPath ??
          (archive.find("xl/sharedStrings.xml")
            ? "xl/sharedStrings.xml"
            : undefined),
      ),
    ];

    const handler = new SheetHandler(strings, dateStyles, workbook.date1904);
    const source = new XlsxRecordSource(
      handler,
      options.totalBytes ?? file.size,
      sheetEntry.compressedSize,
      {
        sheet: sheet.name,
        sheets: workbook.sheets.map((s) => s.name),
        ...(options.zipEntry !== undefined && { zipEntry: options.zipEntry }),
      },
    );
    source.reader = (
      await archive.stream(sheetEntry, (bytes) => {
        source.sheetRead += bytes;
      })
    ).getReader();

    try {
      while (!handler.columnsReady && !source.eof) {
        await source.readNext();
      }
    } catch (error) {
      await source.cancel();
      throw error;
    }
    source.pending = handler.out;
    handler.out = [];

    return source;
  }

  get columns(): readonly string[] {
    return this.handler.columns;
  }

  get bytesRead(): number {
    if (this.eof || this.sheetBytes === 0) {
      return this.eof ? this.totalBytes : 0;
    }

    return Math.min(
      this.totalBytes,
      Math.round((this.sheetRead / this.sheetBytes) * this.totalBytes),
    );
  }

  get info(): SourceInfo {
    return { encoding: "utf-8", compressed: false, ...this.sheetInfo };
  }

  async *batches(): AsyncGenerator<SourceRow[], void, void> {
    if (this.consumed) {
      throw new ImportError(
        "sourceConsumed",
        "The record source was already read. Open a new source for another pass.",
      );
    }
    this.consumed = true;

    try {
      if (this.pending.length > 0) {
        const first = this.pending;
        this.pending = [];
        yield first;
      }

      while (!this.eof && !this.cancelled) {
        await this.readNext();
        const batch = this.handler.out;
        if (batch.length > 0) {
          this.handler.out = [];
          yield batch;
        }
      }
    } finally {
      await this.cancel();
    }
  }

  async *rows(): AsyncGenerator<SourceRow, void, void> {
    for await (const batch of this.batches()) {
      yield* batch;
    }
  }

  async cancel(): Promise<void> {
    if (this.cancelled) {
      return;
    }
    this.cancelled = true;
    this.pending = [];
    if (!this.eof && this.reader) {
      try {
        await this.reader.cancel();
      } catch {
        // the stream already failed; nothing left to release
      }
    }
  }

  private async readNext(): Promise<void> {
    const reader = this.reader;
    if (!reader) {
      throw new Error("The sheet is not open.");
    }
    const { done, value } = await reader.read();
    if (!done) {
      this.decoder ??= decoderFor(value);
      this.tokenizer.push(this.decoder.decode(value, { stream: true }));

      return;
    }

    this.eof = true;
    if (this.decoder) {
      this.tokenizer.push(this.decoder.decode());
    }
    this.tokenizer.end();
    this.handler.end();
  }
}

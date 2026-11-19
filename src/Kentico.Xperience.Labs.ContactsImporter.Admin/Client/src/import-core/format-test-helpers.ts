import { utf8 } from "./test-helpers";

/** Builders of zip / XLSX test files in memory, so no binary fixtures are committed. */

const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) {
      c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    }
    table[n] = c >>> 0;
  }

  return table;
})();

export function crc32(bytes: Uint8Array): number {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc = CRC_TABLE[(crc ^ byte) & 0xff] ^ (crc >>> 8);
  }

  return (crc ^ 0xffffffff) >>> 0;
}

export async function deflateRawBytes(bytes: Uint8Array): Promise<Uint8Array> {
  const stream = new Blob([bytes as BlobPart])
    .stream()
    .pipeThrough(new CompressionStream("deflate-raw"));

  return new Uint8Array(await new Response(stream).arrayBuffer());
}

export interface ZipTestEntry {
  name: string;
  data: string | Uint8Array;
  /** 0 = stored (default), 8 = deflate; other values are written as is with stored data. */
  method?: number;
  /** General purpose flags (bit 0 = encrypted). */
  flags?: number;
}

export interface ZipTestOptions {
  /** Write ZIP64 size / offset fields and end records (for small data, as a format test). */
  zip64?: boolean;
  /** Archive comment (after the end record). */
  comment?: string;
}

function concat(parts: Uint8Array[]): Uint8Array {
  const result = new Uint8Array(
    parts.reduce((sum, part) => sum + part.length, 0),
  );
  let at = 0;
  for (const part of parts) {
    result.set(part, at);
    at += part.length;
  }

  return result;
}

/** Builds a zip file in memory. */
export async function buildZip(
  entries: ZipTestEntry[],
  options: ZipTestOptions = {},
): Promise<Uint8Array> {
  const zip64 = options.zip64 ?? false;
  const parts: Uint8Array[] = [];
  const central: Uint8Array[] = [];
  let offset = 0;

  for (const entry of entries) {
    const raw = typeof entry.data === "string" ? utf8(entry.data) : entry.data;
    const method = entry.method ?? 0;
    const data = method === 8 ? await deflateRawBytes(raw) : raw;
    const name = utf8(entry.name);
    const crc = crc32(raw);
    const flags = (entry.flags ?? 0) | 0x0800;

    const localExtra = new Uint8Array(zip64 ? 20 : 0);
    if (zip64) {
      const view = new DataView(localExtra.buffer);
      view.setUint16(0, 1, true);
      view.setUint16(2, 16, true);
      view.setBigUint64(4, BigInt(raw.length), true);
      view.setBigUint64(12, BigInt(data.length), true);
    }
    const local = new Uint8Array(30 + name.length + localExtra.length);
    const lv = new DataView(local.buffer);
    lv.setUint32(0, 0x04034b50, true);
    lv.setUint16(4, zip64 ? 45 : 20, true);
    lv.setUint16(6, flags, true);
    lv.setUint16(8, method, true);
    lv.setUint32(14, crc, true);
    lv.setUint32(18, zip64 ? 0xffffffff : data.length, true);
    lv.setUint32(22, zip64 ? 0xffffffff : raw.length, true);
    lv.setUint16(26, name.length, true);
    lv.setUint16(28, localExtra.length, true);
    local.set(name, 30);
    local.set(localExtra, 30 + name.length);

    const centralExtra = new Uint8Array(zip64 ? 28 : 0);
    if (zip64) {
      const view = new DataView(centralExtra.buffer);
      view.setUint16(0, 1, true);
      view.setUint16(2, 24, true);
      view.setBigUint64(4, BigInt(raw.length), true);
      view.setBigUint64(12, BigInt(data.length), true);
      view.setBigUint64(20, BigInt(offset), true);
    }
    const header = new Uint8Array(46 + name.length + centralExtra.length);
    const cv = new DataView(header.buffer);
    cv.setUint32(0, 0x02014b50, true);
    cv.setUint16(4, zip64 ? 45 : 20, true);
    cv.setUint16(6, zip64 ? 45 : 20, true);
    cv.setUint16(8, flags, true);
    cv.setUint16(10, method, true);
    cv.setUint32(16, crc, true);
    cv.setUint32(20, zip64 ? 0xffffffff : data.length, true);
    cv.setUint32(24, zip64 ? 0xffffffff : raw.length, true);
    cv.setUint16(28, name.length, true);
    cv.setUint16(30, centralExtra.length, true);
    cv.setUint32(42, zip64 ? 0xffffffff : offset, true);
    header.set(name, 46);
    header.set(centralExtra, 46 + name.length);
    central.push(header);

    parts.push(local, data);
    offset += local.length + data.length;
  }

  const centralBytes = concat(central);
  const centralOffset = offset;
  parts.push(centralBytes);
  offset += centralBytes.length;

  if (zip64) {
    const record = new Uint8Array(56);
    const rv = new DataView(record.buffer);
    rv.setUint32(0, 0x06064b50, true);
    rv.setBigUint64(4, 44n, true);
    rv.setUint16(12, 45, true);
    rv.setUint16(14, 45, true);
    rv.setBigUint64(24, BigInt(entries.length), true);
    rv.setBigUint64(32, BigInt(entries.length), true);
    rv.setBigUint64(40, BigInt(centralBytes.length), true);
    rv.setBigUint64(48, BigInt(centralOffset), true);
    const locator = new Uint8Array(20);
    const locv = new DataView(locator.buffer);
    locv.setUint32(0, 0x07064b50, true);
    locv.setBigUint64(8, BigInt(offset), true);
    locv.setUint32(16, 1, true);
    parts.push(record, locator);
  }

  const comment = utf8(options.comment ?? "");
  const end = new Uint8Array(22 + comment.length);
  const ev = new DataView(end.buffer);
  ev.setUint32(0, 0x06054b50, true);
  ev.setUint16(8, zip64 ? 0xffff : entries.length, true);
  ev.setUint16(10, zip64 ? 0xffff : entries.length, true);
  ev.setUint32(12, zip64 ? 0xffffffff : centralBytes.length, true);
  ev.setUint32(16, zip64 ? 0xffffffff : centralOffset, true);
  ev.setUint16(20, comment.length, true);
  end.set(comment, 22);
  parts.push(end);

  return concat(parts);
}

export function xmlEscape(text: string): string {
  return text
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

export interface XlsxTestSheet {
  name: string;
  /** Content of `<sheetData>` (rows as XML). */
  rows: string;
  hidden?: boolean;
}

export interface XlsxTestOptions {
  sheets: XlsxTestSheet[];
  /** `<si>` items as XML (e.g. `<si><t>Email</t></si>`); no part when not set. */
  sharedStrings?: string[];
  /** `<numFmts>` content. */
  numFmts?: string;
  /** numFmtId of each cell style (`s` index). Default `[0, 14]` (s="1" = date). */
  cellXfs?: number[];
  date1904?: boolean;
  /** Compression of the package parts. Default deflate. */
  method?: number;
}

const NS_MAIN = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
const NS_REL =
  "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
const NS_PKG_REL =
  "http://schemas.openxmlformats.org/package/2006/relationships";

/** Builds a minimal but valid XLSX package in memory. */
export async function buildXlsx(options: XlsxTestOptions): Promise<Uint8Array> {
  const method = options.method ?? 8;
  const entries: ZipTestEntry[] = [];
  const add = (name: string, data: string) =>
    entries.push({ name, data, method });

  add(
    "[Content_Types].xml",
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>',
  );
  add(
    "_rels/.rels",
    `<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="${NS_PKG_REL}"><Relationship Id="rId1" Type="${NS_REL}/officeDocument" Target="xl/workbook.xml"/></Relationships>`,
  );

  const sheetElements = options.sheets
    .map(
      (sheet, i) =>
        `<sheet name="${xmlEscape(sheet.name)}" sheetId="${i + 1}"${sheet.hidden ? ' state="hidden"' : ""} r:id="rId${i + 1}"/>`,
    )
    .join("");
  add(
    "xl/workbook.xml",
    `<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="${NS_MAIN}" xmlns:r="${NS_REL}"><workbookPr${options.date1904 ? ' date1904="1"' : ""}/><sheets>${sheetElements}</sheets></workbook>`,
  );

  const n = options.sheets.length;
  const rels = options.sheets
    .map(
      (_, i) =>
        `<Relationship Id="rId${i + 1}" Type="${NS_REL}/worksheet" Target="worksheets/sheet${i + 1}.xml"/>`,
    )
    .join("");
  add(
    "xl/_rels/workbook.xml.rels",
    `<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="${NS_PKG_REL}">${rels}<Relationship Id="rId${n + 1}" Type="${NS_REL}/styles" Target="styles.xml"/><Relationship Id="rId${n + 2}" Type="${NS_REL}/sharedStrings" Target="sharedStrings.xml"/></Relationships>`,
  );

  const xfs = (options.cellXfs ?? [0, 14])
    .map((id) => `<xf numFmtId="${id}" fontId="0"/>`)
    .join("");
  const numFmts = options.numFmts
    ? `<numFmts>${options.numFmts}</numFmts>`
    : "";
  add(
    "xl/styles.xml",
    `<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="${NS_MAIN}">${numFmts}<cellStyleXfs count="1"><xf numFmtId="0"/></cellStyleXfs><cellXfs>${xfs}</cellXfs></styleSheet>`,
  );
  if (options.sharedStrings) {
    add(
      "xl/sharedStrings.xml",
      `<?xml version="1.0" encoding="UTF-8"?><sst xmlns="${NS_MAIN}" count="${options.sharedStrings.length}">${options.sharedStrings.join("")}</sst>`,
    );
  }
  options.sheets.forEach((sheet, i) =>
    add(
      `xl/worksheets/sheet${i + 1}.xml`,
      `<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="${NS_MAIN}"><sheetData>${sheet.rows}</sheetData></worksheet>`,
    ),
  );

  return buildZip(entries);
}

/** Column letters of a 0-based index (`0` -> `A`, `26` -> `AA`). */
export function columnLetters(index: number): string {
  let letters = "";
  for (let n = index + 1; n > 0; n = Math.floor((n - 1) / 26)) {
    letters = String.fromCharCode(65 + ((n - 1) % 26)) + letters;
  }

  return letters;
}

/**
 * XLSX of a table of strings (first row = header), as shared strings (`shared`, Excel's way) or
 * inline strings. Empty values are left out (sparse cells).
 */
export async function tableXlsx(
  rows: string[][],
  shared = true,
): Promise<Uint8Array> {
  const strings: string[] = [];
  const index = new Map<string, number>();
  const xml = rows
    .map((row, r) => {
      const cells = row
        .map((value, c) => {
          if (value === "") {
            return "";
          }
          const ref = `${columnLetters(c)}${r + 1}`;
          if (!shared) {
            return `<c r="${ref}" t="inlineStr"><is><t>${xmlEscape(value)}</t></is></c>`;
          }
          let i = index.get(value);
          if (i === undefined) {
            i = strings.length;
            strings.push(
              `<si><t xml:space="preserve">${xmlEscape(value)}</t></si>`,
            );
            index.set(value, i);
          }

          return `<c r="${ref}" t="s"><v>${i}</v></c>`;
        })
        .join("");

      return `<row r="${r + 1}">${cells}</row>`;
    })
    .join("");

  return buildXlsx({
    sheets: [{ name: "Sheet1", rows: xml }],
    ...(shared && { sharedStrings: strings }),
  });
}

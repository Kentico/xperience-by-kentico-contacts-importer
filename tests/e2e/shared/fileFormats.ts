import zlib from "node:zlib";

// Builders of small zip and XLSX files for the format specs, so no binary fixtures are committed.

export interface ZipEntryContent {
  name: string;
  data: string | Buffer;
  // Default true (deflate); false = stored
  deflate?: boolean;
}

export function buildZip(entries: ZipEntryContent[]): Buffer {
  const parts: Buffer[] = [];
  const central: Buffer[] = [];
  let offset = 0;

  for (const entry of entries) {
    const raw = typeof entry.data === "string" ? Buffer.from(entry.data, "utf8") : entry.data;
    const deflate = entry.deflate ?? true;
    const data = deflate ? zlib.deflateRawSync(raw) : raw;
    const name = Buffer.from(entry.name, "utf8");
    const crc = zlib.crc32(raw);
    const method = deflate ? 8 : 0;

    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x0800, 6);
    local.writeUInt16LE(method, 8);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(raw.length, 22);
    local.writeUInt16LE(name.length, 26);

    const header = Buffer.alloc(46);
    header.writeUInt32LE(0x02014b50, 0);
    header.writeUInt16LE(20, 4);
    header.writeUInt16LE(20, 6);
    header.writeUInt16LE(0x0800, 8);
    header.writeUInt16LE(method, 10);
    header.writeUInt32LE(crc, 16);
    header.writeUInt32LE(data.length, 20);
    header.writeUInt32LE(raw.length, 24);
    header.writeUInt16LE(name.length, 28);
    header.writeUInt32LE(offset, 42);
    central.push(header, name);

    parts.push(local, name, data);
    offset += local.length + name.length + data.length;
  }

  const directory = Buffer.concat(central);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(directory.length, 12);
  end.writeUInt32LE(offset, 16);

  return Buffer.concat([...parts, directory, end]);
}

function xmlEscape(text: string): string {
  return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

function columnLetters(index: number): string {
  let letters = "";
  for (let n = index + 1; n > 0; n = Math.floor((n - 1) / 26)) {
    letters = String.fromCharCode(65 + ((n - 1) % 26)) + letters;
  }
  return letters;
}

// A cell value: text (shared string) or a date (number with a date format, as Excel stores it).
export type CellValue = string | Date;

export interface SheetContent {
  name: string;
  rows: CellValue[][];
}

// Excel 1900-system serial of a UTC date
function excelSerial(date: Date): number {
  return (date.getTime() - Date.UTC(1899, 11, 30)) / 86_400_000;
}

// XLSX with shared strings (as Excel writes them) and a date style (built-in format 14) for dates.
export function buildXlsx(sheets: SheetContent[]): Buffer {
  const main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
  const rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
  const pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
  const strings: string[] = [];
  const stringIndex = new Map<string, number>();

  const sheetXml = sheets.map((sheet) => {
    const rows = sheet.rows
      .map((row, r) => {
        const cells = row
          .map((value, c) => {
            const ref = `${columnLetters(c)}${r + 1}`;
            if (value instanceof Date) {
              return `<c r="${ref}" s="1"><v>${excelSerial(value)}</v></c>`;
            }
            if (value === "") {
              return "";
            }
            let index = stringIndex.get(value);
            if (index === undefined) {
              index = strings.length;
              strings.push(value);
              stringIndex.set(value, index);
            }
            return `<c r="${ref}" t="s"><v>${index}</v></c>`;
          })
          .join("");
        return `<row r="${r + 1}">${cells}</row>`;
      })
      .join("");
    return `<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="${main}"><sheetData>${rows}</sheetData></worksheet>`;
  });

  const entries: ZipEntryContent[] = [
    { name: "[Content_Types].xml", data: '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>' },
    { name: "_rels/.rels", data: `<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="${pkg}"><Relationship Id="rId1" Type="${rel}/officeDocument" Target="xl/workbook.xml"/></Relationships>` },
    {
      name: "xl/workbook.xml",
      data: `<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="${main}" xmlns:r="${rel}"><sheets>${sheets
        .map((sheet, i) => `<sheet name="${xmlEscape(sheet.name)}" sheetId="${i + 1}" r:id="rId${i + 1}"/>`)
        .join("")}</sheets></workbook>`,
    },
    {
      name: "xl/_rels/workbook.xml.rels",
      data: `<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="${pkg}">${sheets
        .map((_, i) => `<Relationship Id="rId${i + 1}" Type="${rel}/worksheet" Target="worksheets/sheet${i + 1}.xml"/>`)
        .join("")}<Relationship Id="rIdS" Type="${rel}/styles" Target="styles.xml"/><Relationship Id="rIdT" Type="${rel}/sharedStrings" Target="sharedStrings.xml"/></Relationships>`,
    },
    {
      name: "xl/styles.xml",
      data: `<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="${main}"><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/></cellXfs></styleSheet>`,
    },
    ...sheetXml.map((data, i) => ({ name: `xl/worksheets/sheet${i + 1}.xml`, data })),
  ];
  entries.push({
    name: "xl/sharedStrings.xml",
    data: `<?xml version="1.0" encoding="UTF-8"?><sst xmlns="${main}" count="${strings.length}" uniqueCount="${strings.length}">${strings
      .map((text) => `<si><t xml:space="preserve">${xmlEscape(text)}</t></si>`)
      .join("")}</sst>`,
  });

  return buildZip(entries);
}
